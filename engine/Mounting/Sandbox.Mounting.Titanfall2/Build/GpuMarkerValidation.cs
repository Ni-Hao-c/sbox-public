using System;
using System.Collections.Generic;
using NativeEngine;
using Sandbox.Rendering;

namespace MountingTests;

// Native regression for measured markers in the production GPU rendering path.
[TestClass, DoNotParallelize]
public class Titanfall2GpuMarkerValidation
{
	[TestMethod]
	public void VisibilityStagesAndDrawProduceMeasuredGpuMarkers()
	{
		if ( !Graphics.IsAvailable ) Assert.Inconclusive( "Requires SBOX_TEST_GRAPHICS=1." );
		var enabled = Sandbox.Diagnostics.GpuProfilerStats.Enabled;
		var frameCount = Application.FrameCount;
		var managed = ManagedSceneRendering.Enabled;
		Sandbox.SceneRenderer.EngineHooks.Install();
		RenderPipeline.WarmUp();
		var scene = new Scene();
		using var sceneScope = scene.Push();
		var material = Material.Create( $"t2-marker-{Guid.NewGuid():N}", "shaders/titanfall2_default.shader" );
		material.Set( "g_tAlbedo", Texture.White ); material.Set( "g_tOpacity", Texture.White );
		material.Set( "g_tAO", Texture.White ); material.Set( "g_tGloss", Texture.Black );
		material.Set( "g_tSpecular", Texture.Black ); material.Set( "g_tEmissive", Texture.Black );
		material.Set( "g_flT2Unlit", 1f );
		var points = new[] { new Vector3( 0, -12, -12 ), new Vector3( 0, 0, 12 ), new Vector3( 0, 12, -12 ) };
		var vertices = points.Select( ( point, n ) => new Titanfall2StaticPropGpuScene.Vertex
		{
			Position = point,
			Normal = -Vector3.Forward,
			Color = Color.White,
			TexCoord = n == 0 ? Vector2.Zero : n == 1 ? new Vector2( 0.5f, 1 ) : new Vector2( 1, 0 )
		} ).ToArray();
		var layout = new Titanfall2StaticPropGpuScene.Layout( vertices, new uint[] { 0, 1, 2, 2, 1, 0 },
			[new() { Sphere = new Vector4( 0, 0, 0, 20 ), OriginAndDistance = new Vector4( 0, 0, 0, 10000 ) }],
			Enumerable.Range( 0, 4 ).Select( n => new Titanfall2StaticPropGpuScene.Bucket { FirstVisible = (uint)n, Capacity = 1 } ).ToArray(),
			[new() { IndexCount = 6 }], [GpuInstanceTransform.From( Transform.Zero )], [default], 4,
			[material], [0], [1], BBox.FromPositionAndSize( Vector3.Zero, 40 ) );
		using var camera = new SceneCamera( "T2 GPU marker validation" )
		{
			World = scene.SceneWorld,
			Position = new Vector3( -50, 0, 0 ),
			Rotation = Rotation.Identity,
			ZNear = 1,
			ZFar = 500,
			BackgroundColor = Color.Black,
			EnablePostProcessing = false
		};
		using var target = Texture.CreateRenderTarget().WithSize( 96, 96 ).Create();
		Titanfall2StaticPropGpuScene gpu = null;
		try
		{
			ManagedSceneRendering.Enabled = false;
			Sandbox.Diagnostics.GpuProfilerStats.Enabled = true;
			gpu = new( scene.SceneWorld, layout, Color.Black, null, _ => default );
			gpu.Flags.IncludeInCubemap = true;
			for ( var frame = 0; frame < 20; ++frame )
			{
				System.Threading.Thread.Sleep( 30 );
				// Offscreen fixtures do not advance the managed application frame themselves.
				// Advance once per render, never once per pass, to preserve output ownership.
				Application.FrameCount++;
				EngineGlobal.SourceEnginePanelAppFrame(); scene.GameTick( 0 ); scene.PreRender();
				camera.RenderToTexture( target, null, default ); g_pRenderDevice.Present( IntPtr.Zero ); g_pRenderDevice.ForceFlushGPU( IntPtr.Zero );
				EngineLoop.DrainFrameEndDisposables(); RenderTarget.EndOfFrame(); EngineLoop.RunAsyncTasks();
			}
			CSceneSystem.RefreshGpuTimestampSnapshot();
			var measured = new HashSet<string>();
			for ( var row = 0; row < CSceneSystem.GetGpuTimestampCount(); ++row )
			{
				if ( !CSceneSystem.GetGpuTimestampMeasured( row ) ) continue;
				var name = CSceneSystem.GetGpuTimestampName( row );
				var duration = CSceneSystem.GetGpuTimestampDuration( row );
				Assert.IsTrue( float.IsFinite( duration ) && duration >= 0, $"Invalid GPU timestamp: {name}" );
				measured.Add( name );
			}
			foreach ( var name in new[] { "T2 GPU input barriers", "T2 GPU reset", "T2 GPU cull and LOD", "T2 GPU args", "T2 GPU output barriers", "Titanfall2StaticSceneMDI" } )
				Assert.IsTrue( measured.Contains( name ), $"Missing measured GPU marker: {name}" );
			Assert.IsTrue( measured.Any( name => name.StartsWith( "T2 GPU ", StringComparison.Ordinal ) && name.Contains( " slot ", StringComparison.Ordinal ) ), "Missing pass/slot GPU wrapper." );
		}
		finally
		{
			gpu?.Delete(); scene.Destroy(); material.Destroy();
			Sandbox.Diagnostics.GpuProfilerStats.Enabled = enabled;
			Application.FrameCount = frameCount;
			ManagedSceneRendering.Enabled = managed;
		}
	}

}
