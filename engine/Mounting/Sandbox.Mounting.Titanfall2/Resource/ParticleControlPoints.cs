using Titanfall2;

/// <summary>Control points shared by a PCF collection and inherited by its children.</summary>
[Library]
public sealed class Titanfall2ParticleControlPoints : Component
{
	readonly Dictionary<int, Vector3> _world = [];
	readonly Dictionary<int, Vector3> _local = [];
	Titanfall2ParticleControlPoints _parent;

	internal void Configure( Titanfall2ParticleControlPoints parent, IReadOnlyList<ParticleControlPointDefinition> points )
	{
		_parent = parent;
		foreach ( var point in points ) _local[point.Index] = new Vector3( point.Position.x, point.Position.y, point.Position.z );
	}

	/// <summary>Update an endpoint without rebuilding the effect or its materials.</summary>
	public void SetControlPoint( int index, Vector3 worldPosition )
	{
		if ( index is < 0 or > 63 || !IsFinite( worldPosition ) ) return;
		_world[index] = worldPosition;
	}

	public Vector3 GetControlPoint( int index )
	{
		return TryGetControlPoint( index, out var position ) ? position : WorldPosition;
	}

	internal bool TryGetControlPoint( int index, out Vector3 position )
	{
		if ( _world.TryGetValue( index, out position ) ) return true;
		if ( _local.TryGetValue( index, out var local ) ) { position = WorldTransform.PointToWorld( local ); return true; }
		if ( _parent.IsValid() ) return _parent.TryGetControlPoint( index, out position );
		position = WorldPosition;
		return index == 0;
	}

	internal static bool IsFinite( Vector3 value ) => float.IsFinite( value.x ) && float.IsFinite( value.y ) && float.IsFinite( value.z );
}

/// <summary>
/// Applies PCF geometry after the engine finishes simulation, before sprite batching.
/// Trail time is in seconds; it must never be passed to an aspect-ratio stretch input.
/// </summary>
[Library]
public sealed class Titanfall2ParticleGeometry : ParticleController
{
	Titanfall2ParticleDefinition _definition;
	Titanfall2ParticleControlPoints _points;
	ParticleFloat _trailTime;
	float _textureAspect = 1f;
	int _pathCount;
	int _nextParticle;
	readonly Dictionary<Particle, float> _pathFractions = [];
	readonly Dictionary<Particle, (float Radius, float Alpha)> _initialValues = [];
	readonly Dictionary<Particle, (float In, float Out)> _randomFades = [];
	readonly Dictionary<Particle, float[]> _scalarInitialValues = [];

	internal void Configure( Titanfall2ParticleDefinition definition, Titanfall2ParticleControlPoints points,
		ParticleFloat trailTime, float textureAspect = 1f )
	{
		_definition = definition;
		_points = points;
		_trailTime = trailTime;
		_textureAspect = MathF.Max( 0.001f, textureAspect );
		_pathCount = Math.Max( 2, definition.Path is { UseParticleCount: true }
			? Titanfall2ParticleFactory.GetBurstCount( definition, points ) : definition.Path?.Count ?? 2 );
	}

	protected override void OnParticleCreated( Particle particle )
	{
		if ( _definition?.DistanceRemaps.Any( remap => remap.ScaleInitialRange ) == true || _definition?.ScalarRemaps.Any( remap => remap.ScaleInitial ) == true )
			_initialValues[particle] = (particle.StartScale * ParticleEffect.Scale.Evaluate( particle, 6211 ),
				ParticleEffect.Alpha.Evaluate( particle, 8525 ));
		if ( _definition is { } fadeDefinition && (fadeDefinition.RandomFadeIn.HasValue || fadeDefinition.RandomFadeOut.HasValue) )
		{
			float Duration( ParticleRandomFadeDefinition? fade ) => fade is { } f ? MathF.Max( 0f,
				MathX.Lerp( f.Minimum, f.Maximum, MathF.Pow( Random.Shared.Float(), MathF.Max( 0.0001f, f.Exponent ) ) ) ) : 0f;
			_randomFades[particle] = (Duration( fadeDefinition.RandomFadeIn ), Duration( fadeDefinition.RandomFadeOut ));
		}
		if ( _definition?.ScalarRemaps.Any( remap => remap.InitialOnly ) == true )
			_scalarInitialValues[particle] = _definition.ScalarRemaps.Select( remap => remap.InitialOnly ? EvaluateScalarRemap( remap ) : float.NaN ).ToArray();
		var index = _nextParticle++;
		if ( _definition is { } definition && (definition.PositionWarpMinimum != Vector3.One
			|| definition.PositionWarpMaximum != Vector3.One) )
		{
			// Source ground dust flattens a sphere before its final position offset.
			// Preserve that offset: scaling absolute world coordinates moves the
			// entire collection towards the map origin.
			var maximumExtent = Titanfall2StreamingSettings.ParticleMaximumEmitterExtent;
			Vector3 Offset( Vector3 value ) => new(
				Math.Clamp( value.x, -maximumExtent, maximumExtent ), Math.Clamp( value.y, -maximumExtent, maximumExtent ),
				Math.Clamp( value.z, -maximumExtent, maximumExtent ) );
			var centre = WorldTransform.PointToWorld( (Offset( definition.PositionMinimum ) + Offset( definition.PositionMaximum )) * 0.5f );
			var local = WorldRotation.Inverse * (particle.Position - centre);
			float Warp( float minimum, float maximum ) => Math.Clamp( Random.Shared.Float( MathF.Min( minimum, maximum ),
				MathF.Max( minimum, maximum ) ), -16f, 16f );
			var min = definition.PositionWarpMinimum; var max = definition.PositionWarpMaximum;
			local *= new Vector3( Warp( min.x, max.x ), Warp( min.y, max.y ), Warp( min.z, max.z ) );
			particle.Position = centre + WorldRotation * local;
			particle.StartPosition = particle.Position;
		}
		if ( _definition?.Path is not { } path || !_points.IsValid() ) return;
		var fraction = path.Sequential ? (index % _pathCount) / (float)(_pathCount - 1) : Random.Shared.Float();
		_pathFractions[particle] = fraction;
		particle.Position = Vector3.Lerp( _points.GetControlPoint( path.Start ), _points.GetControlPoint( path.End ), fraction );
		particle.StartPosition = particle.Position;
	}

	protected override void OnParticleDestroyed( Particle particle )
	{
		_pathFractions.Remove( particle );
		_initialValues.Remove( particle );
		_randomFades.Remove( particle );
		_scalarInitialValues.Remove( particle );
	}

	protected override void OnAfterStep( float delta )
	{
		if ( _definition is null || !ParticleEffect.IsValid() ) return;
		if ( _definition.PlayerControlPoint >= 0 && Scene.Camera.IsValid() )
			_points.SetControlPoint( _definition.PlayerControlPoint, Scene.Camera.WorldPosition );
		var visibility = GetVisibility();
		foreach ( var particle in ParticleEffect.Particles )
		{
			for ( var remapIndex = 0; remapIndex < _definition.ScalarRemaps.Count; ++remapIndex )
			{
				var remap = _definition.ScalarRemaps[remapIndex];
				var value = remap.InitialOnly && _scalarInitialValues.TryGetValue( particle, out var values ) ? values[remapIndex] : EvaluateScalarRemap( remap );
				if ( !float.IsFinite( value ) ) continue;
				if ( remap.ScaleInitial && _initialValues.TryGetValue( particle, out var initial ) ) value *= remap.Field == 3 ? initial.Radius : initial.Alpha;
				if ( remap.Field == 3 ) particle.Size = Math.Clamp( value, 0f, Titanfall2ParticleFactory.GetRadiusLimit( _definition ) );
				else if ( remap.Field == 7 ) particle.Alpha = value;
				else if ( remap.Field == 16 ) particle.Alpha *= value;
			}
			if ( _definition.Path is { Maintain: true } path && _points.IsValid()
				&& _pathFractions.TryGetValue( particle, out var fraction ) )
			{
				particle.Position = Vector3.Lerp( _points.GetControlPoint( path.Start ), _points.GetControlPoint( path.End ), fraction );
				particle.Velocity = Vector3.Zero;
			}
			if ( _definition.EndCapLifetime )
			{
				particle.Alpha = Titanfall2ParticleFactory.EvaluateLiveGraphs( _definition, 7, particle.Alpha, particle.Age );
				particle.Alpha = Titanfall2ParticleFactory.EvaluateLiveGraphs( _definition, 16, particle.Alpha, particle.Age );
				var graphRadius = Titanfall2ParticleFactory.EvaluateLiveGraphs( _definition, 3, particle.Size.x, particle.Age );
				particle.Size = Math.Clamp( graphRadius, 0.01f, Titanfall2ParticleFactory.GetRadiusLimit( _definition ) );
			}
			foreach ( var remap in _definition.DistanceRemaps )
			{
				if ( !_points.TryGetControlPoint( remap.ControlPoint, out var controlPoint ) ) continue;
				var distance = particle.Position.Distance( controlPoint );
				var value = Remap( distance, remap.InputMin, remap.InputMax, remap.OutputMin, remap.OutputMax );
				if ( remap.ScaleInitialRange && _initialValues.TryGetValue( particle, out var initial ) )
					value *= remap.Field == 3 ? initial.Radius : initial.Alpha;
				if ( remap.Field == 3 ) particle.Size = Math.Clamp( value, 0f, Titanfall2ParticleFactory.GetRadiusLimit( _definition ) );
				else if ( remap.Field == 7 ) particle.Alpha = value;
				else if ( remap.Field == 16 ) particle.Alpha *= value;
			}
			foreach ( var remap in _definition.ColorRemaps )
			{
				if ( !_points.TryGetControlPoint( remap.ControlPoint, out var input ) ) continue;
				particle.Color = ParticleEffect.Tint.WithAlpha( 1f ) * new Color(
					Remap( input.x, remap.InputMin.x, remap.InputMax.x, remap.OutputMin.x, remap.OutputMax.x ),
					Remap( input.y, remap.InputMin.y, remap.InputMax.y, remap.OutputMin.y, remap.OutputMax.y ),
					Remap( input.z, remap.InputMin.z, remap.InputMax.z, remap.OutputMin.z, remap.OutputMax.z ), particle.Color.a );
			}
			if ( _randomFades.TryGetValue( particle, out var fade ) )
			{
				var fadeInAge = _definition.RandomFadeIn?.Proportional == true ? particle.LifeDelta : particle.Age;
				var remaining = _definition.RandomFadeOut?.Proportional == true ? 1f - particle.LifeDelta : particle.LifeTimeRemaining - particle.Age;
				if ( fade.In > 0f ) particle.Alpha *= Math.Clamp( fadeInAge / fade.In, 0f, 1f );
				if ( fade.Out > 0f ) particle.Alpha *= Math.Clamp( remaining / fade.Out, 0f, 1f );
			}
			particle.Alpha *= visibility;
			if ( (_definition.Renderer & Titanfall2ParticleRenderer.Trail) == 0 )
			{
				// Source orientation 2 is a world-Z-aligned card, not an upright
				// camera-facing billboard. Preserve roll/yaw in the card's local
				// frame, including rotation curves and the authored yaw flip.
				if ( _definition.OrientationType == 2 )
					particle.Angles = (Rotation.FromPitch( 90f ) * particle.Angles.ToRotation()).Angles();
				continue;
			}
			var seconds = MathF.Max( 0f, _trailTime.Evaluate( particle, 9145 ) );
			var length = Math.Clamp( particle.Velocity.Length * seconds,
				_definition.TrailRenderMinimum, _definition.TrailRenderMaximum );
			if ( _definition.TrailFadeInTime > 0f ) length *= Math.Clamp( particle.Age / _definition.TrailFadeInTime, 0f, 1f );
			var radius = particle.Size.x;
			if ( _definition.ConstrainRadiusToTrail ) radius = MathF.Min( radius, length * 0.5f );
			particle.Size = new Vector3( 2f * radius / _textureAspect, length, particle.Size.z );
			particle.Angles = Angles.Zero;
		}
	}

	float EvaluateScalarRemap( ParticleControlScalarDefinition remap )
	{
		if ( !_points.IsValid() || !_points.TryGetControlPoint( remap.ControlPoint, out var point ) ) return float.NaN;
		float input;
		if ( remap.EndPoint >= 0 )
		{
			if ( !_points.TryGetControlPoint( remap.EndPoint, out var end ) ) return float.NaN;
			input = point.Distance( end );
		}
		else input = remap.Component switch { 0 => point.x, 1 => point.y, _ => point.z };
		return Remap( input, remap.InputMin, remap.InputMax, remap.OutputMin, remap.OutputMax );
	}

	float GetVisibility()
	{
		if ( _definition.Visibility is not { } visibility || !Scene.Camera.IsValid() ) return 1f;
		var point = _points.GetControlPoint( visibility.ControlPoint );
		var direction = point - Scene.Camera.WorldPosition;
		var factor = 1f;
		if ( visibility.DistanceMin != visibility.DistanceMax )
			factor *= Remap( direction.Length, visibility.DistanceMin, visibility.DistanceMax, 0f, 1f );
		var dot = Vector3.Dot( direction.Normal, WorldRotation.Forward );
		if ( visibility.CullBackFacing && dot >= 0f ) return 0f;
		if ( visibility.DotMin != visibility.DotMax )
			factor *= Remap( dot, visibility.DotMin, visibility.DotMax, 0f, 1f );
		return factor;
	}

	static float Remap( float value, float inputMin, float inputMax, float outputMin, float outputMax ) =>
		MathX.Lerp( outputMin, outputMax, Math.Clamp( MathF.Abs( inputMax - inputMin ) < 0.00001f
			? 0f : (value - inputMin) / (inputMax - inputMin), 0f, 1f ) );

	protected override void OnDisabled()
	{
		base.OnDisabled();
		_pathFractions.Clear();
		_initialValues.Clear();
		_randomFades.Clear();
		_scalarInitialValues.Clear();
		_nextParticle = 0;
	}
}
