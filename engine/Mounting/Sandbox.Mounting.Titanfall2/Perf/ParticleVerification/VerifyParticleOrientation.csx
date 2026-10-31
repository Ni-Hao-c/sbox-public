// Exercise the real PCF factory and native sprite batch from side/top cameras.
var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
var mount = Sandbox.Mounting.Directory.Get( "titanfall2" );
var assembly = mount.GetType().Assembly;
var definitionType = assembly.GetType( "Titanfall2.Titanfall2ParticleDefinition" );
var factory = assembly.GetType( "Titanfall2ParticleFactory" ).GetMethod( "TryCreate", flags );
var rows = new System.Collections.Generic.List<object>();
var scene = new Scene();
using var target = Texture.CreateRenderTarget().WithSize( 128, 128 ).Create();
try
{
	var camera = new GameObject( scene, true, "Orientation camera" ).AddComponent<CameraComponent>();
	camera.BackgroundColor = Color.Black; camera.EnablePostProcessing = false; camera.ZNear = 1; camera.ZFar = 1000;
	var update = typeof( SceneSpriteSystem ).GetMethod( "UpdateSprites", flags );
	foreach ( var mode in new[] { 0, 1, 2, 3 } )
	{
		var definition = Activator.CreateInstance( definitionType, true );
		void Set( string name, object value ) => definitionType.GetProperty( name, flags ).SetValue( definition, value );
		Set( "Name", "orientation-" + mode ); Set( "MaterialName", "particle/smoke/smoke_puff_01_db512" );
		Set( "Renderer", Enum.ToObject( assembly.GetType( "Titanfall2.Titanfall2ParticleRenderer" ), 1 ) );
		Set( "MaxParticles", 1 ); Set( "Burst", 1f ); Set( "LifetimeMinimum", 10f ); Set( "LifetimeMaximum", 10f );
		Set( "RadiusMinimum", 32f ); Set( "RadiusMaximum", 32f ); Set( "OrientationType", mode );
		var root = new GameObject( scene, true, "Orientation fixture" ); root.WorldPosition = new Vector3( 128, 0, 0 );
		root.AddComponent<Titanfall2ParticleControlPoints>();
		var args = new object[] { root, mount, definition, 0, null };
		if ( !(bool)factory.Invoke( null, args ) ) throw new Exception( "Factory failed: " + args[4] );
		var effect = root.GetComponentsInChildren<ParticleEffect>().Single();
		var renderer = root.GetComponentsInChildren<ParticleSpriteRenderer>().Single();
		var expectedAlignment = mode == 1 ? ParticleSpriteRenderer.BillboardAlignment.RotateToCamera
			: mode >= 2 ? ParticleSpriteRenderer.BillboardAlignment.Particle : ParticleSpriteRenderer.BillboardAlignment.LookAtCamera;
		rows.Add( new { name = "source-mode-" + mode, passed = renderer.Alignment == expectedAlignment } );
		renderer.Sprite = Sprite.FromTexture( Texture.White ); renderer.Scale = 2; renderer.Lighting = false; renderer.DepthFeather = 0;
		renderer.CameraFadeNear = renderer.CameraFadeFar = 0; renderer.FogStrength = 0;
		effect.Tint = Color.White; effect.Gradient = Color.White; effect.Alpha = 1f;
		effect.Step( .01f );
		if ( mode <= 2 ) foreach ( var top in new[] { false, true } )
		{
			var transform = top ? new Transform( new Vector3( 128, 0, 128 ), Rotation.LookAt( Vector3.Down ) ) : Transform.Zero;
			camera.WorldTransform = transform;
			for ( var frame = 0; frame < 4; ++frame )
			{
				update.Invoke( scene.GetSystem<SceneSpriteSystem>(), null );
				await Task.Delay( 40, cancellationToken );
				camera.RenderToTexture( target, new Sandbox.Rendering.ViewSetup { Transform = transform } );
			}
			using var bitmap = target.GetBitmap();
			var coveredPixels = 0;
			for ( var y = 0; y < 128; ++y ) for ( var x = 0; x < 128; ++x )
				if ( bitmap.GetPixel( x, y ).r > .5f ) coveredPixels++;
			var visible = coveredPixels > 100;
			var expectedVisible = mode == 0 || (mode == 1 ? !top : top);
			rows.Add( new { name = $"mode-{mode}-top-{top}", coveredPixels, visible, expectedVisible, passed = visible == expectedVisible } );
		}
		if ( mode == 2 )
		{
			effect.Roll = 57f;
			for ( var step = 0; step < 3; ++step )
			{
				effect.Step( .1f );
				var particle = effect.Particles.Single();
				rows.Add( new { name = "horizontal-roll-step-" + step, passed = MathF.Abs( particle.Angles.Forward.z ) > .999f } );
			}
			var beforeFlip = effect.Particles.Single().Angles.Forward.z;
			effect.Yaw = 180f; effect.Step( .1f );
			rows.Add( new { name = "horizontal-yaw-flip", passed = beforeFlip * effect.Particles.Single().Angles.Forward.z < -.999f } );
		}
		root.Enabled = false; root.Destroy(); scene.GameTick( 1.0 / 60 );
	}
	var output = System.IO.Path.Combine( exchangeDirectory, "particle-orientation-results.json" );
	System.IO.File.WriteAllText( output, System.Text.Json.JsonSerializer.Serialize( rows, new System.Text.Json.JsonSerializerOptions { WriteIndented = true } ) );
	return new { output, rows };
}
finally { scene.Destroy(); }
