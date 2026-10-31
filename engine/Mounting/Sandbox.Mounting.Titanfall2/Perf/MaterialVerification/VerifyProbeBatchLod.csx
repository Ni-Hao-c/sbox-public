var scene = new Scene();
using var target = Texture.CreateRenderTarget().WithSize( 384, 384 ).Create();
Titanfall2ProbeInstanceBatch batch = null;
var rows = new System.Collections.Generic.List<object>();
try
{
	var camera = new GameObject( scene, true, "Probe LOD camera" ).AddComponent<CameraComponent>();
	camera.WorldTransform = Transform.Zero; camera.EnablePostProcessing = false; camera.FieldOfView = 90;
	camera.ZNear = 1; camera.ZFar = 2000; camera.BackgroundColor = Color.Black;
	var material = Material.Create( "t2-probe-lod-test", "shaders/tests/titanfall2_probe_index.shader" );
	var vertices = new Vertex[4];
	for ( var i = 0; i < 4; ++i ) { vertices[i].Position = new Vector3( 0, i == 1 || i == 2 ? 18 : -18, i >= 2 ? 18 : -18 ); vertices[i].Normal = Vector3.Backward; vertices[i].Color = Color.White; }
	var mesh = new Mesh( "probe-lod-test", material ); mesh.CreateVertexBuffer( 4, vertices ); mesh.CreateIndexBuffer( 6, new[] { 0, 2, 1, 0, 3, 2 } );
	var model = Model.Builder.WithName( "probe-lod-test" ).AddMesh( mesh ).WithLodDistance( 1, .02f ).WithLodDistance( 2, .01f ).Create();
	var transforms = new[] { new Transform( new Vector3( 384, -96, 0 ) ), new Transform( new Vector3( 160, 0, 0 ) ), new Transform( new Vector3( 256, 96, 0 ) ) };
	var colours = new[] { new Vector3( .1f, 0, 0 ), new Vector3( 0, .1f, 0 ), new Vector3( 0, 0, .1f ) };
	var probes = colours.Select( c => new Titanfall2ProbeData { Red = new Vector4( 0, 0, 0, c.x ), Green = new Vector4( 0, 0, 0, c.y ), Blue = new Vector4( 0, 0, 0, c.z ), Metadata = new Vector4( 1, 0, 0, 0 ) } ).ToArray();
	var reverseLods = false;
	batch = new Titanfall2ProbeInstanceBatch( scene.SceneWorld, model, transforms, probes, Color.Black,
		new Sandbox.Mounting.PerfKit.MountedInstanceBatchOptions { CastShadows = false,
			SelectLod = d => reverseLods ? (d < 200 ? 2 : d < 320 ? 1 : 0) : (d < 200 ? 0 : d < 320 ? 1 : 2) } );
	batch.FrustumCullingEnabled = true;
	float Linear( float x ) => x <= .04045f ? x / 12.92f : MathF.Pow( (x + .055f) / 1.055f, 2.4f );
	var statsBefore = Titanfall2ProbeInstanceBatch.GetIndexStatistics();
	foreach ( var test in new[] { "lod-grouped", "lod-reversed", "subset-reordered", "restored-full", "identity-full", "identity-prefix", "legacy-full" } )
	{
		if ( test == "lod-reversed" ) reverseLods = true;
		if ( test == "subset-reordered" ) batch.SetDrawInstances( new[] { transforms[2], transforms[0] }, new[] { 2, 0 } );
		if ( test == "restored-full" ) batch.SetDrawTransforms( transforms );
		if ( test == "identity-full" || test == "legacy-full" )
		{
			batch.PerInstanceLodEnabled = false;
			batch.FrustumCullingEnabled = false;
			batch.SetDrawTransforms( transforms );
			batch.ReuseIdentityIndices = test != "legacy-full";
			batch.ReuseSubsetIndices = test != "legacy-full";
		}
		if ( test == "identity-prefix" ) batch.SetDrawInstances( transforms.Take( 2 ).ToArray(), new[] { 0, 1 } );
		for ( var f = 0; f < 4; ++f ) { await Task.Delay( 40, cancellationToken ); camera.RenderToTexture( target ); }
		using var bitmap = target.GetBitmap();
		for ( var i = 0; i < transforms.Length; ++i )
		{
			var p = transforms[i].Position;
			var pixel = bitmap.GetPixel( (int)((.5f - p.y / (p.x * 2)) * 384), 192 );
			var actual = new Vector3( Linear( pixel.r ), Linear( pixel.g ), Linear( pixel.b ) );
			var expected = (test == "subset-reordered" && i == 1) || (test == "identity-prefix" && i == 2) ? Vector3.Zero : colours[i];
			rows.Add( new { test, instance = i, actual = actual.ToString(), expected = expected.ToString(), passed = actual.Distance( expected ) < .004f } );
		}
	}
	var statsAfter = Titanfall2ProbeInstanceBatch.GetIndexStatistics();
	rows.Add( new { test = "both-index-upload-paths-exercised", instance = -1, actual = "upload/reuse", expected = "upload/reuse",
		passed = statsAfter.Uploads > statsBefore.Uploads && statsAfter.Reuses > statsBefore.Reuses } );
	// More distinct tables than the bounded cache can hold; read actual pixels after
	// eviction, and repeat each table to exercise its reuse path before moving on.
	batch.ReuseIdentityIndices = false;
	batch.ReuseSubsetIndices = true;
	var permutations = new[] { new[] { 0, 1, 2 }, new[] { 0, 2, 1 }, new[] { 1, 0, 2 }, new[] { 1, 2, 0 }, new[] { 2, 0, 1 }, new[] { 2, 1, 0 },
		new[] { 0, 1 }, new[] { 0, 2 }, new[] { 1, 0 }, new[] { 1, 2 }, new[] { 2, 0 }, new[] { 2, 1 }, new[] { 0, 1, 2 } };
	for ( var order = 0; order < permutations.Length; ++order )
	{
		var ids = permutations[order];
		batch.SetDrawInstances( transforms.Take( ids.Length ).ToArray(), ids );
		for ( var f = 0; f < 4; ++f ) { await Task.Delay( 40, cancellationToken ); camera.RenderToTexture( target ); }
		using var bitmap = target.GetBitmap();
		for ( var i = 0; i < transforms.Length; ++i )
		{
			var p = transforms[i].Position;
			var pixel = bitmap.GetPixel( (int)((.5f - p.y / (p.x * 2)) * 384), 192 );
			var actual = new Vector3( Linear( pixel.r ), Linear( pixel.g ), Linear( pixel.b ) );
			var expected = i < ids.Length ? colours[ids[i]] : Vector3.Zero;
			rows.Add( new { test = "cache-eviction-" + order, instance = i, actual = actual.ToString(), expected = expected.ToString(), passed = actual.Distance( expected ) < .004f } );
		}
	}
	var privateFlags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
	var probeBuffer = (GpuBuffer)typeof( Titanfall2ProbeInstanceBatch ).GetField( "_probes", privateFlags ).GetValue( batch );
	var indexBuffer = (GpuBuffer)typeof( Titanfall2ProbeInstanceBatch ).GetField( "_drawIndices", privateFlags ).GetValue( batch );
	var identityBuffer = (GpuBuffer)typeof( Titanfall2ProbeInstanceBatch ).GetField( "_identityIndices", privateFlags ).GetValue( batch );
	var tables = (Array)typeof( Titanfall2ProbeInstanceBatch ).GetField( "_indexTables", privateFlags ).GetValue( batch );
	var cachedBuffers = tables.Cast<object>().Where( x => x is not null )
		.Select( x => (GpuBuffer)x.GetType().GetField( "Buffer" ).GetValue( x ) ).ToArray();
	batch.Delete();
	rows.Add( new { test = "batch-delete-releases-probe-buffers", instance = -1, actual = "disposed", expected = "disposed", passed = !probeBuffer.IsValid && !indexBuffer.IsValid && !identityBuffer.IsValid } );
	rows.Add( new { test = "batch-delete-releases-cached-subset-buffers", instance = -1, actual = "disposed", expected = "disposed", passed = cachedBuffers.Length > 0 && cachedBuffers.All( x => !x.IsValid ) } );
	var output = System.IO.Path.Combine( exchangeDirectory, "probe-batch-lod-results.json" );
	System.IO.File.WriteAllText( output, System.Text.Json.JsonSerializer.Serialize( rows, new System.Text.Json.JsonSerializerOptions { WriteIndented = true } ) );
	return new { output, rows };
}
finally { batch?.Delete(); scene.Destroy(); }
