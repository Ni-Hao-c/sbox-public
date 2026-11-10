using System;
using NativeEngine;
using Sandbox.Rendering;
namespace MountingTests;

[TestClass, DoNotParallelize]
public class Titanfall2DetailNormalValidation
{
	[TestMethod]
	[DataRow( false, false )]
	[DataRow( true, false )]
	[DataRow( false, true )]
	[DataRow( true, true )]
	public void AuthoredDetailUvAndSlopeSumReachDepthNormals( bool table, bool baseNormal )
	{
		if ( !Graphics.IsAvailable ) Assert.Inconclusive( "Requires SBOX_TEST_GRAPHICS=1." );
		Sandbox.SceneRenderer.EngineHooks.Install(); RenderPipeline.WarmUp();
		var managed = ManagedSceneRendering.Enabled; var scene = new Scene(); using var scope = scene.Push();
		using var primary = Texture.Create( 1, 1 ).WithData( new byte[] { 160, 100, 240, 255 } ).Finish();
		using var detail = Texture.Create( 2, 2 ).WithData( new byte[] { 64, 64, 255, 255, 192, 128, 255, 255, 128, 192, 255, 255, 128, 128, 255, 255 } ).Finish();
		var material = Material.Create( $"t2-detail-normal-{Guid.NewGuid():N}", "shaders/titanfall2_world_lightmapped.shader" );
		material.Set( "g_tAlbedo", Texture.White ); material.Set( "g_tOpacity", Texture.White ); material.Set( "g_tAO", Texture.White );
		material.Set( "g_tGloss", Texture.Black ); material.Set( "g_tSpecular", Texture.Black ); material.Set( "g_tEmissive", Texture.Black );
		material.Set( "g_tNormal", primary ); material.Set( "g_flHasNormalMap", baseNormal ? 1 : 0 ); material.Set( "g_flT2NormalSlope", 1f );
		material.Set( "g_tDetailNormal", detail ); material.Set( "g_vT2DetailNormal", new Vector4( 1, 1, 0, 2 ) );
		material.Set( "g_vT2Uv2RotScale", Vector4.Zero ); material.Set( "g_vT2Uv2Translate", new Vector4( 0.75f, 0.25f, 0, 0 ) );
		var data = Titanfall2GpuMaterialData.Capture( material ); using var buffer = new GpuBuffer<Titanfall2GpuMaterialData>( 2 );
		var dummy = data; dummy.g_vT2DetailNormal = Vector4.Zero; buffer.SetData( new[] { dummy, data } );
		material.SetFeature( "F_T2_WORLD_MATERIAL_TABLE", table ? 1 : 0 );
		var points = new[] { new Vector3( 0, -20, -20 ), new Vector3( 0, 20, -20 ), new Vector3( 0, -20, 20 ), new Vector3( 0, 20, 20 ) };
		var vertices = points.Select( ( p, n ) => new TitanfallWorldMaterialVertex
		{
			Position = p,
			Normal = -Vector3.Forward,
			Color = Color.White,
			TexCoord = new Vector2( n % 2, n / 2 ),
			MaterialId = 1
		} ).ToArray();
		var mesh = new Mesh( "t2-detail-normal", material ); mesh.CreateVertexBuffer( 4, vertices ); mesh.CreateIndexBuffer( 6, new[] { 0, 2, 1, 1, 2, 3 } );
		mesh.Bounds = BBox.FromPositionAndSize( Vector3.Zero, 60 ); var obj = new SceneObject( scene.SceneWorld, Model.Builder.AddMesh( mesh ).Create(), Transform.Zero );
		obj.Flags.IncludeInCubemap = true;
		using var camera = new SceneCamera( "T2 detail normal regression" )
		{
			World = scene.SceneWorld,
			Position = new Vector3( -60, 0, 0 ),
			Rotation = Rotation.Identity,
			ZNear = 1,
			ZFar = 1000,
			BackgroundColor = Color.Black,
			EnablePostProcessing = false,
			AntiAliasing = false
		};
		camera.Attributes.Set( "NeedsDepthNormals", true ); camera.Attributes.Set( "T2WorldMaterials", buffer );
		using var target = Texture.CreateRenderTarget().WithSize( 96, 96 ).WithFormat( ImageFormat.RGBA16161616F ).Create();
		var commands = new CommandList( "T2 detail normal readback" ); commands.Attributes.SetCombo( "D_MSAA_NORMALS", 0 );
		commands.Blit( Material.FromShader( "shaders/tests/titanfall2_gbuffer_read.shader" ) );
		camera.OnRenderStageHook = ( stage, _ ) => { if ( stage == Stage.BeforePostProcess ) Titanfall2GpuSceneCompatibility.Execute( commands ); };
		try
		{
			ManagedSceneRendering.Enabled = false;
			for ( var frame = 0; frame < 20; frame++ )
			{
				System.Threading.Thread.Sleep( 30 ); Application.FrameCount++; EngineGlobal.SourceEnginePanelAppFrame(); scene.GameTick( 0 ); scene.PreRender();
				camera.RenderToTexture( target, null, default ); g_pRenderDevice.Present( IntPtr.Zero ); g_pRenderDevice.ForceFlushGPU( IntPtr.Zero );
				EngineLoop.DrainFrameEndDisposables(); RenderTarget.EndOfFrame(); EngineLoop.RunAsyncTasks();
			}
			var bytes = new ushort[96 * 96 * 4]; target.GetPixels<ushort>( (0, 0, 96, 96), 0, 0, bytes, ImageFormat.RGBA16161616F );
			var actual = new Vector3( (float)BitConverter.UInt16BitsToHalf( bytes[(48 * 96 + 48) * 4] ), (float)BitConverter.UInt16BitsToHalf( bytes[(48 * 96 + 48) * 4 + 1] ), (float)BitConverter.UInt16BitsToHalf( bytes[(48 * 96 + 48) * 4 + 2] ) );
			float Decode( float value ) => value / 255f * 2.867661f - 1.439453f;
			var slopeX = Decode( 192 ) + (baseNormal ? Decode( 160 ) : 0); var slopeY = Decode( 128 ) + (baseNormal ? Decode( 100 ) : 0);
			var expected = (new Vector3( -1, slopeX, slopeY ).Normal + Vector3.One) * 0.5f;
			Console.WriteLine( $"table={table}, base={baseNormal}, actual={actual}, expected={expected}" );
			Assert.IsTrue( (actual - expected).Length < 0.025f, $"Detail normal UV, slope accumulation or material-table stride changed: {actual} vs {expected}." );
		}
		finally { obj.Delete(); scene.Destroy(); material.Destroy(); ManagedSceneRendering.Enabled = managed; }
	}
}
