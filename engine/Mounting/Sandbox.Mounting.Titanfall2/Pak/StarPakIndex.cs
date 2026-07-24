using System.Buffers.Binary;

/// <summary>Reads the footer index and byte ranges of a Titanfall 2 STARPAK.</summary>
static class StarPakIndex
{
	static readonly Dictionary<string, IReadOnlyDictionary<long, long>> Cache = new( StringComparer.OrdinalIgnoreCase );
	static readonly object CacheLock = new();
	static long IndexHits;
	static long IndexMisses;
	static long RangeReads;
	static long BytesRead;
	static long Failures;

	public static bool TryGetEntrySize( string path, long offset, out long size, out string error )
	{
		size = 0;
		error = null;
		if ( string.IsNullOrWhiteSpace( path ) || !File.Exists( path ) )
		{
			error = $"STARPAK file was not found: {path}";
			return false;
		}

		try
		{
			IReadOnlyDictionary<long, long> entries;
			lock ( CacheLock )
			{
				if ( !Cache.TryGetValue( path, out entries ) )
				{
					System.Threading.Interlocked.Increment( ref IndexMisses );
					entries = ReadEntries( path );
					Cache[path] = entries;
				}
				else System.Threading.Interlocked.Increment( ref IndexHits );
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
			using var stream = new FileStream( path, FileMode.Open, FileAccess.Read, FileShare.Read );
			if ( offset < 0 || length < 0 || offset > stream.Length || length > stream.Length - offset )
			{
				error = $"STARPAK range 0x{offset:X}+{length} is invalid.";
				return false;
			}
			stream.Position = offset;
			data = new byte[length];
			stream.ReadExactly( data );
			System.Threading.Interlocked.Increment( ref RangeReads );
			System.Threading.Interlocked.Add( ref BytesRead, length );
			return true;
		}
		catch ( Exception exception )
		{
			System.Threading.Interlocked.Increment( ref Failures );
			error = exception.Message;
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
				System.Threading.Interlocked.Read( ref Failures ) );
		}
	}

	internal static void Clear()
	{
		lock ( CacheLock ) Cache.Clear();
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

		var entries = new Dictionary<long, long>( (int)count );
		stream.Position = stream.Length - 8 - checked((long)count * 16);
		Span<byte> entry = stackalloc byte[16];
		for ( ulong index = 0; index < count; index++ )
		{
			stream.ReadExactly( entry );
			var offset = BinaryPrimitives.ReadInt64LittleEndian( entry[..8] );
			var size = BinaryPrimitives.ReadInt64LittleEndian( entry[8..] );
			if ( offset >= 0 && size > 0 ) entries[offset] = size;
		}
		return entries;
	}
}

readonly record struct StarPakStatistics(
	int IndexedFiles,
	long IndexHits,
	long IndexMisses,
	long RangeReads,
	long BytesRead,
	long Failures );
