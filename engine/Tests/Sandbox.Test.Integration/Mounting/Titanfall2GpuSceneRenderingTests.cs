using System;
using NativeEngine;
using Sandbox.Mounting.PerfKit;
using Sandbox.Rendering;
using static Titanfall2StaticPropGpuScene;
using GpuVertex = Titanfall2StaticPropGpuScene.Vertex;

namespace MountingTests;

[TestClass, DoNotParallelize]
public class Titanfall2GpuSceneRenderingTest
{
	[TestMethod]
	[DataRow( "default", false )]
	[DataRow( "cutout", false )]
	[DataRow( "default", true )]
	[DataRow( "cutout", true )]
	public void ProductionShaderMatchesCpuProbeGeometryAndSecondCamera( string shader, bool managed )
	{
		if ( !Graphics.IsAvailable ) Assert.Inconclusive( "Requires SBOX_TEST_GRAPHICS=1." );
		// TestAppSystem overrides AppSystem.Init without installing the normal renderers.
		// Standalone compute still works, but camera views otherwise have no scene layers.
		Sandbox.SceneRenderer.EngineHooks.Install();
		RenderPipeline.WarmUp();
		var wasManaged = ManagedSceneRendering.Enabled;
		var scene = new Scene();
		using var sceneScope = scene.Push();
		var material = Material.Create( $"tf2-gpu-scene-{shader}", $"shaders/titanfall2_{shader}.shader" );
		using var normal = Texture.Create( 1, 1 ).WithData( new byte[] { 128, 128, 255, 255 } ).Finish();
		using var opacity = Texture.Create( 4, 4 ).WithData( Enumerable.Range( 0, 16 )
			.SelectMany( i => new byte[] { (byte)(i % 4 < 2 ? 0 : 255), 255, 255, 255 } ).ToArray() ).Finish();
		material.Set( "g_tAlbedo", Texture.White ); material.Set( "g_tNormal", normal );
		material.Set( "g_tAO", Texture.White ); material.Set( "g_tOpacity", Texture.White );
		material.Set( "g_tGloss", Texture.Black ); material.Set( "g_tSpecular", Texture.Black ); material.Set( "g_tEmissive", Texture.Black );
		material.Set( "g_tOpacity", shader == "cutout" ? opacity : Texture.White );
		var secondMaterial = material.CreateCopy();
		using var secondAlbedo = Texture.Create( 1, 1 ).WithData( new byte[] { 150, 240, 200, 255 } ).Finish();
		secondMaterial.Set( "g_tAlbedo", secondAlbedo );
		secondMaterial.Set( "g_vT2AlbedoTint", new Vector4( 0.7f, 0.6f, 0.8f, 1 ) );
		var points = new[] { new Vector3( 0, -8, -8 ), new Vector3( 0, 0, 8 ), new Vector3( 0, 8, -8 ) };
		var meshVertices = points.Select( ( position, index ) => new GpuVertex
		{
			Position = position,
			Normal = new Vector3( -1, 0, 0 ),
			TexCoord = index == 0 ? Vector2.Zero : index == 1 ? new Vector2( 0.5f, 1 ) : new Vector2( 1, 0 ),
			Color = Color.White
		} ).ToArray();
		var mesh = new Mesh( "tf2-gpu-probe", material );
		mesh.CreateVertexBuffer( 3, meshVertices ); mesh.CreateIndexBuffer( 6, new[] { 0, 1, 2, 2, 1, 0 } );
		mesh.Bounds = BBox.FromPositionAndSize( Vector3.Zero, 20 );
		var model = Model.Builder.WithName( "tf2-gpu-probe" ).AddMesh( mesh ).Create();
		var secondMesh = new Mesh( "tf2-gpu-probe-second-material", secondMaterial );
		secondMesh.CreateVertexBuffer( 3, meshVertices ); secondMesh.CreateIndexBuffer( 6, new[] { 0, 1, 2, 2, 1, 0 } );
		secondMesh.Bounds = BBox.FromPositionAndSize( Vector3.Zero, 20 );
		var secondModel = Model.Builder.WithName( "tf2-gpu-probe-second-material" ).AddMesh( secondMesh ).Create();
		var transforms = new[] { new Transform( new Vector3( 0, -13, 0 ) ),
			new Transform( new Vector3( 0, 13, 0 ), new Angles( 0, 0, 15 ).ToRotation(), new Vector3( 1, 1.2f, 0.8f ) ) };
		var probes = new[] { new Titanfall2ProbeData { Red = new Vector4( 0, 0, 0, 1 ), Metadata = new Vector4( 1, 0, 0, 0 ) },
			new Titanfall2ProbeData { Green = new Vector4( 0, 0, 0, 1 ), Metadata = new Vector4( 1, 0, 0, 0 ) } };
		var vertices = meshVertices.Concat( meshVertices ).ToArray();
		for ( var i = 3; i < 6; ++i ) { vertices[i].VisibleBase = 4; vertices[i].MaterialId = 1; }
		var buckets = Enumerable.Range( 0, 8 ).Select( i => new Bucket { FirstVisible = (uint)i, Capacity = 1 } ).ToArray();
		var candidates = transforms.Select( ( transform, index ) => new Candidate
		{
			Sphere = new Vector4( transform.Position, 16 ),
			OriginAndDistance = new Vector4( transform.Position, 10000 ),
			SourceId = (uint)index,
			FirstBucket = (uint)index * 4
		} ).ToArray();
		var layout = new Layout( vertices, new uint[] { 0, 1, 2, 2, 1, 0, 0, 1, 2, 2, 1, 0 }, candidates, buckets,
			new[] { new Draw { IndexCount = 6, FirstIndex = 0, BaseVertex = 0, Bucket = 0 },
				new Draw { IndexCount = 6, FirstIndex = 6, BaseVertex = 3, Bucket = 4 } },
			transforms.Select( GpuInstanceTransform.From ).ToArray(), probes, 8, new[] { material }, new uint[] { 0 }, new uint[] { 2 },
			BBox.FromPositionAndSize( Vector3.Zero, 100 ), new[] { true }, new[] { material, secondMaterial },
			new[] { Titanfall2GpuMaterialData.Capture( material ), Titanfall2GpuMaterialData.Capture( secondMaterial ) } );
		Titanfall2StaticPropGpuScene gpu = null;
		Titanfall2ProbeInstanceBatch cpu = null;
		Titanfall2ProbeInstanceBatch secondCpu = null;
		SceneCustomObject marker = null;
		var markerCalls = 0;
		try
		{
			ManagedSceneRendering.Enabled = managed;
			gpu = new( scene.SceneWorld, layout, Color.Black, null, _ => default );
			gpu.Flags.IncludeInCubemap = true; // Offscreen capture views exclude HideInCubemaps objects.
			gpu.RenderingEnabled = false;
			var options = new MountedInstanceBatchOptions { CastShadows = false, IncludeInCubemap = true, SelectLod = _ => 0 };
			cpu = new( scene.SceneWorld, model, new[] { transforms[0] }, new[] { probes[0] }, Color.Black, options );
			secondCpu = new( scene.SceneWorld, secondModel, new[] { transforms[1] }, new[] { probes[1] }, Color.Black, options );
			marker = new SceneCustomObject( scene.SceneWorld );
			marker.Flags.IsOpaque = false; marker.Flags.IsTranslucent = true;
			marker.RenderOverride = _ => System.Threading.Interlocked.Increment( ref markerCalls );
			using var camera = new SceneCamera( "TF2 GPU scene comparison" ) { World = scene.SceneWorld };
			camera.Position = new Vector3( -60, 0, 0 ); camera.Rotation = Rotation.Identity;
			camera.FieldOfView = 70; camera.ZNear = 1; camera.ZFar = 1000; camera.BackgroundColor = Color.Black;
			camera.EnablePostProcessing = false;
			using var target = Texture.CreateRenderTarget().WithSize( 128, 128 ).WithFormat( ImageFormat.RGBA16161616F ).Create( "tf2-gpu-production-comparison" );
			Color[] Capture( bool useGpu )
			{
				gpu.RenderingEnabled = useGpu; cpu.RenderingEnabled = !useGpu;
				secondCpu.RenderingEnabled = !useGpu;
				for ( var frame = 0; frame < 20; ++frame )
				{
					System.Threading.Thread.Sleep( 50 ); // Native pipeline creation is asynchronous on first use.
					EngineGlobal.SourceEnginePanelAppFrame(); scene.GameTick( 0.0f );
					scene.PreRender();
					camera.RenderToTexture( target, null, default ); g_pRenderDevice.Present( IntPtr.Zero );
					g_pRenderDevice.ForceFlushGPU( IntPtr.Zero );
					EngineLoop.DrainFrameEndDisposables(); RenderTarget.EndOfFrame(); EngineLoop.RunAsyncTasks();
				}
				var data = new ushort[128 * 128 * 4];
				target.GetPixels<ushort>( (0, 0, 128, 128), 0, 0, data, ImageFormat.RGBA16161616F );
				return Enumerable.Range( 0, 128 * 128 ).Select( i => new Color(
					(float)BitConverter.UInt16BitsToHalf( data[i * 4] ), (float)BitConverter.UInt16BitsToHalf( data[i * 4 + 1] ),
					(float)BitConverter.UInt16BitsToHalf( data[i * 4 + 2] ), 1 ) ).ToArray();
			}
			void Compare()
			{
				Color[] solid = null;
				if ( shader == "cutout" )
				{
					material.Set( "g_tOpacity", Texture.White ); secondMaterial.Set( "g_tOpacity", Texture.White ); solid = Capture( false );
					material.Set( "g_tOpacity", opacity ); secondMaterial.Set( "g_tOpacity", opacity );
				}
				var reference = Capture( false ); var actual = Capture( true );
				Console.WriteLine( $"{shader} CPU: red={reference.Count( color => color.r > 0.1f )}, green={reference.Count( color => color.g > 0.1f )}; GPU: red={actual.Count( color => color.r > 0.1f )}, green={actual.Count( color => color.g > 0.1f )}; "
					+ $"markerCalls={markerCalls}; cpuEnabled={cpu.RenderingEnabled}; gpuEnabled={gpu.RenderingEnabled}; objects={scene.SceneWorld.SceneObjects.Count}; "
					+ System.Text.Json.JsonSerializer.Serialize( gpu.Statistics ) );
				Assert.IsTrue( reference.Count( color => color.r > 0.1f ) > 30 && reference.Count( color => color.g > 0.1f ) > 30, "CPU positive control must show both probes." );
				Assert.IsTrue( actual.Count( color => color.r > 0.1f ) > 30 && actual.Count( color => color.g > 0.1f ) > 30, "GPU must show both source probes." );
				if ( solid is not null ) Assert.IsTrue( reference.Count( c => c.r > 0.1f || c.g > 0.1f )
					< solid.Count( c => c.r > 0.1f || c.g > 0.1f ) * 0.85f, "Cutout positive control must remove visible pixels." );
				var error = actual.Zip( reference, ( a, b ) => MathF.Abs( a.r - b.r ) + MathF.Abs( a.g - b.g ) + MathF.Abs( a.b - b.b ) ).Average();
				Assert.IsTrue( error < 0.02f, $"Production CPU/GPU mean RGB difference: {error}." );
			}
			Compare();
			camera.Position = new Vector3( -80, 15, 10 ); camera.Rotation = Rotation.LookAt( -camera.Position );
			Compare();
		}
		finally { gpu?.Delete(); cpu?.Delete(); secondCpu?.Delete(); marker?.Delete(); scene.Destroy(); material.Destroy(); secondMaterial.Destroy(); ManagedSceneRendering.Enabled = wasManaged; }
	}
}
