var root = exchangeDirectory;
var scene = new Scene();
var owned = new System.Collections.Generic.List<SceneObject>();
using var target = Texture.CreateRenderTarget().WithSize( 256, 256 ).Create();
var data = new byte[8 * 8 * 4];
for ( int y = 0; y < 8; y++ ) for ( int x = 0; x < 8; x++ )
{
	int k = (y * 8 + x) * 4;
	data[k] = (byte)((x + y) % 2 == 0 ? 255 : 0);
	data[k + 2] = (byte)(255 - data[k]); data[k + 3] = 255;
}
using var checker = Texture.Create( 8, 8 ).WithData( data ).Finish();
using var brown = Texture.Create( 1, 1 ).WithData( new byte[] { 100, 60, 40, 255 } ).Finish();
SceneObject Plane( Material material, byte alpha, float depth, Vector3 normal )
{
	var vertices = new Vertex[4];
	var points = new[] { new Vector3( depth, -128, -128 ), new Vector3( depth, 128, -128 ), new Vector3( depth, 128, 128 ), new Vector3( depth, -128, 128 ) };
	for ( int j = 0; j < 4; j++ )
	{
		vertices[j].Position = points[j]; vertices[j].Normal = normal;
		vertices[j].Color = new Color32( 255, 255, 255, alpha );
		vertices[j].TexCoord0 = new Vector4( j == 1 || j == 2 ? 1 : 0, j >= 2 ? 0 : 1, 0, 0 );
	}
	var mesh = new Mesh( "water-composite-probe", material );
	mesh.CreateVertexBuffer( 4, vertices ); mesh.CreateIndexBuffer( 6, new[] { 0, 2, 1, 0, 3, 2 } );
	return new SceneObject( scene.SceneWorld, Model.Builder.WithName( "water-composite-probe-" + Guid.NewGuid() ).AddMesh( mesh ).Create() );
}
try
{
	var camera = new GameObject( scene, true, "Water composite probe" ).AddComponent<CameraComponent>();
	camera.FieldOfView = 70; camera.ZNear = 1; camera.ZFar = 1000; camera.EnablePostProcessing = false;
	var background = Material.Create( "water-composite-background", "shaders/titanfall2_default.shader" );
	background.Set( "g_tAlbedo", checker ); background.Set( "g_flT2Unlit", 1f );
	background.Set( "g_tAO", Texture.White ); background.Set( "g_tOpacity", Texture.White ); background.Set( "g_tEmissive", Texture.Black );
	owned.Add( Plane( background, 255, 180, new Vector3( -1, 0, 0 ) ) );
	camera.RenderToTexture( target );
	var original = target.GetPixels();
	using ( var bmp = target.GetBitmap() ) System.IO.File.WriteAllBytes( root + "/water-probe-background.png", bmp.ToPng() );
	var results = new System.Collections.Generic.List<object>();
	foreach ( byte alpha in new byte[] { 0, 128, 255 } )
	{
		var water = Material.Create( "water-composite-alpha-" + alpha, "shaders/titanfall2_water.shader" );
		water.Set( "g_tAlbedo", brown ); water.Set( "g_tAO", Texture.White ); water.Set( "g_tOpacity", Texture.White ); water.Set( "g_tEmissive", Texture.Black );
		water.Set( "g_tGloss", Texture.Black ); water.Set( "g_tSpecular", Texture.Black );
		water.Set( "g_tDistortion", Texture.Transparent ); water.Set( "g_tDistortion2", Texture.Transparent );
		water.Set( "g_flUseVertexAlpha", 1f ); water.Set( "g_vT2WaterFlow", Vector4.Zero );
		var obj = Plane( water, alpha, 128, Vector3.Up );
		obj.Flags.IsOpaque = false; obj.Flags.IsTranslucent = true; obj.Flags.WantsFrameBufferCopy = true;
		try
		{
			for ( int frame = 0; frame < 3; frame++ )
			{
				await Task.Delay( 50, cancellationToken );
				camera.RenderToTexture( target );
			}
			var actual = target.GetPixels();
			double difference = 0; int count = 0;
			for ( int y = 32; y < 224; y++ ) for ( int x = 32; x < 224; x++ )
			{
				int k = y * 256 + x;
				difference += Math.Abs( actual[k].r - original[k].r ) + Math.Abs( actual[k].g - original[k].g ) + Math.Abs( actual[k].b - original[k].b ); count += 3;
			}
			using var bmp = target.GetBitmap();
			System.IO.File.WriteAllBytes( root + "/water-probe-alpha-" + alpha + ".png", bmp.ToPng() );
			results.Add( new { alpha, meanDifference = difference / count, passed = alpha != 0 || difference / count < 2 } );
		}
		finally { obj.Delete(); }
	}
	var report = System.Text.Json.JsonSerializer.Serialize( results, new System.Text.Json.JsonSerializerOptions { WriteIndented = true } );
	System.IO.File.WriteAllText( root + "/water-composite-check.json", report ); return report;
}
finally { foreach ( var obj in owned ) obj.Delete(); scene.Destroy(); }

