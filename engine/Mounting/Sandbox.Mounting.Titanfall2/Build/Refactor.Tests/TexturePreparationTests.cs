using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public class TexturePreparationTests
{
	sealed class BytesSource( int length ) : ITitanfall2AssetSource
	{
		internal int Reads;
		public string Description => "preparation-test";
		public bool TryReadAllBytes( out byte[] bytes, out string error )
		{
			Interlocked.Increment( ref Reads );
			bytes = new byte[length];
			error = null;
			return true;
		}
	}

	[TestMethod]
	public void CrossingPayloadIsCountedAndPreventsFurtherReadsUntilConsumed()
	{
		using var pool = new Titanfall2MaterialPrefetch( null, [], 8 );
		var first = new BytesSource( 12 );
		var second = new BytesSource( 4 );
		var loader = new Titanfall2TextureLoader( first );
		var next = new Titanfall2TextureLoader( second );
		Assert.IsFalse( pool.PrepareTexture( loader ) );
		Assert.AreEqual( 12L, pool.RetainedBytes, "Overshoot must remain in the accounting." );
		Assert.IsFalse( pool.PrepareTexture( next ) );
		Assert.AreEqual( 0, second.Reads, "A full budget must stop I/O, not just reject accounting after the read." );
		loader.ClearPreparation();
		loader.ClearPreparation();
		Assert.AreEqual( 0L, pool.RetainedBytes, "Consumption releases the crossing payload exactly once." );
		Assert.IsTrue( pool.PrepareTexture( next ) );
		Assert.AreEqual( 1, second.Reads );
		pool.Dispose();
		Assert.AreEqual( 0L, pool.RetainedBytes, "Cancellation also returns every retained byte." );
		Assert.IsFalse( pool.PrepareTexture( loader ), "A retired pool must not read more data." );
		Assert.AreEqual( 1, first.Reads );
	}

	[TestMethod]
	public void ExactTargetStopsReadAheadAndSharedLoaderDoesNotDoubleReserve()
	{
		using var pool = new Titanfall2MaterialPrefetch( null, [], 8 );
		var source = new BytesSource( 4 );
		var loader = new Titanfall2TextureLoader( source );
		Assert.IsTrue( pool.PrepareTexture( loader ) );
		Assert.IsTrue( pool.PrepareTexture( loader ) );
		Assert.AreEqual( 1, source.Reads );
		Assert.AreEqual( 4L, pool.RetainedBytes );
		Assert.IsFalse( pool.PrepareTexture( new Titanfall2TextureLoader( new BytesSource( 4 ) ) ) );
		Assert.AreEqual( 8L, pool.RetainedBytes );
		pool.Dispose();
		Assert.AreEqual( 0L, pool.RetainedBytes );
	}

	[TestMethod]
	public void ConcurrentCrossingReservationsKeepAnAccuratePeakAndBalance()
	{
		var budget = new Titanfall2TexturePreparationBudget( 8 );
		using var rendezvous = new Barrier( 4 );
		Parallel.For( 0, 4, _ =>
		{
			budget.Reserve( 5 );
			rendezvous.SignalAndWait();
			Assert.IsFalse( budget.HasCapacity );
			rendezvous.SignalAndWait();
			budget.Release( 5 );
		} );
		Assert.AreEqual( 20L, budget.PeakBytes );
		Assert.AreEqual( 0L, budget.RetainedBytes );
		Assert.IsTrue( budget.HasCapacity );
	}
}
