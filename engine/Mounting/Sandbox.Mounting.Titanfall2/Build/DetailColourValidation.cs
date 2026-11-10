using System;
using NativeEngine;
using Sandbox.Rendering;

namespace MountingTests;

[TestClass, DoNotParallelize]
public class Titanfall2DetailColourValidation
{
	[TestMethod]
	[DataRow( "titanfall2_default.shader", false, true )]
	[DataRow( "titanfall2_cutout.shader", false, true )]
	[DataRow( "titanfall2_world_lightmapped.shader", false, true )]
	[DataRow( "titanfall2_world_lightmapped.shader", true, true )]
	[DataRow( "titanfall2_default.shader", false, false )]
	public void LinearDetailModulationMatchesRetailByteDomain( string shader, bool table, bool retail )
	{
		if ( !Graphics.IsAvailable ) Assert.Inconclusive( "Requires SBOX_TEST_GRAPHICS=1." );
		Sandbox.SceneRenderer.EngineHooks.Install(); RenderPipeline.WarmUp();
		var managed = ManagedSceneRendering.Enabled;
		var scene = new Scene(); using var scope = scene.Push();
		using var neutral = Texture.Create( 1, 1 ).WithData( new byte[] { 128, 128, 128, 255 } ).Finish();
		using var colour = Texture.Create( 1, 1 ).WithData( new byte[] { 192, 128, 64, 255 } ).Finish();
		using var sky = Texture.Create( 1, 1 ).WithData( new byte[] { 255, 255, 255, 64 } ).Finish();
		using var direction = Texture.Create( 1, 1 ).WithData( new byte[] { 128, 128, 128, 255 } ).Finish();
		var material = Material.Create( $"t2-detail-colour-{Guid.NewGuid():N}", "shaders/" + shader );
		material.Set( "g_tAlbedo", Texture.White ); material.Set( "g_tOpacity", Texture.White ); material.Set( "g_tAO", Texture.White );
		material.Set( "g_tGloss", Texture.Black ); material.Set( "g_tSpecular", Texture.Black ); material.Set( "g_tEmissive", Texture.Black );
		material.Set( "g_vT2AlbedoTint", new Vector4( 0.12f, 0.24f, 0.36f, 1 ) );
		material.Set( "g_flT2Unlit", 1f ); material.Set( "g_flT2SpecularWorkflow", retail ? 1f : 0f );
		material.Set( "g_tLightmapSkyA", sky ); material.Set( "g_tLightmapSkyB", direction );
		material.Set( "g_vT2LightmapSkyColor", Vector4.Zero ); material.Set( "g_flT2HasSunLightmap", 0f );
		material.SetFeature( "F_T2_WORLD_MATERIAL_TABLE", table ? 1 : 0 );
		using var buffer = new GpuBuffer<Titanfall2GpuMaterialData>( 2 );
		var points = new[] { new Vector3( 0, -20, -20 ), new Vector3( 0, 20, -20 ), new Vector3( 0, -20, 20 ), new Vector3( 0, 20, 20 ) };
		var vertices = points.Select( p => new TitanfallWorldMaterialVertex { Position = p, Normal = -Vector3.Forward, Color = Color.White, TexCoord = new Vector2( 0.5f ), LightmapTexCoord = new Vector2( 0.5f ), MaterialId = 1 } ).ToArray();
		var mesh = new Mesh( "t2-detail-colour", material ); mesh.CreateVertexBuffer( 4, vertices ); mesh.CreateIndexBuffer( 6, new[] { 0, 2, 1, 1, 2, 3 } );
		mesh.Bounds = BBox.FromPositionAndSize( Vector3.Zero, 60 );
		var obj = new SceneObject( scene.SceneWorld, Model.Builder.AddMesh( mesh ).Create(), Transform.Zero ); obj.Flags.IncludeInCubemap = true;
		using var camera = new SceneCamera( "T2 detail colour regression" ) { World = scene.SceneWorld, Position = new Vector3( -60, 0, 0 ), Rotation = Rotation.Identity, ZNear = 1, ZFar = 1000, BackgroundColor = Color.Black, EnablePostProcessing = false, AntiAliasing = false };
		camera.Attributes.Set( "T2WorldMaterials", buffer );
		using var target = Texture.CreateRenderTarget().WithSize( 96, 96 ).WithFormat( ImageFormat.RGBA16161616F ).Create();
		try
		{
			ManagedSceneRendering.Enabled = false;
			var reference = Capture( neutral, 0 );
			var middleGrey = Capture( neutral, 1 );
			var tinted = Capture( colour, 1 );
			Assert.IsTrue( reference.x > 0.02f && reference.y > 0.02f && reference.z > 0.02f, $"Reference geometry is not visible: {reference}" );
			float Gamma( float value ) => value <= 0.04045f ? value / 12.92f : MathF.Pow( (value + 0.055f) / 1.055f, 2.4f );
			var expected = reference * (retail ? new Vector3( (192f - 0.5f) / 255f * 2, 1, (64f - 0.5f) / 255f * 2 ) : new Vector3( Gamma( 192f / 255f ), Gamma( 128f / 255f ), Gamma( 64f / 255f ) ) * 2);
			var expectedNeutral = reference * (retail ? 1 : Gamma( 128f / 255f ) * 2);
			Console.WriteLine( $"{shader}, table={table}, retail={retail}, reference={reference}, neutral={middleGrey}, colour={tinted}, expected={expected}" );
			Assert.IsTrue( (middleGrey - expectedNeutral).Length < 0.004f, "Neutral detail must preserve retail base colour; legacy VMT sampling must retain its previous gamma behavior." );
			Assert.IsTrue( (tinted - expected).Length < 0.004f, "Detail RGB must modulate in the original byte domain, including retail's half-byte offset." );
			Assert.IsTrue( (tinted - reference).Length > 0.02f, "The coloured detail positive control must alter pixels." );
		}
		finally { obj.Delete(); scene.Destroy(); material.Destroy(); ManagedSceneRendering.Enabled = managed; }

		Vector3 Capture( Texture detail, float blend )
		{
			material.Set( "g_tDetail", detail ); material.Set( "g_flT2DetailBlend", blend );
			var row = Titanfall2GpuMaterialData.Capture( material ); buffer.SetData( new[] { row, row } );
			for ( var frame = 0; frame < 12; frame++ )
			{
				System.Threading.Thread.Sleep( 20 ); Application.FrameCount++; EngineGlobal.SourceEnginePanelAppFrame(); scene.GameTick( 0 ); scene.PreRender();
				camera.RenderToTexture( target, null, default ); g_pRenderDevice.Present( IntPtr.Zero ); g_pRenderDevice.ForceFlushGPU( IntPtr.Zero );
				EngineLoop.DrainFrameEndDisposables(); RenderTarget.EndOfFrame(); EngineLoop.RunAsyncTasks();
			}
			var bytes = new ushort[96 * 96 * 4]; target.GetPixels<ushort>( (0, 0, 96, 96), 0, 0, bytes, ImageFormat.RGBA16161616F );
			var offset = (48 * 96 + 48) * 4;
			return new Vector3( (float)BitConverter.UInt16BitsToHalf( bytes[offset] ), (float)BitConverter.UInt16BitsToHalf( bytes[offset + 1] ), (float)BitConverter.UInt16BitsToHalf( bytes[offset + 2] ) );
		}
	}
}
