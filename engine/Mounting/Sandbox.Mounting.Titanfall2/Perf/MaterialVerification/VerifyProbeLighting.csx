// Full material shading, using the actual ordinary-object and rigid-batch paths.
var scene = new Scene();
using var target = Texture.CreateRenderTarget().WithSize( 384, 384 ).Create();
var objects = new System.Collections.Generic.List<SceneObject>();
var batches = new System.Collections.Generic.List<Titanfall2ProbeInstanceBatch>();
var rows = new System.Collections.Generic.List<object>();
var skyColor = new Color( .02f, .025f, .03f );
var source = new[]
{
	new Titanfall2.Titanfall2LightProbe { Red = new System.Numerics.Vector4( .02f, .01f, -.03f, .07f ),
		Green = new System.Numerics.Vector4( -.01f, .03f, .02f, .05f ), Blue = new System.Numerics.Vector4( .01f, -.02f, .04f, .06f ),
		SkyDirectionSunVisibility = new System.Numerics.Vector4( 0, 0, .5f, 1 ) },
	new Titanfall2.Titanfall2LightProbe { Red = new System.Numerics.Vector4( -.03f, .02f, .01f, .05f ),
		Green = new System.Numerics.Vector4( .02f, -.01f, -.02f, .06f ), Blue = new System.Numerics.Vector4( -.02f, .01f, -.03f, .07f ),
		SkyDirectionSunVisibility = new System.Numerics.Vector4( .5f, 0, 0, 0 ) },
	new Titanfall2.Titanfall2LightProbe()
};
var data = source.Select( Titanfall2ProbeData.FromSource ).ToArray();
float Linear( float x ) => x <= .04045f ? x / 12.92f : MathF.Pow( (x + .055f) / 1.055f, 2.4f );
Vector3 Expected( int probeIndex, Vector3 normal )
{
	var p = source[probeIndex];
	var n = new System.Numerics.Vector3( normal.x, normal.y, normal.z );
	var sh = p.Evaluate( n );
	var sky = p.SkyDirectionSunVisibility;
	var dir = new Vector3( sky.X, sky.Y, sky.Z );
	var lengthSquared = dir.LengthSquared;
	var normalDot = normal.Dot( dir );
	var visibility = Math.Clamp( (MathF.Max( lengthSquared, normalDot ) + normalDot) * MathF.Sqrt( lengthSquared ) * .5f, 0f, 1f );
	return new Vector3( sh.X, sh.Y, sh.Z ) + new Vector3( skyColor.r, skyColor.g, skyColor.b ) * visibility;
}
try
{
	var camera = new GameObject( scene, true, "Probe material camera" ).AddComponent<CameraComponent>();
	camera.WorldTransform = Transform.Zero; camera.EnablePostProcessing = false; camera.FieldOfView = 90;
	camera.ZNear = 1; camera.ZFar = 2000; camera.BackgroundColor = Color.Black;
	foreach ( var shader in new[] { "default", "cutout" } )
	foreach ( var normal in new[] { Vector3.Forward, Vector3.Backward, Vector3.Left, Vector3.Right,
		Vector3.Up, Vector3.Down, new Vector3( 1, 1, 1 ).Normal } )
	{
		var material = Material.Create( "t2-sh-probe-" + shader, "shaders/titanfall2_" + shader + ".shader" );
		foreach ( var slot in new[] { "g_tAlbedo", "g_tOpacity", "g_tAO" } ) material.Set( slot, Texture.White );
		foreach ( var slot in new[] { "g_tGloss", "g_tSpecular", "g_tEmissive", "g_tAlbedo2", "g_tGloss2", "g_tSpecular2" } ) material.Set( slot, Texture.Black );
		material.Set( "g_vT2AlbedoTint", Vector4.One );
		material.Set( "g_flHasNormalMap", 0f ); material.Set( "g_flT2HasEnvironment", 0f );
		material.Set( "g_flT2Layered", 0f ); material.Set( "g_flT2Unlit", 0f );
		var vertices = new Vertex[4];
		for ( var i = 0; i < 4; ++i )
		{
			vertices[i].Position = new Vector3( 0, i == 1 || i == 2 ? 24 : -24, i >= 2 ? 24 : -24 );
			vertices[i].Normal = normal; vertices[i].Color = Color.White;
		}
		var mesh = new Mesh( "t2-sh-probe", material ); mesh.CreateVertexBuffer( 4, vertices ); mesh.CreateIndexBuffer( 6, new[] { 0, 2, 1, 0, 3, 2 } );
		var model = Model.Builder.WithName( "t2-sh-probe" ).AddMesh( mesh ).Create();
		var transforms = Enumerable.Range( 0, 3 ).Select( c => new Transform( new Vector3( 256, (c - 1) * 96, -64 ) ) ).ToArray();
		var batch = new Titanfall2ProbeInstanceBatch( scene.SceneWorld, model, transforms, data, skyColor,
			new Sandbox.Mounting.PerfKit.MountedInstanceBatchOptions { CastShadows = false } );
		batches.Add( batch );
		for ( var c = 0; c < 3; ++c )
		{
			var obj = new SceneObject( scene.SceneWorld, model, new Transform( new Vector3( 256, (c - 1) * 96, 64 ) ) );
			obj.Flags.CastShadows = false; data[c].Apply( obj, skyColor ); objects.Add( obj );
		}
		for ( var frame = 0; frame < 4; ++frame ) { await Task.Delay( 40, cancellationToken ); camera.RenderToTexture( target ); }
		using var bitmap = target.GetBitmap();
		for ( var c = 0; c < 3; ++c )
		{
			Vector3 Sample( int row )
			{
				var pixel = bitmap.GetPixel( (int)((.5f - (c - 1) * 96f / 512f) * 384), (int)((.5f - row * 64f / 512f) * 384) );
				return new Vector3( Linear( pixel.r ), Linear( pixel.g ), Linear( pixel.b ) );
			}
			var ordinary = Sample( 1 ); var instanced = Sample( -1 ); var expected = Expected( c, normal );
			rows.Add( new { shader, normal = normal.ToString(), probe = c, ordinary = ordinary.ToString(),
				instanced = instanced.ToString(), expected = expected.ToString(), passed = ordinary.Distance( instanced ) < .003f
				&& ordinary.Distance( expected ) < .006f } );
		}
		System.IO.File.WriteAllBytes( System.IO.Path.Combine( exchangeDirectory, shader + "-normal-" + normal.ToString().Replace( ",", "_" ) + ".png" ), bitmap.ToPng() );
		foreach ( var obj in objects ) obj.Delete(); objects.Clear();
		foreach ( var item in batches ) item.Delete(); batches.Clear();
	}
	var output = System.IO.Path.Combine( exchangeDirectory, "probe-lighting-results.json" );
	System.IO.File.WriteAllText( output, System.Text.Json.JsonSerializer.Serialize( rows, new System.Text.Json.JsonSerializerOptions { WriteIndented = true } ) );
	return new { output, rows };
}
finally
{
	foreach ( var obj in objects ) obj.Delete();
	foreach ( var batch in batches ) batch.Delete();
	scene.Destroy();
}
