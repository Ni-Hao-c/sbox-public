using System;
using NativeEngine;
using Sandbox.Rendering;
using static ModelLoader;

namespace MountingTests;

[TestClass, DoNotParallelize]
public class Titanfall2MixedPropRenderingTest
{
	[TestMethod]
	[DataRow( "decal", false, false )]
	[DataRow( "translucent", false, false )]
	[DataRow( "decal", true, false )]
	[DataRow( "translucent", true, false )]
	[DataRow( "decal", false, true )]
	[DataRow( "translucent", false, true )]
	[DataRow( "decal", true, true )]
	[DataRow( "translucent", true, true )]
	[DataRow( "cutout+decal", false, false )]
	[DataRow( "cutout+translucent", false, false )]
	[DataRow( "cutout+decal", true, false )]
	[DataRow( "cutout+translucent", true, false )]
	public void MixedMeshMasksMatchFullModelAndKeepEmptyFarLod( string residualShader, bool managed, bool emptyGpuFarLod )
	{
		if ( !Graphics.IsAvailable ) Assert.Inconclusive( "Requires SBOX_TEST_GRAPHICS=1." );
		Sandbox.SceneRenderer.EngineHooks.Install();
		RenderPipeline.WarmUp();
		var wasManaged = ManagedSceneRendering.Enabled;
		var scene = new Scene();
		using var scope = scene.Push();
		var cutout = residualShader.StartsWith( "cutout+", StringComparison.Ordinal );
		residualShader = residualShader.Replace( "cutout+", "" );
		var opaque = Material.Create( $"tf2-mixed-opaque-{Guid.NewGuid():N}", $"shaders/titanfall2_{(cutout ? "cutout" : "default")}.shader" );
		var residual = Material.Create( $"tf2-mixed-special-{Guid.NewGuid():N}", $"shaders/titanfall2_{residualShader}.shader" );
		foreach ( var material in new[] { opaque, residual } )
		{
			material.Set( "g_tAlbedo", Texture.White ); material.Set( "g_tAO", Texture.White );
			material.Set( "g_tOpacity", Texture.White ); material.Set( "g_tGloss", Texture.Black );
			material.Set( "g_tSpecular", Texture.Black ); material.Set( "g_tEmissive", Texture.Black );
		}
		using var opacity = Texture.Create( 4, 4 ).WithData( Enumerable.Range( 0, 16 )
			.SelectMany( i => new byte[] { (byte)(i % 4 < 2 ? 0 : 255), 255, 255, 255 } ).ToArray() ).Finish();
		if ( cutout ) opaque.Set( "g_tOpacity", opacity );
		residual.Set( "g_tAlbedo", Texture.Black ); residual.Set( "g_tEmissive", Texture.White );
		residual.Set( "g_vT2EmissiveTint", new Vector4( 0, 1, 0, 1 ) );
		residual.Set( "g_flT2EmissiveStrength", 1f );
		var fullBounds = BBox.FromPositionAndSize( Vector3.Zero, 20 );
		var opaqueVertices = new[] { new Vector3( 0, -9, -9 ), new Vector3( 0, 0, 9 ), new Vector3( 0, 9, -9 ) }
			.Select( ( point, i ) => new TitanfallVertex { Position = point, Normal = -Vector3.Forward,
				TexCoord = i == 0 ? Vector2.Zero : i == 1 ? new Vector2( 0.5f, 1 ) : new Vector2( 1, 0 ), Color = Color.White } ).ToArray();
		var residualVertices = opaqueVertices.Select( vertex => vertex with { Position = vertex.Position * 0.45f - Vector3.Forward * 0.1f } ).ToArray();
		var indices = new[] { 0, 1, 2, 2, 1, 0 };
		var baseMesh = new Mesh( "mixed-base", opaque );
		baseMesh.CreateVertexBuffer( opaqueVertices.Length, opaqueVertices ); baseMesh.CreateIndexBuffer( indices.Length, indices ); baseMesh.Bounds = fullBounds;
		var specialMesh = new Mesh( "mixed-special", residual );
		specialMesh.CreateVertexBuffer( residualVertices.Length, residualVertices ); specialMesh.CreateIndexBuffer( indices.Length, indices ); specialMesh.Bounds = fullBounds;
		var baseGeometry = new StaticMeshGeometry( 0, "base", opaqueVertices, indices, fullBounds ) { RuntimeMaterial = opaque, GpuCompatible = true };
		var specialGeometry = new StaticMeshGeometry( 1, "special", residualVertices, indices, fullBounds ) { RuntimeMaterial = residual };
		var builder = Model.Builder.WithName( $"tf2-mixed-test-{Guid.NewGuid():N}" );
		for ( var lod = 0; lod < 8; ++lod )
		{
			if ( lod < 3 || !emptyGpuFarLod ) AddStaticRenderMesh( builder, baseMesh, lod, true, true );
			if ( lod < 3 || emptyGpuFarLod ) AddStaticRenderMesh( builder, specialMesh, lod, false, true );
		}
		var model = builder.Create();
		var levels = Enumerable.Range( 0, 4 ).Select( lod => new StaticLodGeometry( lod < 3 ? new[] { baseGeometry, specialGeometry }
			: emptyGpuFarLod ? new[] { specialGeometry } : new[] { baseGeometry }, 10 ) ).ToArray();
		RegisterGpuGeometry( model, levels );
		Assert.IsTrue( TryGetGpuGeometry( model, out var geometry ) );
		Assert.IsTrue( geometry.IsMixed );
		Assert.AreEqual( model.Parts.Get( ResidualMeshGroup ).Mask, geometry.ResidualMask );
		Assert.AreEqual( 0UL, geometry.ResidualMask & model.Parts.Get( GpuMeshGroup ).Mask );
		Assert.AreEqual( emptyGpuFarLod ? 0 : 1, geometry.Lods[3].Length );
		Assert.IsTrue( geometry.Lods.SelectMany( meshes => meshes ).All( mesh => mesh.Material == opaque ) );
		var transforms = new[] { new Transform( new Vector3( 0, -13, 0 ) ), new Transform( new Vector3( 0, 13, 0 ), Rotation.Identity, new Vector3( 1, 1.2f, 0.8f ) ) };
		var probes = new[] {
			new Titanfall2ProbeData { Red = new Vector4( 0, 0, 0, 1 ), Metadata = new Vector4( 1, 0, 0, 0 ) },
			new Titanfall2ProbeData { Blue = new Vector4( 0, 0, 0, 1 ), Metadata = new Vector4( 1, 0, 0, 0 ) } };
		var layout = Titanfall2StaticPropGpuScene.Compile( transforms.Select( ( transform, i ) => new Titanfall2StaticPropGpuScene.Instance(
			i == 0 ? 2 : 7, model, transform, 10, 10000, false, probes[i], default ) ).ToArray(), 8 );
		var native = transforms.Select( transform => new SceneObject( scene.SceneWorld, model, transform ) ).ToArray();
		Titanfall2StaticPropGpuScene gpu = null;
		try
		{
			ManagedSceneRendering.Enabled = managed;
			for ( var i = 0; i < native.Length; ++i ) { probes[i].Apply( native[i], Color.Black ); native[i].Flags.IncludeInCubemap = true; }
			gpu = new( scene.SceneWorld, layout, Color.Black, null, _ => default );
			gpu.Flags.IncludeInCubemap = true;
			using var camera = new SceneCamera( "TF2 mixed coverage" ) { World = scene.SceneWorld, Position = new Vector3( -60, 0, 0 ),
				Rotation = Rotation.Identity, FieldOfView = 70, ZNear = 1, ZFar = 20000, BackgroundColor = Color.Black, EnablePostProcessing = false, AntiAliasing = false };
			using var target = Texture.CreateRenderTarget().WithSize( 128, 128 ).WithFormat( ImageFormat.RGBA16161616F ).Create( "tf2-mixed-coverage" );
			Color[] Capture( bool useGpu, int lod, bool residualOnly = false )
			{
				gpu.RenderingEnabled = useGpu;
				foreach ( var obj in native ) { obj.MeshGroupMask = useGpu || residualOnly ? geometry.ResidualMask : model.Parts.DefaultMask; obj.LodOverride = lod; }
				for ( var frame = 0; frame < 20; ++frame )
				{
					System.Threading.Thread.Sleep( 50 ); Application.FrameCount++; EngineGlobal.SourceEnginePanelAppFrame(); scene.GameTick( 0 ); scene.PreRender();
					camera.RenderToTexture( target, null, default ); g_pRenderDevice.Present( IntPtr.Zero ); g_pRenderDevice.ForceFlushGPU( IntPtr.Zero );
					EngineLoop.DrainFrameEndDisposables(); RenderTarget.EndOfFrame(); EngineLoop.RunAsyncTasks();
				}
				var pixels = new ushort[128 * 128 * 4];
				target.GetPixels<ushort>( (0, 0, 128, 128), 0, 0, pixels, ImageFormat.RGBA16161616F );
				return Enumerable.Range( 0, 128 * 128 ).Select( i => new Color( (float)BitConverter.UInt16BitsToHalf( pixels[i * 4] ),
					(float)BitConverter.UInt16BitsToHalf( pixels[i * 4 + 1] ), (float)BitConverter.UInt16BitsToHalf( pixels[i * 4 + 2] ), 1 ) ).ToArray();
			}
			foreach ( var lod in new[] { 0, 3 } )
			{
				camera.Position = new Vector3( lod == 0 ? -60 : -9000, 0, 0 );
				camera.FieldOfView = lod == 0 ? 70 : 1;
				var reference = Capture( false, lod );
				var opaqueExpected = lod == 0 || !emptyGpuFarLod;
				if ( opaqueExpected ) Assert.IsTrue( reference.Count( c => c.r > 0.1f ) > 20 && reference.Count( c => c.b > 0.1f ) > 20, "Full CPU positive control must show both source probes." );
				else Assert.AreEqual( 0, reference.Count( c => c.r > 0.1f || c.b > 0.1f ), "The original far LOD contains only the special mesh." );
				var green = reference.Count( c => c.g > 0.1f );
				if ( lod == 0 || emptyGpuFarLod ) Assert.IsTrue( green > 10, "Special material must produce visible pixels." );
				else Assert.AreEqual( 0, green, "The original far LOD deliberately contains no special mesh." );
				var residualPixels = Capture( false, lod, true );
				Assert.AreEqual( 0, residualPixels.Count( c => c.r > 0.1f || c.b > 0.1f ), "Residual mask must not redraw the opaque geometry." );
				var residualGreen = residualPixels.Count( c => c.g > 0.1f );
				if ( green == 0 ) Assert.AreEqual( 0, residualGreen, "An empty residual LOD must stay empty." );
				else Assert.IsTrue( Math.Abs( green - residualGreen ) <= Math.Max( 2, green * 0.1 ),
					"Removing the opaque background may change boundary coverage, but must retain the special mesh." );
				var actual = Capture( true, lod );
				Assert.AreEqual( green, actual.Count( c => c.g > 0.1f ), "The GPU split must retain every visible special-material pixel." );
				if ( opaqueExpected ) Assert.IsTrue( actual.Count( c => c.r > 0.1f ) > 20 && actual.Count( c => c.b > 0.1f ) > 20, "GPU positive control must show both source probes." );
				else Assert.AreEqual( 0, actual.Count( c => c.r > 0.1f || c.b > 0.1f ), "An intentionally empty GPU LOD must not reuse a near LOD." );
				var error = reference.Zip( actual, ( a, b ) => (Math.Abs( a.r - b.r ) + Math.Abs( a.g - b.g ) + Math.Abs( a.b - b.b )) / 3 ).Average();
				Assert.IsTrue( error < 0.02, $"Mixed CPU/GPU RGB error at LOD {lod}: {error}" );
				var restored = Capture( false, lod );
				Assert.IsTrue( reference.Zip( restored, ( a, b ) => Math.Abs( a.r - b.r ) + Math.Abs( a.g - b.g ) + Math.Abs( a.b - b.b ) ).Average() < 0.001,
					"Restoring the full mesh mask must restore the CPU reference." );

			}
		}
		finally
		{
			gpu?.Delete(); foreach ( var obj in native ) obj.Delete(); scene.Destroy();
			opaque.Destroy(); residual.Destroy(); ManagedSceneRendering.Enabled = wasManaged;
		}
	}
}
