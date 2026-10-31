// Sample on the GPU: compressed-mip CPU readback alone cannot distinguish an
// upload problem from a readback conversion problem below a full BC block.
var assembly = Sandbox.Mounting.Directory.Get( "titanfall2" ).GetType().Assembly;
var dataType = assembly.GetType( "RpakTextureData" );
var flags = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic;
byte[] Block( ushort colour ) => new byte[] { (byte)colour, (byte)(colour >> 8), (byte)colour, (byte)(colour >> 8), 0, 0, 0, 0 };
var data = Block( 0xF800 ).Concat( Block( 0x07E0 ) ).Concat( Block( 0x001F ) )
	.Concat( Enumerable.Range( 0, 4 ).SelectMany( _ => Block( 0xFFE0 ) ) ).ToArray();
var source = Activator.CreateInstance( dataType, new object[] { 8, 8, ImageFormat.DXT1, 4, 1, false, data } );
var args = new object[] { source, null, null };
var create = assembly.GetType( "RpakTextureDecoder" ).GetMethods( flags )
	.Single( method => method.Name == "TryCreate" && method.GetParameters().Length == 3 );
if ( !(bool)create.Invoke( null, args ) ) throw new Exception( args[2]?.ToString() );
using var texture = (Texture)args[1];
using var target = Texture.CreateRenderTarget().WithSize( 256, 256 ).Create();
var results = new System.Collections.Generic.List<object>();
var scene = new Scene();
try
{
	var camera = new GameObject( scene, true, "Texture mip camera" ).AddComponent<CameraComponent>();
	camera.WorldPosition = Vector3.Zero;
	camera.WorldRotation = Rotation.Identity;
	camera.FieldOfView = 70;
	camera.ZNear = 1;
	camera.ZFar = 1000;
	camera.BackgroundColor = Color.Cyan;
	camera.EnablePostProcessing = false;
	for ( int mip = 0; mip < 4; ++mip )
	{
		var material = Material.Create( "texture-mip-" + mip, "shaders/tests/titanfall2_mip_probe.shader" );
		material.Set( "g_tAlbedo", texture );
		material.Set( "g_flMipLevel", (float)mip );
		var positions = new[] { new Vector3( 128, -80, -80 ), new Vector3( 128, 80, -80 ),
			new Vector3( 128, 80, 80 ), new Vector3( 128, -80, 80 ) };
		var vertices = new Vertex[4];
		for ( int j = 0; j < vertices.Length; ++j )
		{
			vertices[j].Position = positions[j];
			vertices[j].Normal = new Vector3( -1, 0, 0 );
			vertices[j].Color = Color.White;
			vertices[j].TexCoord0 = new Vector4( j == 1 || j == 2 ? 1 : 0, j >= 2 ? 0 : 1, 0, 0 );
		}
		var mesh = new Mesh( "texture-mip", material );
		mesh.CreateVertexBuffer( vertices.Length, vertices );
		mesh.CreateIndexBuffer( 6, new[] { 0, 2, 1, 0, 3, 2 } );
		var model = Model.Builder.WithName( "texture-mip-" + mip ).AddMesh( mesh ).Create();
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
			var passed = texture.Mips == 2 && (mip == 0
				? pixel.r > 0.99f && pixel.g > 0.99f && pixel.b < 0.01f
				: pixel.b > 0.99f && pixel.r < 0.01f && pixel.g < 0.01f);
			System.IO.File.WriteAllBytes( System.IO.Path.Combine( exchangeDirectory, "texture-mip-" + mip + ".png" ), bitmap.ToPng() );
			results.Add( new { mip, texture.Mips, pixel = new[] { pixel.r, pixel.g, pixel.b }, passed } );
		}
		finally { obj.Delete(); }
	}
}
finally { scene.Destroy(); }
return results;
