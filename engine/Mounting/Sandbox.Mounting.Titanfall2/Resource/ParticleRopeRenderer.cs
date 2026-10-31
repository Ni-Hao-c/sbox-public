using Titanfall2;

/// <summary>One textured ribbon per PCF collection, rather than one draw per segment.</summary>
[Library]
public sealed class Titanfall2ParticleRopeRenderer : ParticleController
{
	SceneLineObject _line;
	Material _material;
	Titanfall2ParticleDefinition _definition;
	readonly List<Particle> _ordered = [];
	readonly Dictionary<Particle, long> _order = [];
	Comparison<Particle> _compare;
	long _nextOrder;
	float _time;
	float _brightness;
	float _addSelf;
	bool _additive;

	internal void Configure( Titanfall2ParticleDefinition definition, Texture texture,
		Titanfall2VmtDefinition materialDefinition, bool additive )
	{
		_definition = definition;
		_additive = additive;
		_brightness = Math.Clamp( materialDefinition.GetFloat( "$overbrightfactor", 1f ), 0f, 64f );
		_addSelf = Math.Clamp( materialDefinition.GetFloat( "$addself", 0f ), 0f, 64f );
		_material = Material.Create( $"titanfall2.particles.rope/{Guid.NewGuid():N}", "shaders/titanfall2_particle_rope.shader" );
		_material.Set( "g_tColor", texture );
		CreateLine( additive );
	}

	protected override void OnEnabled()
	{
		base.OnEnabled();
		if ( _definition is not null && _material is not null ) CreateLine( _additive );
	}

	void CreateLine( bool additive )
	{
		_line = new SceneLineObject( Scene.SceneWorld )
		{
			Material = _material,
			Opaque = false,
			Lighting = false,
			Face = SceneLineObject.FaceMode.Camera,
			RenderingEnabled = false
		};
		_line.Flags.CastShadows = false;
		_line.Attributes.Set( "g_flParticleOverbright", _brightness );
		_line.Attributes.Set( "g_flParticleAddSelf", _addSelf );
		_line.Attributes.Set( "g_flParticleAdditive", additive ? 1f : 0f );
		_line.Attributes.Set( "g_DepthFeather", _definition.DepthFeather );
	}

	protected override void OnParticleCreated( Particle particle ) => _order[particle] = _nextOrder++;
	protected override void OnParticleDestroyed( Particle particle ) => _order.Remove( particle );

	protected override void OnAfterStep( float delta )
	{
		if ( !_line.IsValid() || _definition is null || !ParticleEffect.IsValid() ) return;
		_time += delta;
		_ordered.Clear();
		foreach ( var particle in ParticleEffect.Particles )
			if ( Titanfall2ParticleControlPoints.IsFinite( particle.Position ) ) _ordered.Add( particle );
		// The comparison captures this; cache it so the per-frame sort does not allocate a delegate.
		_compare ??= ( a, b ) => _order.GetValueOrDefault( a ).CompareTo( _order.GetValueOrDefault( b ) );
		_ordered.Sort( _compare );
		_line.RenderingEnabled = _ordered.Count >= 2;
		if ( _ordered.Count < 2 ) return;
		_line.StartLine();
		var distance = 0f;
		var maximumWidth = 0f;
		Vector3? previous = null;
		for ( var index = 0; index < _ordered.Count; ++index )
		{
			var particle = _ordered[index];
			var next = _ordered[Math.Min( index + 1, _ordered.Count - 1 )];
			var subdivisions = index == _ordered.Count - 1 ? 1 : _definition.RopeSubdivisions;
			for ( var step = 0; step < subdivisions; ++step )
			{
				var fraction = step / (float)subdivisions;
				var position = particle.Position;
				if ( subdivisions > 1 )
				{
					var a = _ordered[Math.Max( index - 1, 0 )].Position;
					var b = particle.Position; var c = next.Position;
					var d = _ordered[Math.Min( index + 2, _ordered.Count - 1 )].Position;
					position = 0.5f * (2f * b + (c - a) * fraction + (2f * a - 5f * b + 4f * c - d) * fraction * fraction
						+ (-a + 3f * b - 3f * c + d) * fraction * fraction * fraction);
				}
				if ( previous.HasValue ) distance += previous.Value.Distance( position );
				previous = position;
				var texture = distance * _definition.RopeTextureScale + _definition.RopeTextureOffset + _time * _definition.RopeTextureScroll;
				var startColor = particle.Color.WithAlpha( Math.Clamp( particle.Color.a * particle.Alpha, 0f, 1f ) );
				var endColor = next.Color.WithAlpha( Math.Clamp( next.Color.a * next.Alpha, 0f, 1f ) );
				var width = Math.Clamp( MathX.Lerp( particle.Size.x, next.Size.x, fraction ) * 2f,
					0f, Titanfall2StreamingSettings.ParticleMaximumRadius * 2f );
				maximumWidth = MathF.Max( maximumWidth, width );
				_line.AddLinePoint( position, Color.Lerp( startColor, endColor, fraction ), width, texture );
			}
		}
		_line.EndLine();
		_line.Bounds = _line.Bounds.Grow( maximumWidth * 0.5f );
	}

	protected override void OnDisabled()
	{
		base.OnDisabled();
		_line?.Clear();
		_line?.Delete();
		_line = null;
		_order.Clear();
		_ordered.Clear();
		_time = 0f;
	}

	protected override void OnDestroy()
	{
		_line?.Delete();
		_line = null;
		// Material exposes no public release API, so dropping the reference is all this
		// assembly can do; the Resource finalizer then releases the native material.
		_material = null;
		_order.Clear();
		_ordered.Clear();
	}
}
