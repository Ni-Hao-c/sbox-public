var rows = new System.Collections.Generic.List<object>();
var scene = new Scene();
using var target = Texture.CreateRenderTarget().WithSize( 128, 128 ).Create();
float Linear( float v ) => v <= .04045f ? v / 12.92f : MathF.Pow( (v + .055f) / 1.055f, 2.4f );
float Gamma( float v ) => v <= .0031308f ? v * 12.92f : 1.055f * MathF.Pow( v, 1f / 2.4f ) - .055f;
var configure = typeof( Titanfall2ColorCorrection ).GetMethod( "Configure", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic );
var stages = new System.Collections.Concurrent.ConcurrentDictionary<string, int>();
SceneObject fixture = null;
using var sampleTexture = Texture.Create( 1, 1 ).WithData( new byte[] { 51, 102, 153, 255 } ).Finish();
try
{
	var camera = new GameObject( scene, true, "Color lookup camera" ).AddComponent<CameraComponent>();
	camera.WorldTransform = Transform.Zero; camera.EnablePostProcessing = true; camera.ZNear = 1; camera.ZFar = 1000;
	camera.BackgroundColor = Color.Black;
	var hookField = typeof( CameraComponent ).GetField( "executeCommandListsHook", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic );
	var postProcess = typeof( CameraComponent ).GetProperty( "PostProcess", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic ).GetValue( camera );
	var postStage = postProcess.GetType().GetMethod( "OnRenderStage" );
	Action<Sandbox.Rendering.Stage, SceneCamera> hook = ( stage, cam ) =>
	{
		stages.AddOrUpdate( stage.ToString(), 1, ( key, count ) => count + 1 );
		postStage.Invoke( postProcess, new object[] { stage } );
	};
	hookField.SetValue( camera, hook );
	var material = Material.Create( "lookup-fixture", "shaders/titanfall2_default.shader" );
	material.Set( "g_flT2Unlit", 1f ); material.Set( "g_tAlbedo", sampleTexture ); material.Set( "g_vT2AlbedoTint", Vector4.One );
	material.Set( "g_tEmissive", Texture.Black ); material.Set( "g_tAO", Texture.White ); material.Set( "g_tOpacity", Texture.White );
	var vertices = new Vertex[4];
	for ( var i = 0; i < 4; ++i ) { vertices[i].Position = new Vector3( 128, i == 1 || i == 2 ? 64 : -64, i >= 2 ? 64 : -64 ); vertices[i].Normal = Vector3.Backward; vertices[i].Color = Color.White; }
	var mesh = new Mesh( "lookup-fixture", material ); mesh.CreateVertexBuffer( 4, vertices ); mesh.CreateIndexBuffer( 6, new[] { 0, 2, 1, 0, 3, 2 } );
	fixture = new SceneObject( scene.SceneWorld, Model.Builder.WithName( "lookup-fixture" ).AddMesh( mesh ).Create() ); fixture.Flags.CastShadows = false;
	async Task<Vector3> Render()
	{
		scene.GameTick( 1.0 / 60 );
		// This independent fixture scene is not part of the game's normal tick.
		// Build the actual post-process command lists explicitly before rendering.
		var system = scene.GetSystem<PostProcessSystem>();
		typeof( PostProcessSystem ).GetMethod( "UpdateCamera", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic )
			.Invoke( system, new object[] { camera } );
		typeof( Scene ).GetMethod( "PreRender", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic ).Invoke( scene, null );
		var callbacks = (System.Collections.IList)typeof( Scene ).GetField( "renderThreadEventTargets", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic ).GetValue( scene );
		if ( !callbacks.Contains( system ) ) callbacks.Add( system );
		for ( var frame = 0; frame < 5; ++frame ) { await Task.Delay( 40, cancellationToken ); camera.RenderToTexture( target, new Sandbox.Rendering.ViewSetup { EnablePostprocessing = true } ); }
		using var bitmap = target.GetBitmap(); var p = bitmap.GetPixel( 64, 64 ); return new Vector3( Linear( p.r ), Linear( p.g ), Linear( p.b ) );
	}
	var baseline = await Render();
	var control = camera.GameObject.AddComponent<ColorAdjustments>(); control.Brightness = 0;
	var blackControl = await Render(); control.Destroy();
	rows.Add( new { name = "built-in-postprocessing-positive-control", actual = blackControl.ToString(), passed = blackControl.Length < .005f } );
	rows.Add( new { name = "fixture-is-visible-and-has-three-distinct-channels", passed = baseline.x > .01f && baseline.y > baseline.x && baseline.z > baseline.y && baseline.z < .8f } );
	var identity = new byte[32 * 32 * 32 * 3]; var swapped = new byte[identity.Length];
	for ( var b = 0; b < 32; ++b ) for ( var g = 0; g < 32; ++g ) for ( var r = 0; r < 32; ++r )
	{
		var index = (r + g * 32 + b * 1024) * 3;
		identity[index] = swapped[index + 2] = (byte)MathF.Round( r * 255f / 31 );
		identity[index + 1] = swapped[index + 1] = (byte)MathF.Round( g * 255f / 31 );
		identity[index + 2] = swapped[index] = (byte)MathF.Round( b * 255f / 31 );
	}
	var tests = new System.Collections.Generic.List<(string name, byte[] raw)> { ("identity", identity), ("swap-red-blue", swapped) };
	var directory = @"C:\Users\Administrator\.codex\visualizations\2026\10\24\01a16c2c-16d3-7f51-b93e-2cde2bd4796a\tf2-lighting-calibration";
	foreach ( var map in new[] { "mp_angel_city", "mp_forwardbase_kodai", "mp_homestead" } ) tests.Add( (map, System.IO.File.ReadAllBytes( directory + "/" + map + "-correction.raw" )) );
	Vector3 Evaluate( byte[] raw, Vector3 linear )
	{
		var c = new Vector3( Gamma( linear.x ), Gamma( linear.y ), Gamma( linear.z ) ) * 31;
		var floor = new Vector3( MathF.Floor( c.x ), MathF.Floor( c.y ), MathF.Floor( c.z ) ); var fraction = c - floor; var result = Vector3.Zero;
		for ( var z = 0; z < 2; ++z ) for ( var y = 0; y < 2; ++y ) for ( var x = 0; x < 2; ++x )
		{
			var index = ((int)Math.Clamp( floor.x + x, 0, 31 ) + (int)Math.Clamp( floor.y + y, 0, 31 ) * 32 + (int)Math.Clamp( floor.z + z, 0, 31 ) * 1024) * 3;
			var weight = (x == 0 ? 1 - fraction.x : fraction.x) * (y == 0 ? 1 - fraction.y : fraction.y) * (z == 0 ? 1 - fraction.z : fraction.z);
			result += new Vector3( raw[index], raw[index + 1], raw[index + 2] ) * (weight / 255);
		}
		return new Vector3( Linear( result.x ), Linear( result.y ), Linear( result.z ) );
	}
	var effect = camera.GameObject.AddComponent<Titanfall2ColorCorrection>();
	foreach ( var (name, raw) in tests )
	{
		configure.Invoke( effect, new object[] { raw, name, 1f } );
		var actual = await Render(); var expected = Evaluate( raw, baseline );
		var post = typeof( CameraComponent ).GetProperty( "PostProcess", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic ).GetValue( camera );
		var layers = (System.Collections.IDictionary)post.GetType().GetField( "Layers" ).GetValue( post );
		var attrs = (RenderAttributes)typeof( BasePostProcess ).GetField( "Attributes", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic ).GetValue( effect );
		var shader = (Material)typeof( Titanfall2ColorCorrection ).GetField( "_shader", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic ).GetValue( null );
		rows.Add( new { name, actual = actual.ToString(), expected = expected.ToString(), layers = layers.Count, weight = attrs.GetFloat( "T2ColorLutWeight" ), shader = shader.ShaderName, passed = actual.Distance( expected ) < .008f && (name != "swap-red-blue" || actual.Distance( baseline ) > .1f) } );
		effect.Weight = 0; var zero = await Render();
		rows.Add( new { name = name + "-zero-weight", passed = zero.Distance( baseline ) < .005f } );
	}
	// Different lattice cells, near-black values and saturated channels must all
	// agree with an independent CPU trilinear lookup, including partial weight.
	foreach ( var sample in new[] { new byte[] { 9, 47, 113, 255 }, new byte[] { 231, 82, 17, 255 }, new byte[] { 77, 192, 247, 255 } } )
	{
		effect.Weight = 0;
		sampleTexture.Update( sample );
		var sampleBaseline = await Render();
		foreach ( var (name, raw) in tests )
		{
			foreach ( var weight in new[] { .35f, 1f } )
			{
				configure.Invoke( effect, new object[] { raw, name, weight } );
				var actual = await Render();
				var expected = Vector3.Lerp( sampleBaseline, Evaluate( raw, sampleBaseline ), weight );
				rows.Add( new { name = $"{name}-rgb-{sample[0]}-{sample[1]}-{sample[2]}-weight-{weight}", actual = actual.ToString(), expected = expected.ToString(), passed = actual.Distance( expected ) < .008f } );
			}
		}
	}
	sampleTexture.Update( new byte[] { 51, 102, 153, 255 } );
	configure.Invoke( effect, new object[] { swapped, "swap-red-blue", 1f } );
	var swappedExpected = Evaluate( swapped, baseline );
	effect.Enabled = false;
	rows.Add( new { name = "disabled-component", passed = (await Render()).Distance( baseline ) < .005f } );
	effect.Enabled = true;
	camera.EnablePostProcessing = false;
	rows.Add( new { name = "camera-postprocessing-disabled", passed = (await Render()).Distance( baseline ) < .005f } );
	camera.EnablePostProcessing = true;
	// Clone serialized properties into the fixture scene, then destroy the source
	// so its GPU texture cannot accidentally satisfy the clone's binding.
	var cloneParent = new GameObject( scene, true, "LUT clone parent" );
	GameObject clone;
	using ( scene.Push() ) clone = camera.GameObject.Clone( new CloneConfig( Transform.Zero, cloneParent ) );
	var clonedEffect = clone.GetComponent<Titanfall2ColorCorrection>();
	rows.Add( new { name = "clone-keeps-encoded-source", passed = clonedEffect.EncodedLut == effect.EncodedLut && clonedEffect.SourcePath == effect.SourcePath } );
	var clonedCamera = clone.GetComponent<CameraComponent>();
	clonedCamera.Destroy();
	// Use a volume so the cloned component is evaluated by the original camera.
	var volume = clone.AddComponent<PostProcessVolume>();
	volume.SceneVolume = new Sandbox.Volumes.SceneVolume { Type = Sandbox.Volumes.SceneVolume.VolumeTypes.Infinite };
	effect.Destroy();
	rows.Add( new { name = "clone-volume-restores-texture-after-source-destroy", passed = (await Render()).Distance( swappedExpected ) < .008f } );
	volume.BlendWeight = .5f;
	rows.Add( new { name = "volume-half-weight", passed = (await Render()).Distance( Vector3.Lerp( baseline, swappedExpected, .5f ) ) < .008f } );
	volume.BlendWeight = 0;
	rows.Add( new { name = "volume-zero-weight", passed = (await Render()).Distance( baseline ) < .005f } );
	var output = System.IO.Path.Combine( exchangeDirectory, "color-correction-results.json" );
	System.IO.File.WriteAllText( output, System.Text.Json.JsonSerializer.Serialize( new { baseline = baseline.ToString(), rows }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true } ) );
	return new { output, rows, stages, postProcessEnabled = typeof( PostProcessSystem ).GetProperty( "EnablePostProcess", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic ).GetValue( null ) };
}
finally { fixture?.Delete(); scene.Destroy(); }

