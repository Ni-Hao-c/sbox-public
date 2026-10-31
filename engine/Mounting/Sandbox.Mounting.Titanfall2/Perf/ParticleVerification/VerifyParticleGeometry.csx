// Trusted MCP method body. Uses retail PCFs and the real GPU sprite/ribbon paths.
var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
var mount = Sandbox.Mounting.Directory.Get( "titanfall2" );
if ( mount is null || !mount.IsMounted ) throw new InvalidOperationException( "Mount titanfall2 before running this check." );
var mountType = mount.GetType();
mountType.GetMethod( "EnsureRuntimeTypesRegistered", flags ).Invoke( mount, null );
mountType.GetMethod( "IndexParticleFiles", flags ).Invoke( mount, new object[] { 512, 5000f } );
var factory = mountType.Assembly.GetType( "Titanfall2ParticleFactory" ).GetMethod( "TryCreate", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic );
var updateSprites = typeof( SceneSpriteSystem ).GetMethod( "UpdateSprites", flags );
var rows = new System.Collections.Generic.List<object>();
object Definition( string name )
{
	var arguments = new object[] { name, null };
	if ( !(bool)mountType.GetMethod( "TryGetParticleDefinition", flags ).Invoke( mount, arguments ) ) throw new InvalidOperationException( "Missing PCF: " + name );
	return arguments[1];
}
void SetPoint( GameObject root, int index, Vector3 position )
{
	foreach ( var component in root.GetComponentsInChildren<Component>().Where( c => c.GetType().Name == "Titanfall2ParticleControlPoints" ) )
		component.GetType().GetMethod( "SetControlPoint" ).Invoke( component, new object[] { index, position } );
}
async Task<(int Visible, long Energy)> Capture( Scene scene, CameraComponent camera, Texture target, string name )
{
	var sprites = scene.GetSystem<SceneSpriteSystem>();
	for ( var frame = 0; frame < 4; ++frame )
	{
		updateSprites.Invoke( sprites, null );
		await Task.Delay( 50, cancellationToken );
		camera.RenderToTexture( target, new Sandbox.Rendering.ViewSetup { Transform = Transform.Zero } );
	}
	var pixels = target.GetPixels();
	using var bitmap = target.GetBitmap();
	System.IO.File.WriteAllBytes( System.IO.Path.Combine( exchangeDirectory, name + ".png" ), bitmap.ToPng() );
	return (pixels.Count( p => p.r > 8 || p.g > 8 || p.b > 8 ), pixels.Sum( p => (long)p.r + p.g + p.b ));
}
foreach ( var name in new[] { "tw_harv_beam_rope_solid", "impact_ricochet_sparks", "impact_steam", "P_tw_harvester_beam", "P_beacon_beam_small", "glow_imc_light03_wide" } )
{
	var scene = new Scene();
	using var target = Texture.CreateRenderTarget().WithSize( 1024, 768 ).Create();
	try
	{
		var camera = new GameObject( scene, true, "Particle check camera" ).AddComponent<CameraComponent>();
		camera.WorldTransform = Transform.Zero;
		camera.FieldOfView = 70; camera.ZNear = 1; camera.ZFar = 8000;
		camera.BackgroundColor = Color.Black; camera.EnablePostProcessing = false;
		typeof( Scene ).GetMethod( "UpdateMainCamera", flags ).Invoke( scene, null );
		var beam = name.Contains( "beam" );
		var root = new GameObject( scene, true, name );
		root.WorldPosition = beam ? new Vector3( 800, 0, -250 ) : new Vector3( 150, 0, 0 );
		if ( name == "glow_imc_light03_wide" ) root.WorldRotation = new Angles( 0, 180, 0 ).ToRotation();
		var beacon = name == "P_beacon_beam_small";
		if ( beacon ) root.AddComponent<Titanfall2ParticleControlPoints>().SetControlPoint( 1, new Vector3( 800, 0, 250 ) );
		var arguments = new object[] { root, mount, Definition( name ), 0, null };
		var created = (bool)factory.Invoke( null, arguments );
		if ( !beacon ) SetPoint( root, 1, new Vector3( 80, 160, 255 ) );
		SetPoint( root, 2, new Vector3( 800, 0, 250 ) );
		var effects = root.GetComponentsInChildren<ParticleEffect>().ToArray();
		var steps = name.Contains( "sparks" ) ? 4 : 30;
		for ( var frame = 0; frame < steps; ++frame ) foreach ( var effect in effects ) effect.Step( 1f / 60f );
		var initial = await Capture( scene, camera, target, name );
		var count = effects.Sum( e => e.Particles.Count );
		var renderers = root.GetComponentsInChildren<ParticleSpriteRenderer>().ToArray();
		var ropes = root.GetComponentsInChildren<Component>().Where( c => c.GetType().Name == "Titanfall2ParticleRopeRenderer" ).ToArray();
		rows.Add( new { check = name + ":visible", created, systems = arguments[3], particles = count, sprites = renderers.Length, ropes = ropes.Length, initial.Visible, passed = created && initial.Visible > 10 } );
		if ( beacon )
		{
			var nodes = effects[0].Particles.ToArray();
			rows.Add( new { check = "distance-emitter-covers-entire-path", nodes = nodes.Length,
				passed = nodes.Length > 2 && nodes[0].Position.Distance( new Vector3( 800, 0, -250 ) ) < 0.01f
					&& nodes[^1].Position.Distance( new Vector3( 800, 0, 250 ) ) < 0.01f } );
		}
		if ( name == "glow_imc_light03_wide" )
		{
			for ( var frame = 0; frame < 720; ++frame ) foreach ( var effect in effects ) effect.Step( 1f / 60f );
			var persistent = await Capture( scene, camera, target, "persistent-map-glow" );
			rows.Add( new { check = "endcap-fade-does-not-kill-live-light", persistent.Visible,
				passed = effects[0].Particles.Count == 1 && effects[0].Particles[0].Age > 12f && persistent.Visible > 10 } );
			root.WorldRotation = Rotation.Identity;
			foreach ( var effect in effects ) effect.Step( 1f / 60f );
			var back = await Capture( scene, camera, target, "back-facing-map-glow" );
			rows.Add( new { check = "back-facing-map-glow-hidden", back.Visible, passed = back.Visible == 0 } );
			root.WorldRotation = new Angles( 0, 180, 0 ).ToRotation();
			SetPoint( root, 0, new Vector3( 2400, 0, 0 ) );
			foreach ( var effect in effects ) effect.Step( 1f / 60f );
			var far = await Capture( scene, camera, target, "distant-map-glow" );
			rows.Add( new { check = "distant-map-glow-fades-out", far.Visible, passed = far.Visible == 0 } );
		}
		if ( name == "tw_harv_beam_rope_solid" )
		{
			rows.Add( new { check = "retail-burst-count", expected = 12, actual = count, passed = count == 12 } );
			var endpoint = new Vector3( 800, 160, 180 );
			SetPoint( root, 2, endpoint );
			foreach ( var effect in effects ) effect.Step( 1f / 60f );
			var last = effects[0].Particles.Last().Position;
			rows.Add( new { check = "moving-endpoint", last = last.ToString(), passed = last.Distance( endpoint ) < 0.01f } );
			var material = (Material)ropes[0].GetType().GetField( "_material", flags ).GetValue( ropes[0] );
			var testLine = (SceneLineObject)ropes[0].GetType().GetField( "_line", flags ).GetValue( ropes[0] );
			testLine.Attributes.Set( "g_flParticleOverbright", 1f );
			ropes[0].GetType().GetMethod( "OnAfterStep", flags ).Invoke( ropes[0], new object[] { 0f } );
			var normal = await Capture( scene, camera, target, "beam-brightness-1" );
			testLine.Attributes.Set( "g_flParticleOverbright", 4f );
			ropes[0].GetType().GetMethod( "OnAfterStep", flags ).Invoke( ropes[0], new object[] { 0f } );
			var bright = await Capture( scene, camera, target, "beam-brightness-4" );
			rows.Add( new { check = "rope-hdr-material", normal = normal.Energy, bright = bright.Energy, passed = normal.Energy > 0 && bright.Energy > normal.Energy * 1.25 } );
			for ( var frame = 0; frame < 720; ++frame ) foreach ( var effect in effects ) effect.Step( 1f / 60f );
			rows.Add( new { check = "beam-survives-original-lifetime", age = effects[0].Particles.FirstOrDefault()?.Age, passed = effects[0].Particles.Count == 12 && effects[0].Particles.All( p => p.Age > 12f ) } );
			var line = (SceneLineObject)ropes[0].GetType().GetField( "_line", flags ).GetValue( ropes[0] );
			root.Enabled = false;
			rows.Add( new { check = "rope-disable-releases-native-object", passed = !line.IsValid() } );
			root.Enabled = true;
			for ( var frame = 0; frame < 3; ++frame ) foreach ( var effect in effects ) effect.Step( 1f / 60f );
			var resumed = await Capture( scene, camera, target, "beam-resumed" );
			rows.Add( new { check = "rope-reactivation", resumed.Visible, passed = effects[0].Particles.Count == 12 && resumed.Visible > 10 } );
		}
		if ( name.Contains( "sparks" ) )
		{
			rows.Add( new { check = "trail-length-limit", maximum = effects.SelectMany( e => e.Particles ).Max( p => p.Size.y ), passed = renderers.All( r => r.FaceVelocity && !r.Lighting && !r.MotionBlur ) && effects.SelectMany( e => e.Particles ).All( p => p.Size.y > 0 && p.Size.y <= 10.001f ) } );
			for ( var frame = 0; frame < 240; ++frame ) foreach ( var effect in effects ) effect.Step( 1f / 60f );
			rows.Add( new { check = "instant-burst-does-not-repeat", remaining = effects.Sum( e => e.Particles.Count ), passed = effects.All( e => e.Particles.Count == 0 ) } );
		}
		if ( name == "impact_steam" )
		{
			for ( var frame = 0; frame < 180; ++frame ) foreach ( var effect in effects ) effect.Step( 1f / 60f );
			var youngest = effects.SelectMany( e => e.Particles ).Select( p => p.Age ).DefaultIfEmpty( float.MaxValue ).Min();
			rows.Add( new { check = "continuous-emission-no-restart-gap", youngest, passed = youngest < 0.1f } );
		}
	}
	finally { scene.Destroy(); }
}
// A stationary/parallel particle has no screen-space velocity direction.
// FaceVelocity must preserve its current rotation and still draw finite geometry.
{
	var scene = new Scene();
	using var target = Texture.CreateRenderTarget().WithSize( 256, 256 ).Create();
	try
	{
		var camera = new GameObject( scene, true, "Zero velocity camera" ).AddComponent<CameraComponent>();
		camera.WorldTransform = Transform.Zero; camera.BackgroundColor = Color.Black;
		camera.ZNear = 1; camera.ZFar = 1000; camera.EnablePostProcessing = false;
		var root = new GameObject( scene, true, "Zero velocity particle" ); root.WorldPosition = new Vector3( 128, 0, 0 );
		var effect = root.AddComponent<ParticleEffect>(); effect.ApplyColor = true; effect.Gradient = Color.White;
		effect.ApplyShape = true; effect.Scale = 32f; effect.Lifetime = 10f;
		var renderer = root.AddComponent<ParticleSpriteRenderer>(); renderer.Sprite = Sprite.FromTexture( Texture.White );
		renderer.FaceVelocity = true; renderer.Lighting = false; renderer.Shadows = false;
		var emitter = root.AddComponent<ParticleSphereEmitter>(); emitter.Radius = 0; emitter.Velocity = 0;
		emitter.Burst = 1; emitter.Rate = 0; emitter.Loop = false; emitter.ResetEmitter(); effect.Step( 0.01f );
		foreach ( var velocity in new[] { Vector3.Zero, new Vector3( 100, 0, 0 ) } )
		{
			foreach ( var particle in effect.Particles ) particle.Velocity = velocity;
			var capture = await Capture( scene, camera, target, "velocity-" + (velocity == Vector3.Zero ? "zero" : "parallel") );
			rows.Add( new { check = "face-velocity-" + velocity, capture.Visible, passed = capture.Visible > 100 } );
		}
		effect.Gradient = new Color( 0.02f, 0.02f, 0.02f );
		effect.Tint = Color.White;
		effect.Step( 0.01f );
		var dim = await Capture( scene, camera, target, "sprite-brightness-1" );
		effect.Tint = new Color( 4f, 4f, 4f );
		effect.Step( 0.01f );
		var hdr = await Capture( scene, camera, target, "sprite-brightness-4" );
		rows.Add( new { check = "sprite-hdr-colour", normal = dim.Energy, bright = hdr.Energy, passed = dim.Energy > 0 && hdr.Energy > dim.Energy * 1.2 } );
		var glow = new GameObject( scene, true, "Retail overbright material" );
		var glowArguments = new object[] { glow, mount, Definition( "shield_wall_pilot_CH_ground_hemi" ), 0, null };
		var glowCreated = (bool)factory.Invoke( null, glowArguments );
		var glowTint = glow.GetComponentInChildren<ParticleEffect>()?.Tint ?? Color.Black;
		rows.Add( new { check = "retail-sprite-overbright-factor", tint = glowTint.ToString(), passed = glowCreated && MathF.Abs( glowTint.r - 6f ) < 0.01f } );
	}
	finally { scene.Destroy(); }
}
var output = System.IO.Path.Combine( exchangeDirectory, "particle-geometry-results.json" );
System.IO.File.WriteAllText( output, System.Text.Json.JsonSerializer.Serialize( rows, new System.Text.Json.JsonSerializerOptions { WriteIndented = true } ) );
return new { output, rows };
