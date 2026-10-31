// GPU checks for the retail CBufUberStatic distance and angular opacity terms.
// Uses a controlled normal independently of the quad's geometry to test grazing angles.
var rows = new System.Collections.Generic.List<object>();
using var target = Texture.CreateRenderTarget().WithSize( 256, 256 ).Create();
using var red = Texture.Create( 1, 1 ).WithData( new byte[] { 128, 0, 0, 255 } ).Finish();
float Linear( float v ) => v <= 0.04045f ? v / 12.92f : MathF.Pow( (v + 0.055f) / 1.055f, 2.4f );
foreach ( var shader in new[] { "godray", "translucent", "alpha", "additive" } )
{
	var scene = new Scene();
	try
	{
		var camera = new GameObject( scene, true, "Opacity fade camera" ).AddComponent<CameraComponent>();
		camera.WorldTransform = Transform.Zero; camera.ZNear = 1; camera.ZFar = 5000;
		camera.BackgroundColor = Color.Blue; camera.EnablePostProcessing = false;
		var depthMaterial = Material.Create( "opacity-depth-background", "shaders/titanfall2_default.shader" );
		depthMaterial.Set( "g_flT2Unlit", 1f ); depthMaterial.Set( "g_vT2AlbedoTint", new Vector4( 0, 0, 1, 1 ) );
		depthMaterial.Set( "g_tAlbedo", Texture.White );
		depthMaterial.Set( "g_tEmissive", Texture.Black ); depthMaterial.Set( "g_tGloss", Texture.Black );
		depthMaterial.Set( "g_tSpecular", Texture.Black ); depthMaterial.Set( "g_tAO", Texture.White );
		var depthVertices = new Vertex[4];
		for ( var j = 0; j < 4; ++j )
		{
			depthVertices[j].Position = new Vector3( 160, j == 1 || j == 2 ? 128 : -128, j >= 2 ? 128 : -128 );
			depthVertices[j].Normal = Vector3.Backward;
			depthVertices[j].Color = new Color32( 255, 255, 255, 255 );
		}
		var depthMesh = new Mesh( "opacity-depth-background", depthMaterial );
		depthMesh.CreateVertexBuffer( 4, depthVertices ); depthMesh.CreateIndexBuffer( 6, new[] { 0, 2, 1, 0, 3, 2 } );
		var depthObject = new SceneObject( scene.SceneWorld, Model.Builder.WithName( "opacity-depth-background" ).AddMesh( depthMesh ).Create() );
		depthObject.RenderingEnabled = false;
		var reference = 0f;
		foreach ( var (name, distance, scale, bias, cosine, inner, outer, exponent, alpha, expected, back, doubleSided) in new[]
		{
			("neutral", 128f, 0f, 1f, 1f, 1f, 1f, 1f, (byte)255, 1f, false, false),
			("retail-near", 128f, 1f / 470f, -30f / 470f, 1f, 1f, 1f, 1f, (byte)255, 98f / 470f, false, false),
			("retail-far", 600f, 1f / 470f, -30f / 470f, 1f, 1f, 1f, 1f, (byte)255, 1f, false, false),
			("cloud-angle", 128f, 0f, 1f, 0.5f, 1f, -0.5f, 1f, (byte)255, 0.25f, false, false),
			("cloud-grazing", 128f, 0f, 1f, 0.2f, 1f, -0.5f, 1f, (byte)255, 0f, false, false),
			("godray-angle", 128f, 0f, 1f, 0.5f, 1f, 0f, 2f, (byte)255, 0.75f, false, false),
			("byte-alpha-one", 128f, 0f, 1f, 1f, 1f, 1f, 1f, (byte)1, 1f / 255f, false, false),
			("back-face-culled", 128f, 0f, 1f, 1f, 1f, 1f, 1f, (byte)255, 0f, true, false),
			("back-face-enabled", 128f, 0f, 1f, 1f, 1f, 1f, 1f, (byte)255, 1f, true, true),
			("front-face-enabled", 128f, 0f, 1f, 1f, 1f, 1f, 1f, (byte)255, 1f, false, true),
			("atmosphere-scale", 128f, 0f, 1f, 1f, 1f, 1f, 1f, (byte)255, shader == "godray" ? 1f : 0.15f, false, false),
			("depth-intersection", 128f, 0f, 1f, 1f, 1f, 1f, 1f, (byte)255, shader == "godray" ? 1f : 0.5f, false, false)
		} )
		{
			var material = Material.Create( "opacity-fade-" + shader + "-" + name, "shaders/titanfall2_" + shader + ".shader" );
			material.SetFeature( "F_RENDER_BACKFACES", doubleSided ? 1 : 0 );
			material.Set( "g_tAlbedo", red ); material.Set( "g_tNormal", Texture.White );
			material.Set( "g_tOpacity", Texture.White ); material.Set( "g_tAO", Texture.White );
			material.Set( "g_tGloss", Texture.Black ); material.Set( "g_tSpecular", Texture.Black );
			material.Set( "g_tEmissive", Texture.Black ); material.Set( "g_tDetail", Texture.White );
			material.Set( "g_tDistortion", Texture.White );
			material.Set( "g_flT2Unlit", 1f ); material.Set( "g_flUseVertexAlpha", 1f );
			material.Set( "g_flGodrayDepthFeather", 0f );
			material.Set( "g_flT2AtmosphereOpacityScale", name == "atmosphere-scale" ? 0.15f : 1f );
			material.Set( "g_flT2AtmosphereDepthFeather", name == "depth-intersection" ? 64f : 0f );
			depthObject.RenderingEnabled = name == "depth-intersection";
			material.Set( "g_flT2AlphaDistanceFadeScale", scale ); material.Set( "g_flT2AlphaDistanceFadeBias", bias );
			material.Set( "g_vT2AlphaEdgeFade", new Vector4( exponent, inner, outer, 0 ) );
			var positions = new[] { new Vector3( distance, -64, -64 ), new Vector3( distance, 64, -64 ),
				new Vector3( distance, 64, 64 ), new Vector3( distance, -64, 64 ) };
			var vertices = new Vertex[4];
			for ( var index = 0; index < vertices.Length; ++index )
			{
				vertices[index].Position = positions[index];
				vertices[index].Normal = new Vector3( -cosine, MathF.Sqrt( 1f - cosine * cosine ), 0 );
				vertices[index].Tangent = new Vector4( 0, 0, 1, 1 );
				vertices[index].Color = new Color32( 255, 255, 255, alpha );
				vertices[index].TexCoord0 = new Vector4( index == 1 || index == 2 ? 1 : 0, index >= 2 ? 0 : 1, 0, 0 );
			}
			var mesh = new Mesh( "opacity-fade", material );
			mesh.CreateVertexBuffer( 4, vertices );
			mesh.CreateIndexBuffer( 6, back ? new[] { 0, 1, 2, 0, 2, 3 } : new[] { 0, 2, 1, 0, 3, 2 } );
			var model = Model.Builder.WithName( "opacity-fade-" + shader + "-" + name ).AddMesh( mesh ).Create();
			var obj = new SceneObject( scene.SceneWorld, model );
			obj.Flags.IsOpaque = false; obj.Flags.IsTranslucent = true;
			try
			{
				for ( var frame = 0; frame < 4; ++frame ) { await Task.Delay( 25, cancellationToken ); camera.RenderToTexture( target ); }
				using var bitmap = target.GetBitmap();
				var pixel = bitmap.GetPixel( 128, 128 );
				var radiance = Linear( pixel.r );
				if ( name == "neutral" ) reference = radiance;
				var actual = reference > 0 ? radiance / reference : 0;
				rows.Add( new { shader, name, expected, actual, passed = reference > 0.01f && MathF.Abs( actual - expected ) < 0.035f } );
				System.IO.File.WriteAllBytes( System.IO.Path.Combine( exchangeDirectory, shader + "-" + name + ".png" ), bitmap.ToPng() );
			}
			finally { obj.Delete(); }
		}
	}
	finally { scene.Destroy(); }
}
var output = System.IO.Path.Combine( exchangeDirectory, "opacity-fade-results.json" );
System.IO.File.WriteAllText( output, System.Text.Json.JsonSerializer.Serialize( rows, new System.Text.Json.JsonSerializerOptions { WriteIndented = true } ) );
return new { output, rows };
