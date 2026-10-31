// An atlas with four distinct colours must follow TEXCOORD1, independently of UV0.
using var atlas = Texture.Create( 2, 2 ).WithData( new byte[] {
	64, 0, 0, 255, 0, 64, 0, 255,
	0, 0, 64, 255, 64, 64, 64, 255 } ).Finish();
using var direction = Texture.Create( 1, 1 ).WithData( new byte[] { 127, 127, 127, 255 } ).Finish();
using var target = Texture.CreateRenderTarget().WithSize( 256, 256 ).Create();
var scene = new Scene();
SceneObject obj = null;
try
{
	var camera = new GameObject( scene, true, "Lightmap UV probe" ).AddComponent<CameraComponent>();
	camera.FieldOfView = 90; camera.ZNear = 1; camera.ZFar = 1000; camera.EnablePostProcessing = false;
	var material = Material.Create( "tf2-lightmap-uv-probe", "shaders/titanfall2_world_lightmapped.shader" );
	material.Set( "g_tAlbedo", Texture.White ); material.Set( "g_tAO", Texture.White );
	material.Set( "g_tGloss", Texture.Black ); material.Set( "g_tSpecular", Texture.Black );
	material.Set( "g_tEmissive", Texture.Black ); material.Set( "g_tLightmapSkyA", atlas );
	material.Set( "g_tLightmapSkyB", direction ); material.Set( "g_vT2LightmapSkyColor", Vector3.Zero );
	var vertices = new Vertex[4];
	var positions = new[] { new Vector3( 128, -128, -128 ), new Vector3( 128, 128, -128 ),
		new Vector3( 128, 128, 128 ), new Vector3( 128, -128, 128 ) };
	for ( int j = 0; j < 4; j++ )
	{
		vertices[j].Position = positions[j]; vertices[j].Normal = new Vector3( -1, 0, 0 );
		vertices[j].Color = new Color32( 255, 255, 255, 255 );
		vertices[j].TexCoord0 = Vector4.Zero;
		vertices[j].TexCoord1 = new Vector4( j == 1 || j == 2 ? 1 : 0, j >= 2 ? 0 : 1, 0, 0 );
	}
	var mesh = new Mesh( "tf2-lightmap-uv-probe", material ); mesh.CreateVertexBuffer( 4, vertices );
	mesh.CreateIndexBuffer( 6, new[] { 0, 2, 1, 0, 3, 2 } );
	obj = new SceneObject( scene.SceneWorld, Model.Builder.WithName( "tf2-lightmap-uv-probe" ).AddMesh( mesh ).Create() );
		for ( int frame = 0; frame < 3; frame++ )
		{
			await Task.Delay( 50, cancellationToken );
			camera.RenderToTexture( target );
		}
	using var bitmap = target.GetBitmap();
	var pixels = new[] { bitmap.GetPixel( 64, 64 ), bitmap.GetPixel( 192, 64 ), bitmap.GetPixel( 64, 192 ), bitmap.GetPixel( 192, 192 ) };
	bool red = pixels.Any( p => p.r > 0.2f && p.r > p.g * 2 && p.r > p.b * 2 );
	bool green = pixels.Any( p => p.g > 0.2f && p.g > p.r * 2 && p.g > p.b * 2 );
	bool blue = pixels.Any( p => p.b > 0.2f && p.b > p.r * 2 && p.b > p.g * 2 );
	bool white = pixels.Any( p => Math.Min( p.r, Math.Min( p.g, p.b ) ) > 0.2f );
	System.IO.File.WriteAllBytes( System.IO.Path.Combine( exchangeDirectory, "lightmap-uv-probe.png" ), bitmap.ToPng() );
	return new { passed = red && green && blue && white, pixels = pixels.Select( p => new[] { p.r, p.g, p.b } ).ToArray() };
}
finally { obj?.Delete(); scene.Destroy(); }
