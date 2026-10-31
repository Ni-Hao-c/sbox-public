// Exercise the actual non-decal transparent shaders used by shoreline foam.
var rows = new System.Collections.Generic.List<object>();
var scene = new Scene();
using var red = Texture.Create( 1, 1 ).WithData( new byte[] { 255, 0, 0, 255 } ).Finish();
using var target = Texture.CreateRenderTarget().WithSize( 256, 256 ).Create();
float Linear( float x ) => x <= .04045f ? x / 12.92f : MathF.Pow( (x + .055f) / 1.055f, 2.4f );
SceneObject obj = null;
try
{
	var camera = new GameObject( scene, true, "Surface coverage camera" ).AddComponent<CameraComponent>();
	camera.ZNear = 1; camera.ZFar = 1000; camera.FieldOfView = 90; camera.BackgroundColor = Color.Blue; camera.EnablePostProcessing = false;
	foreach ( var shader in new[] { "alpha", "translucent" } )
	foreach ( var (name, layered, alpha, maskValue) in new[] { ("visible", 1f, 0, 0), ("hidden", 1f, 255, 255),
		("half", 1f, 128, 128), ("ordinary-visible", 0f, 255, 0), ("ordinary-hidden", 0f, 0, 255) } )
	{
		using var mask = Texture.Create( 1, 1 ).WithData( new byte[] { (byte)maskValue, 0, 0, 255 } ).Finish();
		var material = Material.Create( "t2-surface-coverage-" + shader + name, "shaders/titanfall2_" + shader + ".shader" );
		material.Set( "g_tAlbedo", red ); material.Set( "g_tOpacity", Texture.White ); material.Set( "g_tEmissive", Texture.Black );
		material.Set( "g_tLayerBlend", mask ); material.Set( "g_flT2Unlit", 1f ); material.Set( "g_flUseVertexAlpha", 1f );
		material.Set( "g_flT2LayeredOpacity", layered ); material.Set( "g_flT2LayerBlendRamp", 1.2f );
		material.Set( "g_flT2LayerBlendUvChannel", 0f );
		var vertices = new Vertex[4];
		for ( var j = 0; j < 4; ++j )
		{
			vertices[j].Position = new Vector3( 128, j == 1 || j == 2 ? 64 : -64, j >= 2 ? 64 : -64 );
			vertices[j].Normal = Vector3.Backward; vertices[j].Color = new Color32( 255, 255, 255, (byte)alpha );
			vertices[j].TexCoord0 = new Vector4( j == 1 || j == 2 ? 1 : 0, j >= 2 ? 0 : 1, 0, 0 );
		}
		var mesh = new Mesh( "t2-surface-coverage", material ); mesh.CreateVertexBuffer( 4, vertices ); mesh.CreateIndexBuffer( 6, new[] { 0, 2, 1, 0, 3, 2 } );
		obj = new SceneObject( scene.SceneWorld, Model.Builder.AddMesh( mesh ).Create() );
		for ( var frame = 0; frame < 3; ++frame ) { await Task.Delay( 40, cancellationToken ); camera.RenderToTexture( target ); }
		using var bitmap = target.GetBitmap(); var pixel = bitmap.GetPixel( 128, 128 );
		var expected = layered > 0 ? 1f - Math.Clamp( 2.2f * alpha / 255f + 1.2f * (maskValue / 255f - 1f), 0, 1 ) : alpha / 255f;
		rows.Add( new { shader, name, expected, actual = new[] { Linear( pixel.r ), Linear( pixel.g ), Linear( pixel.b ) },
			passed = MathF.Abs( Linear( pixel.r ) - expected ) < .02f && MathF.Abs( Linear( pixel.b ) - (1 - expected) ) < .02f && pixel.g < .02f } );
		obj.Delete(); obj = null;
	}
	var output = System.IO.Path.Combine( exchangeDirectory, "surface-coverage-results.json" );
	System.IO.File.WriteAllText( output, System.Text.Json.JsonSerializer.Serialize( rows, new System.Text.Json.JsonSerializerOptions { WriteIndented = true } ) );
	return new { output, rows };
}
finally { obj?.Delete(); scene.Destroy(); }
