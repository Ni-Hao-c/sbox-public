using Sandbox.Rendering;

/// <summary>
/// Mount-owned SpriteCard lookup rendering, including sheet interpolation before
/// the nonlinear RG lookup. Uses public APIs shared by Steam and the source SDK.
/// </summary>
[Library]
public sealed class Titanfall2ParticleRampRenderer : ParticleController
{
	[Property, Hide] public ParticleSpriteRenderer Source { get; set; }
	[Property, Hide] public Texture ColorRamp { get; set; }
	readonly object _sync = new();
	readonly List<Item> _items = [];
	readonly Dictionary<Particle, Vector3> _lastVelocity = [];
	readonly Sprite.AnimationState _animation = new();
	readonly VertexBuffer _vertices = new();
	SceneCustomObject _object;
	Material _material;
	Texture _texture;
	Vector2 _pivot;
	float _scale;
	Rotation _rotation;
	Vector3 _sortOrigin;
	Comparison<Item> _compare;

	readonly record struct Item( Vector3 Position, Vector3 Size, Vector3 Velocity, Angles Angles,
		Color Tint, Color Overlay, float Alpha, int Sequence, float SequenceTime );

	internal void Configure( ParticleSpriteRenderer source, Texture ramp )
	{
		Source = source;
		ColorRamp = ramp;
		source.Enabled = false;
		EnsureObject();
	}

	void EnsureObject()
	{
		if ( _object.IsValid() || !Source.IsValid() || ColorRamp is null || Scene?.SceneWorld is null ) return;
		_material ??= Material.FromShader( "shaders/titanfall2_particle_ramp.shader" );
		_compare ??= ( a, b ) => b.Position.DistanceSquared( _sortOrigin ).CompareTo( a.Position.DistanceSquared( _sortOrigin ) );
		_object = new SceneCustomObject( Scene.SceneWorld ) { RenderOverride = Render };
		_object.Flags.IsOpaque = false;
		_object.Flags.IsTranslucent = true;
		_object.Flags.CastShadows = false;
		_object.RenderingEnabled = false;
	}

	protected override void OnParticleDestroyed( Particle particle ) => _lastVelocity.Remove( particle );

	protected override void OnAfterStep( float delta )
	{
		EnsureObject();
		if ( !_object.IsValid() || !Source.IsValid() || !ParticleEffect.IsValid() ) return;
		lock ( _sync )
		{
			var animation = Source.CurrentAnimation;
			_animation.PlaybackSpeed = Source.PlaybackSpeed;
			_animation.TryAdvanceFrame( animation, delta );
			_texture = animation?.Frames.ElementAtOrDefault( _animation.CurrentFrameIndex )?.Texture;
			_items.Clear();
			if ( _texture is null || !_texture.IsValid || _texture.IsError ) { _object.RenderingEnabled = false; return; }
			_pivot = Source.Pivot;
			_scale = MathF.Abs( Source.Scale * Source.WorldScale.x * 0.5f );
			_rotation = Source.WorldRotation;
			var min = new Vector3( float.MaxValue ); var max = new Vector3( float.MinValue );
			// SequenceData is a native query; evaluate it once per step rather than per particle.
			var aspect = _texture.SequenceData == Vector4.Zero ? _texture.Width / (float)_texture.Height : 1f;
			foreach ( var p in ParticleEffect.Particles )
			{
				if ( !p.Position.IsFinite || !p.Size.IsFinite ) continue;
				var velocity = p.Velocity;
				if ( Source.FaceVelocity )
				{
					if ( velocity.LengthSquared > 0.001f ) _lastVelocity[p] = velocity;
					else velocity = _lastVelocity.GetValueOrDefault( p );
				}
				var sequenceTime = p.SequenceTime.y + p.SequenceTime.z + MathF.Max( 0, p.SequenceTime.x );
				_items.Add( new( p.Position, p.Size, velocity, p.Angles, p.Color, p.OverlayColor,
					Math.Clamp( p.Color.a * p.Alpha, 0, 1 ), p.Sequence, sequenceTime ) );
				var extentX = MathF.Abs( p.Size.x ) * _scale * aspect * 2f * MathF.Max( MathF.Abs( _pivot.x ), MathF.Abs( 1f - _pivot.x ) );
				var extentY = MathF.Abs( p.Size.y ) * _scale * 2f * MathF.Max( MathF.Abs( _pivot.y ), MathF.Abs( 1f - _pivot.y ) );
				var radius = MathF.Sqrt( extentX * extentX + extentY * extentY );
				min = Vector3.Min( min, p.Position - radius ); max = Vector3.Max( max, p.Position + radius );
			}
			_object.RenderingEnabled = _items.Count > 0;
			if ( _items.Count > 0 ) _object.Bounds = new BBox( min, max );
		}
	}

	void Render( SceneObject sceneObject )
	{
		lock ( _sync )
		{
			if ( _items.Count == 0 || _texture is null || !Source.IsValid() ) return;
			_sortOrigin = Graphics.CameraPosition;
			if ( !Source.Additive ) _items.Sort( _compare );
			_vertices.Init( false );
			var camera = Graphics.CameraRotation;
			// Native sheet query, once per draw instead of once per particle.
			var sheet = _texture.SequenceData;
			var aspect = _texture.Width / (float)_texture.Height;
			foreach ( var p in _items )
			{
				var right = camera.Right; var up = camera.Up;
				if ( Source.Alignment == ParticleSpriteRenderer.BillboardAlignment.RotateToCamera )
				{
					up = Vector3.Up;
					var forward = camera.Forward.WithZ( 0 );
					right = forward.LengthSquared > 0.000001f ? Vector3.Cross( forward.Normal, up ) : Vector3.Right;
				}
				else if ( Source.Alignment is ParticleSpriteRenderer.BillboardAlignment.Particle or ParticleSpriteRenderer.BillboardAlignment.Object )
				{
					var rotation = p.Angles.ToRotation();
					if ( Source.Alignment == ParticleSpriteRenderer.BillboardAlignment.Object ) rotation = _rotation * rotation;
					right = rotation.Left; up = rotation.Up;
				}
				if ( Source.Alignment is ParticleSpriteRenderer.BillboardAlignment.LookAtCamera or ParticleSpriteRenderer.BillboardAlignment.RotateToCamera )
				{
					var angle = -p.Angles.roll * MathF.PI / 180;
					if ( Source.FaceVelocity && p.Velocity.LengthSquared > 0.001f )
						angle = MathF.Atan2( -Vector3.Dot( p.Velocity, right ), Vector3.Dot( p.Velocity, up ) ) + Source.RotationOffset * MathF.PI / 180;
					var r = right; right = r * MathF.Cos( angle ) + up * MathF.Sin( angle );
					up = up * MathF.Cos( angle ) - r * MathF.Sin( angle );
				}
				var size = p.Size * _scale;
				if ( sheet == Vector4.Zero ) size.x *= aspect;
				Vertex Corner( float x, float y ) => new()
				{
					Position = p.Position + right * ((x - _pivot.x) * 2f * size.x) + up * ((_pivot.y - y) * 2f * size.y),
					Normal = new Vector3( p.Tint.r, p.Tint.g, p.Tint.b ),
					Tangent = new Vector4( p.Overlay.r, p.Overlay.g, p.Overlay.b, p.Overlay.a ),
					Color = Color.White,
					TexCoord0 = new Vector4( x, y, p.Sequence, p.SequenceTime ),
					TexCoord1 = new Vector4( p.Alpha, 0, 0, 0 )
				};
				var a = Corner( 0, 0 ); var b = Corner( 1, 0 ); var c = Corner( 1, 1 ); var d = Corner( 0, 1 );
				_vertices.Add( a ); _vertices.Add( b ); _vertices.Add( c );
				_vertices.Add( a ); _vertices.Add( c ); _vertices.Add( d );
			}
			var attributes = sceneObject.Attributes;
			attributes.Set( "T2ParticleTexture", _texture ); attributes.Set( "T2ParticleRamp", ColorRamp );
			attributes.Set( "T2ParticleSheet", sheet );
			attributes.Set( "T2ParticleSnap", ParticleEffect.SnapToFrame ? 1f : 0f );
			attributes.Set( "T2ParticleAdditive", Source.Additive ? 1f : 0f );
			attributes.Set( "T2ParticleLit", Source.Lighting ? 1f : 0f );
			attributes.Set( "T2ParticleFog", Source.FogStrength );
			attributes.Set( "T2ParticleDepthFeather", Source.DepthFeather );
			attributes.Set( "T2ParticleCameraFade", new Vector2( Source.CameraFadeNear, Source.CameraFadeFar ) );
			_vertices.Draw( _material, attributes );
		}
	}

	protected override void OnDisabled()
	{
		base.OnDisabled();
		lock ( _sync )
		{
			_object?.Delete(); _object = null; _items.Clear(); _lastVelocity.Clear();
			_animation.ResetState();
		}
	}
}
