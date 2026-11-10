using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sandbox;
using Sandbox.Rendering;
using System.Runtime.InteropServices;
using static Titanfall2StaticPropGpuScene;
using GpuVertex = Titanfall2StaticPropGpuScene.Vertex;

// Slots returned by TryAcquire are borrowed. These tests verify that the pool, including
// its failed-construction path, disposes them exactly once; the caller must not dispose them.
#pragma warning disable CA2000

[TestClass]
public class RefactorTests
{
	[DataTestMethod]
	[DataRow( "models\\industrial\\hanging_plant_misters", false )]
	[DataRow( "models/industrial/hanging_plant_mister_cap", false )]
	[DataRow( "effects/mist", true )]
	[DataRow( "effects/mist01", true )]
	[DataRow( "effects/ground_mist_loop", true )]
	[DataRow( "effects/MIST_02", true )]
	[DataRow( "effects/smoke_column", true )]
	[DataRow( "models/water/water_puddle_decal_01", false )]
	public void CollisionEffectNamesDoNotFilterSolidMisterMaterials( string name, bool expected )
	{
		Assert.AreEqual( expected, Titanfall2CollisionFilter.IsVisualEffectName( name ) );
	}

	[DataTestMethod]
	[DataRow( 0f, 0f, 0f, true )]
	[DataRow( -0f, 0f, -1f, true )]
	[DataRow( 0.000001f, 0f, 0f, false )]
	[DataRow( 0f, -0.000001f, 0f, false )]
	[DataRow( 0f, 0f, 0.000001f, false )]
	[DataRow( 0f, 0f, float.NaN, false )]
	[DataRow( float.NaN, 0f, 0f, false )]
	[DataRow( 0f, 0f, float.NegativeInfinity, false )]
	public void SimpleSurfaceClassificationDoesNotDiscardSmallOrInvalidParameters( float x, float y, float detail, bool expected )
	{
		Assert.AreEqual( expected, IsSimpleSurface( new Vector4( x, y, 9, 9 ), detail ) );
	}

	sealed class Slot : IDisposable
	{
		public int DisposeCount;
		public void Dispose() => DisposeCount++;
	}

	[TestMethod]
	public void ViewPoolExhaustionNeverAllocatesOrReusesAnInFlightSlot()
	{
		var created = new List<Slot>();
		using var pool = new Titanfall2GpuViewPool<Slot>( () => { var slot = new Slot(); created.Add( slot ); return slot; }, 2 );
		Assert.AreEqual( 2, created.Count );
		Assert.IsTrue( pool.TryAcquire( 1, out var first ) );
		Assert.IsTrue( pool.TryAcquire( 1, out var second ) );
		Assert.AreNotSame( first, second );
		for ( var i = 0; i < 100; ++i ) Assert.IsFalse( pool.TryAcquire( 1, out _ ) );
		Assert.AreEqual( 2, created.Count );
		Assert.AreEqual( 2, pool.HighWaterMark );
		Assert.AreEqual( 100L, pool.OverflowCount );
		Assert.AreSame( first, pool.Acquire( 2 ) );
		Assert.AreSame( second, pool.Acquire( 2 ) );
		pool.Dispose();
		Assert.IsTrue( created.All( slot => slot.DisposeCount == 1 ) );
		Assert.ThrowsException<ObjectDisposedException>( () => pool.TryAcquire( 3, out _ ) );
	}

	[TestMethod]
	public void ViewPoolConstructionFailureReleasesCompletedSlots()
	{
		var first = new Slot();
		var count = 0;
		Assert.ThrowsException<IOException>( () => new Titanfall2GpuViewPool<Slot>(
			() => ++count == 1 ? first : throw new IOException( "allocation failed" ), 3 ) );
		Assert.AreEqual( 1, first.DisposeCount );
	}

	[TestMethod]
	public void ConcurrentRecordingsReceiveDistinctSlotsUntilCapacity()
	{
		using var pool = new Titanfall2GpuViewPool<Slot>( () => new(), 4 );
		var results = new System.Collections.Concurrent.ConcurrentBag<Slot>();
		Parallel.For( 0, 64, _ => { if ( pool.TryAcquire( 7, out var slot ) ) results.Add( slot ); } );
		Assert.AreEqual( 4, results.Count );
		Assert.AreEqual( 4, results.Distinct().Count() );
		Assert.AreEqual( 60L, pool.OverflowCount );
	}

	[TestMethod]
	public async Task NewMapGenerationCancelsOldDelayedWorkWithoutCancellingTheNewTicket()
	{
		var generations = new Titanfall2SceneReleaseGeneration();
		var old = generations.Capture();
		var delayed = Task.Delay( TimeSpan.FromMinutes( 1 ), old.Cancellation );
		generations.Advance();
		var current = generations.Capture();
		await Assert.ThrowsExceptionAsync<TaskCanceledException>( async () => await delayed );
		Assert.IsFalse( generations.IsCurrent( old ) );
		Assert.IsTrue( generations.IsCurrent( current ) );
		Assert.IsFalse( current.Cancellation.IsCancellationRequested );
		generations.Advance();
		Assert.IsFalse( generations.IsCurrent( current ) );
	}

	static Layout MakeLayout() => new(
		new GpuVertex[3], new uint[3],
		[
			new Candidate { SourceId = 2, FirstBucket = 0, CastShadows = 1 },
			new Candidate { SourceId = 5, FirstBucket = 0, CastShadows = 1 },
			new Candidate { SourceId = 8, FirstBucket = 0 }
		],
		Enumerable.Range( 0, 4 ).Select( i => new Bucket { FirstVisible = (uint)(i * 3), Capacity = 3 } ).ToArray(),
		Enumerable.Range( 0, 4 ).Select( i => new Draw { IndexCount = 3, FirstIndex = (uint)i, BaseVertex = 7, Bucket = (uint)i } ).ToArray(),
		new GpuInstanceTransform[9], new Titanfall2ProbeData[9], 12,
		[], [], [], default );

	[DataTestMethod]
	[DataRow( false, 3 )]
	[DataRow( true, 2 )]
	public void ConservativeOutputsPreserveSourceIdsAndShadowEligibility( bool shadow, int expected )
	{
		var layout = MakeLayout();
		var output = BuildConservativeOutput( layout, shadow );
		Assert.AreEqual( (uint)expected, output.Arguments[0].InstanceCount );
		Assert.IsTrue( output.Arguments.All( args => args.InstanceCount == expected ) );
		var expectedIds = layout.Candidates.Where( c => !shadow || c.CastShadows != 0 ).Select( c => c.SourceId ).ToArray();
		CollectionAssert.AreEqual( expectedIds, output.Visible.Take( expected ).ToArray() );
		for ( var i = 0; i < output.Arguments.Length; ++i )
		{
			Assert.AreEqual( layout.Draws[i].FirstIndex, output.Arguments[i].FirstIndex );
			Assert.AreEqual( layout.Draws[i].BaseVertex, output.Arguments[i].BaseVertex );
			Assert.AreEqual( 0u, output.Arguments[i].FirstInstance );
		}
	}

	[TestMethod]
	public void MemoryBudgetIncludesAllSlotsAndBothReadOnlyOutputs()
	{
		var layout = MakeLayout();
		var small = GetBufferBudget( layout, 1 );
		var large = GetBufferBudget( layout, 8 );
		var expectedOutput = 12L * sizeof( uint ) + 4L * sizeof( uint ) + 4L * Marshal.SizeOf<GpuBuffer.IndirectDrawIndexedArguments>();
		Assert.AreEqual( expectedOutput, small.OutputBytes );
		Assert.AreEqual( small.StaticBytes + 3 * expectedOutput, small.TotalBytes );
		Assert.AreEqual( small.TotalBytes + 7 * expectedOutput, large.TotalBytes );
		Assert.ThrowsException<ArgumentOutOfRangeException>( () => GetBufferBudget( layout, 0 ) );
	}

	[TestMethod]
	public void ConservativeOutputsKeepSeparateModelBucketsAndRejectOverflow()
	{
		var layout = MakeLayout();
		var additionalBuckets = Enumerable.Range( 0, 4 ).Select( i => new Bucket { FirstVisible = (uint)(12 + i), Capacity = 1 } );
		layout = layout with
		{
			Candidates = [.. layout.Candidates, new Candidate { SourceId = 1, FirstBucket = 4, CastShadows = 1 }],
			Buckets = [.. layout.Buckets, .. additionalBuckets],
			Draws = [.. layout.Draws, new Draw { Bucket = 4, IndexCount = 9, BaseVertex = 13 }],
			VisibleCapacity = 16
		};
		var output = BuildConservativeOutput( layout, shadow: true );
		Assert.AreEqual( 2u, output.Counts[0] );
		Assert.AreEqual( 1u, output.Counts[4] );
		Assert.AreEqual( 1u, output.Visible[12] );
		Assert.AreEqual( 1u, output.Arguments[4].InstanceCount );
		Assert.AreEqual( 13, output.Arguments[4].BaseVertex );
		layout.Buckets[0] = new Bucket { Capacity = 1 };
		Assert.ThrowsException<InvalidDataException>( () => BuildConservativeOutput( layout, shadow: false ) );
	}
}
