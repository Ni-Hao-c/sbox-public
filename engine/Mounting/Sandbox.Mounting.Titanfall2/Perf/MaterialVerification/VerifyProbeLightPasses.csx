var scene = new Scene();
using var target = Texture.CreateRenderTarget().WithSize( 256, 256 ).Create();
using var emission = Texture.Create( 1, 1 ).WithData( new byte[] { 64, 32, 16, 255 } ).Finish();
SceneObject obj = null;
var rows = new System.Collections.Generic.List<object>();
float Linear( float x ) => x <= .04045f ? x / 12.92f : MathF.Pow( (x + .055f) / 1.055f, 2.4f );
try
{
	var camera = new GameObject( scene, true, "Probe light-pass camera" ).AddComponent<CameraComponent>();
	camera.WorldTransform = Transform.Zero; camera.EnablePostProcessing = false; camera.ZNear = 1; camera.ZFar = 2000; camera.BackgroundColor = Color.Black;
	var sunlight = new GameObject( scene, true, "Probe sunlight" ).AddComponent<DirectionalLight>();
	sunlight.LightColor = new Color( .2f, .2f, .2f ); sunlight.SkyColor = Color.Black; sunlight.Shadows = true; sunlight.ShadowCascadeCount = 1;
	var localObject = new GameObject( scene, true, "Probe local light" ); localObject.WorldPosition = new Vector3( 64, 0, 0 );
	var local = localObject.AddComponent<PointLight>(); local.LightColor = new Color( .12f, .08f, .04f ); local.Radius = 512; local.Shadows = false; local.Enabled = false;
	var material = Material.Create( "t2-probe-light-pass", "shaders/titanfall2_default.shader" );
	foreach ( var slot in new[] { "g_tAlbedo", "g_tAO", "g_tOpacity" } ) material.Set( slot, Texture.White );
	foreach ( var slot in new[] { "g_tGloss", "g_tSpecular" } ) material.Set( slot, Texture.Black );
	material.Set( "g_tEmissive", emission ); material.Set( "g_vT2AlbedoTint", Vector4.One ); material.Set( "g_vT2EmissiveTint", Vector4.One );
	material.Set( "g_flHasNormalMap", 0f ); material.Set( "g_flT2Unlit", 0f ); material.Set( "g_flT2HasEnvironment", 0f );
	var vertices = new Vertex[4];
	for ( var i = 0; i < 4; ++i ) { vertices[i].Position = new Vector3( 128, i == 1 || i == 2 ? 64 : -64, i >= 2 ? 64 : -64 ); vertices[i].Normal = Vector3.Backward; vertices[i].Color = Color.White; }
	var mesh = new Mesh( "probe-light-pass", material ); mesh.CreateVertexBuffer( 4, vertices ); mesh.CreateIndexBuffer( 6, new[] { 0, 2, 1, 0, 3, 2 } );
	obj = new SceneObject( scene.SceneWorld, Model.Builder.WithName( "probe-light-pass" ).AddMesh( mesh ).Create() ); obj.Flags.CastShadows = false;
	var samples = new System.Collections.Generic.Dictionary<string, Vector3>();
	foreach ( var test in new[] { "sun-full", "sun-half", "sun-hidden", "local-with-sun-hidden", "emission-only" } )
	{
		var visibility = test == "sun-full" ? 1f : test == "sun-half" ? .5f : 0f;
		local.Enabled = test == "local-with-sun-hidden";
		var dc = test == "emission-only" ? 0f : .025f;
		new Titanfall2ProbeData { Red = new Vector4( 0, 0, 0, dc ), Green = new Vector4( 0, 0, 0, dc ), Blue = new Vector4( 0, 0, 0, dc ),
			SkyDirectionSunVisibility = new Vector4( 0, 0, 0, visibility ), Metadata = new Vector4( 1, 0, 0, 0 ) }.Apply( obj, Color.Black );
		for ( var f = 0; f < 4; ++f ) { await Task.Delay( 50, cancellationToken ); camera.RenderToTexture( target ); }
		using var bitmap = target.GetBitmap(); var pixel = bitmap.GetPixel( 128, 128 );
		samples[test] = new Vector3( Linear( pixel.r ), Linear( pixel.g ), Linear( pixel.b ) );
	}
	var full = samples["sun-full"]; var half = samples["sun-half"]; var hidden = samples["sun-hidden"];
	rows.Add( new { test = "sun-visibility-scales-direct-light", actual = half.ToString(), expected = ((full + hidden) * .5f).ToString(), passed = full.Distance( hidden ) > .02f && half.Distance( (full + hidden) * .5f ) < .006f } );
	var localValue = samples["local-with-sun-hidden"];
	rows.Add( new { test = "dynamic-local-light-survives-hidden-sun", actual = localValue.ToString(), expected = "brighter than " + hidden, passed = localValue.x - hidden.x > .01f } );
	var expectedEmission = new Vector3( Linear( 64f / 255 ), Linear( 32f / 255 ), Linear( 16f / 255 ) );
	rows.Add( new { test = "dark-probe-preserves-emission", actual = samples["emission-only"].ToString(), expected = expectedEmission.ToString(), passed = samples["emission-only"].Distance( expectedEmission ) < .004f } );
	var output = System.IO.Path.Combine( exchangeDirectory, "probe-light-passes-results.json" );
	System.IO.File.WriteAllText( output, System.Text.Json.JsonSerializer.Serialize( new { rows, samples = samples.ToDictionary( p => p.Key, p => p.Value.ToString() ) }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true } ) );
	return new { output, rows, samples = samples.ToDictionary( p => p.Key, p => p.Value.ToString() ) };
}
finally { obj?.Delete(); scene.Destroy(); }
