// Independent colour/coverage checks for the authored UnlitTwoTexture variant.
var scene = new Scene();
using var target = Texture.CreateRenderTarget().WithSize( 128, 128 ).Create();
var rows = new System.Collections.Generic.List<object>();
SceneObject obj = null;
float Linear( float value ) => value <= .04045f ? value / 12.92f : MathF.Pow( (value + .055f) / 1.055f, 2.4f );
try
{
	var camera = new GameObject( scene, true, "Legacy grid verification" ).AddComponent<CameraComponent>();
	camera.ZNear = 1; camera.ZFar = 1000; camera.FieldOfView = 90; camera.BackgroundColor = Color.Black; camera.EnablePostProcessing = false;
	foreach ( var variant in new[] { "additive", "alpha", "opaque" } )
	foreach ( var (name, first, second, multiply, vertexAlpha, expected) in new[] {
		("disjoint-colours-multiply-to-black", new byte[] { 255, 0, 0, 255 }, new byte[] { 0, 255, 0, 255 }, true, 1f, Vector3.Zero),
		("red-times-yellow-retains-red", new byte[] { 255, 0, 0, 255 }, new byte[] { 255, 255, 0, 255 }, true, 1f, new Vector3( 1, 0, 0 )),
		("yellow-times-cyan-retains-green", new byte[] { 255, 255, 0, 255 }, new byte[] { 0, 255, 255, 255 }, true, 1f, new Vector3( 0, 1, 0 )),
		("both-alpha-and-vertex-alpha-modulate", new byte[] { 255, 255, 255, 128 }, new byte[] { 255, 255, 255, 128 }, true, 128f / 255, new Vector3( MathF.Pow( 128f / 255, 3 ) )),
		("zero-vertex-alpha-hides-grid", new byte[] { 255, 255, 255, 255 }, new byte[] { 255, 255, 255, 255 }, true, 0f, Vector3.Zero),
		("layer-alpha-applied-once", new byte[] { 255, 255, 255, 255 }, new byte[] { 255, 255, 255, 255 }, true, .5f, new Vector3( .125f )),
		("authored-tints-modulate-both-layers", new byte[] { 255, 255, 255, 255 }, new byte[] { 255, 255, 255, 255 }, true, 1f, new Vector3( .5f, .5f, .125f )),
		("legacy-sum-variant-retains-both-layers", new byte[] { 255, 0, 0, 255 }, new byte[] { 0, 255, 0, 255 }, false, 1f, new Vector3( 1, 1, 0 )) } )
	{
		using var firstTexture = Texture.Create( 1, 1 ).WithData( first ).Finish();
		using var secondTexture = Texture.Create( 1, 1 ).WithData( second ).Finish();
		var shader = variant == "alpha" ? "titanfall2_vmt_unlit_two_texture.shader" : "titanfall2_vmt_unlit_two_texture_" + variant + ".shader";
		var material = Material.Create( "legacy-grid-" + variant + name, "shaders/" + shader );
		material.Set( "g_tTexture1", firstTexture ); material.Set( "g_tTexture2", secondTexture );
		material.Set( "g_flVmtHasTexture1", 1f ); material.Set( "g_flVmtHasTexture2", 1f );
		material.Set( "g_flVmtMultiplyLayers", multiply ? 1f : 0f );
		if ( variant != "opaque" ) material.Set( "g_flVmtUseVertexAlpha", 1f );
		if ( name == "layer-alpha-applied-once" )
		{
			material.Set( "g_flVmtLayerAlpha1", .5f ); material.Set( "g_flVmtLayerAlpha2", .5f );
		}
		if ( name == "authored-tints-modulate-both-layers" )
		{
			material.Set( "g_vVmtColor1", new Vector4( .5f, 1, .25f, 1 ) );
			material.Set( "g_vVmtColor2", new Vector4( 1, .5f, .5f, 1 ) );
		}
		var vertices = new Vertex[4];
		for ( var j = 0; j < 4; ++j )
		{
			vertices[j].Position = new Vector3( 128, j == 1 || j == 2 ? 100 : -100, j >= 2 ? 100 : -100 );
			vertices[j].Normal = Vector3.Backward; vertices[j].Color = new Color( 1, 1, 1, vertexAlpha );
			vertices[j].TexCoord0 = new Vector4( .25f, .25f, 0, 0 );
		}
		var mesh = new Mesh( name, material ); mesh.CreateVertexBuffer( 4, vertices ); mesh.CreateIndexBuffer( 6, new[] { 0, 2, 1, 0, 3, 2 } );
		obj = new SceneObject( scene.SceneWorld, Model.Builder.AddMesh( mesh ).Create() );
		var view = new Sandbox.Rendering.ViewSetup { Transform = Transform.Zero, FieldOfView = 90, ZNear = 1, ZFar = 1000, EnableUI = false };
		for ( var frame = 0; frame < 3; ++frame ) { await Task.Delay( 40, cancellationToken ); camera.RenderToTexture( target, view ); }
		using var bitmap = target.GetBitmap(); var pixel = bitmap.GetPixel( 64, 64 );
		var actual = new Vector3( Linear( pixel.r ), Linear( pixel.g ), Linear( pixel.b ) );
		var expectedColour = variant == "opaque" && (name.Contains( "alpha" ) || name == "zero-vertex-alpha-hides-grid") ? Vector3.One : expected;
		rows.Add( new { variant, name, actual = actual.ToString(), expected = expectedColour.ToString(), passed = (actual - expectedColour).Length < .025f } );
		obj.Delete(); obj = null;
	}
	foreach ( var (name, shader) in new[] {
		("world/utility/holo_map_grid_orange", "vmt_unlit_two_texture_additive"),
		("world/utility/holo_map_grid_orange_shootable", "vmt_unlit_two_texture_additive"),
		("world/utility/holo_map_grid_light_blue", "vmt_unlit_two_texture_additive"),
		("world/utility/holo_map_grid_light_blue_shootable", "vmt_unlit_two_texture_additive"),
		("models/holo_screens/tv_imc_map_01", "vmt_unlit_two_texture"),
		("models/holo_screens/tv_imc_saver_logo", "vmt_unlit_two_texture"),
		("models/holo_screens/tv_imc_screen_graphs_scroll_01", "additive") } )
	{
		var material = Material.Load( "mount://titanfall2/" + name + ".vmat" );
		var isTwoTexture = shader.StartsWith( "vmt_unlit" );
		rows.Add( new { name, material.ShaderName, passed = material.ShaderName == "shaders/titanfall2_" + shader + ".shader"
			&& (!isTwoTexture || material.GetVector4( "g_flVmtMultiplyLayers" ).x == 1) } );
	}
	var output = System.IO.Path.Combine( exchangeDirectory, "legacy-grid-results.json" );
	System.IO.File.WriteAllText( output, System.Text.Json.JsonSerializer.Serialize( rows, new System.Text.Json.JsonSerializerOptions { WriteIndented = true } ) );
	return new { output, rows };
}
finally { obj?.Delete(); scene.Destroy(); }
