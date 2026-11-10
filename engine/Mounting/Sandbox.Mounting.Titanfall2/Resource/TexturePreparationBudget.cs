using System.Threading;

/// <summary>
/// Counts every retained CPU payload, including the payload that crosses the target.
/// Decoding has to finish before its size is known, so in-flight workers may overshoot;
/// callers stop starting texture reads once the target is reached. Never blocks a worker
/// waiting for the main thread that may itself be waiting for that worker's material.
/// </summary>
sealed class Titanfall2TexturePreparationBudget
{
	internal long CapacityBytes { get; }
	long _bytes;
	long _peakBytes;

	internal Titanfall2TexturePreparationBudget( long capacityBytes )
	{
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero( capacityBytes );
		CapacityBytes = capacityBytes;
	}

	internal long RetainedBytes => Interlocked.Read( ref _bytes );
	internal long PeakBytes => Interlocked.Read( ref _peakBytes );
	internal bool HasCapacity => RetainedBytes < CapacityBytes;

	internal bool Reserve( long bytes )
	{
		ArgumentOutOfRangeException.ThrowIfNegative( bytes );
		var current = Interlocked.Add( ref _bytes, bytes );
		long peak;
		do { peak = Interlocked.Read( ref _peakBytes ); }
		while ( current > peak && Interlocked.CompareExchange( ref _peakBytes, current, peak ) != peak );
		return current < CapacityBytes;
	}

	internal void Release( long bytes ) => Interlocked.Add( ref _bytes, -bytes );
}
