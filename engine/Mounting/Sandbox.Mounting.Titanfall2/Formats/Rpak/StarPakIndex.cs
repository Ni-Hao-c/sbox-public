using System.Buffers.Binary;

/// <summary>Reads the footer index and byte ranges of a Titanfall 2 STARPAK.</summary>
static class StarPakIndex
{
	// A shared handle serves range reads in ascending offset order, so a texture load
	// costs one open per file instead of one open per texture. Two serve threads per
	// file keep some read parallelism for the prefetch workers, and the batch never
	// reads ahead: every byte it reads was already requested by a waiting caller, so
	// batch memory is bounded by the buffers those callers allocated and never by a
	// streamed mip block.
	const int MaximumOpenFiles = 16;
	const int MaximumBatchRequests = 16;
	// A quarter of the 128 MB material prefetch budget; it caps how many caller
	// buffers one batch may hold, not how much data is read.
	const long MaximumBatchBytes = 32L * 1024 * 1024;
	const int MaximumServeThreads = 2;
	static readonly Dictionary<string, IReadOnlyDictionary<long, long>> Cache = new( StringComparer.OrdinalIgnoreCase );
	static readonly Dictionary<string, StarPakReader> Readers = new( StringComparer.OrdinalIgnoreCase );
	static readonly object CacheLock = new();
	static long IndexHits;
	static long IndexMisses;
	static long RangeReads;
	static long BytesRead;
	static long Failures;
	static long FileOpens;
	static long Batches;
	static long MergedRanges;
	static long ReadCalls;
	static long LastUsedClock;

	public static bool TryGetEntrySize( string path, long offset, out long size, out string error )
	{
		size = 0;
		error = null;
		if ( string.IsNullOrWhiteSpace( path ) )
		{
			error = $"STARPAK file was not found: {path}";
			return false;
		}

		try
		{
			IReadOnlyDictionary<long, long> entries;
			bool cached;
			lock ( CacheLock ) cached = Cache.TryGetValue( path, out entries );
			if ( cached ) System.Threading.Interlocked.Increment( ref IndexHits );
			else
			{
				// Only probe the disk on a cache miss: the existence check used to run once
				// per texture, which is one extra metadata open for every streamed mip read.
				if ( !File.Exists( path ) )
				{
					error = $"STARPAK file was not found: {path}";
					return false;
				}

				// Read the footer outside the global lock: holding it across file IO made
				// every texture worker wait on the first index read of any STARPAK.
				System.Threading.Interlocked.Increment( ref IndexMisses );
				var read = ReadEntries( path );
				lock ( CacheLock )
				{
					if ( !Cache.TryGetValue( path, out entries ) )
					{
						entries = read;
						Cache[path] = entries;
					}
				}
			}

			if ( !entries.TryGetValue( offset, out size ) )
			{
				error = $"STARPAK has no stream entry at 0x{offset:X}.";
				return false;
			}
			return true;
		}
		catch ( Exception exception )
		{
			System.Threading.Interlocked.Increment( ref Failures );
			error = exception.Message;
			return false;
		}
	}

	public static bool TryRead( string path, long offset, int length, out byte[] data, out string error )
	{
		data = null;
		error = null;
		try
		{
			var reader = AcquireReader( path );
			try
			{
				if ( offset < 0 || length < 0 || offset > reader.Length || length > reader.Length - offset )
				{
					error = $"STARPAK range 0x{offset:X}+{length} is invalid.";
					return false;
				}

				// A serve thread takes every request that is already queued, sorts it by
				// offset and fills the buffers in file order; the others block until their
				// own buffer is filled. Only ranges other callers already asked for are ever
				// read, so coalescing can never turn one texture's mip range into a whole
				// file read.
				var request = new PendingRequest( offset, length );
				bool serve;
				lock ( reader.Gate )
				{
					reader.Pending.Enqueue( request );
					serve = reader.Serving < MaximumServeThreads;
					if ( serve ) reader.Serving++;
				}
				if ( serve ) Serve( reader );

				var readError = request.Completion.GetAwaiter().GetResult();
				if ( readError is not null )
				{
					System.Threading.Interlocked.Increment( ref Failures );
					error = readError;
					return false;
				}

				data = request.Data;
				System.Threading.Interlocked.Increment( ref RangeReads );
				return true;
			}
			finally
			{
				ReleaseReader( reader );
			}
		}
		catch ( Exception exception )
		{
			System.Threading.Interlocked.Increment( ref Failures );
			error = exception.Message;
			data = null;
			return false;
		}
	}

	internal static StarPakStatistics GetStatistics()
	{
		lock ( CacheLock )
		{
			return new StarPakStatistics(
				Cache.Count,
				System.Threading.Interlocked.Read( ref IndexHits ),
				System.Threading.Interlocked.Read( ref IndexMisses ),
				System.Threading.Interlocked.Read( ref RangeReads ),
				System.Threading.Interlocked.Read( ref BytesRead ),
				System.Threading.Interlocked.Read( ref Failures ),
				Readers.Count,
				System.Threading.Interlocked.Read( ref FileOpens ),
				System.Threading.Interlocked.Read( ref Batches ),
				System.Threading.Interlocked.Read( ref MergedRanges ),
				System.Threading.Interlocked.Read( ref ReadCalls ) );
		}
	}

	internal static void Clear()
	{
		lock ( CacheLock )
		{
			Cache.Clear();
			foreach ( var reader in Readers.Values )
			{
				// A reader that is currently filling buffers is closed by its last Release:
				// disposing it here would turn an unload into a spurious read failure.
				if ( reader.Active == 0 ) reader.Dispose();
				else reader.Retired = true;
			}
			Readers.Clear();
		}
	}

	static StarPakReader AcquireReader( string path )
	{
		lock ( CacheLock )
		{
			if ( Readers.TryGetValue( path, out var existing ) )
			{
				existing.Active++;
				existing.LastUsed = ++LastUsedClock;
				return existing;
			}

			var pooled = Readers.Count < MaximumOpenFiles || TryEvictReader();
			var reader = new StarPakReader( path, pooled );
			reader.Active = 1;
			reader.LastUsed = ++LastUsedClock;
			if ( pooled ) Readers[path] = reader;
			return reader;
		}
	}

	static void ReleaseReader( StarPakReader reader )
	{
		lock ( CacheLock )
		{
			reader.Active--;
			if ( reader.Active > 0 || !reader.ShouldDisposeOnRelease ) return;
			// A reader retired by Clear() may already have been replaced for the same path,
			// so only drop the dictionary entry that still points at this instance.
			if ( reader.Pooled && Readers.TryGetValue( reader.Path, out var current ) && ReferenceEquals( current, reader ) )
				Readers.Remove( reader.Path );
			reader.Dispose();
		}
	}

	// Called under CacheLock. Only an idle reader may be dropped, so an evicted file
	// can never be closed underneath a thread that is still reading from it.
	static bool TryEvictReader()
	{
		StarPakReader idle = null;
		foreach ( var candidate in Readers.Values )
		{
			if ( candidate.Active > 0 ) continue;
			if ( idle is null || candidate.LastUsed < idle.LastUsed ) idle = candidate;
		}
		if ( idle is null ) return false;
		Readers.Remove( idle.Path );
		idle.Dispose();
		return true;
	}

	static void Serve( StarPakReader reader )
	{
		var batch = new List<PendingRequest>( MaximumBatchRequests );
		var released = false;
		try
		{
			while ( true )
			{
				lock ( reader.Gate )
				{
					if ( reader.Pending.Count == 0 )
					{
						reader.Serving--;
						released = true;
						return;
					}

					batch.Clear();
					var bytes = 0L;
					while ( reader.Pending.Count > 0 && batch.Count < MaximumBatchRequests )
					{
						var next = reader.Pending.Peek();
						if ( batch.Count > 0 && bytes + next.Length > MaximumBatchBytes ) break;
						reader.Pending.Dequeue();
						batch.Add( next );
						bytes += next.Length;
					}
				}

				try
				{
					ServeBatch( reader, batch );
				}
				catch ( Exception exception )
				{
					// A serving thread must never leave a blocked caller without a result.
					FailBatch( batch, exception.Message );
				}
			}
		}
		finally
		{
			// Only a catastrophic failure (allocation) reaches this: the serve slot must
			// still be released so the next caller can take the queue over.
			if ( !released ) lock ( reader.Gate ) reader.Serving--;
		}
	}

	static void ServeBatch( StarPakReader reader, List<PendingRequest> batch )
	{
		System.Threading.Interlocked.Increment( ref Batches );
		batch.Sort( static ( left, right ) => left.Offset.CompareTo( right.Offset ) );
		var index = 0;
		while ( index < batch.Count )
		{
			var first = batch[index];
			var end = first.Offset + first.Length;
			var last = index + 1;
			while ( last < batch.Count )
			{
				var next = batch[last];
				// Merge only when the union adds no bytes between the two ranges.
				if ( next.Offset > end ) break;
				if ( next.Offset + next.Length > end ) end = next.Offset + next.Length;
				last++;
			}

			try
			{
				ReadGroup( reader, batch, index, last, first.Offset, end );
			}
			catch ( Exception exception )
			{
				for ( var failed = index; failed < last; failed++ ) batch[failed].Fail( exception.Message );
			}
			index = last;
		}
	}

	static void ReadGroup( StarPakReader reader, List<PendingRequest> batch, int first, int last, long start, long end )
	{
		if ( last == first + 1 )
		{
			var request = batch[first];
			ReadExactly( reader, request.Data, start );
			request.Complete();
			return;
		}

		// Overlapping or touching requests are uncommon: read their union once and hand
		// each caller its own slice. The union is never longer than the sum of the two
		// ranges, so merging cannot read a byte that no caller asked for.
		var union = GC.AllocateUninitializedArray<byte>( checked((int)(end - start)) );
		ReadExactly( reader, union, start );
		for ( var index = first; index < last; index++ )
		{
			var request = batch[index];
			Buffer.BlockCopy( union, checked((int)(request.Offset - start)), request.Data, 0, request.Length );
			request.Complete();
		}
		System.Threading.Interlocked.Add( ref MergedRanges, last - first - 1 );
	}

	static void FailBatch( List<PendingRequest> batch, string error )
	{
		foreach ( var request in batch ) request.Fail( error );
	}

	static void ReadExactly( StarPakReader reader, byte[] buffer, long offset )
	{
		// RandomAccess keeps the handle position independent, so the shared handle needs
		// no seek and no lock around a position that other readers would move.
		var total = 0;
		while ( total < buffer.Length )
		{
			var read = RandomAccess.Read( reader.Stream.SafeFileHandle, buffer.AsSpan( total ), offset + total );
			if ( read <= 0 ) throw new EndOfStreamException( "STARPAK range is truncated." );
			total += read;
		}
		System.Threading.Interlocked.Increment( ref ReadCalls );
		System.Threading.Interlocked.Add( ref BytesRead, buffer.Length );
	}

	static IReadOnlyDictionary<long, long> ReadEntries( string path )
	{
		using var stream = new FileStream( path, FileMode.Open, FileAccess.Read, FileShare.Read );
		if ( stream.Length < 8 ) throw new InvalidDataException( "STARPAK footer is missing." );
		Span<byte> value = stackalloc byte[8];
		stream.Position = stream.Length - 8;
		stream.ReadExactly( value );
		var count = BinaryPrimitives.ReadUInt64LittleEndian( value );
		if ( count > int.MaxValue || count > (ulong)((stream.Length - 8) / 16) )
			throw new InvalidDataException( $"Invalid STARPAK entry count {count}." );

		var tableLength = checked((int)count * 16);
		var dataEnd = stream.Length - 8 - tableLength;
		stream.Position = dataEnd;
		// One read for the whole table instead of one 16-byte FileStream read per entry.
		var table = GC.AllocateUninitializedArray<byte>( tableLength );
		stream.ReadExactly( table );
		var entries = new Dictionary<long, long>( (int)count );
		for ( var index = 0; index < (int)count; index++ )
		{
			var entry = table.AsSpan( index * 16, 16 );
			var offset = BinaryPrimitives.ReadInt64LittleEndian( entry[..8] );
			var size = BinaryPrimitives.ReadInt64LittleEndian( entry[8..] );
			// Streamed data must end before the index, not merely before EOF.
			// Subtraction avoids overflowing on a corrupt offset + size pair.
			if ( offset < 0 || size <= 0 || offset > dataEnd || size > dataEnd - offset )
				throw new InvalidDataException( $"STARPAK entry 0x{offset:X}+{size} exceeds its data region." );
			if ( !entries.TryAdd( offset, size ) )
				throw new InvalidDataException( $"STARPAK contains duplicate stream entry 0x{offset:X}." );
		}
		return entries;
	}

	// One open STARPAK handle, shared by every thread that reads from this file.
	sealed class StarPakReader
	{
		internal readonly string Path;
		internal readonly FileStream Stream;
		internal readonly bool Pooled;
		internal readonly object Gate = new();
		internal readonly Queue<PendingRequest> Pending = new();
		internal int Active;
		internal long LastUsed;
		internal int Serving;
		internal bool Retired;
		bool _disposed;

		internal StarPakReader( string path, bool pooled )
		{
			Path = path;
			Pooled = pooled;
			// bufferSize 1 keeps FileStream out of the data path: every read goes through
			// RandomAccess with an explicit offset, so the shared handle never seeks.
			Stream = new FileStream( path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 );
			System.Threading.Interlocked.Increment( ref FileOpens );
		}

		internal long Length => Stream.Length;

		// Readers that were never pooled, and pooled readers retired by Clear(), close
		// their handle once the last request that was using them releases it.
		internal bool ShouldDisposeOnRelease => !Pooled || Retired;

		internal void Dispose()
		{
			if ( _disposed ) return;
			_disposed = true;
			Stream.Dispose();
		}
	}

	// One caller's range read, plus the buffer the caller already owns for it.
	sealed class PendingRequest( long offset, int length )
	{
		readonly TaskCompletionSource<string> _completion = new( TaskCreationOptions.RunContinuationsAsynchronously );
		internal readonly long Offset = offset;
		internal readonly int Length = length;
		internal readonly byte[] Data = new byte[length];

		internal Task<string> Completion => _completion.Task;

		// null means the range was read; an error string is reported instead of data.
		internal void Complete() => _completion.TrySetResult( null );

		internal void Fail( string error ) => _completion.TrySetResult( error ?? "STARPAK range read failed." );
	}
}

readonly record struct StarPakStatistics(
	int IndexedFiles,
	long IndexHits,
	long IndexMisses,
	long RangeReads,
	long BytesRead,
	long Failures,
	int OpenFiles,
	long FileOpens,
	long Batches,
	long MergedRanges,
	long ReadCalls );
