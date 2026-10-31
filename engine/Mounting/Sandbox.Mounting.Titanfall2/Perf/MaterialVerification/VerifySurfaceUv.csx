var output = exchangeDirectory;
var results = new System.Collections.Generic.List<object>();
using var red = Texture.Create( 1, 1 ).WithData( new byte[] { 255, 0, 0, 255 } ).Finish();
using var blue = Texture.Create( 1, 1 ).WithData( new byte[] { 0, 0, 255, 255 } ).Finish();
using var normal = Texture.Create( 1, 1 ).WithData( new byte[] { 128, 128, 255, 255 } ).Finish();
using var target = Texture.CreateRenderTarget().WithSize( 256, 256 ).Create();
var scene = new Scene();
try
{
	var cameraObject = new GameObject( scene, true, "Material probe camera" );
	var camera = cameraObject.AddComponent<CameraComponent>();
	camera.WorldPosition = Vector3.Zero;
	camera.WorldRotation = Rotation.Identity;
	camera.FieldOfView = 70;
	camera.ZNear = 1;
	camera.ZFar = 1000;
	camera.BackgroundColor = Color.Green;
	camera.EnablePostProcessing = false;
	foreach ( var (name, channel) in new[] { ("raw", 0f), ("scaled", 1f) } )
	{
		var data = new byte[64 * 4 * 4];
		for ( int y = 0; y < 4; ++y )
		{
			for ( int x = 0; x < 64; ++x )
			{
				var offset = (y * 64 + x) * 4;
				data[offset] = (byte)(x < 32 ? 0 : 255);
				data[offset + 3] = 255;
			}
		}
		using var mask = Texture.Create( 64, 4 ).WithData( data ).Finish();
		var material = Material.Create( "tf2-uv-probe-" + name, "shaders/titanfall2_default.shader" );
		foreach ( var slot in new[] { "g_tAO", "g_tOpacity" } ) material.Set( slot, Texture.White );
		foreach ( var slot in new[] { "g_tGloss", "g_tSpecular", "g_tGloss2", "g_tSpecular2", "g_tEmissive" } ) material.Set( slot, Texture.Black );
		material.Set( "g_tAlbedo", red );
		material.Set( "g_tAlbedo2", blue );
		material.Set( "g_tNormal", normal );
		material.Set( "g_tNormal2", normal );
		material.Set( "g_tLayerBlend", mask );
		material.Set( "g_flT2Layered", 1f );
		material.Set( "g_flT2LayerBlendRamp", 1f );
		material.Set( "g_vT2Uv1RotScale", new Vector4( 8, 0, 0, 8 ) );
		material.Set( "g_vT2SurfaceUvChannels", Vector4.Zero );
		material.Set( "g_vT2SurfaceUvChannels2", Vector4.Zero );
		material.Set( "g_flT2LayerBlendUvChannel", channel );
		material.Set( "g_flT2Unlit", 1f );
		material.Set( "g_vT2AlbedoTint", Vector4.One );
		var vertices = new Vertex[4];
		var positions = new[] { new Vector3( 128, -64, -64 ), new Vector3( 128, 64, -64 ),
			new Vector3( 128, 64, 64 ), new Vector3( 128, -64, 64 ) };
		for ( int j = 0; j < 4; j++ )
		{
			vertices[j].Position = positions[j];
			vertices[j].Normal = new Vector3( -1, 0, 0 );
			vertices[j].Color = new Color32( 255, 255, 255, 128 );
			vertices[j].TexCoord0 = new Vector4( j == 1 || j == 2 ? 1 : 0, j >= 2 ? 0 : 1, 0, 0 );
		}
		var mesh = new Mesh( "tf2-layer-probe", material );
		mesh.CreateVertexBuffer( 4, vertices );
		mesh.CreateIndexBuffer( 6, new int[] { 0, 2, 1, 0, 3, 2 } );
		var model = Model.Builder.WithName( "tf2-layer-probe-" + name ).AddMesh( mesh ).Create();
		var obj = new SceneObject( scene.SceneWorld, model );
		try
		{
			// Let newly uploaded resources and first-use pipelines settle before
			// reading pixels. A single startup frame can still contain the prior draw.
			for ( int frame = 0; frame < 3; frame++ )
			{
				await Task.Delay( 50, cancellationToken );
				camera.RenderToTexture( target );
			}
			using var bitmap = target.GetBitmap();
			var pixel = bitmap.GetPixel( 128, 128 );
			System.IO.File.WriteAllBytes( System.IO.Path.Combine( output, "surface-uv-" + name + ".png" ), bitmap.ToPng() );
			int transitions = 0;
			bool? prior = null;
			for ( int x = 48; x < 208; ++x )
			{
				var colour = bitmap.GetPixel( x, 128 );
				bool blueHalf = colour.b > colour.r;
				if ( prior.HasValue && prior.Value != blueHalf ) transitions++;
				prior = blueHalf;
			}
			bool passed = channel == 0 ? transitions == 1 : transitions >= 5;
			results.Add( new { name, transitions, pixel = new[] { pixel.r, pixel.g, pixel.b }, passed } );
		}
		finally { obj.Delete(); }
	}
}
finally { scene.Destroy(); }
var json = System.Text.Json.JsonSerializer.Serialize( results, new System.Text.Json.JsonSerializerOptions { WriteIndented = true } );
System.IO.File.WriteAllText( System.IO.Path.Combine( output, "surface-uv-check.json" ), json );
return json;

