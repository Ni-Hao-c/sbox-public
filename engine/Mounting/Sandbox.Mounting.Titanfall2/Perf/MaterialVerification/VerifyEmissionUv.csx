// A scanline UV must modulate emission without collapsing its image or albedo.
var scene = new Scene();
using var target = Texture.CreateRenderTarget().WithSize( 256, 256 ).Create();
var colours = new byte[][] { new byte[] { 255, 0, 0, 255 }, new byte[] { 0, 255, 0, 255 }, new byte[] { 0, 0, 255, 255 }, new byte[] { 255, 255, 0, 255 } };
var bytes = new byte[16 * 16 * 4]; var multiplyBytes = new byte[bytes.Length];
for ( var y = 0; y < 16; ++y ) for ( var x = 0; x < 16; ++x )
{
	colours[(y >= 8 ? 2 : 0) + (x >= 8 ? 1 : 0)].CopyTo( bytes, (y * 16 + x) * 4 );
	var value = (byte)(y < 8 ? 255 : 0);
	new byte[] { value, value, value, 255 }.CopyTo( multiplyBytes, (y * 16 + x) * 4 );
}
using var image = Texture.Create( 16, 16 ).WithData( bytes ).Finish();
using var multiply = Texture.Create( 16, 16 ).WithData( multiplyBytes ).Finish();
var rows = new System.Collections.Generic.List<object>();
SceneObject obj = null;
try
{
	var camera = new GameObject( scene, true, "Emission UV camera" ).AddComponent<CameraComponent>();
	camera.ZNear = 1; camera.ZFar = 1000; camera.FieldOfView = 90; camera.BackgroundColor = Color.Black; camera.EnablePostProcessing = false;
	foreach ( var shader in new[] { "default", "cutout", "alpha", "translucent" } )
	foreach ( var mode in new[] { "albedo", "emission", "multiply-white", "multiply-black" } )
	{
		var material = Material.Create( "t2-emission-uv-" + shader + mode, "shaders/titanfall2_" + shader + ".shader" );
		material.Set( "g_tAlbedo", mode == "albedo" ? image : Texture.Black );
		material.Set( "g_tOpacity", Texture.White ); material.Set( "g_tEmissive", mode == "albedo" ? Texture.Black : image );
		material.Set( "g_tEmissiveMultiply", multiply ); material.Set( "g_flT2Unlit", 1f );
		material.Set( "g_vT2SurfaceUvChannels", Vector4.Zero );
		material.Set( "g_vT2AuxUvChannels", Vector4.Zero );
		material.Set( "g_vT2Uv1RotScale", new Vector4( 1, 0, 0, 0 ) );
		material.Set( "g_vT2Uv1Translate", new Vector4( 0, mode == "multiply-black" ? .75f : .25f, 0, 0 ) );
		material.Set( "g_flT2EmissiveMultiplyUvChannel", 1f );
		material.Set( "g_flT2HasEmissiveMultiply", mode.StartsWith( "multiply" ) ? 1f : 0f );
		var vertices = new Vertex[4];
		for ( var j = 0; j < 4; ++j )
		{
			vertices[j].Position = new Vector3( 128, j == 1 || j == 2 ? 64 : -64, j >= 2 ? 64 : -64 );
			vertices[j].Normal = Vector3.Backward; vertices[j].Color = Color.White;
			vertices[j].TexCoord0 = new Vector4( j == 1 || j == 2 ? 1 : 0, j >= 2 ? 0 : 1, 0, 0 );
		}
		var mesh = new Mesh( "t2-emission-uv", material ); mesh.CreateVertexBuffer( 4, vertices ); mesh.CreateIndexBuffer( 6, new[] { 0, 2, 1, 0, 3, 2 } );
		obj = new SceneObject( scene.SceneWorld, Model.Builder.AddMesh( mesh ).Create() );
		var view = new Sandbox.Rendering.ViewSetup { Transform = Transform.Zero, FieldOfView = 90, ZNear = 1, ZFar = 1000, EnableUI = false };
		for ( var frame = 0; frame < 3; ++frame ) { await Task.Delay( 40, cancellationToken ); camera.RenderToTexture( target, view ); }
		using var bitmap = target.GetBitmap();
		var pixels = new[] { bitmap.GetPixel( 160, 96 ), bitmap.GetPixel( 96, 96 ), bitmap.GetPixel( 160, 160 ), bitmap.GetPixel( 96, 160 ) };
		var expected = mode == "multiply-black" ? new[] { Color.Black, Color.Black, Color.Black, Color.Black } : new[] { Color.Red, Color.Green, Color.Blue, Color.Yellow };
		rows.Add( new { shader, mode, pixels = pixels.Select( c => new[] { c.r, c.g, c.b } ).ToArray(),
			passed = pixels.Zip( expected ).All( pair => new Vector3( pair.First.r - pair.Second.r, pair.First.g - pair.Second.g, pair.First.b - pair.Second.b ).Length < .03f ) } );
		obj.Delete(); obj = null;
	}
	var output = System.IO.Path.Combine( exchangeDirectory, "emission-uv-results.json" );
	System.IO.File.WriteAllText( output, System.Text.Json.JsonSerializer.Serialize( rows, new System.Text.Json.JsonSerializerOptions { WriteIndented = true } ) );
	return new { output, rows };
}
finally { obj?.Delete(); scene.Destroy(); }
