var scene = new Scene();
using var target = Texture.CreateRenderTarget().WithSize( 128, 128 ).Create();
using var visibility = Texture.Create( 1, 1 ).WithData( new byte[] { 64, 128, 192, 255 } ).Finish();
using var edgeVisibility = Texture.Create( 2, 1 ).WithData( new byte[] { 64, 0, 0, 255, 128, 0, 0, 255 } ).Finish();
using var edgeChannels = Texture.Create( 2, 1, ImageFormat.I8 ).WithData( new byte[] { 1, 8 } ).Finish();
using var emission = Texture.Create( 1, 1 ).WithData( new byte[] { 32, 16, 8, 255 } ).Finish();
var rows = new System.Collections.Generic.List<object>();
SceneObject obj = null;
float Linear( float x ) => x <= .04045f ? x / 12.92f : MathF.Pow( (x + .055f) / 1.055f, 2.4f );
try
{
	var camera = new GameObject( scene, true, "World sun lightmap camera" ).AddComponent<CameraComponent>();
	camera.WorldTransform = Transform.Zero; camera.EnablePostProcessing = false; camera.ZNear = 1; camera.ZFar = 1000;
	var sunlight = new GameObject( scene, true, "World sun" ).AddComponent<DirectionalLight>();
	sunlight.LightColor = new Color( .12f, .1f, .08f ); sunlight.SkyColor = Color.Black; sunlight.Shadows = true; sunlight.ShadowCascadeCount = 1;
	var localObject = new GameObject( scene, true, "Local light" ); localObject.WorldPosition = new Vector3( 64, 0, 0 );
	var local = localObject.AddComponent<PointLight>(); local.LightColor = new Color( .12f, .08f, .04f ); local.Radius = 512; local.Shadows = false; local.Enabled = false;
	var samples = new System.Collections.Generic.Dictionary<string, Vector3>();
	foreach ( var test in new[] { "full", "red", "green", "blue", "hidden", "fallback", "local", "edge-left", "edge-right" } )
	{
		var mask = test == "red" ? 1 : test == "green" ? 2 : test == "blue" ? 4 : test == "hidden" || test == "local" ? 0 : 8;
		using var channels = Texture.Create( 1, 1, ImageFormat.I8 ).WithData( new byte[] { (byte)mask } ).Finish();
		var edge = test.StartsWith( "edge-" ); var uv = test == "edge-left" ? .375f : test == "edge-right" ? .625f : .5f;
		local.Enabled = test == "local";
		var material = Material.Create( "t2-world-sun", "shaders/titanfall2_world_lightmapped.shader" );
		foreach ( var slot in new[] { "g_tAlbedo", "g_tAO" } ) material.Set( slot, Texture.White );
		foreach ( var slot in new[] { "g_tGloss", "g_tSpecular", "g_tLightmapSkyA" } ) material.Set( slot, Texture.Black );
		material.Set( "g_tLightmapSkyB", Texture.Black ); material.Set( "g_vT2LightmapSkyColor", Vector3.Zero );
		material.Set( "g_flHasNormalMap", 0f ); material.Set( "g_flT2SpecularWorkflow", 1f );
		material.Set( "g_tEmissive", emission ); material.Set( "g_tLightmapSunChannels", edge ? edgeChannels : channels );
		material.Set( "g_tLightmapRealTimeB", edge ? edgeVisibility : visibility ); material.Set( "g_flT2HasSunLightmap", test == "fallback" ? 0f : 1f );
		var vertices = new Vertex[4];
		for ( var v = 0; v < 4; ++v )
		{
			vertices[v].Position = new Vector3( 128, v == 1 || v == 2 ? 80 : -80, v >= 2 ? 80 : -80 );
			vertices[v].Normal = Vector3.Backward; vertices[v].Color = Color.White;
			vertices[v].TexCoord1 = new Vector4( uv, .5f, 0, 0 );
		}
		var mesh = new Mesh( "t2-world-sun", material ); mesh.CreateVertexBuffer( 4, vertices ); mesh.CreateIndexBuffer( 6, new[] { 0, 2, 1, 0, 3, 2 } );
		obj = new SceneObject( scene.SceneWorld, Model.Builder.WithName( "t2-world-sun" ).AddMesh( mesh ).Create() );
		for ( var f = 0; f < 4; ++f ) { await Task.Delay( 30, cancellationToken ); camera.RenderToTexture( target ); }
		using var bitmap = target.GetBitmap(); var pixel = bitmap.GetPixel( 64, 64 );
		samples[test] = new Vector3( Linear( pixel.r ), Linear( pixel.g ), Linear( pixel.b ) );
		obj.Delete(); obj = null;
	}
	var baseline = samples["full"]; var hidden = samples["hidden"];
	foreach ( var (test, factor) in new[] { ("red", 64f / 255), ("green", 128f / 255), ("blue", 192f / 255), ("fallback", 1f), ("edge-left", 80f / 255), ("edge-right", 1f) } )
	{
		var expected = hidden + (baseline - hidden) * factor;
		rows.Add( new { test, actual = samples[test].ToString(), expected = expected.ToString(), passed = baseline.Distance( hidden ) > .01f && samples[test].Distance( expected ) < .005f } );
	}
	rows.Add( new { test = "sun-mask-preserves-local-light", passed = samples["local"].x - hidden.x > .01f } );
	var expectedEmission = new Vector3( Linear( 32f / 255 ), Linear( 16f / 255 ), Linear( 8f / 255 ) );
	rows.Add( new { test = "sun-mask-preserves-emission", passed = hidden.Distance( expectedEmission ) < .003f } );
	var output = System.IO.Path.Combine( exchangeDirectory, "world-sun-lightmap-results.json" );
	System.IO.File.WriteAllText( output, System.Text.Json.JsonSerializer.Serialize( new { rows, samples = samples.ToDictionary( p => p.Key, p => p.Value.ToString() ) }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true } ) );
	return new { output, rows };
}
finally { obj?.Delete(); scene.Destroy(); }
