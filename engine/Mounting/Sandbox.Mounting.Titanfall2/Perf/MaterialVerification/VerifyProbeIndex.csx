var scene = new Scene();
using var target = Texture.CreateRenderTarget().WithSize( 384, 384 ).Create();
using var probes = new GpuBuffer( 3, 80 );
using var indices = new GpuBuffer<int>( 3 );
var rows = new System.Collections.Generic.List<object>();
var colours = new[] { new Vector3( .1f, 0, 0 ), new Vector3( 0, .1f, 0 ), new Vector3( 0, 0, .1f ) };
var coefficients = new Vector4[15];
for ( var p = 0; p < 3; ++p )
{
	coefficients[p * 5] = new Vector4( 0, 0, 0, colours[p].x );
	coefficients[p * 5 + 1] = new Vector4( 0, 0, 0, colours[p].y );
	coefficients[p * 5 + 2] = new Vector4( 0, 0, 0, colours[p].z );
	coefficients[p * 5 + 4] = new Vector4( 1, 0, 0, 0 );
}
probes.SetData<Vector4>( coefficients );
SceneCustomObject drawing = null;
try
{
	var camera = new GameObject( scene, true, "Probe index camera" ).AddComponent<CameraComponent>();
	camera.WorldTransform = Transform.Zero; camera.EnablePostProcessing = false;
	camera.FieldOfView = 90; camera.ZNear = 1; camera.ZFar = 2000; camera.BackgroundColor = Color.Black;
	var material = Material.Create( "t2-probe-index-test", "shaders/tests/titanfall2_probe_index.shader" );
	var vertices = new Vertex[4];
	for ( var i = 0; i < 4; ++i )
	{
		vertices[i].Position = new Vector3( 256, i == 1 || i == 2 ? 24 : -24, i >= 2 ? 24 : -24 );
		vertices[i].Normal = Vector3.Backward; vertices[i].Color = Color.White;
	}
	var mesh = new Mesh( "probe-index-test", material ); mesh.CreateVertexBuffer( 4, vertices ); mesh.CreateIndexBuffer( 6, new[] { 0, 2, 1, 0, 3, 2 } );
	var model = Model.Builder.WithName( "probe-index-test" ).AddMesh( mesh ).Create();
	var orders = new[] { new[] { 2, 0, 1 }, new[] { 1, 2, 0 } };
	var transforms = Enumerable.Range( 0, 3 ).Select( r => Enumerable.Range( 0, 3 )
		.Select( c => new Transform( new Vector3( 0, (c - 1) * 96, (r - 1) * 96 ) ) ).ToArray() ).ToArray();
	drawing = new SceneCustomObject( scene.SceneWorld ) { Bounds = new BBox( new Vector3( -1024 ), new Vector3( 1024 ) ) };
	drawing.Flags.IsOpaque = true; drawing.Flags.IsTranslucent = false;
	drawing.RenderOverride = _ =>
	{
		Graphics.Attributes.Set( "T2LightProbes", probes ); Graphics.Attributes.Set( "T2ProbeIndices", indices );
		Graphics.Attributes.Set( "T2ProbeNormal", Vector3.Forward );
		for ( var r = 0; r < 2; ++r )
		{
			indices.SetData( orders[r].AsSpan() ); Graphics.Attributes.Set( "T2ProbeMode", 2 );
			Graphics.DrawModelInstanced( model, transforms[r] );
		}
		Graphics.Attributes.Set( "T2ProbeMode", 1 );
		Graphics.Attributes.Set( "T2ProbeRed", new Vector4( 0, 0, 0, .05f ) );
		Graphics.Attributes.Set( "T2ProbeGreen", new Vector4( 0, 0, 0, .025f ) );
		Graphics.Attributes.Set( "T2ProbeBlue", new Vector4( 0, 0, 0, .0125f ) );
		Graphics.DrawModelInstanced( model, transforms[2] );
	};
	for ( var f = 0; f < 4; ++f ) { await Task.Delay( 50, cancellationToken ); camera.RenderToTexture( target ); }
	using var bitmap = target.GetBitmap();
	System.IO.File.WriteAllBytes( System.IO.Path.Combine( exchangeDirectory, "probe-index.png" ), bitmap.ToPng() );
	float Linear( float x ) => x <= .04045f ? x / 12.92f : MathF.Pow( (x + .055f) / 1.055f, 2.4f );
	for ( var r = 0; r < 3; ++r ) for ( var c = 0; c < 3; ++c )
	{
		// RenderToTexture uses a square viewport. PointToScreenNormal would
		// reapply the editor window's aspect ratio after this offscreen render.
		var point = new Vector2( .5f - (c - 1) * 96f / 512f, .5f - (r - 1) * 96f / 512f );
		var pixel = bitmap.GetPixel( Math.Clamp( (int)(point.x * 384), 0, 383 ), Math.Clamp( (int)(point.y * 384), 0, 383 ) );
		var actual = new Vector3( Linear( pixel.r ), Linear( pixel.g ), Linear( pixel.b ) );
		var expected = r < 2 ? colours[orders[r][c]] : new Vector3( .05f, .025f, .0125f );
		rows.Add( new { row = r, column = c, point = point.ToString(), actual = actual.ToString(), expected = expected.ToString(), passed = actual.Distance( expected ) < .004f } );
	}
	var output = System.IO.Path.Combine( exchangeDirectory, "probe-index-results.json" );
	System.IO.File.WriteAllText( output, System.Text.Json.JsonSerializer.Serialize( rows, new System.Text.Json.JsonSerializerOptions { WriteIndented = true } ) );
	return new { output, rows };
}
finally { drawing?.Delete(); scene.Destroy(); }
