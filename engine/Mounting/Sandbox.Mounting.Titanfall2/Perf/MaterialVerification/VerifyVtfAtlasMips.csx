// Exercises real animated VTF atlas mips and the particle frame extraction path.
var decoder = typeof( Titanfall2Mount ).Assembly.GetType( "VtfTextureDecoder" );
var create = decoder.GetMethod( "TryCreate", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public );
var colours = new ushort[] { 0xF800, 0x07E0, 0x001F, 0xFFE0, 0xF81F, 0x07FF };
Vector3 Rgb( ushort value ) => new( ((value >> 11) & 31) / 31f, ((value >> 5) & 63) / 63f, (value & 31) / 31f );
byte[] Source()
{
	using var stream = new System.IO.MemoryStream();
	using var writer = new System.IO.BinaryWriter( stream );
	var header = new byte[80];
	BitConverter.GetBytes( 0x00465456u ).CopyTo( header, 0 );
	BitConverter.GetBytes( 7u ).CopyTo( header, 4 ); BitConverter.GetBytes( 5u ).CopyTo( header, 8 );
	BitConverter.GetBytes( 80u ).CopyTo( header, 12 );
	BitConverter.GetBytes( (ushort)16 ).CopyTo( header, 16 ); BitConverter.GetBytes( (ushort)16 ).CopyTo( header, 18 );
	BitConverter.GetBytes( 0u ).CopyTo( header, 20 ); BitConverter.GetBytes( (ushort)2 ).CopyTo( header, 24 );
	BitConverter.GetBytes( 13 ).CopyTo( header, 52 ); header[56] = 5; BitConverter.GetBytes( -1 ).CopyTo( header, 57 );
	writer.Write( header );
	for ( var mip = 4; mip >= 0; --mip ) for ( var frame = 0; frame < 2; ++frame )
	{
		var colour = colours[(frame + mip) % 6];
		var blocks = Math.Max( 1, (16 >> mip) / 4 );
		for ( var block = 0; block < blocks * blocks; ++block ) { writer.Write( colour ); writer.Write( colour ); writer.Write( 0u ); }
	}
	return stream.ToArray();
}
var scene = new Scene();
using var target = Texture.CreateRenderTarget().WithSize( 128, 128 ).Create();
var rows = new System.Collections.Generic.List<object>();
var textures = new System.Collections.Generic.List<Texture>();
SceneObject obj = null;
try
{
	var camera = new GameObject( scene, true, "VTF atlas mip camera" ).AddComponent<CameraComponent>();
	camera.WorldTransform = Transform.Zero; camera.EnablePostProcessing = false; camera.ZNear = 1; camera.ZFar = 1000;
	{
		var args = new object[] { Source(), true, null, null };
		if ( !(bool)create.Invoke( null, args ) ) throw new Exception( args[3]?.ToString() );
		var atlas = (Texture)args[2]; var frames = new[] { atlas, atlas }; textures.Add( atlas );
		for ( var frame = 0; frame < frames.Length; ++frame )
		foreach ( var mip in new[] { 0, 1, 2, 7 } )
		{
			var material = Material.Create( "t2-vtf-cube-mip", "shaders/tests/titanfall2_mip_probe.shader" );
			material.Set( "g_tAlbedo", atlas ); material.Set( "g_flMipLevel", (float)mip );
			var vertices = new Vertex[4];
			for ( var v = 0; v < 4; ++v ) { vertices[v].Position = new Vector3( 128, v == 1 || v == 2 ? 100 : -100, v >= 2 ? 100 : -100 ); vertices[v].Normal = Vector3.Backward; vertices[v].Color = Color.White; vertices[v].TexCoord0 = new Vector4( .5f, (frame + .5f) / 2, 0, 0 ); }
			var mesh = new Mesh( "t2-vtf-cube-mip", material ); mesh.CreateVertexBuffer( 4, vertices ); mesh.CreateIndexBuffer( 6, new[] { 0, 2, 1, 0, 3, 2 } );
			obj = new SceneObject( scene.SceneWorld, Model.Builder.WithName( "t2-vtf-cube-mip" ).AddMesh( mesh ).Create() );
			for ( var f = 0; f < 3; ++f ) { await Task.Delay( 20, cancellationToken ); camera.RenderToTexture( target ); }
			using var bitmap = target.GetBitmap(); var pixel = bitmap.GetPixel( 64, 64 );
			float Linear( float x ) => x <= .04045f ? x / 12.92f : MathF.Pow( (x + .055f) / 1.055f, 2.4f );
			var actual = new Vector3( Linear( pixel.r ), Linear( pixel.g ), Linear( pixel.b ) );
			var expected = Rgb( colours[(frame + Math.Min( mip, 2 )) % 6] );
			rows.Add( new { name = "atlas-mip", frame, mip, frames[frame].Mips, actual = actual.ToString(), expected = expected.ToString(),
				passed = frames.Length == 2 && atlas.Mips == 3 && actual.Distance( expected ) < .012f } );
			obj.Delete(); obj = null;
		}
	}
	var split = decoder.GetMethod( "TryCreateFrames", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic );
	var splitArgs = new object[] { Source(), null, null };
	if ( !(bool)split.Invoke( null, splitArgs ) ) throw new Exception( splitArgs[2]?.ToString() );
	var particleFrames = (Texture[])splitArgs[1]; textures.AddRange( particleFrames );
	for ( var frame = 0; frame < particleFrames.Length; ++frame )
	{
		using var bitmap = particleFrames[frame].GetBitmap(); var pixel = bitmap.GetPixel( 8, 8 );
		var expected = Rgb( colours[frame] ); var actual = new Vector3( pixel.r, pixel.g, pixel.b );
		rows.Add( new { name = "particle-frame-retains-largest-mip", frame, passed = particleFrames.Length == 2
			&& particleFrames[frame].Width == 16 && particleFrames[frame].Height == 16 && actual.Distance( expected ) < .012f } );
	}
	var output = System.IO.Path.Combine( exchangeDirectory, "vtf-atlas-mip-results.json" );
	System.IO.File.WriteAllText( output, System.Text.Json.JsonSerializer.Serialize( rows, new System.Text.Json.JsonSerializerOptions { WriteIndented = true } ) );
	return new { output, rows };
}
finally { obj?.Delete(); scene.Destroy(); foreach ( var texture in textures ) texture.Dispose(); }

