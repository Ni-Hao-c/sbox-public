using System.Diagnostics;
using Titanfall2;

/// <summary>
/// Prepares the particle systems referenced by a map's *_fx.ent partition during
/// scene loading. Instances are retained; distant systems are
/// suspended instead of destroyed so moving through a map does not reveal gaps.
/// </summary>
[Library, Hide]
public sealed class Titanfall2ParticleStreamer : Component, Component.DontExecuteOnServer, Sandbox.Internal.IUpdateSubscriber
{
	static readonly Sandbox.Diagnostics.Logger Log = new( "Titanfall2Particles" );
	const int DataMagic = 0x58465054; // TPFX
	const int DataVersion = 4;
	const int MaximumEntityCount = 100_000;
	// PCF libraries and map effects are created in two bounded stages so hundreds of
	// small effects never become one long loading or movement stall.
	const int LibrariesPerFrame = 8;
	const float LibraryFrameBudgetMilliseconds = 2f;
	const int InstancesPerFrame = 4;
	const float InstanceFrameBudgetMilliseconds = 2f;
	const float ActivationRadius = 4096f;
	// A suspended looping effect is stepped forward this long on waking so it reappears filled.
	const float WakeWarmupSeconds = 1f;
	const float WakeWarmupBudgetMilliseconds = 1.5f;

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
	readonly List<InitialWarmup> _initialWarmups = [];
	// Reusable budget/probe timers: this path runs every frame and Stopwatch.StartNew()
	// allocates, so they are created once and restarted in place.
	readonly Stopwatch _spawnTimer = new();
	readonly Stopwatch _spawnProbe = new();
	readonly Stopwatch _warmupProbe = new();
	// Frustum planes are tested against the effect's influence sphere grown by this
	// margin, which covers sprites past the estimate and the camera turning between frames.
	const float SleepFrustumMargin = 256f;
	const float WakeWarmupStep = 1f / 15f;
	// Milliseconds spent warming woken effects in the current frame.
	double _warmupMilliseconds;
	bool _initialized;
	bool _catalogResolved;
	bool _preparing;
	int _failed;
	int _failureLogs;
	/// <summary>All requested particle resources were prepared successfully during scene load.</summary>
	public bool ResourcesPrepared { get; private set; }
	public int PendingSystemCount => _pending.Count;
	public int FailedSystemCount => _failed;

	internal void Configure( string mountIdent, string mapPath, Vector3 fallbackAnchor,
		IReadOnlyList<Titanfall2ParticleMapEntity> entities )
	{
		MountIdent = mountIdent;
		MapPath = mapPath;
		FallbackAnchor = fallbackAnchor;
		EncodedEntities = Encode( entities );
	}

	protected override void OnAwake() => InitializeEntities();

	protected override void OnEnabled() => RefreshActivation();

	protected override void OnDisabled()
	{
		foreach ( var entity in _spawned )
			if ( entity.GameObject.IsValid() ) entity.GameObject.Enabled = false;
	}

	protected override async Task OnLoad( LoadingContext context )
	{
		ResourcesPrepared = false;
		if ( string.IsNullOrWhiteSpace( EncodedEntities ) )
		{
			ResourcesPrepared = true;
			return;
		}
		if ( !_initialized ) InitializeEntities();
		if ( !_initialized ) return;
		_preparing = true;
		try
		{
			while ( _pending.Count > 0 || _initialWarmups.Count > 0 )
			{
				if ( !this.IsValid() || !GameObject.IsValid() || GameObject.IsDestroyed ) return;
				var mount = Sandbox.Mounting.Directory.Get( MountIdent ) as Titanfall2Mount;
				if ( mount is null || !mount.IsMounted ) return;
				// Catalog and creation have separate bounds. A yield never publishes readiness.
				if ( !mount.IsParticleCatalogComplete ) mount.IndexParticleFiles( LibrariesPerFrame, LibraryFrameBudgetMilliseconds );
				// Child definitions can live in a later PCF. Creating a parent against a partial
				// catalog silently loses those children for the lifetime of the map.
				if ( !mount.IsParticleCatalogComplete ) { await Task.Yield(); continue; }
				SpawnAvailable( mount );
				ResolveMissingDefinitions( mount );
				AdvanceInitialWarmups();
				context.Title = $"Preparing Titanfall 2 particles ({_spawned.Count} created, {_pending.Count} pending, {_initialWarmups.Count} warming)";
				if ( _pending.Count > 0 || _initialWarmups.Count > 0 ) await Task.Yield();
			}
			ResourcesPrepared = _failed == 0;
			Log.Info( $"Titanfall 2 particle preparation complete during scene loading: {_spawned.Count} map FX retained, "
				+ $"{_failed} skipped ({MapPath})." );
		}
		finally { _preparing = false; }
	}

	void ResolveMissingDefinitions( Titanfall2Mount mount )
	{
		if ( _pending.Count > 0 && mount.IsParticleCatalogComplete && !_catalogResolved )
		{
			_catalogResolved = true;
			// A completed catalog only proves which names are missing. Valid
			// systems can still be queued behind this frame's creation budget.
			for ( var index = _pending.Count - 1; index >= 0; --index )
			{
				var entity = _pending[index];
				if ( mount.TryGetParticleDefinition( entity.EffectName, out _ ) ) continue;
				_pending.RemoveAt( index );
				_failed++;
				if ( _failureLogs++ < 16 )
					Titanfall2Log.Warning( $"Titanfall 2 particle definition '{entity.EffectName}' was not found for '{MapPath}'." );
			}
		}
	}

	protected override void OnUpdate()
	{
		if ( !_initialized || _preparing ) return;
		// Every frame: a woken effect must be enabled before it reaches the screen, and
		// the pass is one distance and one frustum test per retained system.
		RefreshActivation();
	}

	protected override void OnDestroy()
	{
		ResourcesPrepared = false;
		var released = _spawned.Count;
		foreach ( var entity in _spawned )
			if ( entity.GameObject.IsValid() && !entity.GameObject.IsDestroyed ) entity.GameObject.Destroy();
		_spawned.Clear();
		_pending.Clear();
		_initialWarmups.Clear();
		Log.Info( $"Titanfall 2 particle scene resources released: {released} systems ({MapPath})." );
	}

	void InitializeEntities()
	{
		if ( _initialized || string.IsNullOrWhiteSpace( EncodedEntities ) ) return;
		_initialized = true;
		try
		{
			_pending.AddRange( Decode( EncodedEntities ) );
			var anchor = GetAnchor();
			_pending.Sort( ( left, right ) => left.Position.DistanceSquared( anchor ).CompareTo( right.Position.DistanceSquared( anchor ) ) );
			Log.Info( $"Titanfall 2 particle streamer ready: {_pending.Count} active map FX; "
				+ $"{LibrariesPerFrame} PCFs/frame, "
				+ $"{InstancesPerFrame} systems/frame ({MapPath})." );
		}
		catch ( Exception exception )
		{
			_pending.Clear();
			_failed++;
			Log.Warning( $"Failed to initialize Titanfall 2 particle streaming for '{MapPath}': {exception.Message}" );
		}
	}

	void SpawnAvailable( Titanfall2Mount mount )
	{
		if ( _pending.Count == 0 ) return;
		_spawnTimer.Restart();
		var created = 0;
		for ( var index = 0; index < _pending.Count
			&& created < InstancesPerFrame
			&& _spawnTimer.Elapsed.TotalMilliseconds < InstanceFrameBudgetMilliseconds; )
		{
			var entity = _pending[index];
			if ( !mount.TryGetParticleDefinition( entity.EffectName, out var definition ) )
			{
				index++;
				continue;
			}

			_pending.RemoveAt( index );
			// One system creation can decode a VTF sprite sheet and build materials and
			// models, so it is measured on its own rather than relying on the budget
			// check above, which only runs between entities.
			_spawnProbe.Restart();
			if ( SpawnEntity( mount, entity, definition ) ) created++;
			else _failed++;
			Titanfall2Log.ReportSpike( _spawnProbe, "particle system '{0}'", entity.EffectName );
		}
	}

	bool SpawnEntity( Titanfall2Mount mount, RuntimeEntity entity, Titanfall2ParticleDefinition definition )
	{
		var gameObject = new GameObject( GameObject, true,
			string.IsNullOrWhiteSpace( entity.TargetName ) ? entity.EffectName : entity.TargetName );
		gameObject.Flags |= GameObjectFlags.NotSaved;
		gameObject.WorldPosition = entity.Position;
		gameObject.WorldRotation = entity.Rotation;
		gameObject.WorldScale = Vector3.One * entity.Scale;
		var points = gameObject.AddComponent<Titanfall2ParticleControlPoints>();
		foreach ( var point in entity.ControlPoints ) points.SetControlPoint( point.Index, point.Position );
		bool createdSystem;
		int createdSystems;
		string error;
		var systems = new List<Titanfall2ParticleFactory.CreatedSystem>();
		try
		{
			createdSystem = Titanfall2ParticleFactory.TryCreate( gameObject, mount, definition, out createdSystems, out error, systems );
		}
		catch ( Exception exception )
		{
			// This runs from OnUpdate: a malformed PCF must not escape the frame callback.
			createdSystem = false;
			createdSystems = 0;
			error = exception.Message;
		}
		if ( !createdSystem )
		{
			gameObject.Destroy();
			if ( _failureLogs++ < 16 ) Titanfall2Log.Warning( $"Unable to create Titanfall 2 particle '{entity.EffectName}': {error}" );
			return false;
		}

		var alwaysActive = IsGlobalEffect( entity.EffectName ) || entity.WarmupAndPause;
		var influenceRadius = Titanfall2ParticleFactory.GetInfluenceRadius( definition ) * entity.Scale;
		// A system tree is drawn while any of its systems is, so the tree keeps the largest
		// draw distance and sleep time of its members.
		var drawDistance = 0f;
		var sleepTime = 0f;
		var warmup = new List<(ParticleEffect Effect, float Seconds)>();
		foreach ( var system in systems )
		{
			drawDistance = MathF.Max( drawDistance, system.Definition.MaxDrawDistance * entity.Scale );
			sleepTime = MathF.Max( sleepTime, system.Definition.SleepWhenNotDrawnTime );
			var seconds = system.Definition.Looping
				? MathF.Min( WakeWarmupSeconds,
					MathF.Max( system.Definition.WarmupTime, system.Definition.LifetimeMaximum ) )
				: 0f;
			if ( seconds > 0f && system.Effect.IsValid() ) warmup.Add( (system.Effect, seconds) );
		}
		var spawned = new SpawnedEntity( gameObject, entity.Position, alwaysActive, createdSystems, influenceRadius,
			drawDistance + influenceRadius, sleepTime, warmup.ToArray() );
		_spawned.Add( spawned );
		// Entity-level pre-roll is separate from the PCF default. Retail Beacon authors
		// 40 seconds on one smoke plume, so spread this work across loading frames.
		var initial = new Titanfall2ParticleWarmup( systems.Select( system => (system.Effect, system.Definition.WarmupTime) ),
			entity.WarmupTime, entity.WarmupAndPause );
		if ( !initial.Complete ) _initialWarmups.Add( new( spawned, initial ) );
		else ApplyActivation( spawned, GetAnchor(), GetViewFrustum(), Time.Now, false );
		return true;
	}

	void AdvanceInitialWarmups()
	{
		if ( _initialWarmups.Count == 0 ) return;
		var warmup = _initialWarmups[0];
		if ( !warmup.Work.Advance( 64, WakeWarmupBudgetMilliseconds ) ) return;
		_initialWarmups.RemoveAt( 0 );
		ApplyActivation( warmup.Entity, GetAnchor(), GetViewFrustum(), Time.Now, false );
	}

	void RefreshActivation()
	{
		_warmupMilliseconds = 0;
		var anchor = GetAnchor();
		var frustum = GetViewFrustum();
		var now = Time.Now;
		foreach ( var entity in _spawned ) ApplyActivation( entity, anchor, frustum, now, true );
	}

	/// <summary>
	/// The camera frustum for the sleep test, or null when sleeping is off or there is no
	/// camera to test against.
	/// </summary>
	Frustum? GetViewFrustum()
	{
		var camera = Scene?.Camera;
		return camera.IsValid() ? camera.GetFrustum() : null;
	}

	void ApplyActivation( SpawnedEntity entity, Vector3 anchor, Frustum? frustum, float now, bool warm )
	{
		if ( !entity.GameObject.IsValid() || entity.GameObject.IsDestroyed ) return;
		var wanted = entity.AlwaysActive || IsWanted( entity, anchor, frustum, now );
		if ( entity.GameObject.Enabled == wanted ) return;
		entity.GameObject.Enabled = wanted;
		if ( wanted && warm ) WarmUp( entity );
	}

	static bool IsWanted( SpawnedEntity entity, Vector3 anchor, Frustum? frustum, float now )
	{
		var radius = ActivationRadius + entity.InfluenceRadius;
		radius = MathF.Min( radius, entity.DrawDistance );
		if ( entity.Position.DistanceSquared( anchor ) > radius * radius ) return false;
		if ( frustum is not { } view ) return true;

		if ( view.IsInside( entity.Position, entity.InfluenceRadius + SleepFrustumMargin, true ) )
		{
			entity.LastVisibleTime = now;
			return true;
		}
		return now - entity.LastVisibleTime < entity.SleepTime;
	}

	/// <summary>
	/// Disabling an effect clears its particles, so a looping effect that wakes would refill
	/// from empty in plain view. It is stepped forward a little instead, within a per-frame
	/// budget so turning around does not stall on many wakes at once.
	/// </summary>
	void WarmUp( SpawnedEntity entity )
	{
		if ( entity.Warmup.Length == 0 ) return;
		if ( _warmupMilliseconds >= WakeWarmupBudgetMilliseconds ) return;
		_warmupProbe.Restart();
		foreach ( var (effect, seconds) in entity.Warmup )
		{
			if ( !effect.IsValid() || !effect.Active ) continue;
			for ( var remaining = seconds; remaining > 0f; remaining -= WakeWarmupStep )
				effect.Step( MathF.Min( remaining, WakeWarmupStep ) );
		}
		_warmupMilliseconds += _warmupProbe.Elapsed.TotalMilliseconds;
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

	internal static string Encode( IReadOnlyList<Titanfall2ParticleMapEntity> entities )
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
				var points = entity.ControlPoints ?? Array.Empty<Titanfall2ParticleMapControlPoint>();
				writer.Write( points.Count );
				foreach ( var point in points )
				{
					writer.Write( point.Index );
					writer.Write( point.Position.x ); writer.Write( point.Position.y ); writer.Write( point.Position.z );
				}
				writer.Write( entity.WarmupTime );
				writer.Write( entity.WarmupAndPause );
			}
		}
		return Convert.ToBase64String( stream.GetBuffer(), 0, checked((int)stream.Length) );
	}

	internal static IEnumerable<RuntimeEntity> Decode( string encoded )
	{
		using var stream = new MemoryStream( Convert.FromBase64String( encoded ), false );
		using var reader = new BinaryReader( stream, System.Text.Encoding.UTF8, false );
		if ( reader.ReadInt32() != DataMagic ) throw new InvalidDataException( "Particle entity data has an invalid signature." );
		var version = reader.ReadInt32();
		if ( version is < 2 or > DataVersion ) throw new InvalidDataException( "Particle entity data has an unsupported version." );
		var count = reader.ReadInt32();
		if ( count < 0 || count > MaximumEntityCount ) throw new InvalidDataException( $"Invalid particle entity count {count}." );
		for ( var index = 0; index < count; index++ )
		{
			var effectName = reader.ReadString();
			var targetName = reader.ReadString();
			var position = new Vector3( reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle() );
			var rotation = new Angles( reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle() );
			var scale = Math.Clamp( reader.ReadSingle(), 0.001f, 1000f );
			var points = Array.Empty<Titanfall2ParticleMapControlPoint>();
			if ( version >= 3 )
			{
				var pointCount = reader.ReadInt32();
				if ( pointCount is < 0 or > 64 ) throw new InvalidDataException( "Particle control point count is invalid." );
				points = new Titanfall2ParticleMapControlPoint[pointCount];
				for ( var point = 0; point < pointCount; ++point )
				{
					var id = reader.ReadInt32();
					var location = new Vector3( reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle() );
					if ( id is < 0 or > 63 || !Titanfall2ParticleControlPoints.IsFinite( location ) )
						throw new InvalidDataException( "Particle control point is invalid." );
					points[point] = new( id, location );
				}
			}
			var warmup = version >= 4 ? reader.ReadSingle() : 0f;
			var pause = version >= 4 && reader.ReadBoolean();
			if ( !float.IsFinite( warmup ) || !Titanfall2ParticleControlPoints.IsFinite( position )
				|| !float.IsFinite( rotation.pitch ) || !float.IsFinite( rotation.yaw ) || !float.IsFinite( rotation.roll ) || !float.IsFinite( scale ) )
				throw new InvalidDataException( "Particle entity transform or warmup is invalid." );
			yield return new RuntimeEntity( effectName, targetName, position, rotation, scale, points, Math.Clamp( warmup, 0f, 60f ), pause );
		}
	}

	internal readonly record struct RuntimeEntity( string EffectName, string TargetName, Vector3 Position, Angles Rotation, float Scale,
		IReadOnlyList<Titanfall2ParticleMapControlPoint> ControlPoints, float WarmupTime, bool WarmupAndPause );
	sealed record InitialWarmup( SpawnedEntity Entity, Titanfall2ParticleWarmup Work );
	sealed class SpawnedEntity( GameObject gameObject, Vector3 position, bool alwaysActive, int systemCount, float influenceRadius,
		float drawDistance, float sleepTime, (ParticleEffect Effect, float Seconds)[] warmup )
	{
		public GameObject GameObject { get; } = gameObject;
		public Vector3 Position { get; } = position;
		public bool AlwaysActive { get; } = alwaysActive;
		public int SystemCount { get; } = systemCount;
		public float InfluenceRadius { get; } = influenceRadius;
		/// <summary>Distance from <see cref="Position"/> beyond which no system in the tree is drawn.</summary>
		public float DrawDistance { get; } = drawDistance;
		public float SleepTime { get; } = sleepTime;
		public (ParticleEffect Effect, float Seconds)[] Warmup { get; } = warmup;
		/// <summary>Starts at creation, so a new system gets its full sleep time before it can sleep.</summary>
		public float LastVisibleTime { get; set; } = Time.Now;
	}
}

static class Titanfall2ParticleFactory
{
	// Caps keep a malformed or kilometre-sized definition from covering the map.
	internal const float MaximumRadius = 256f;
	internal const float AtmosphereMaximumRadius = 8192f;
	internal const float MaximumEmitterExtent = 2048f;
	internal const int MaxParticlesPerSystem = 256;
	internal const float MaximumVelocity = 2048f;
	const int MaximumChildDepth = 8;

	internal static float GetRadiusLimit( Titanfall2ParticleDefinition definition ) =>
		definition.MaterialName?.StartsWith( "particle/smoke/", StringComparison.OrdinalIgnoreCase ) == true
			? MathF.Max( Titanfall2ParticleFactory.MaximumRadius, Titanfall2ParticleFactory.AtmosphereMaximumRadius )
			: Titanfall2ParticleFactory.MaximumRadius;

	internal static float GetInfluenceRadius( Titanfall2ParticleDefinition definition )
	{
		var radius = MathF.Max( definition.RadiusMinimum * definition.RadiusMultiplierMinimum,
			definition.RadiusMaximum * definition.RadiusMultiplierMaximum );
		if ( definition.RadiusScale is { } scale ) radius *= MathF.Max( 1, MathF.Max( scale.StartScale, scale.EndScale ) );
		foreach ( var remap in definition.DistanceRemaps.Where( remap => remap.Field == 3 && remap.ScaleInitialRange ) )
			radius *= MathF.Max( 1, MathF.Max( remap.OutputMin, remap.OutputMax ) );
		var offset = MathF.Max( definition.PositionMinimum.Length, definition.PositionMaximum.Length );
		return Math.Clamp( radius, 0, GetRadiusLimit( definition ) ) + MathF.Min( Titanfall2ParticleFactory.MaximumEmitterExtent,
			MathF.Max( offset, definition.ShapeMaximum ) );
	}

	internal readonly record struct CreatedSystem( ParticleEffect Effect, Titanfall2ParticleDefinition Definition );

	internal static bool TryCreate( GameObject entityObject, Titanfall2Mount mount,
		Titanfall2ParticleDefinition definition, out int createdSystems, out string error, List<CreatedSystem> systems = null )
	{
		createdSystems = 0;
		error = null;
		if ( entityObject is null || mount is null || definition is null )
		{
			error = "Particle factory received an invalid entity, mount or definition.";
			return false;
		}

		var stack = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
		if ( !CreateDefinition( entityObject, mount, definition, stack, 0, ref createdSystems, out error, systems ) ) return false;
		return createdSystems > 0;
	}

	static bool CreateDefinition( GameObject parent, Titanfall2Mount mount, Titanfall2ParticleDefinition definition,
		HashSet<string> stack, int depth, ref int createdSystems, out string error, List<CreatedSystem> systems )
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
		var controlPoints = gameObject.AddComponent<Titanfall2ParticleControlPoints>();
		controlPoints.Configure( parent.Components.GetInAncestorsOrSelf<Titanfall2ParticleControlPoints>(), definition.ControlPoints );
		var geometry = ConfigureSimulation( gameObject, definition, controlPoints );
		var effect = gameObject.Components.Get<ParticleEffect>();
		var hasRenderer = ConfigureRenderer( gameObject, mount, definition, geometry );
		var hasChildren = false;

		foreach ( var childName in definition.Children.Distinct( StringComparer.OrdinalIgnoreCase ) )
		{
			if ( !mount.TryGetParticleDefinition( childName, out var childDefinition ) ) continue;
			hasChildren |= CreateDefinition( gameObject, mount, childDefinition, stack, depth + 1, ref createdSystems, out _, systems );
		}

		stack.Remove( definition.Name );
		if ( hasRenderer || hasChildren )
		{
			// PCFs can author a pre-roll so low-rate fog emitters start filled.
			// Small steps preserve their normal lifetime curves and operator order.
			for ( var remaining = definition.WarmupTime; remaining > 0f; remaining -= 1f / 30f )
				effect.Step( MathF.Min( remaining, 1f / 30f ) );
			createdSystems++;
			systems?.Add( new CreatedSystem( effect, definition ) );
			return true;
		}
		gameObject.Destroy();
		error = $"Particle '{definition.Name}' has no supported renderer or child system.";
		return false;
	}

	internal static bool HasShapePositionOffset( Titanfall2ParticleDefinition definition ) =>
		definition.Shape is Titanfall2ParticleShape.Sphere or Titanfall2ParticleShape.Ring
		&& (definition.PositionMinimum != Vector3.Zero || definition.PositionMaximum != Vector3.Zero);

	internal static Titanfall2ParticleGeometry ConfigureSimulation( GameObject gameObject, Titanfall2ParticleDefinition definition,
		Titanfall2ParticleControlPoints controlPoints )
	{
		var effect = gameObject.AddComponent<ParticleEffect>();
		ConfigureEffect( effect, definition );
		Titanfall2ParticleGeometry geometry = null;
		if ( definition.OrientationType == 2 || definition.Path.HasValue || definition.Visibility.HasValue || definition.EndCapLifetime || definition.DistanceRemaps.Count > 0
			|| definition.ColorRemaps.Count > 0 || definition.ScalarRemaps.Count > 0 || definition.RandomFadeIn.HasValue || definition.RandomFadeOut.HasValue || definition.PositionWarpMinimum != Vector3.One
			|| definition.PositionWarpMaximum != Vector3.One || (definition.Renderer & Titanfall2ParticleRenderer.Trail) != 0
			|| definition.Oscillations.Count > 0 || HasShapePositionOffset( definition ) )
		{
			geometry = gameObject.AddComponent<Titanfall2ParticleGeometry>();
			geometry.Configure( definition, controlPoints, CreateStretch( definition ) );
		}
		ConfigureEmitter( gameObject, definition, controlPoints );
		return geometry;
	}

	static void ConfigureEffect( ParticleEffect effect, Titanfall2ParticleDefinition definition )
	{
		effect.MaxParticles = Math.Clamp( definition.MaxParticles, 1, Titanfall2ParticleFactory.MaxParticlesPerSystem );
		effect.Lifetime = definition.EndCapLifetime ? float.MaxValue : Range( definition.LifetimeMinimum, definition.LifetimeMaximum );
		effect.LocalSpace = definition.LocalPosition || definition.LocalVelocity ? 1f : 0f;
		var maximumVelocity = Titanfall2ParticleFactory.MaximumVelocity;
		effect.InitialVelocity = new ParticleVector3
		{
			X = Range(
				Math.Clamp( definition.VelocityMinimum.x, -maximumVelocity, maximumVelocity ),
				Math.Clamp( definition.VelocityMaximum.x, -maximumVelocity, maximumVelocity ) ),
			Y = Range(
				Math.Clamp( definition.VelocityMinimum.y, -maximumVelocity, maximumVelocity ),
				Math.Clamp( definition.VelocityMaximum.y, -maximumVelocity, maximumVelocity ) ),
			Z = Range(
				Math.Clamp( definition.VelocityMinimum.z, -maximumVelocity, maximumVelocity ),
				Math.Clamp( definition.VelocityMaximum.z, -maximumVelocity, maximumVelocity ) )
		};
		effect.Damping = MathF.Max( 0f, definition.Damping );

		effect.ApplyColor = true;
		effect.Tint = Color.White;
		effect.Gradient = CreateColor( definition );

		effect.ApplyAlpha = true;
		effect.Alpha = CreateAlpha( definition );
		effect.ApplyShape = true;
		effect.Scale = CreateScale( definition );
		// Sprite trails are sized after simulation using velocity * trail seconds,
		// with the PCF renderer's separate world-space min/max lengths.

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
		// World-Z sprites still need their source angles reset each simulation
		// step before the geometry controller rotates the card into the XY plane.
		if ( definition.OrientationType == 2 ) effect.ApplyRotation = true;

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

	internal static int GetBurstCount( Titanfall2ParticleDefinition definition, Titanfall2ParticleControlPoints points )
	{
		var maximum = Math.Clamp( definition.MaxParticles, 1, Titanfall2ParticleFactory.MaxParticlesPerSystem );
		var count = Math.Clamp( definition.Burst, 0f, maximum );
		if ( definition.BurstSpacing > 0f && definition.Path is { } path && points.IsValid() )
		{
			var distance = points.GetControlPoint( path.Start ).Distance( points.GetControlPoint( path.End ) );
			if ( float.IsFinite( distance ) )
				count += MathF.Max( 2f, MathF.Ceiling( Math.Clamp( distance / definition.BurstSpacing, 0f, maximum ) ) + 1f );
		}
		return (int)Math.Clamp( count, 0f, maximum );
	}

	static void ConfigureEmitter( GameObject effectObject, Titanfall2ParticleDefinition definition, Titanfall2ParticleControlPoints points )
	{
		var emitterObject = new GameObject( effectObject, true, "emitter" );
		var maximumExtent = Titanfall2ParticleFactory.MaximumEmitterExtent;
		var minimum = ClampVector( definition.PositionMinimum, maximumExtent );
		var maximum = ClampVector( definition.PositionMaximum, maximumExtent );
		var center = (minimum + maximum) * 0.5f;
		emitterObject.LocalPosition = ToSandbox( center );

		ParticleEmitter emitter;
		if ( definition.Shape == Titanfall2ParticleShape.Ring )
		{
			var ring = emitterObject.AddComponent<ParticleRingEmitter>();
			ring.Radius = Math.Clamp( definition.ShapeMaximum, 0f, maximumExtent );
			ring.Thickness = Math.Clamp( definition.ShapeThickness, 0f, maximumExtent );
			emitter = ring;
		}
		else if ( definition.Shape == Titanfall2ParticleShape.Sphere )
		{
			var sphere = emitterObject.AddComponent<ParticleSphereEmitter>();
			sphere.Radius = Math.Clamp( definition.ShapeMaximum, 0f, maximumExtent );
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

		// Continuous PCF emission does not restart at every particle lifetime.
		// Bursts occur once; zero-duration continuous emitters run until stopped.
		emitter.Loop = false;
		emitter.DestroyOnEnd = false;
		emitter.Duration = definition.EmissionDuration > 0f ? definition.EmissionDuration
			: definition.Looping ? float.MaxValue : MathF.Max( 0.05f, definition.Duration );
		emitter.Delay = definition.EmissionStartTime;
		emitter.Burst = GetBurstCount( definition, points );
		emitter.Rate = MathF.Max( 0f, definition.EmissionRate );
		emitter.RateOverDistance = definition.EmissionRateOverDistance;
		// OnEnabled ran when the component was attached, before these PCF values
		// were assigned. Refresh its cached burst, duration and start delay now.
		emitter.ResetEmitter();
	}

	static bool ConfigureRenderer( GameObject gameObject, Titanfall2Mount mount, Titanfall2ParticleDefinition definition,
		Titanfall2ParticleGeometry geometry )
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
			// Runtime textures carry no native sheet data, so the batch cannot pick a sheet
			// sequence per particle. Use one sequence from the PCF range for the system
			// rather than drawing the whole atlas (e.g. 25 one-frame chips in fleks.vtf).
			var sequence = definition.SequenceMaximum > definition.SequenceMinimum
				? Random.Shared.Int( definition.SequenceMinimum, definition.SequenceMaximum )
				: definition.SequenceMinimum;
			if ( !string.IsNullOrWhiteSpace( textureName )
				&& mount.TryGetLegacyParticleSprite( textureName, frameRate, out var sprite, out _, Math.Max( 0, sequence ) ) )
			{
				var metadata = materialDescriptor.Metadata;
				// The sprite batch preserves HDR colour via RGBE. Keep the VMT's
				// overbright factor in that path rather than clipping it to an 8-bit tint.
				var tint = materialDefinition.GetVector( "$color", Vector4.One );
				var brightness = Math.Clamp( materialDefinition.GetFloat( "$overbrightfactor", 1f ), 0f, 64f ) * definition.ColorHdrScale;
				gameObject.Components.Get<ParticleEffect>().Tint = new Color(
					tint.x * brightness, tint.y * brightness, tint.z * brightness,
					Math.Clamp( materialDefinition.GetFloat( "$alpha", 1f ), 0f, 1f ) );
				var renderer = gameObject.AddComponent<ParticleSpriteRenderer>();
				if ( materialDefinition.GetString( "$startfadesize" ) is not null || materialDefinition.GetString( "$endfadesize" ) is not null )
				{
					if ( geometry is null )
					{
						geometry = gameObject.AddComponent<Titanfall2ParticleGeometry>();
						geometry.Configure( definition, gameObject.Components.Get<Titanfall2ParticleControlPoints>(), CreateStretch( definition ) );
					}
					geometry.ConfigureSizeFade( materialDefinition.GetFloat( "$startfadesize", 10f ), materialDefinition.GetFloat( "$endfadesize", 20f ) );
				}
				var trail = (definition.Renderer & Titanfall2ParticleRenderer.Trail) != 0;
				if ( trail )
				{
					// Pin the head to the particle. Stretching a centred sprite pushes
					// half the streak ahead of its actual position.
					sprite = new Sprite
					{
						Animations = sprite.Animations.Select( animation => new Sprite.Animation
						{
							Name = animation.Name,
							Frames = animation.Frames,
							FrameRate = animation.FrameRate,
							LoopMode = animation.LoopMode,
							Origin = new Vector2( 0.5f, 0f )
						} ).ToList()
					};
					var frame = sprite.Animations.FirstOrDefault()?.Frames.FirstOrDefault()?.Texture;
					geometry?.Configure( definition, gameObject.Components.Get<Titanfall2ParticleControlPoints>(),
						CreateStretch( definition ), frame is not null ? frame.Width / (float)frame.Height : 1f );
				}
				renderer.Sprite = sprite;
				var rampPath = Titanfall2LegacyPath.NormalizeTextureName( materialDefinition.GetString( "$ramptexture" ) );
				Texture colorRamp = null;
				if ( !string.IsNullOrEmpty( rampPath ) )
				{
					var ramp = Texture.Load( $"mount://{mount.Ident}/{rampPath}.vtex", false );
					if ( ramp is not null && ramp.IsValid && !ramp.IsError ) colorRamp = ramp;
					else Titanfall2Log.Warning( $"Unable to load particle color ramp '{rampPath}' ({definition.MaterialName})." );
				}
				renderer.FaceVelocity = trail;
				renderer.MotionBlur = false;
				renderer.PlaybackSpeed = 1f;
				// Source PCF size is a radius; the native sprite scale is a diameter.
				// Trails already construct their full width/length in the controller.
				renderer.Scale = trail ? 1f : 2f;
				renderer.Additive = metadata.Mode == Titanfall2MaterialMode.Additive;
				renderer.Lighting = !metadata.IsUnlit;
				renderer.Opaque = false;
				renderer.Shadows = false;
				renderer.SortMode = renderer.Additive
					? ParticleSpriteRenderer.ParticleSortMode.Unsorted
					: ParticleSpriteRenderer.ParticleSortMode.ByDistance;
				renderer.Alignment = definition.OrientationType switch
				{
					1 => ParticleSpriteRenderer.BillboardAlignment.RotateToCamera,
					2 => ParticleSpriteRenderer.BillboardAlignment.Particle,
					3 => ParticleSpriteRenderer.BillboardAlignment.Particle,
					_ => ParticleSpriteRenderer.BillboardAlignment.LookAtCamera
				};
				var depthFeather = definition.DepthFeather > 0f ? definition.DepthFeather
					: materialDefinition.GetBool( "$depthblend" ) ? materialDefinition.GetFloat( "$depthblendscale", 50f )
					: materialDefinition.GetString( "$depthblend" ) is not null ? 0f : 4f;
				renderer.DepthFeather = Math.Clamp( depthFeather, 0f, 8192f );
				renderer.FogStrength = Math.Clamp( materialDefinition.GetFloat( "$vertexfogamount", 1f ), 0, 1 );
				renderer.CameraFadeNear = 4f;
				renderer.CameraFadeFar = 24f;
				if ( colorRamp is not null ) gameObject.AddComponent<Titanfall2ParticleRampRenderer>().Configure( renderer, colorRamp );
				created = true;
			}
		}

		if ( (definition.Renderer & Titanfall2ParticleRenderer.Rope) != 0
			&& !string.IsNullOrWhiteSpace( definition.MaterialName )
			&& mount.TryGetOrCreateLegacyMaterialDescriptor( definition.MaterialName, out var ropeMaterial, out _ )
			&& mount.TryGetLegacyMaterialDefinition( definition.MaterialName, out var ropeDefinition, out _ ) )
		{
			var texturePath = ropeMaterial.TextureBindings.FirstOrDefault( binding => binding.Semantic == Titanfall2TextureSemantic.Albedo ).TexturePath;
			if ( !string.IsNullOrWhiteSpace( texturePath ) )
			{
				var texture = Texture.Load( $"mount://{mount.Ident}/{texturePath}.vtex", false );
				if ( texture is not null && texture.IsValid && !texture.IsError )
				{
					gameObject.AddComponent<Titanfall2ParticleRopeRenderer>().Configure( definition, texture, ropeDefinition,
						ropeMaterial.Metadata.Mode == Titanfall2MaterialMode.Additive );
					created = true;
				}
			}
		}

		if ( (definition.Renderer & Titanfall2ParticleRenderer.Light) != 0 )
		{
			var light = gameObject.AddComponent<ParticleLightRenderer>();
			light.Ratio = 1f;
			light.MaximumLights = 4;
			light.CastShadows = false;
			light.Scale = definition.LightRadiusScale;
			light.Brightness = definition.LightColorScale;
			light.UseParticleColor = true;
			created = true;
		}

		if ( (definition.Renderer & Titanfall2ParticleRenderer.Model) != 0 && !string.IsNullOrWhiteSpace( definition.ModelName ) )
		{
			var model = Model.Load( $"mount://{mount.Ident}/{definition.ModelName}.vmdl" );
			if ( ModelLoader.IsUsableModel( model ) )
			{
				var modelRenderer = gameObject.AddComponent<ParticleModelRenderer>();
				modelRenderer.Choices = [new ParticleModelRenderer.ModelEntry { Model = model }];
				modelRenderer.RotateWithGameObject = true;
				modelRenderer.Scale = 1f;
				modelRenderer.CastShadows = false;
				// PCF "Render models" has no colour inputs: retail draws the model with its
				// own materials. The particle colour (often the editor default, e.g. green
				// 124 159 99 in env_training.pcf) would otherwise become the model ColorTint,
				// which the mount shaders multiply into albedo.
				if ( (definition.Renderer & (Titanfall2ParticleRenderer.Sprite | Titanfall2ParticleRenderer.Trail
					| Titanfall2ParticleRenderer.Rope | Titanfall2ParticleRenderer.Light)) == 0 )
					gameObject.Components.Get<ParticleEffect>().ApplyColor = false;
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
		// fade_start_time is raw PCF data; clamp it before it becomes a Clamp minimum,
		// or a value above 1 throws from the streamer's OnUpdate.
		var fadeStart = Math.Clamp( definition.ColorFadeStart, 0f, 1f );
		gradient.AddColor( fadeStart, start );
		gradient.AddColor( Math.Clamp( definition.ColorFadeEnd, fadeStart, 1f ), target );
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
		if ( definition.EndCapLifetime || (definition.AlphaFade is null && graphs.Length == 0) ) return Range( minimum, maximum );
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
		var radiusLimit = GetRadiusLimit( definition );
		var minimum = Math.Clamp( definition.RadiusMinimum * definition.RadiusMultiplierMinimum, 0.01f, radiusLimit );
		var maximum = Math.Clamp( definition.RadiusMaximum * definition.RadiusMultiplierMaximum, minimum, radiusLimit );
		if ( definition.EndCapLifetime || (definition.RadiusScale is null && graphs.Length == 0) ) return Range( minimum, maximum );
		return new ParticleFloat
		{
			Type = ParticleFloat.ValueType.CurveRange,
			Evaluation = ParticleFloat.EvaluationType.Life,
			CurveA = CreateComposedCurve( definition.LifetimeMinimum, graphs,
				time => minimum * EvaluateScale( definition.RadiusScale, time ),
				GetScaleTimes( definition.RadiusScale ), 0.01f, radiusLimit ),
			CurveB = CreateComposedCurve( definition.LifetimeMaximum, graphs,
				time => maximum * EvaluateScale( definition.RadiusScale, time ),
				GetScaleTimes( definition.RadiusScale ), 0.01f, radiusLimit )
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
		Func<float, float> baseValue, IEnumerable<float> extraTimes = null,
		float minimumValue = float.MinValue, float maximumValue = float.MaxValue )
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
			value = Math.Clamp( value, minimumValue, maximumValue );
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

	internal static float EvaluateLiveGraphs( Titanfall2ParticleDefinition definition, int field, float value, float age )
	{
		var lifetime = MathF.Max( 0.01f, definition.LifetimeMaximum );
		foreach ( var graph in definition.ScalarGraphs )
		{
			if ( graph.OutputField != field ) continue;
			var sample = EvaluateGraph( graph, age / lifetime, lifetime );
			value = graph.OutputOperation == 1 ? value * sample : sample;
		}
		return value;
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

	static Vector3 ClampVector( Vector3 value, float extent ) => new(
		Math.Clamp( value.x, -extent, extent ),
		Math.Clamp( value.y, -extent, extent ),
		Math.Clamp( value.z, -extent, extent ) );

	static Color ToColor( RgbaColor value ) => new( value.R / 255f, value.G / 255f, value.B / 255f, value.A / 255f );
	static Vector3 ToSandbox( Vector3 value ) => value;
}

readonly record struct Titanfall2ParticleMapControlPoint( int Index, Vector3 Position );
readonly record struct Titanfall2ParticleMapEntity( string EffectName, string TargetName, Vector3 Position, Angles Rotation, float Scale,
	IReadOnlyList<Titanfall2ParticleMapControlPoint> ControlPoints = null, float WarmupTime = 0f, bool WarmupAndPause = false );
