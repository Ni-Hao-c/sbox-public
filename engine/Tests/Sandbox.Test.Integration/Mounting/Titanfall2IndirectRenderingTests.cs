using System;
using NativeEngine;
using Sandbox.Rendering;

namespace MountingTests;

/// <summary>The v3 capability gate: verify source-ID addressing through actual native MDI.</summary>
[TestClass]
public class Titanfall2IndirectRenderingTest
{
	struct ProbeVertex
	{
		[Sandbox.VertexLayout.Position] public Vector3 Position;
	}

	[System.Runtime.InteropServices.StructLayout( System.Runtime.InteropServices.LayoutKind.Sequential, Size = 32 )]
	struct PaddedIndirectRecord
	{
		public GpuBuffer.IndirectDrawIndexedArguments Draw;
	}

	[DataTestMethod]
	[DataRow( 0u, true )]
	[DataRow( 20u, true )]
	[DataRow( 0u, false )]
	[DataRow( 20u, false )]
	[DataRow( 32u, true )]
	[DataRow( 32u, false )]
	public void MultiDrawUsesCompiledBucketBasesAndPreservesGeometryRanges( uint stride, bool rebasedIndices )
	{
		if ( !Graphics.IsAvailable ) Assert.Inconclusive( "Requires SBOX_TEST_GRAPHICS=1." );
		using var positions = new GpuBuffer<Vector4>( 12 );
		using var vertices = new GpuBuffer<ProbeVertex>( 12, GpuBuffer.UsageFlags.Vertex );
		// A vertex attribute follows native BaseVertex addressing. SV_VertexID does not
		// include BaseVertex on this shader backend, so it cannot address a shared page.
		vertices.SetData( Enumerable.Range( 0, 12 ).Select( index => new ProbeVertex { Position = new Vector3( 0, 0, index ) } ).ToArray() );
		using var visible = new GpuBuffer<uint>( 5 );
		using var vertexBuckets = new GpuBuffer<uint>( 12 );
		using var transforms = new GpuBuffer<Vector4>( 4 );
		using var probes = new GpuBuffer<Vector4>( 4 );
		using var indices = new GpuBuffer<uint>( 9, GpuBuffer.UsageFlags.Index );
		using var args = new GpuBuffer( 3, stride == 32 ? 32 : 20, GpuBuffer.UsageFlags.IndirectDrawArguments );
		positions.SetData( new Vector4[]
		{
			new( -0.12f, -0.3f, 0.5f, 1 ), new( 0, 0.3f, 0.5f, 1 ), new( 0.12f, -0.3f, 0.5f, 1 ),
			new( -0.62f, -0.3f, 0.5f, 1 ), new( -0.5f, 0.3f, 0.5f, 1 ), new( -0.38f, -0.3f, 0.5f, 1 ),
			new( 0.13f, -0.3f, 0.5f, 1 ), new( 0.25f, 0.3f, 0.5f, 1 ), new( 0.37f, -0.3f, 0.5f, 1 ),
			new( -0.37f, -0.3f, 0.5f, 1 ), new( -0.25f, 0.3f, 0.5f, 1 ), new( -0.13f, -0.3f, 0.5f, 1 )
		} );
		visible.SetData( new uint[] { 3, 0, 3, 0, 1 } );
		vertexBuckets.SetData( new uint[] { 0, 0, 0, 0, 0, 0, 2, 2, 2, 4, 4, 4 } );
		transforms.SetData( new Vector4[] { new( 5 ), new( 0.5f, 0, 0, 0 ), new( 5 ), new( -0.5f, 0, 0, 0 ) } );
		probes.SetData( new Vector4[] { new( 0, 0, 1, 1 ), new( 0, 1, 0, 1 ), new( 0, 0, 1, 1 ), new( 1, 0, 0, 1 ) } );
		indices.SetData( rebasedIndices ? new uint[] { 0, 1, 2, 6, 7, 8, 9, 10, 11 } : new uint[] { 0, 1, 2, 3, 4, 5, 6, 7, 8 } );
		var baseVertex = rebasedIndices ? 0 : 3;
		var drawArguments = new GpuBuffer.IndirectDrawIndexedArguments[]
		{
			new() { IndexCount = 3, InstanceCount = 1, FirstIndex = 0, BaseVertex = 0, FirstInstance = 0 },
			new() { IndexCount = 3, InstanceCount = 1, FirstIndex = 3, BaseVertex = baseVertex, FirstInstance = 0 },
			new() { IndexCount = 3, InstanceCount = 1, FirstIndex = 6, BaseVertex = baseVertex, FirstInstance = 0 }
		};
		if ( stride == 32 ) args.SetData<PaddedIndirectRecord>( drawArguments.Select( draw => new PaddedIndirectRecord { Draw = draw } ).ToArray() );
		else args.SetData<GpuBuffer.IndirectDrawIndexedArguments>( drawArguments );
		var uploaded = new GpuBuffer.IndirectDrawIndexedArguments[3];
		if ( stride == 32 )
		{
			var padded = new PaddedIndirectRecord[3];
			args.GetData<PaddedIndirectRecord>( padded );
			for ( var i = 0; i < 3; ++i ) uploaded[i] = padded[i].Draw;
		}
		else args.GetData<GpuBuffer.IndirectDrawIndexedArguments>( uploaded );
		Assert.AreEqual( 6u, uploaded[2].FirstIndex, "Last indirect record was not uploaded intact." );
		Assert.AreEqual( baseVertex, uploaded[2].BaseVertex );
		Assert.AreEqual( 0u, uploaded[2].FirstInstance );
		var material = Material.FromShader( "shaders/tests/static_scene_indirect_probe.shader" );
		Assert.IsFalse( material.native.GetMode().IsNull, "The diagnostic material has no default rendering mode." );
		var attributes = new RenderAttributes();
		attributes.Set( "ProbePositions", positions );
		attributes.Set( "ProbeVisibleIds", visible );
		attributes.Set( "ProbeVertexBuckets", vertexBuckets );
		attributes.Set( "ProbeTransforms", transforms );
		attributes.Set( "ProbeLighting", probes );
		using var target = RenderTarget.GetTemporary( 64, 64, ImageFormat.RGBA8888, ImageFormat.None );
		byte[] Render( uint offset, uint count )
		{
			var commands = new CommandList( "StaticSceneIndirectProbe" );
			commands.SetRenderTarget( target );
			commands.Clear( Color.Blue );
			commands.ResourceBarrierTransition( args, ResourceState.IndirectArgument );
			commands.ResourceBarrierTransition( indices, ResourceState.VertexOrIndexBuffer );
			commands.ResourceBarrierTransition( vertices, ResourceState.VertexOrIndexBuffer );
			commands.ResourceBarrierTransition( positions, ResourceState.NonPixelShaderResource );
			commands.ResourceBarrierTransition( visible, ResourceState.NonPixelShaderResource );
			commands.ResourceBarrierTransition( vertexBuckets, ResourceState.NonPixelShaderResource );
			commands.ResourceBarrierTransition( transforms, ResourceState.NonPixelShaderResource );
			commands.ResourceBarrierTransition( probes, ResourceState.NonPixelShaderResource );
			commands.DrawIndexedInstancedIndirect( vertices, indices, material, args, offset, attributes, Graphics.PrimitiveType.Triangles, count, stride );
			using ( Graphics.Scope.Create() )
			{
				attributes.Get().SetGlobalBindlessDescriptorSet();
				Graphics.Context.SetScissorRect( new Rect( 0, 0, 64, 64 ) );
				commands.ExecuteOnRenderThread();
			}
			g_pRenderDevice.ForceFlushGPU( IntPtr.Zero );
			var data = new byte[64 * 64 * 4];
			target.ColorTarget.GetPixels<byte>( (0, 0, 64, 64), 0, 0, data, ImageFormat.RGBA8888 );
			return data;
		}
		var left = (32 * 64 + 16) * 4;
		byte[] control = null;
		// First use may queue a native pipeline specialization. Advance actual engine frames
		// before judging the pixel result, rather than treating a cold PSO as unsupported MDI.
		for ( var frame = 0; frame < 20; ++frame )
		{
			EngineGlobal.SourceEnginePanelAppFrame();
			control = Render( 0, 1 );
			if ( control[left] > 240 && control[left + 2] < 10 ) break;
			g_pRenderDevice.Present( IntPtr.Zero );
			EngineLoop.DrainFrameEndDisposables();
			RenderTarget.EndOfFrame();
			EngineLoop.RunAsyncTasks();
		}
		Assert.IsTrue( control[left] > 240 && control[left + 1] < 10 && control[left + 2] < 10,
			$"Single-draw positive control failed: {control[left]}, {control[left + 1]}, {control[left + 2]}; corner {control[0]}, {control[1]}, {control[2]}." );
		// Offset is one argument ELEMENT, not one byte. Both following records must execute.
		var secondControl = Render( 2, 1 );
		var secondSample = (32 * 64 + 40) * 4;
		Assert.IsTrue( secondControl[secondSample] < 10 && secondControl[secondSample + 1] > 240 && secondControl[secondSample + 2] < 10,
			$"Second single-draw control failed: {secondControl[secondSample]}, {secondControl[secondSample + 1]}, {secondControl[secondSample + 2]}." );
		var pixels = Render( 1, 2 );
		var redPixels = 0;
		var greenPixels = 0;
		for ( var i = 0; i < pixels.Length; i += 4 )
		{
			if ( pixels[i] > 240 && pixels[i + 1] < 10 && pixels[i + 2] < 10 ) redPixels++;
			if ( pixels[i] < 10 && pixels[i + 1] > 240 && pixels[i + 2] < 10 ) greenPixels++;
		}
		Console.WriteLine( $"MDI stride={stride}, rebasedIndices={rebasedIndices}: red={redPixels}, green={greenPixels}" );
		left = (32 * 64 + 24) * 4;
		var right = (32 * 64 + 40) * 4;
		Assert.IsTrue( pixels[left] > 240 && pixels[left + 1] < 10 && pixels[left + 2] < 10,
			$"First record/source 3 should be red; got {pixels[left]}, {pixels[left + 1]}, {pixels[left + 2]}." );
		Assert.IsTrue( pixels[right] < 10 && pixels[right + 1] > 240 && pixels[right + 2] < 10,
			$"Second record/source 1 should be green; got {pixels[right]}, {pixels[right + 1]}, {pixels[right + 2]}." );
	}
}
