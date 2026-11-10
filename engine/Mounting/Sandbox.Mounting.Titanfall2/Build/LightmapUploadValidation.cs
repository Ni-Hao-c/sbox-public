using System;
using System.Runtime.InteropServices;
using NativeEngine;
using Sandbox.Rendering;
using Titanfall2;

namespace MountingTests;

[TestClass, DoNotParallelize]
public class Titanfall2LightmapUploadValidation
{
	[TestMethod]
	[DataRow( 4, false )]
	[DataRow( 32, true )]
	public void UploadedSkyDirectionsStayWithin565Quantization( int size, bool mips )
	{
		if ( !Graphics.IsAvailable ) Assert.Inconclusive( "Requires SBOX_TEST_GRAPHICS=1." );
		var rgba = new byte[size * size * 4];
		for ( var i = 0; i < size * size; ++i )
		{
			rgba[i * 4] = (byte)(i * 37); rgba[i * 4 + 1] = (byte)(i * 71);
			rgba[i * 4 + 2] = (byte)(i * 113); rgba[i * 4 + 3] = 255;
		}
		var page = CreatePage( rgba, size, mips, rgba );
		try
		{
			Assert.AreEqual( mips ? 3 : 1, page.SkyB.Mips );
			var chain = MapLoader.BuildLightmapMipChain( rgba, size, size, page.SkyB.Mips );
			for ( var mip = 0; mip < chain.Count; ++mip )
			{
				var expected = chain[mip];
				var actual = MemoryMarshal.AsBytes( page.SkyB.GetPixels( mip ).AsSpan() ).ToArray();
				var maximum = new int[3];
				for ( var i = 0; i < expected.Length / 4; ++i )
					for ( var c = 0; c < 3; ++c ) maximum[c] = Math.Max( maximum[c], Math.Abs( actual[i * 4 + c] - expected[i * 4 + c] ) );
				Console.WriteLine( $"SkyB native upload {size}x{size}, {page.SkyB.ImageFormat}, mip={mip}, maximum RGB error={string.Join( ",", maximum )}" );
				Assert.IsTrue( maximum[0] <= 4 && maximum[1] <= 2 && maximum[2] <= 4, "Native upload must preserve the source directions within 565 precision; a valid texture handle alone is insufficient." );
				var skyA = MemoryMarshal.AsBytes( page.SkyA.GetPixels( mip ).AsSpan() ).ToArray();
				CollectionAssert.AreEqual( expected, skyA, "RGBA lightmap mips must preserve native upload ordering too." );
				var realTime = MemoryMarshal.AsBytes( page.RealTimeB.GetPixels( mip ).AsSpan() ).ToArray();
				var realTimeError = realTime.Zip( expected, ( a, b ) => Math.Abs( a - b ) ).Max();
				Console.WriteLine( $"RealTimeB native readback mip={mip}, maximum error={realTimeError}" );
				if ( mips )
				{
					Assert.AreEqual( ImageFormat.RGBA8888, page.RealTimeB.ImageFormat );
					Assert.AreEqual( 0, realTimeError, "RealTimeB must use the exact RGBA path for multi-mip uploads." );
				}
				// Native 4444 -> RGBA readback expands nibbles with << 4 (255 reads as 240), so
				// nearest-level packing plus that conversion has a maximum byte error of 22.
				Assert.IsTrue( realTimeError <= 22, "4444 lightmap mips must preserve their source within native readback precision." );
			}
		}
		finally { Dispose( page ); }
	}

	[TestMethod]
	[DataRow( false )]
	[DataRow( true )]
	public void UploadedSkyDirectionsMatchRgbaInWorldShader( bool table )
	{
		if ( !Graphics.IsAvailable ) Assert.Inconclusive( "Requires SBOX_TEST_GRAPHICS=1." );
		Sandbox.SceneRenderer.EngineHooks.Install(); RenderPipeline.WarmUp();
		var managed = ManagedSceneRendering.Enabled;
		var scene = new Scene(); using var scope = scene.Push();
		var rgba = Enumerable.Range( 0, 16 ).SelectMany( _ => new byte[] { 64, 128, 128, 255 } ).ToArray();
		var page = CreatePage( rgba, 4, false );
		using var reference = Texture.Create( 4, 4 ).WithData( rgba ).Finish();
		using var neutral = Texture.Create( 1, 1 ).WithData( new byte[] { 128, 128, 128, 255 } ).Finish();
		var material = Material.Create( $"t2-lightmap-upload-{Guid.NewGuid():N}", "shaders/titanfall2_world_lightmapped.shader" );
		material.Set( "g_tAlbedo", Texture.White ); material.Set( "g_tOpacity", Texture.White ); material.Set( "g_tAO", Texture.White );
		material.Set( "g_tGloss", Texture.Black ); material.Set( "g_tSpecular", Texture.Black ); material.Set( "g_tEmissive", Texture.Black );
		material.Set( "g_tLightmapSkyA", page.SkyA ); material.Set( "g_flT2LightmapIntensity", 1f );
		material.Set( "g_vT2LightmapSkyColor", new Vector4( 1, 2, 3, 0 ) ); material.Set( "g_flT2HasSunLightmap", 0f );
		material.SetFeature( "F_T2_WORLD_MATERIAL_TABLE", table ? 1 : 0 );
		using var buffer = new GpuBuffer<Titanfall2GpuMaterialData>( 2 );
		var points = new[] { new Vector3( 0, -20, -20 ), new Vector3( 0, 20, -20 ), new Vector3( 0, -20, 20 ), new Vector3( 0, 20, 20 ) };
		var vertices = points.Select( p => new TitanfallWorldMaterialVertex { Position = p, Normal = -Vector3.Forward, Color = Color.White, TexCoord = new Vector2( 0.5f ), LightmapTexCoord = new Vector2( 0.5f ), MaterialId = 1 } ).ToArray();
		var mesh = new Mesh( "t2-lightmap-upload", material ); mesh.CreateVertexBuffer( 4, vertices ); mesh.CreateIndexBuffer( 6, new[] { 0, 2, 1, 1, 2, 3 } );
		mesh.Bounds = BBox.FromPositionAndSize( Vector3.Zero, 60 );
		var obj = new SceneObject( scene.SceneWorld, Model.Builder.AddMesh( mesh ).Create(), Transform.Zero ); obj.Flags.IncludeInCubemap = true;
		using var camera = new SceneCamera( "T2 lightmap upload regression" ) { World = scene.SceneWorld, Position = new Vector3( -60, 0, 0 ), Rotation = Rotation.Identity, ZNear = 1, ZFar = 1000, BackgroundColor = Color.Black, EnablePostProcessing = false, AntiAliasing = false };
		camera.Attributes.Set( "T2WorldMaterials", buffer );
		using var target = Texture.CreateRenderTarget().WithSize( 96, 96 ).WithFormat( ImageFormat.RGBA16161616F ).Create();
		try
		{
			ManagedSceneRendering.Enabled = false;
			var expected = Capture( reference ); var actual = Capture( page.SkyB ); var control = Capture( neutral );
			Console.WriteLine( $"SkyB shader table={table}, RGBA={expected}, packed={actual}, neutral={control}" );
			Assert.IsTrue( expected.Length > 0.05f, "Reference sky illumination must be visible." );
			Assert.IsTrue( (expected - control).Length > 0.05f, "The direction texture must affect rendered pixels." );
			Assert.IsTrue( (actual - expected).Length < expected.Length * 0.1f, "Packed directions must match RGBA illumination within quantization error." );
		}
		finally { obj.Delete(); scene.Destroy(); material.Destroy(); Dispose( page ); ManagedSceneRendering.Enabled = managed; }

		Vector3 Capture( Texture direction )
		{
			material.Set( "g_tLightmapSkyB", direction );
			var row = Titanfall2GpuMaterialData.Capture( material ); buffer.SetData( new[] { row, row } );
			for ( var frame = 0; frame < 12; ++frame )
			{
				System.Threading.Thread.Sleep( 20 ); Application.FrameCount++; EngineGlobal.SourceEnginePanelAppFrame(); scene.GameTick( 0 ); scene.PreRender();
				camera.RenderToTexture( target, null, default ); g_pRenderDevice.Present( IntPtr.Zero ); g_pRenderDevice.ForceFlushGPU( IntPtr.Zero );
				EngineLoop.DrainFrameEndDisposables(); RenderTarget.EndOfFrame(); EngineLoop.RunAsyncTasks();
			}
			var pixels = new ushort[96 * 96 * 4]; target.GetPixels<ushort>( (0, 0, 96, 96), 0, 0, pixels, ImageFormat.RGBA16161616F );
			var offset = (48 * 96 + 48) * 4;
			return new Vector3( (float)BitConverter.UInt16BitsToHalf( pixels[offset] ), (float)BitConverter.UInt16BitsToHalf( pixels[offset + 1] ), (float)BitConverter.UInt16BitsToHalf( pixels[offset + 2] ) );
		}
	}

	static RuntimeLightmapPage CreatePage( byte[] sky, int size, bool mips, byte[] skyA = null ) => MapLoader.CreateLightmapPage(
		new Titanfall2BspReader.BspLightmapPage { Width = size, Height = size, SkyA = skyA ?? new byte[sky.Length], SkyB = sky, RealTimeA = new byte[sky.Length], RealTimeB = sky },
		Array.Empty<short>(), Array.Empty<Titanfall2WorldLight>(), mips ? Enumerable.Repeat( true, size * size ).ToArray() : null );

	static void Dispose( RuntimeLightmapPage page )
	{
		page.SkyA?.Dispose(); page.SkyB?.Dispose(); page.RealTimeA?.Dispose(); page.RealTimeB?.Dispose(); page.SunChannels?.Dispose();
	}
}
