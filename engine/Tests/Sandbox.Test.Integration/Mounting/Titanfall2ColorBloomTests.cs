using System;
using System.Collections.Generic;
using System.Reflection;
using NativeEngine;
using Sandbox.Rendering;

namespace MountingTests;

[TestClass, DoNotParallelize]
public class Titanfall2ColorBloomTests
{
	[TestMethod]
	public void ProductionColorCorrectionCommandListRendersBothTables()
	{
		if ( !Graphics.IsAvailable ) Assert.Inconclusive( "Requires SBOX_TEST_GRAPHICS=1." );
		using var red = Solid( 255, 0, 0 ); using var green = Solid( 0, 255, 0 );
		var scene = new Scene();
		try
		{
			using var scope = scene.Push();
			var camera = scene.CreateObject().AddComponent<CameraComponent>();
			camera.CustomSize = new Vector2( 64, 64 ); camera.AutoExposure.Enabled = false;
			var owner = scene.CreateObject();
			owner.AddComponent<PostProcessVolume>().SceneVolume = new Sandbox.Volumes.SceneVolume { Type = Sandbox.Volumes.SceneVolume.VolumeTypes.Infinite };
			var correction = owner.AddComponent<Titanfall2ColorCorrection>();
			static Dictionary<string, string> Definition( string name ) => new()
			{
				["classname"] = "color_correction",
				["filename"] = name,
				["minfalloff"] = "-1",
				["maxfalloff"] = "-1",
				["maxweight"] = "1"
			};
			correction.ConfigureStack( "titanfall2", [Definition( "test-red.raw" ), Definition( "test-green.raw" )] );
			// Substitute only the mounted texture cache; spatial evaluation, command
			// recording, compute composition and post-process rendering remain production code.
			var entries = (Array)typeof( Titanfall2ColorCorrection ).GetField( "_entries", BindingFlags.Instance | BindingFlags.NonPublic ).GetValue( correction );
			for ( var i = 0; i < entries.Length; ++i )
				entries.GetValue( i ).GetType().GetField( "Texture", BindingFlags.Instance | BindingFlags.NonPublic ).SetValue( entries.GetValue( i ), i == 0 ? red : green );
			using var target = Texture.CreateRenderTarget().WithSize( 64, 64 ).WithFormat( ImageFormat.RGBA16161616F ).Create( "tf2-color-stack-production" );
			for ( var frame = 0; frame < 20; ++frame )
			{
				EngineGlobal.SourceEnginePanelAppFrame(); scene.GameTick( 1.0 / 60 ); scene.PreRender();
				Assert.IsTrue( camera.RenderToTexture( target, new ViewSetup { EnablePostprocessing = true, EnableUI = false, EnableDebugOverlays = false } ) );
				g_pRenderDevice.Present( IntPtr.Zero ); g_pRenderDevice.ForceFlushGPU( IntPtr.Zero );
				EngineLoop.DrainFrameEndDisposables(); RenderTarget.EndOfFrame(); EngineLoop.RunAsyncTasks();
			}
			var pixels = new ushort[64 * 64 * 4];
			target.GetPixels<ushort>( (0, 0, 64, 64), 0, 0, pixels, ImageFormat.RGBA16161616F );
			for ( var i = 0; i < pixels.Length; i += 4 )
			{
				var r = (float)BitConverter.UInt16BitsToHalf( pixels[i] );
				var g = (float)BitConverter.UInt16BitsToHalf( pixels[i + 1] );
				var b = (float)BitConverter.UInt16BitsToHalf( pixels[i + 2] );
				Assert.IsTrue( r > 0.1f && g > 0.1f && b < 0.01f, $"Expected both red and green LUTs, got ({r},{g},{b})." );
				Assert.AreEqual( r, g, 0.002f );
			}
		}
		finally { scene.Destroy(); }
	}

	[TestMethod]
	public void WideAndStreakBranchesAffectRenderedPixelsAndExposureRestoresTwice()
	{
		if ( !Graphics.IsAvailable ) Assert.Inconclusive( "Requires SBOX_TEST_GRAPHICS=1." );
		var scene = new Scene();
		try
		{
			using var scope = scene.Push();
			var camera = scene.CreateObject().AddComponent<CameraComponent>();
			camera.CustomSize = new Vector2( 128, 128 );
			camera.BackgroundColor = new Color( 0.4f, 0.4f, 0.4f );
			camera.AutoExposure.Enabled = true;
			camera.SceneCamera.Attributes.Set( "msaa", 0 );
			var owner = scene.CreateObject();
			var volume = owner.AddComponent<PostProcessVolume>();
			volume.SceneVolume = new Sandbox.Volumes.SceneVolume { Type = Sandbox.Volumes.SceneVolume.VolumeTypes.Infinite };
			var display = Titanfall2Tonemapping.CreateMapDisplay( owner, 0 );
			// Use an enabled host meter to make restoration observable. The imported
			// native baseline normally disables it before our effect captures the state.
			owner.Components.Get<Tonemapping>().AutoExposureEnabled = true;
			display.ForcedExposure = 1;
			display.BloomStrength = 0.5f;
			display.BloomCutoff = 0;
			display.StreakBloomCutoff = 0;
			using var target = Texture.CreateRenderTarget().WithSize( 128, 128 ).WithFormat( ImageFormat.RGBA16161616F ).Create( "tf2-bloom-branches" );
			float Capture( float wide, float streak )
			{
				display.WideBloomAmount = wide; display.StreakBloomAmount = streak;
				for ( var frame = 0; frame < 20; ++frame )
				{
					EngineGlobal.SourceEnginePanelAppFrame(); scene.GameTick( 1.0 / 60 ); scene.PreRender();
					Assert.IsTrue( camera.RenderToTexture( target, new ViewSetup { EnablePostprocessing = true, EnableUI = false, EnableDebugOverlays = false } ) );
					g_pRenderDevice.Present( IntPtr.Zero ); g_pRenderDevice.ForceFlushGPU( IntPtr.Zero );
					EngineLoop.DrainFrameEndDisposables(); RenderTarget.EndOfFrame(); EngineLoop.RunAsyncTasks();
				}
				var pixels = new ushort[128 * 128 * 4];
				target.GetPixels<ushort>( (0, 0, 128, 128), 0, 0, pixels, ImageFormat.RGBA16161616F );
				var sum = 0d;
				for ( var i = 0; i < pixels.Length; i += 4 )
				{
					var red = (float)BitConverter.UInt16BitsToHalf( pixels[i] );
					Assert.IsTrue( float.IsFinite( red ) ); sum += red;
				}
				return (float)(sum / (128 * 128));
			}
			var core = Capture( 0, 0 );
			var wide = Capture( 1, 0 );
			var streak = Capture( 0, 1 );
			Assert.IsTrue( wide > core + 0.005f, $"Wide branch missing: core={core}, wide={wide}" );
			Assert.IsTrue( streak > core + 0.005f, $"Streak branch missing: core={core}, streak={streak}" );
			Assert.IsFalse( camera.AutoExposure.Enabled );
			display.Enabled = false; Assert.IsTrue( camera.AutoExposure.Enabled );
			display.Enabled = true; Capture( 0, 0 );
			display.Enabled = false; Assert.IsTrue( camera.AutoExposure.Enabled );
			Console.WriteLine( $"Bloom output means: core={core}, wide={wide}, streak={streak}" );
		}
		finally { scene.Destroy(); }
	}

	static Texture Solid( byte red, byte green, byte blue )
	{
		var data = new byte[32 * 32 * 32 * 4];
		for ( var i = 0; i < data.Length; i += 4 ) { data[i] = red; data[i + 1] = green; data[i + 2] = blue; data[i + 3] = 255; }
		return Texture.CreateVolume( 32, 32, 32 ).WithData( data ).WithStaticUsage().Finish();
	}

	[TestMethod]
	public void GpuLutCompositionAccumulatesASecondBatchAcrossEveryDepthSlice()
	{
		if ( !Graphics.IsAvailable ) Assert.Inconclusive( "Requires SBOX_TEST_GRAPHICS=1." );
		using var red = Solid( 255, 0, 0 ); using var green = Solid( 0, 255, 0 );
		using var blue = Solid( 0, 0, 255 ); using var white = Solid( 255, 255, 255 );
		using var first = Texture.CreateVolume( 32, 32, 32 ).WithFormat( ImageFormat.RGBA16161616F ).WithUAVBinding().Finish();
		using var second = Texture.CreateVolume( 32, 32, 32 ).WithFormat( ImageFormat.RGBA16161616F ).WithUAVBinding().Finish();
		var shader = new ComputeShader( "shaders/titanfall2_color_lut_blend_cs.shader" );
		using ( Graphics.Scope.Create() )
		{
			shader.Attributes.Get().SetGlobalBindlessDescriptorSet();
			foreach ( var texture in new[] { red, green, blue, white } ) Graphics.ResourceBarrierTransition( texture, ResourceState.NonPixelShaderResource );
			shader.Attributes.Set( "T2LutBlend0", red ); shader.Attributes.Set( "T2LutBlend1", green );
			shader.Attributes.Set( "T2LutBlend2", blue ); shader.Attributes.Set( "T2LutBlend3", white );
			shader.Attributes.Set( "T2LutBlendPrevious", red ); shader.Attributes.Set( "T2LutBlendFirst", 1 );
			shader.Attributes.Set( "T2LutBlendWeights", new Vector4( 0.2f ) ); shader.Attributes.Set( "T2LutBlendOutput", first );
			Graphics.ResourceBarrierTransition( first, ResourceState.UnorderedAccess ); shader.Dispatch( 32, 32, 32 );
			Graphics.ResourceBarrierTransition( first, ResourceState.NonPixelShaderResource );
			shader.Attributes.Set( "T2LutBlendPrevious", first ); shader.Attributes.Set( "T2LutBlendFirst", 0 );
			shader.Attributes.Set( "T2LutBlendWeights", new Vector4( 0.2f, 0, 0, 0 ) ); shader.Attributes.Set( "T2LutBlendOutput", second );
			Graphics.ResourceBarrierTransition( second, ResourceState.UnorderedAccess ); shader.Dispatch( 32, 32, 32 );
			Graphics.ResourceBarrierTransition( second, ResourceState.PixelShaderResource );
		}
		g_pRenderDevice.ForceFlushGPU( IntPtr.Zero );
		for ( var slice = 0; slice < 32; ++slice )
		{
			var pixels = new ushort[32 * 32 * 4];
			second.GetPixels<ushort>( (0, 0, 32, 32), slice, 0, pixels, ImageFormat.RGBA16161616F );
			for ( var i = 0; i < pixels.Length; i += 4 )
			{
				Assert.AreEqual( 0.6f, (float)BitConverter.UInt16BitsToHalf( pixels[i] ), 0.002f );
				Assert.AreEqual( 0.4f, (float)BitConverter.UInt16BitsToHalf( pixels[i + 1] ), 0.002f );
				Assert.AreEqual( 0.4f, (float)BitConverter.UInt16BitsToHalf( pixels[i + 2] ), 0.002f );
			}
		}
	}
}
