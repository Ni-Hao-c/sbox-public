using System;
using NativeEngine;
using Sandbox.Rendering;
using static Titanfall2StaticPropGpuScene;

namespace MountingTests;

[TestClass]
public class Titanfall2GpuVisibilityTest
{
	[TestMethod]
	public void UnifiedCullBuildsSourceIdsLodsAndAllSubmeshCountsWithoutCpuGather()
	{
		if ( !Graphics.IsAvailable ) Assert.Inconclusive( "Requires SBOX_TEST_GRAPHICS=1." );
		using var candidates = new GpuBuffer<Candidate>( 5 );
		using var buckets = new GpuBuffer<Bucket>( 8 );
		using var draws = new GpuBuffer<Draw>( 3 );
		using var counts = new GpuBuffer<uint>( 8 );
		using var visible = new GpuBuffer<uint>( 20 );
		using var shadowStates = new GpuBuffer<uint>( 10 );
		shadowStates.SetData( Enumerable.Repeat( 1u, 10 ).ToArray() );
		using var args = new GpuBuffer<GpuBuffer.IndirectDrawIndexedArguments>( 3,
			GpuBuffer.UsageFlags.Structured | GpuBuffer.UsageFlags.IndirectDrawArguments );
		static Candidate Make( uint source, float x, uint firstBucket, uint pvs, bool shadow = true ) => new()
		{
			SourceId = source,
			Sphere = new Vector4( x, 0, 0, 0.1f ),
			OriginAndDistance = new Vector4( x, 0, 0, 20 ),
			FirstBucket = firstBucket,
			CastShadows = shadow ? 1u : 0u,
			Pvs0 = pvs
		};
		var first = Make( 7, 0.5f, 0, 1 );
		candidates.SetData( new[] { first, Make( 3, 2.5f, 4, 1 ), Make( 9, 0, 4, 2 ),
			Make( 1, 30, 0, 1 ), Make( 5, 0.25f, 0, 1, false ) } );
		buckets.SetData( Enumerable.Range( 0, 8 ).Select( i => new Bucket { FirstVisible = (uint)i * 2, Capacity = 2 } ).ToArray() );
		draws.SetData( new[] { new Draw { IndexCount = 6, FirstIndex = 4, BaseVertex = 10, Bucket = 0 },
			new Draw { IndexCount = 9, FirstIndex = 20, BaseVertex = 30, Bucket = 6 },
			new Draw { IndexCount = 12, FirstIndex = 40, BaseVertex = 50, Bucket = 6 } } );
		var compute = new ComputeShader( "shaders/titanfall2_static_scene_cs.shader" );
		var attributes = compute.Attributes;
		attributes.Set( "T2Candidates", candidates ); attributes.Set( "T2Buckets", buckets ); attributes.Set( "T2Draws", draws );
		attributes.Set( "T2Counts", counts ); attributes.Set( "T2VisibleIds", visible ); attributes.Set( "T2Arguments", args );
		attributes.Set( "T2ShadowStates", shadowStates ); attributes.Set( "T2ShadowDistance", 0f );
		attributes.Set( "T2CandidateCount", 5 ); attributes.Set( "T2BucketCount", 8 ); attributes.Set( "T2DrawCount", 3 );
		attributes.Set( "T2Pvs0", 1 ); attributes.Set( "T2LodDistances", new Vector3( 1, 2, 3 ) );
		attributes.Set( "T2PolicyCamera", Vector3.Zero ); attributes.Set( "T2LodCamera", Vector3.Zero );
		// Inward planes x/y/z in [-4,4], with a separate authored distance limit.
		attributes.Set( "T2Plane0", new Vector4( 1, 0, 0, -4 ) ); attributes.Set( "T2Plane1", new Vector4( -1, 0, 0, -4 ) );
		attributes.Set( "T2Plane2", new Vector4( 0, 1, 0, -4 ) ); attributes.Set( "T2Plane3", new Vector4( 0, -1, 0, -4 ) );
		attributes.Set( "T2Plane4", new Vector4( 0, 0, 1, -4 ) ); attributes.Set( "T2Plane5", new Vector4( 0, 0, -1, -4 ) );
		void Dispatch( bool shadow )
		{
			using ( Graphics.Scope.Create() )
			{
				attributes.Get().SetGlobalBindlessDescriptorSet();
				Graphics.ResourceBarrierTransition( args, ResourceState.UnorderedAccess );
				Graphics.ResourceBarrierTransition( visible, ResourceState.UnorderedAccess );
				Graphics.ResourceBarrierTransition( counts, ResourceState.UnorderedAccess );
				Graphics.ResourceBarrierTransition( shadowStates, ResourceState.UnorderedAccess ); Graphics.UavBarrier( shadowStates );
				attributes.Set( "T2ShadowPass", shadow ? 1 : 0 );
				attributes.Set( "T2Stage", 0 ); compute.Dispatch( 8, 1, 1 ); Graphics.UavBarrier( counts );
				attributes.Set( "T2Stage", 1 ); compute.Dispatch( 5, 1, 1 ); Graphics.UavBarrier( counts );
				Graphics.UavBarrier( shadowStates );
				attributes.Set( "T2Stage", 2 ); compute.Dispatch( 3, 1, 1 );
				Graphics.ResourceBarrierTransition( args, ResourceState.UnorderedAccess, ResourceState.IndirectArgument );
				Graphics.ResourceBarrierTransition( visible, ResourceState.UnorderedAccess, ResourceState.NonPixelShaderResource );
			}
			g_pRenderDevice.ForceFlushGPU( IntPtr.Zero );
		}
		Dispatch( false );
		var result = new GpuBuffer.IndirectDrawIndexedArguments[3]; args.GetData( result );
		var ids = new uint[20]; visible.GetData( ids );
		Assert.AreEqual( 2u, result[0].InstanceCount );
		CollectionAssert.AreEquivalent( new uint[] { 7, 5 }, ids.Take( 2 ).ToArray() );
		Assert.AreEqual( 1u, result[1].InstanceCount ); Assert.AreEqual( 1u, result[2].InstanceCount );
		Assert.AreEqual( 3u, ids[12] ); // Model B's LOD2, shared by both material submeshes.
		Assert.AreEqual( 20u, result[1].FirstIndex ); Assert.AreEqual( 30, result[1].BaseVertex );
		Assert.AreEqual( 40u, result[2].FirstIndex ); Assert.AreEqual( 50, result[2].BaseVertex );
		Dispatch( true );
		args.GetData( result ); visible.GetData( ids );
		Assert.AreEqual( 1u, result[0].InstanceCount, "A non-caster must leave the shadow bucket." );
		Assert.AreEqual( 7u, ids[0] );
		Assert.AreEqual( 9u, ids[8], "Shadow eligibility must not reuse the main camera PVS." );
		attributes.Set( "T2ShadowDistance", 1024f );
		attributes.Set( "T2ShadowAnchor", new Vector3( 2000, 0, 0 ) ); Dispatch( true ); args.GetData( result );
		Assert.AreEqual( 0u, result[0].InstanceCount, "Distant casters must be gated on GPU." );
		attributes.Set( "T2ShadowAnchor", new Vector3( 800, 0, 0 ) ); Dispatch( true ); args.GetData( result );
		Assert.AreEqual( 0u, result[0].InstanceCount, "The restore hysteresis must survive another view/pass." );
		attributes.Set( "T2ShadowAnchor", Vector3.Zero ); Dispatch( true ); args.GetData( result );
		Assert.AreEqual( 1u, result[0].InstanceCount, "Near casters must restore without a CPU flag update." );
	}
}
