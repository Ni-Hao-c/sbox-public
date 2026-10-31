var rows = new System.Collections.Generic.List<object>();
void Check( string name, bool passed, object detail = null ) => rows.Add( new { name, passed, detail } );
var scene = new Scene();
using var target = Texture.CreateRenderTarget().WithSize( 128, 128 ).Create();
var objects = new System.Collections.Generic.List<SceneObject>();
var resources = new System.Collections.Generic.List<IDisposable>();
var samples = new System.Collections.Generic.Dictionary<string, Vector3>();
float Linear( float x ) => x <= .04045f ? x / 12.92f : MathF.Pow( (x + .055f) / 1.055f, 2.4f );
try
{
	var camera = new GameObject( scene, true, "Compiled-light camera" ).AddComponent<CameraComponent>();
	camera.WorldTransform = Transform.Zero; camera.EnablePostProcessing = false; camera.ZNear = 1; camera.ZFar = 2000; camera.BackgroundColor = Color.Black;
	var source = new Titanfall2.Titanfall2WorldLight[] {
		new() { Type = 5 },
		new() { Type = 1, Origin = new System.Numerics.Vector3( 64, 0, 0 ), Intensity = new System.Numerics.Vector3( .2f, .1f, .05f ), Radius = 256, Flags = 4, LinearAttenuation = -1f / 256, SpecularIntensity = 1 },
		new() { Type = 2, Origin = new System.Numerics.Vector3( 64, 0, 0 ), Direction = System.Numerics.Vector3.UnitX, Intensity = new System.Numerics.Vector3( .2f, .1f, .05f ), Radius = 256, InnerConeCosine = .9f, OuterConeCosine = .5f, ConeExponent = 1 },
		new() { Type = 2, Origin = new System.Numerics.Vector3( 64, 0, 0 ), Direction = -System.Numerics.Vector3.UnitX, Intensity = new System.Numerics.Vector3( .2f, .1f, .05f ), Radius = 256, InnerConeCosine = .9f, OuterConeCosine = .5f, ConeExponent = 1 },
		new() { Type = 1, Origin = new System.Numerics.Vector3( 64, 0, 0 ), Intensity = new System.Numerics.Vector3( 800, 400, 200 ), Radius = 256, Flags = 32 },
		new() { Type = 1, Origin = new System.Numerics.Vector3( 64, 0, 0 ), Intensity = new System.Numerics.Vector3( .2f ), Radius = 32 }
	};
	using var table = new Titanfall2LocalLightBuffer( source );
	table.Apply( scene.RenderAttributes );
	using var restored = Titanfall2LocalLightBuffer.Restore( Titanfall2LocalLightBuffer.Encode( source ) );
	Check( "light-table-preserves-full-order-and-clone-data", table.Count == 6 && restored.Count == 6 );
	Check( "light-gpu-layout-80-bytes", System.Runtime.InteropServices.Marshal.SizeOf<Titanfall2LocalLightData>() == 80 );
	var vertices = new Vertex[4];
	for ( var i = 0; i < 4; ++i ) { vertices[i].Position = new Vector3( 128, i == 1 || i == 2 ? 64 : -64, i >= 2 ? 64 : -64 ); vertices[i].Normal = Vector3.Backward; vertices[i].Color = Color.White; }
	using var pages = new GpuBuffer<int>( 2 ); pages.SetData( new[] { 1, 2 }.AsSpan() );
	Texture Tex( byte[] bytes ) { var t = Texture.Create( 1, 1 ).WithData( bytes ).Finish(); resources.Add( t ); return t; }
	var ids = Tex( BitConverter.GetBytes( 1u | 63u << 6 | 63u << 12 | 63u << 18 ) );
	var weights = Tex( new byte[] { 255, 0, 0, 0 } );
	foreach ( var shader in new[] { "default", "cutout", "alpha", "translucent", "world_lightmapped" } )
	{
		var material = Material.Create( "t2-compiled-lights-" + shader, "shaders/titanfall2_" + shader + ".shader" );
		foreach ( var slot in new[] { "g_tAlbedo", "g_tAO", "g_tOpacity" } ) material.Set( slot, Texture.White );
		foreach ( var slot in new[] { "g_tGloss", "g_tSpecular", "g_tEmissive", "g_tLightmapSkyA" } ) material.Set( slot, Texture.Black );
		material.Set( "g_vT2AlbedoTint", Vector4.One ); material.Set( "g_flHasNormalMap", 0f ); material.Set( "g_flT2SpecularWorkflow", 1f );
		material.Set( "g_tLightmapRealTimeA", ids ); material.Set( "g_tLightmapRealTimeB", weights ); material.Set( "g_tLightmapSkyB", Texture.Black );
		scene.RenderAttributes.Set( "T2LightPages", pages ); scene.RenderAttributes.Set( "T2LightPageEntryCount", 2 );
		var mesh = new Mesh( "compiled-lights", material ); mesh.CreateVertexBuffer( 4, vertices ); mesh.CreateIndexBuffer( 6, new[] { 0, 2, 1, 0, 3, 2 } );
		var model = Model.Builder.WithName( "compiled-lights" ).AddMesh( mesh ).Create();
		var obj = new SceneObject( scene.SceneWorld, model ); obj.Flags.CastShadows = false; objects.Add( obj );
		async Task<Vector3> Sample( string name, int index, uint weight, bool instanced = false, bool repeat = false )
		{
			var probe = Titanfall2ProbeData.FromSource( new Titanfall2.Titanfall2LightProbe { PackedLightWeights = weight, LightIndex0 = (ushort)index,
				LightIndex1 = repeat ? (ushort)index : ushort.MaxValue, LightIndex2 = repeat ? (ushort)index : ushort.MaxValue, LightIndex3 = repeat ? (ushort)index : ushort.MaxValue } );
			probe.Apply( obj, Color.Black );
			obj.RenderingEnabled = !instanced;
			Titanfall2ProbeInstanceBatch batch = null;
			if ( instanced ) batch = new Titanfall2ProbeInstanceBatch( scene.SceneWorld, model, new[] { Transform.Zero }, new[] { probe }, Color.Black, localLights: restored );
			try
			{
				for ( var f = 0; f < 3; ++f ) { await Task.Delay( 40, cancellationToken ); camera.RenderToTexture( target ); }
				using var bitmap = target.GetBitmap(); var p = bitmap.GetPixel( 64, 64 );
				var result = new Vector3( Linear( p.r ), Linear( p.g ), Linear( p.b ) ); samples[shader + ":" + name] = result; return result;
			}
			finally { batch?.Delete(); obj.RenderingEnabled = true; }
		}
		if ( shader == "world_lightmapped" )
		{
			var actual = await Sample( "page-remaps-to-full-spot", 0, 0 );
			Check( shader + "-integer-page-light-with-baked-visibility", actual.x > .05f && actual.x > actual.z * 2, actual.ToString() );
			scene.RenderAttributes.Set( "T2LightPageEntryCount", 0 );
			var off = await Sample( "no-page", 0, 0 ); Check( shader + "-no-page-is-dark", off.Length < .008f, off.ToString() );
		}
		else
		{
			var point = await Sample( "point", 1, 255 );
			var half = await Sample( "half", 1, 128 );
			var dark = await Sample( "occluded", 1, 0 );
			var spot = await Sample( "spot-inside", 2, 255 );
			var outside = await Sample( "spot-outside", 3, 255 );
			var inverse = await Sample( "inverse-square", 4, 255 );
			var range = await Sample( "outside-range", 5, 255 );
			var invalid = await Sample( "invalid-id", 65535, 255 );
			var four = await Sample( "four-packed-weights", 1, 0xffffffff, repeat: true );
			Check( shader + "-four-packed-weights-preserve-all-bits", point.x > .04f && four.Distance( point * 4 ) < .014f, four.ToString() );
			Check( shader + "-point-visible-and-colored", point.x > .04f && point.x > point.z * 2, point.ToString() );
			Check( shader + "-baked-weight-scales-light", half.Distance( point * (128f / 255) ) < .006f && dark.Length < .008f );
			Check( shader + "-spot-rejects-backside", spot.x > point.x && outside.Length < .008f );
			// 64-unit distance: inverse-square window / (4096+1), compared to polynomial .75.
			var ratio = 4000f * MathF.Pow( 1 - MathF.Pow( 4096f / 65536, 2 ), 2 ) / 4097 / .75f;
			Check( shader + "-inverse-square-retains-hdr-units", point.x > .04f && inverse.Distance( point * ratio ) < .009f, inverse.ToString() );
			Check( shader + "-range-and-invalid-light-remain-dark", range.Length < .008f && invalid.Length < .008f );
			// Mixed transparent models deliberately use individual objects in the importer.
			if ( shader is "default" or "cutout" )
			{
				var batch = await Sample( "instance-point", 1, 255, true );
				Check( shader + "-ordinary-and-instance-agree", point.x > .04f && batch.Distance( point ) < .006f, batch.ToString() );
			}
		}
		obj.Delete();
	}
	var output = System.IO.Path.Combine( exchangeDirectory, "compiled-local-lights-results.json" );
	System.IO.File.WriteAllText( output, System.Text.Json.JsonSerializer.Serialize( new { rows, samples = samples.ToDictionary( p => p.Key, p => p.Value.ToString() ) }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true } ) );
	return new { output, rows };
}
finally { foreach ( var obj in objects ) if ( obj.IsValid() ) obj.Delete(); foreach ( var resource in resources ) resource.Dispose(); scene.Destroy(); }
