using System.Diagnostics;
using Titanfall2;

/// <summary>
/// Incrementally indexes PCF libraries and creates the particle systems referenced
/// by a map's *_fx.ent partition. Instances are retained; distant systems are
/// suspended instead of destroyed so moving through a map does not reveal gaps.
/// </summary>
[Library]
public sealed class Titanfall2ParticleStreamer : Component, Component.DontExecuteOnServer
{
	static readonly Sandbox.Diagnostics.Logger Log = new( "Titanfall2Particles" );
	const int DataMagic = 0x58465054; // TPFX
	const int DataVersion = 2;
	const int MaximumEntityCount = 100_000;

	[Property, Hide]
	public string MountIdent { get; set; }

	[Property, Hide]
	public string MapPath { get; set; }

	[Property, Hide]
	public Vector3 FallbackAnchor { get; set; }

	[Property, Hide]
	public string EncodedEntities { get; set; }

	readonly List<RuntimeEntity> _pending = [];
	readonly List<SpawnedEntity> _spawned = [];
	readonly Stopwatch _statusTimer = new();
	readonly Stopwatch _activationTimer = new();
	bool _initialized;
	bool _completionLogged;
	int _failed;
	int _failureLogs;

	internal void Configure( string mountIdent, string mapPath, Vector3 fallbackAnchor,
		IReadOnlyList<Titanfall2ParticleMapEntity> entities )
	{
		MountIdent = mountIdent;
		MapPath = mapPath;
		FallbackAnchor = fallbackAnchor;
		EncodedEntities = Encode( entities );
	}

	protected override void OnAwake() => InitializeEntities();

	protected override void OnUpdate()
	{
		if ( !_initialized ) InitializeEntities();
		if ( !_initialized ) return;

		var mount = Sandbox.Mounting.Directory.Get( MountIdent ) as Titanfall2Mount;
		if ( mount is null || !mount.IsMounted ) return;

		if ( _pending.Count > 0 && !mount.IsParticleCatalogComplete )
		{
			mount.IndexParticleFiles(
				Titanfall2StreamingSettings.ParticleLibrariesPerFrame,
				Titanfall2StreamingSettings.ParticleLibraryFrameBudgetMilliseconds );
		}

		SpawnAvailable( mount );
		if ( _pending.Count > 0 && mount.IsParticleCatalogComplete )
		{
			_failed += _pending.Count;
			foreach ( var entity in _pending.Take( 16 - Math.Min( 16, _failureLogs ) ) )
			{
				Log.Warning( $"Titanfall 2 particle definition '{entity.EffectName}' was not found for '{MapPath}'." );
				_failureLogs++;
			}
			_pending.Clear();
		}

		if ( _activationTimer.Elapsed.TotalSeconds >= 0.25 ) RefreshActivation();
		if ( !_completionLogged && _pending.Count == 0 )
		{
			_completionLogged = true;
			Log.Info( $"Titanfall 2 particle streaming complete: {_spawned.Count} map FX retained, {_failed} skipped; "
				+ $"{mount.ParticleDefinitionCount} definitions indexed from {mount.IndexedParticleFileCount}/{mount.ParticleFileCount} PCFs"
				+ $"{(mount.IsParticleCatalogComplete ? string.Empty : " (catalog stopped after all requested effects resolved)")} ({MapPath})." );
		}
		else if ( !_completionLogged && _statusTimer.Elapsed.TotalSeconds >= 5.0 )
		{
			Log.Info( $"Titanfall 2 particle streaming: {_spawned.Count} spawned, {_pending.Count} pending, {_failed} skipped; "
				+ $"{mount.IndexedParticleFileCount}/{mount.ParticleFileCount} PCFs, {mount.ParticleDefinitionCount} definitions ({MapPath})." );
			_statusTimer.Restart();
		}
	}

	protected override void OnDestroy()
	{
		foreach ( var entity in _spawned )
			if ( entity.GameObject.IsValid() && !entity.GameObject.IsDestroyed ) entity.GameObject.Destroy();
		_spawned.Clear();
		_pending.Clear();
	}

	void InitializeEntities()
	{
		if ( _initialized ) return;
		_initialized = true;
		if ( string.IsNullOrWhiteSpace( EncodedEntities ) ) return;
		try
		{
			_pending.AddRange( Decode( EncodedEntities ) );
			var anchor = GetAnchor();
			_pending.Sort( ( left, right ) => left.Position.DistanceSquared( anchor ).CompareTo( right.Position.DistanceSquared( anchor ) ) );
			_statusTimer.Start();
			_activationTimer.Start();
			Log.Info( $"Titanfall 2 particle streamer ready: {_pending.Count} active map FX; "
				+ $"{Titanfall2StreamingSettings.ParticleLibrariesPerFrame} PCFs/frame, "
				+ $"{Titanfall2StreamingSettings.ParticleInstancesPerFrame} systems/frame ({MapPath})." );
		}
		catch ( Exception exception )
		{
			_pending.Clear();
			Log.Warning( $"Failed to initialize Titanfall 2 particle streaming for '{MapPath}': {exception.Message}" );
		}
	}

	void SpawnAvailable( Titanfall2Mount mount )
	{
		if ( _pending.Count == 0 ) return;
		var timer = Stopwatch.StartNew();
		var created = 0;
		for ( var index = 0; index < _pending.Count
			&& created < Titanfall2StreamingSettings.ParticleInstancesPerFrame
			&& timer.Elapsed.TotalMilliseconds < Titanfall2StreamingSettings.ParticleInstanceFrameBudgetMilliseconds; )
		{
			var entity = _pending[index];
			if ( !mount.TryGetParticleDefinition( entity.EffectName, out var definition ) )
			{
				index++;
				continue;
			}

			_pending.RemoveAt( index );
			if ( SpawnEntity( mount, entity, definition ) ) created++;
			else _failed++;
		}
	}

	bool SpawnEntity( Titanfall2Mount mount, RuntimeEntity entity, Titanfall2ParticleDefinition definition )
	{
		var gameObject = new GameObject( GameObject, true,
			string.IsNullOrWhiteSpace( entity.TargetName ) ? entity.EffectName : entity.TargetName );
		gameObject.WorldPosition = entity.Position;
		gameObject.WorldRotation = entity.Rotation;
		gameObject.WorldScale = Vector3.One * entity.Scale;
		if ( !Titanfall2ParticleFactory.TryCreate( gameObject, mount, definition, out var createdSystems, out var error ) )
		{
			gameObject.Destroy();
			if ( _failureLogs++ < 16 ) Log.Warning( $"Unable to create Titanfall 2 particle '{entity.EffectName}': {error}" );
			return false;
		}

		var alwaysActive = IsGlobalEffect( entity.EffectName );
		var spawned = new SpawnedEntity( gameObject, entity.Position, alwaysActive, createdSystems );
		_spawned.Add( spawned );
		ApplyActivation( spawned, GetAnchor() );
		return true;
	}

	void RefreshActivation()
	{
		_activationTimer.Restart();
		var anchor = GetAnchor();
		foreach ( var entity in _spawned ) ApplyActivation( entity, anchor );
	}

	static void ApplyActivation( SpawnedEntity entity, Vector3 anchor )
	{
		if ( !entity.GameObject.IsValid() || entity.GameObject.IsDestroyed ) return;
		var radius = Titanfall2StreamingSettings.ParticleActivationRadius;
		var wanted = entity.AlwaysActive || entity.Position.DistanceSquared( anchor ) <= radius * radius;
		if ( entity.GameObject.Enabled != wanted ) entity.GameObject.Enabled = wanted;
	}

	Vector3 GetAnchor() => Scene?.Camera.IsValid() == true ? Scene.Camera.WorldPosition : FallbackAnchor;

	static bool IsGlobalEffect( string name )
	{
		if ( string.IsNullOrWhiteSpace( name ) ) return false;
		return name.Contains( "sun", StringComparison.OrdinalIgnoreCase )
			|| name.Contains( "star", StringComparison.OrdinalIgnoreCase )
			|| name.Contains( "sky", StringComparison.OrdinalIgnoreCase )
			|| name.Contains( "weather", StringComparison.OrdinalIgnoreCase );
	}

	static string Encode( IReadOnlyList<Titanfall2ParticleMapEntity> entities )
	{
		var valid = entities?.Where( static entity => !string.IsNullOrWhiteSpace( entity.EffectName ) ).ToArray()
			?? Array.Empty<Titanfall2ParticleMapEntity>();
		using var stream = new MemoryStream();
		using ( var writer = new BinaryWriter( stream, System.Text.Encoding.UTF8, true ) )
		{
			writer.Write( DataMagic );
			writer.Write( DataVersion );
			writer.Write( valid.Length );
			foreach ( var entity in valid )
			{
				writer.Write( entity.EffectName );
				writer.Write( entity.TargetName ?? string.Empty );
				writer.Write( entity.Position.x );
				writer.Write( entity.Position.y );
				writer.Write( entity.Position.z );
				writer.Write( entity.Rotation.pitch );
				writer.Write( entity.Rotation.yaw );
				writer.Write( entity.Rotation.roll );
				writer.Write( entity.Scale );
			}
		}
		return Convert.ToBase64String( stream.GetBuffer(), 0, checked((int)stream.Length) );
	}

	static IEnumerable<RuntimeEntity> Decode( string encoded )
	{
		using var stream = new MemoryStream( Convert.FromBase64String( encoded ), false );
		using var reader = new BinaryReader( stream, System.Text.Encoding.UTF8, false );
		if ( reader.ReadInt32() != DataMagic ) throw new InvalidDataException( "Particle entity data has an invalid signature." );
		if ( reader.ReadInt32() != DataVersion ) throw new InvalidDataException( "Particle entity data has an unsupported version." );
		var count = reader.ReadInt32();
		if ( count < 0 || count > MaximumEntityCount ) throw new InvalidDataException( $"Invalid particle entity count {count}." );
		for ( var index = 0; index < count; index++ )
		{
			var effectName = reader.ReadString();
			var targetName = reader.ReadString();
			var position = new Vector3( reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle() );
			var rotation = new Angles( reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle() );
			var scale = Math.Clamp( reader.ReadSingle(), 0.001f, 1000f );
			yield return new RuntimeEntity( effectName, targetName, position, rotation, scale );
		}
	}

	readonly record struct RuntimeEntity( string EffectName, string TargetName, Vector3 Position, Angles Rotation, float Scale );
	readonly record struct SpawnedEntity( GameObject GameObject, Vector3 Position, bool AlwaysActive, int SystemCount );
}

static class Titanfall2ParticleFactory
{
	const int MaximumChildDepth = 8;

	internal static bool TryCreate( GameObject entityObject, Titanfall2Mount mount,
		Titanfall2ParticleDefinition definition, out int createdSystems, out string error )
	{
		createdSystems = 0;
		error = null;
		if ( entityObject is null || mount is null || definition is null )
		{
			error = "Particle factory received an invalid entity, mount or definition.";
			return false;
		}

		var stack = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
		if ( !CreateDefinition( entityObject, mount, definition, stack, 0, ref createdSystems, out error ) ) return false;
		return createdSystems > 0;
	}

	static bool CreateDefinition( GameObject parent, Titanfall2Mount mount, Titanfall2ParticleDefinition definition,
		HashSet<string> stack, int depth, ref int createdSystems, out string error )
	{
		error = null;
		if ( depth > MaximumChildDepth )
		{
			error = $"Particle child depth exceeded {MaximumChildDepth} at '{definition.Name}'.";
			return false;
		}
		if ( !stack.Add( definition.Name ) )
		{
			error = $"Particle child cycle detected at '{definition.Name}'.";
			return false;
		}

		var gameObject = new GameObject( parent, true, definition.Name );
		gameObject.LocalTransform = Transform.Zero;
		var effect = gameObject.AddComponent<ParticleEffect>();
		ConfigureEffect( effect, definition );
		ConfigureEmitter( gameObject, definition );
		var hasRenderer = ConfigureRenderer( gameObject, mount, definition );
		createdSystems++;

		foreach ( var childName in definition.Children.Distinct( StringComparer.OrdinalIgnoreCase ) )
		{
			if ( !mount.TryGetParticleDefinition( childName, out var childDefinition ) ) continue;
			_ = CreateDefinition( gameObject, mount, childDefinition, stack, depth + 1, ref createdSystems, out _ );
		}

		stack.Remove( definition.Name );
		if ( hasRenderer || definition.Children.Count > 0 ) return true;
		error = $"Particle '{definition.Name}' has no supported renderer or child system.";
		return false;
	}

	static void ConfigureEffect( ParticleEffect effect, Titanfall2ParticleDefinition definition )
	{
		effect.MaxParticles = Math.Clamp( definition.MaxParticles, 1, Titanfall2StreamingSettings.ParticleMaxParticlesPerSystem );
		effect.Lifetime = Range( definition.LifetimeMinimum, definition.LifetimeMaximum );
		effect.LocalSpace = definition.LocalPosition || definition.LocalVelocity ? 1f : 0f;
		effect.InitialVelocity = new ParticleVector3
		{
			X = Range( definition.VelocityMinimum.x, definition.VelocityMaximum.x ),
			Y = Range( definition.VelocityMinimum.y, definition.VelocityMaximum.y ),
			Z = Range( definition.VelocityMinimum.z, definition.VelocityMaximum.z )
		};
		effect.Damping = MathF.Max( 0f, definition.Damping );

		effect.ApplyColor = true;
		effect.Tint = Color.White;
		effect.Gradient = CreateColor( definition );

		effect.ApplyAlpha = true;
		effect.Alpha = CreateAlpha( definition );
		effect.ApplyShape = true;
		effect.Scale = CreateScale( definition );
		if ( (definition.Renderer & Titanfall2ParticleRenderer.Trail) != 0 ) effect.Stretch = CreateStretch( definition );

		if ( MathF.Abs( definition.RotationMinimum ) > float.Epsilon
			|| MathF.Abs( definition.RotationMaximum ) > float.Epsilon
			|| MathF.Abs( definition.RotationRate ) > float.Epsilon
			|| MathF.Abs( definition.RotationRateMinimum ) > float.Epsilon
			|| MathF.Abs( definition.RotationRateMaximum ) > float.Epsilon
			|| definition.ScalarGraphs.Any( static graph => graph.OutputField == 4 ) )
		{
			effect.ApplyRotation = true;
			effect.Roll = CreateRotation( definition );
		}
		if ( definition.RandomYawFlip
			|| MathF.Abs( definition.YawMinimum ) > float.Epsilon
			|| MathF.Abs( definition.YawMaximum ) > float.Epsilon )
		{
			effect.ApplyRotation = true;
			effect.Yaw = definition.RandomYawFlip
				? CreateYawFlip()
				: Range( definition.YawMinimum, definition.YawMaximum );
		}

		if ( definition.Gravity.LengthSquared > float.Epsilon )
		{
			var gravity = ToSandbox( definition.Gravity );
			effect.Force = true;
			effect.ForceDirection = gravity.Normal;
			effect.ForceScale = gravity.Length;
			effect.ForceSpace = definition.LocalVelocity ? ParticleEffect.SimulationSpace.Local : ParticleEffect.SimulationSpace.World;
		}

		if ( definition.SequenceMaximum > 0 )
		{
			effect.SheetSequence = true;
			effect.SequenceId = Range( definition.SequenceMinimum, definition.SequenceMaximum );
			effect.SequenceTime = 0f;
			effect.SequenceSpeed = definition.AnimationRate;
		}
	}

	static void ConfigureEmitter( GameObject effectObject, Titanfall2ParticleDefinition definition )
	{
		var emitterObject = new GameObject( effectObject, true, "emitter" );
		var minimum = definition.PositionMinimum;
		var maximum = definition.PositionMaximum;
		var center = (minimum + maximum) * 0.5f;
		emitterObject.LocalPosition = ToSandbox( center );

		ParticleEmitter emitter;
		if ( definition.Shape == Titanfall2ParticleShape.Ring )
		{
			var ring = emitterObject.AddComponent<ParticleRingEmitter>();
			ring.Radius = definition.ShapeMaximum;
			ring.Thickness = definition.ShapeThickness;
			emitter = ring;
		}
		else if ( definition.Shape == Titanfall2ParticleShape.Sphere )
		{
			var sphere = emitterObject.AddComponent<ParticleSphereEmitter>();
			sphere.Radius = definition.ShapeMaximum;
			sphere.OnEdge = definition.ShapeMinimum > 0f && MathF.Abs( definition.ShapeMinimum - definition.ShapeMaximum ) < 0.01f;
			sphere.Velocity = 0f;
			emitter = sphere;
		}
		else if ( (maximum - minimum).LengthSquared > float.Epsilon )
		{
			var box = emitterObject.AddComponent<ParticleBoxEmitter>();
			var size = maximum - minimum;
			box.Size = new Vector3( MathF.Abs( size.x ), MathF.Abs( size.y ), MathF.Abs( size.z ) );
			emitter = box;
		}
		else
		{
			var point = emitterObject.AddComponent<ParticleSphereEmitter>();
			point.Radius = 0f;
			point.Velocity = 0f;
			emitter = point;
		}

		emitter.Loop = definition.Looping;
		emitter.DestroyOnEnd = false;
		emitter.Duration = MathF.Max( 0.05f, definition.Duration );
		emitter.Delay = 0f;
		emitter.Burst = MathF.Max( 0f, definition.Burst );
		emitter.Rate = MathF.Max( 0f, definition.EmissionRate );
	}

	static bool ConfigureRenderer( GameObject gameObject, Titanfall2Mount mount, Titanfall2ParticleDefinition definition )
	{
		var created = false;
		if ( (definition.Renderer & (Titanfall2ParticleRenderer.Sprite | Titanfall2ParticleRenderer.Trail)) != 0
			&& !string.IsNullOrWhiteSpace( definition.MaterialName )
			&& mount.TryGetOrCreateLegacyMaterialDescriptor( definition.MaterialName, out var materialDescriptor, out _ )
			&& mount.TryGetLegacyMaterialDefinition( definition.MaterialName, out var materialDefinition, out _ ) )
		{
			var textureName = materialDescriptor.TextureBindings
				.FirstOrDefault( binding => binding.Semantic == Titanfall2TextureSemantic.Albedo ).TexturePath;
			var frameRate = definition.AnimationRate;
			if ( materialDefinition.TryGetAnimatedTexture( false, out var materialFrameRate ) && materialFrameRate > 0f )
				frameRate = materialFrameRate;
			if ( !string.IsNullOrWhiteSpace( textureName )
				&& mount.TryGetLegacyParticleSprite( textureName, frameRate, out var sprite, out _ ) )
			{
				var metadata = materialDescriptor.Metadata;
				var renderer = gameObject.AddComponent<ParticleSpriteRenderer>();
				renderer.Sprite = sprite;
				renderer.PlaybackSpeed = 1f;
				renderer.Scale = 2f;
				renderer.Additive = metadata.Mode == Titanfall2MaterialMode.Additive;
				renderer.Lighting = !metadata.IsUnlit;
				renderer.Opaque = false;
				renderer.Shadows = false;
				renderer.SortMode = renderer.Additive
					? ParticleSpriteRenderer.ParticleSortMode.Unsorted
					: ParticleSpriteRenderer.ParticleSortMode.ByDistance;
				renderer.Alignment = definition.OrientationType switch
				{
					2 => ParticleSpriteRenderer.BillboardAlignment.RotateToCamera,
					3 => ParticleSpriteRenderer.BillboardAlignment.Particle,
					_ => ParticleSpriteRenderer.BillboardAlignment.LookAtCamera
				};
				renderer.DepthFeather = definition.DepthFeather > 0f ? definition.DepthFeather : 4f;
				renderer.FogStrength = 1f;
				if ( (definition.Renderer & Titanfall2ParticleRenderer.Trail) != 0 )
				{
					renderer.FaceVelocity = true;
					renderer.MotionBlur = true;
					renderer.LeadingTrail = true;
					renderer.BlurAmount = 1f;
					renderer.BlurSpacing = 0.15f;
					renderer.BlurOpacity = 0.65f;
				}
				created = true;
			}
		}

		if ( (definition.Renderer & Titanfall2ParticleRenderer.Light) != 0 )
		{
			var light = gameObject.AddComponent<ParticleLightRenderer>();
			light.Ratio = 1f;
			light.MaximumLights = Math.Min( 4, Titanfall2StreamingSettings.ParticleMaxLightsPerSystem );
			light.CastShadows = false;
			light.Scale = definition.LightRadiusScale;
			light.Brightness = definition.LightColorScale;
			light.UseParticleColor = true;
			created = true;
		}

		if ( (definition.Renderer & Titanfall2ParticleRenderer.Model) != 0 && !string.IsNullOrWhiteSpace( definition.ModelName ) )
		{
			var model = Model.Load( $"mount://{mount.Ident}/{definition.ModelName}.vmdl" );
			if ( model is not null && model != Model.Error )
			{
				var modelRenderer = gameObject.AddComponent<ParticleModelRenderer>();
				modelRenderer.Choices = [new ParticleModelRenderer.ModelEntry { Model = model }];
				modelRenderer.RotateWithGameObject = true;
				modelRenderer.Scale = 1f;
				modelRenderer.CastShadows = false;
				created = true;
			}
		}
		return created;
	}

	static ParticleGradient CreateColor( Titanfall2ParticleDefinition definition )
	{
		if ( definition.ColorFade is not { } fade )
		{
			return new ParticleGradient
			{
				Type = ParticleGradient.ValueType.Range,
				Evaluation = ParticleGradient.EvaluationType.Particle,
				ConstantA = ToColor( definition.ColorMinimum ),
				ConstantB = ToColor( definition.ColorMaximum )
			};
		}

		var start = Color.Lerp( ToColor( definition.ColorMinimum ), ToColor( definition.ColorMaximum ), 0.5f );
		var target = ToColor( fade );
		var gradient = new Gradient();
		gradient.AddColor( 0f, start );
		gradient.AddColor( Math.Clamp( definition.ColorFadeStart, 0f, 1f ), start );
		gradient.AddColor( Math.Clamp( definition.ColorFadeEnd, definition.ColorFadeStart, 1f ), target );
		gradient.AddColor( 1f, target );
		return new ParticleGradient
		{
			Type = ParticleGradient.ValueType.Gradient,
			Evaluation = ParticleGradient.EvaluationType.Life,
			GradientA = gradient
		};
	}

	static ParticleFloat CreateAlpha( Titanfall2ParticleDefinition definition )
	{
		var graphs = definition.ScalarGraphs.Where( static graph => graph.OutputField is 7 or 16 ).ToArray();
		var minimum = definition.AlphaMinimum * definition.AlphaMultiplierMinimum;
		var maximum = definition.AlphaMaximum * definition.AlphaMultiplierMaximum;
		if ( definition.AlphaFade is null && graphs.Length == 0 ) return Range( minimum, maximum );
		return new ParticleFloat
		{
			Type = ParticleFloat.ValueType.CurveRange,
			Evaluation = ParticleFloat.EvaluationType.Life,
			CurveA = CreateComposedCurve( definition.LifetimeMinimum, graphs,
				time => minimum * EvaluateFade( definition.AlphaFade, time ), GetFadeTimes( definition.AlphaFade ) ),
			CurveB = CreateComposedCurve( definition.LifetimeMaximum, graphs,
				time => maximum * EvaluateFade( definition.AlphaFade, time ), GetFadeTimes( definition.AlphaFade ) )
		};
	}

	static ParticleFloat CreateScale( Titanfall2ParticleDefinition definition )
	{
		var graphs = definition.ScalarGraphs.Where( static graph => graph.OutputField == 3 ).ToArray();
		var minimum = definition.RadiusMinimum * definition.RadiusMultiplierMinimum;
		var maximum = definition.RadiusMaximum * definition.RadiusMultiplierMaximum;
		if ( definition.RadiusScale is null && graphs.Length == 0 ) return Range( minimum, maximum );
		return new ParticleFloat
		{
			Type = ParticleFloat.ValueType.CurveRange,
			Evaluation = ParticleFloat.EvaluationType.Life,
			CurveA = CreateComposedCurve( definition.LifetimeMinimum, graphs,
				time => minimum * EvaluateScale( definition.RadiusScale, time ), GetScaleTimes( definition.RadiusScale ) ),
			CurveB = CreateComposedCurve( definition.LifetimeMaximum, graphs,
				time => maximum * EvaluateScale( definition.RadiusScale, time ), GetScaleTimes( definition.RadiusScale ) )
		};
	}

	static ParticleFloat CreateStretch( Titanfall2ParticleDefinition definition )
	{
		var graphs = definition.ScalarGraphs.Where( static graph => graph.OutputField == 10 ).ToArray();
		var minimum = definition.TrailMinimum > 0f ? definition.TrailMinimum : 1f;
		var maximum = definition.TrailMaximum > 0f ? definition.TrailMaximum : minimum;
		if ( graphs.Length == 0 ) return Range( minimum, maximum );
		return new ParticleFloat
		{
			Type = ParticleFloat.ValueType.CurveRange,
			Evaluation = ParticleFloat.EvaluationType.Life,
			CurveA = CreateComposedCurve( definition.LifetimeMinimum, graphs, _ => minimum ),
			CurveB = CreateComposedCurve( definition.LifetimeMaximum, graphs, _ => maximum )
		};
	}

	static ParticleFloat CreateRotation( Titanfall2ParticleDefinition definition )
	{
		var graphs = definition.ScalarGraphs.Where( static graph => graph.OutputField == 4 ).ToArray();
		var minimumRate = definition.RotationRate + definition.RotationRateMinimum;
		var maximumRate = definition.RotationRate + definition.RotationRateMaximum;
		return new ParticleFloat
		{
			Type = ParticleFloat.ValueType.CurveRange,
			Evaluation = ParticleFloat.EvaluationType.Life,
			CurveA = CreateComposedCurve( definition.LifetimeMinimum, graphs,
				time => definition.RotationMinimum + minimumRate * definition.LifetimeMinimum * time ),
			CurveB = CreateComposedCurve( definition.LifetimeMaximum, graphs,
				time => definition.RotationMaximum + maximumRate * definition.LifetimeMaximum * time )
		};
	}

	static ParticleFloat CreateYawFlip()
	{
		return new ParticleFloat
		{
			Type = ParticleFloat.ValueType.Curve,
			Evaluation = ParticleFloat.EvaluationType.Seed,
			CurveA = new Curve(
				new Curve.Frame( 0f, 0f ) { Mode = Curve.HandleMode.Stepped },
				new Curve.Frame( 0.5f, 180f ) { Mode = Curve.HandleMode.Stepped },
				new Curve.Frame( 1f, 180f ) { Mode = Curve.HandleMode.Stepped } )
		};
	}

	static Curve CreateComposedCurve( float lifetime, IReadOnlyList<ParticleScalarGraphDefinition> graphs,
		Func<float, float> baseValue, IEnumerable<float> extraTimes = null )
	{
		lifetime = MathF.Max( lifetime, 0.01f );
		var times = new SortedSet<float> { 0f, 1f };
		if ( extraTimes is not null )
		{
			foreach ( var time in extraTimes ) times.Add( Math.Clamp( time, 0f, 1f ) );
		}
		foreach ( var graph in graphs ) AddGraphTimes( times, graph, lifetime );

		var frames = new List<Curve.Frame>( times.Count );
		foreach ( var time in times )
		{
			var value = baseValue( time );
			foreach ( var graph in graphs )
			{
				var graphValue = EvaluateGraph( graph, time, lifetime );
				value = graph.OutputOperation == 1 ? value * graphValue : graphValue;
			}
			frames.Add( new Curve.Frame( time, value ) { Mode = Curve.HandleMode.Linear } );
		}
		return new Curve( frames );
	}

	static void AddGraphTimes( SortedSet<float> times, ParticleScalarGraphDefinition graph, float lifetime )
	{
		if ( graph.TimeInLifespans )
		{
			foreach ( var point in graph.Points ) times.Add( Math.Clamp( point.x, 0f, 1f ) );
			return;
		}

		var cycles = graph.Loop ? Math.Min( 32, (int)MathF.Ceiling( lifetime / graph.Duration ) ) : 1;
		for ( var cycle = 0; cycle < cycles; cycle++ )
		{
			foreach ( var point in graph.Points )
			{
				var normalized = (cycle * graph.Duration + point.x * graph.Duration) / lifetime;
				if ( normalized > 1f ) break;
				times.Add( Math.Clamp( normalized, 0f, 1f ) );
			}
		}
	}

	static float EvaluateGraph( ParticleScalarGraphDefinition graph, float normalizedTime, float lifetime )
	{
		var graphTime = normalizedTime;
		if ( !graph.TimeInLifespans )
		{
			graphTime = normalizedTime * lifetime / graph.Duration;
			graphTime = graph.Loop ? graphTime - MathF.Floor( graphTime ) : Math.Clamp( graphTime, 0f, 1f );
		}
		var points = graph.Points;
		if ( points.Count == 0 ) return 1f;
		if ( graphTime <= points[0].x ) return RemapGraphValue( graph, points[0].y );
		for ( var index = 1; index < points.Count; index++ )
		{
			if ( graphTime > points[index].x ) continue;
			var previous = points[index - 1];
			var current = points[index];
			var fraction = MathF.Abs( current.x - previous.x ) <= float.Epsilon
				? 1f
				: Math.Clamp( (graphTime - previous.x) / (current.x - previous.x), 0f, 1f );
			return RemapGraphValue( graph, MathX.Lerp( previous.y, current.y, fraction ) );
		}
		return RemapGraphValue( graph, points[^1].y );
	}

	static float RemapGraphValue( ParticleScalarGraphDefinition graph, float value ) =>
		MathX.Lerp( graph.OutputMinimum, graph.OutputMaximum, value );

	static IEnumerable<float> GetFadeTimes( ParticleFadeDefinition? fade )
	{
		if ( fade is not { } value ) yield break;
		yield return value.FadeInStart;
		yield return value.FadeInEnd;
		yield return value.FadeOutStart;
		yield return value.FadeOutEnd;
	}

	static float EvaluateFade( ParticleFadeDefinition? fade, float time )
	{
		if ( fade is not { } value ) return 1f;
		var fadeInStart = Math.Clamp( value.FadeInStart, 0f, 1f );
		var fadeInEnd = Math.Clamp( value.FadeInEnd, fadeInStart, 1f );
		var fadeOutStart = Math.Clamp( value.FadeOutStart, fadeInEnd, 1f );
		var fadeOutEnd = Math.Clamp( value.FadeOutEnd, fadeOutStart, 1f );
		if ( time < fadeInStart ) return 0f;
		if ( time < fadeInEnd && fadeInEnd > fadeInStart ) return (time - fadeInStart) / (fadeInEnd - fadeInStart);
		if ( time <= fadeOutStart ) return 1f;
		if ( time < fadeOutEnd && fadeOutEnd > fadeOutStart ) return 1f - (time - fadeOutStart) / (fadeOutEnd - fadeOutStart);
		return 0f;
	}

	static IEnumerable<float> GetScaleTimes( ParticleScaleDefinition? scale )
	{
		if ( scale is not { } value ) yield break;
		yield return value.StartTime;
		yield return value.EndTime;
	}

	static float EvaluateScale( ParticleScaleDefinition? scale, float time )
	{
		if ( scale is not { } value ) return 1f;
		var start = Math.Clamp( value.StartTime, 0f, 1f );
		var end = Math.Clamp( value.EndTime, start, 1f );
		if ( time <= start ) return value.StartScale;
		if ( time >= end || end <= start ) return value.EndScale;
		return MathX.Lerp( value.StartScale, value.EndScale, (time - start) / (end - start) );
	}

	static ParticleFloat Range( float minimum, float maximum ) =>
		MathF.Abs( maximum - minimum ) < 0.0001f ? minimum : new ParticleFloat( minimum, maximum );

	static Color ToColor( RgbaColor value ) => new( value.R / 255f, value.G / 255f, value.B / 255f, value.A / 255f );
	static Vector3 ToSandbox( Vector3 value ) => value;
}

readonly record struct Titanfall2ParticleMapEntity( string EffectName, string TargetName, Vector3 Position, Angles Rotation, float Scale );
