// Lyrtrans must expose the underlying surface, including when vertex alpha is
// one. Ordinary decals retain their independent vertex-alpha multiplication.
var results = new System.Collections.Generic.List<object>();
using var red = Texture.Create( 1, 1 ).WithData( new byte[] { 255, 0, 0, 255 } ).Finish();
using var target = Texture.CreateRenderTarget().WithSize( 256, 256 ).Create();
var scene = new Scene();
try
{
	var camera = new GameObject( scene, true, "Layer opacity camera" ).AddComponent<CameraComponent>();
	camera.WorldPosition = Vector3.Zero;
	camera.WorldRotation = Rotation.Identity;
	camera.ZNear = 1;
	camera.ZFar = 1000;
	camera.FieldOfView = 70;
	camera.BackgroundColor = Color.Blue;
	camera.EnablePostProcessing = false;
	var cases = new[] { ("layer-visible", 1f, 0, 0), ("layer-hidden", 1f, 255, 255),
		("layer-half", 1f, 128, 128), ("decal-visible", 0f, 255, 255), ("decal-hidden", 0f, 0, 0) };
	foreach ( var (name, layered, alpha, maskValue) in cases )
	{
		using var mask = Texture.Create( 1, 1 ).WithData( new byte[] { (byte)maskValue, 0, 0, 255 } ).Finish();
		var material = Material.Create( "layer-opacity-" + name, "shaders/titanfall2_decal_translucent.shader" );
		material.Set( "g_tAlbedo", Texture.Black );
		material.Set( "g_tNormal", Texture.White );
		material.Set( "g_tGloss", Texture.Black );
		material.Set( "g_tSpecular", Texture.Black );
		material.Set( "g_tAO", Texture.White );
		material.Set( "g_tOpacity", Texture.White );
		material.Set( "g_tEmissive", red );
		material.Set( "g_tDetail", Texture.White );
		material.Set( "g_tDistortion", Texture.White );
		material.Set( "g_tLayerBlend", mask );
		material.Set( "g_flT2LayeredOpacity", layered );
		material.Set( "g_flT2LayerBlendRamp", 1.61f );
		material.Set( "g_flT2LayerBlendUvChannel", 0f );
		var vertices = new Vertex[4];
		var positions = new[] { new Vector3( 128, -64, -64 ), new Vector3( 128, 64, -64 ),
			new Vector3( 128, 64, 64 ), new Vector3( 128, -64, 64 ) };
		for ( int j = 0; j < vertices.Length; ++j )
		{
			vertices[j].Position = positions[j];
			vertices[j].Normal = new Vector3( -1, 0, 0 );
			vertices[j].Color = new Color32( 255, 255, 255, (byte)alpha );
			vertices[j].TexCoord0 = new Vector4( j == 1 || j == 2 ? 1 : 0, j >= 2 ? 0 : 1, 0, 0 );
		}
		var mesh = new Mesh( "layer-opacity", material );
		mesh.CreateVertexBuffer( vertices.Length, vertices );
		mesh.CreateIndexBuffer( 6, new[] { 0, 2, 1, 0, 3, 2 } );
		var model = Model.Builder.WithName( "layer-opacity-" + name ).AddMesh( mesh ).Create();
		var obj = new SceneObject( scene.SceneWorld, model );
		try
		{
			for ( int frame = 0; frame < 3; ++frame )
			{
				await Task.Delay( 50, cancellationToken );
				camera.RenderToTexture( target );
			}
			using var bitmap = target.GetBitmap();
			var pixel = bitmap.GetPixel( 128, 128 );
			var expected = layered > 0f
				? 1f - Math.Clamp( 2.61f * alpha / 255f + 1.61f * (maskValue / 255f - 1f), 0f, 1f )
				: alpha / 255f;
			var passed = expected < 0.01f ? pixel.r < 0.02f && pixel.b > 0.9f
				: expected > 0.99f ? pixel.r > 0.9f && pixel.b < 0.02f
				: pixel.r > 0.3f && pixel.b > 0.3f && pixel.g < 0.02f;
			System.IO.File.WriteAllBytes( System.IO.Path.Combine( exchangeDirectory, name + ".png" ), bitmap.ToPng() );
			results.Add( new { name, expected, pixel = new[] { pixel.r, pixel.g, pixel.b }, passed } );
		}
		finally { obj.Delete(); }
	}
}
finally { scene.Destroy(); }
return results;
