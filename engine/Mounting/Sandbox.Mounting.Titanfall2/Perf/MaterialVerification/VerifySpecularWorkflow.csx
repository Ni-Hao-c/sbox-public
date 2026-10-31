// A coloured/white F0 map must not erase baked diffuse as a metalness mask.
var scene = new Scene();
using var target = Texture.CreateRenderTarget().WithSize( 256, 256 ).Create();
using var albedo = Texture.Create( 1, 1 ).WithData( new byte[] { 188, 137, 89, 255 } ).Finish();
using var emission = Texture.Create( 1, 1 ).WithData( new byte[] { 16, 32, 48, 255 } ).Finish();
using var lightmap = Texture.Create( 1, 1 ).WithData( new byte[] { 26, 26, 26, 63 } ).Finish();
using var direction = Texture.Create( 1, 1 ).WithData( new byte[] { 127, 127, 127, 255 } ).Finish();
var rows = new System.Collections.Generic.List<object>();
SceneObject obj = null;
float Linear( float x ) => x <= .04045f ? x / 12.92f : MathF.Pow( (x + .055f) / 1.055f, 2.4f );
try
{
	var camera = new GameObject( scene, true, "Specular workflow camera" ).AddComponent<CameraComponent>();
	camera.WorldTransform = Transform.Zero; camera.EnablePostProcessing = false; camera.ZNear = 1; camera.ZFar = 1000; camera.BackgroundColor = Color.Black;
	foreach ( var shader in new[] { "default", "cutout", "world_lightmapped" } )
	foreach ( var f0 in new[] { new byte[] { 0, 0, 0, 255 }, new byte[] { 255, 128, 32, 255 }, new byte[] { 255, 255, 255, 255 } } )
	foreach ( var emissive in new[] { false, true } )
	{
		using var specular = Texture.Create( 1, 1 ).WithData( f0 ).Finish();
		var material = Material.Create( "t2-specular-workflow-" + shader, "shaders/titanfall2_" + shader + ".shader" );
		material.Set( "g_flT2SpecularWorkflow", 1f ); material.Set( "g_tAlbedo", albedo );
		material.Set( "g_tSpecular", specular ); material.Set( "g_tGloss", Texture.Black ); material.Set( "g_tAO", Texture.White );
		material.Set( "g_tOpacity", Texture.White ); material.Set( "g_tEmissive", emissive ? emission : Texture.Black );
		material.Set( "g_vT2AlbedoTint", Vector4.One ); material.Set( "g_vT2SpecularTint", Vector4.One );
		material.Set( "g_vT2EmissiveTint", Vector4.One ); material.Set( "g_flHasNormalMap", 0f );
		material.Set( "g_tLightmapSkyA", lightmap ); material.Set( "g_tLightmapSkyB", direction ); material.Set( "g_vT2LightmapSkyColor", Vector3.Zero );
		var vertices = new Vertex[4];
		for ( var i = 0; i < 4; ++i ) { vertices[i].Position = new Vector3( 128, i == 1 || i == 2 ? 64 : -64, i >= 2 ? 64 : -64 ); vertices[i].Normal = Vector3.Backward; vertices[i].Color = Color.White; }
		var mesh = new Mesh( "t2-specular-workflow", material ); mesh.CreateVertexBuffer( 4, vertices ); mesh.CreateIndexBuffer( 6, new[] { 0, 2, 1, 0, 3, 2 } );
		obj = new SceneObject( scene.SceneWorld, Model.Builder.WithName( "t2-specular-workflow" ).AddMesh( mesh ).Create() );
		if ( shader != "world_lightmapped" )
		{
			var dc = 26f / 255;
			new Titanfall2ProbeData { Red = new Vector4( 0, 0, 0, dc ), Green = new Vector4( 0, 0, 0, dc ), Blue = new Vector4( 0, 0, 0, dc ), Metadata = new Vector4( 1, 0, 0, 0 ) }.Apply( obj, Color.Black );
		}
		for ( var frame = 0; frame < 4; ++frame ) { await Task.Delay( 40, cancellationToken ); camera.RenderToTexture( target ); }
		using var bitmap = target.GetBitmap(); var pixel = bitmap.GetPixel( 128, 128 );
		var actual = new Vector3( Linear( pixel.r ), Linear( pixel.g ), Linear( pixel.b ) );
		var expected = new Vector3( Linear( 188f / 255 ), Linear( 137f / 255 ), Linear( 89f / 255 ) ) * (26f / 255);
		if ( emissive ) expected += new Vector3( Linear( 16f / 255 ), Linear( 32f / 255 ), Linear( 48f / 255 ) );
		rows.Add( new { shader, f0 = string.Join( ",", f0.Take( 3 ) ), emissive, actual = actual.ToString(), expected = expected.ToString(), passed = actual.Distance( expected ) < .004f } );
		obj.Delete(); obj = null;
	}
	var output = System.IO.Path.Combine( exchangeDirectory, "specular-workflow-results.json" );
	System.IO.File.WriteAllText( output, System.Text.Json.JsonSerializer.Serialize( rows, new System.Text.Json.JsonSerializerOptions { WriteIndented = true } ) );
	return new { output, rows };
}
finally { obj?.Delete(); scene.Destroy(); }
