namespace Sandbox.Mounting.Helldivers2;

/// <summary>Compression identifier stored in each DSAR chunk header.</summary>
internal enum Hd2Compression : byte
{
	None = 0,
	Lz4 = 3
}

/// <summary>One 32-byte entry of a DSAR chunk table.</summary>
internal readonly struct Hd2DsarChunk
{
	public const int StoredSize = 32;

	public readonly ulong UncompressedOffset;
	public readonly ulong CompressedOffset;
	public readonly uint UncompressedSize;
	public readonly uint CompressedSize;
	public readonly Hd2Compression Compression;
	public readonly byte Flags;

	public Hd2DsarChunk( ReadOnlySpan<byte> data )
	{
		UncompressedOffset = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian( data );
		CompressedOffset = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian( data[8..] );
		UncompressedSize = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian( data[16..] );
		CompressedSize = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian( data[20..] );
		Compression = (Hd2Compression)data[24];
		Flags = data[25];
	}

	/// <summary>Set on the first chunk of a logical block; informational only.</summary>
	public bool IsStart => (Flags & 0x02) != 0;

	public override string ToString() =>
		$"uncompressed {UncompressedOffset}+{UncompressedSize}, compressed {CompressedOffset}+{CompressedSize}, {Compression}";
}

/// <summary>
/// A DSAR container: the outer wrapper used by Helldivers 2 for <c>bundles.NN.nxa</c> as well as
/// every loose bundle file in <c>data/</c>. It stores its payload as a list of independently
/// compressed chunks that are contiguous in uncompressed space.
/// </summary>
internal sealed class Hd2DsarFile : IDisposable
{
	public const int HeaderSize = 32;

	public string Path { get; }

	public string Name { get; }

	public Hd2DsarChunk[] Chunks { get; }

	public uint HeaderAndChunkInfoSize { get; }

	public ulong UncompressedDataSize { get; }

	FileStream _stream;

	/// <summary>Diagnostics: chunks decompressed and bytes produced, for the mount self-test.</summary>
	public long ChunksRead;

	public long BytesDecompressed;

	Hd2DsarFile( string path, FileStream stream, Hd2DsarChunk[] chunks, uint headerAndChunkInfoSize, ulong uncompressedDataSize )
	{
		Path = path;
		Name = System.IO.Path.GetFileName( path );
		_stream = stream;
		Chunks = chunks;
		HeaderAndChunkInfoSize = headerAndChunkInfoSize;
		UncompressedDataSize = uncompressedDataSize;
	}

	public static bool TryOpen( string path, out Hd2DsarFile file, out string error )
	{
		file = null;
		error = null;

		try
		{
			var stream = new FileStream( path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16, FileOptions.RandomAccess );

			Span<byte> header = stackalloc byte[HeaderSize];
			if ( stream.ReadAtLeast( header, HeaderSize, false ) != HeaderSize )
			{
				stream.Dispose();
				error = "file is too short to contain a DSAR header";
				return false;
			}

			if ( header[0] != (byte)'D' || header[1] != (byte)'S' || header[2] != (byte)'A' || header[3] != (byte)'R' )
			{
				stream.Dispose();
				error = "missing DSAR magic";
				return false;
			}

			var chunkCount = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian( header[8..] );
			var headerAndChunkInfoSize = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian( header[12..] );
			var uncompressedDataSize = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian( header[16..] );

			if ( chunkCount == 0 || chunkCount > 1_000_000 )
			{
				stream.Dispose();
				error = $"implausible DSAR chunk count {chunkCount}";
				return false;
			}

			var table = new byte[(long)chunkCount * Hd2DsarChunk.StoredSize];
			if ( stream.ReadAtLeast( table, table.Length, false ) != table.Length )
			{
				stream.Dispose();
				error = "truncated DSAR chunk table";
				return false;
			}

			var chunks = new Hd2DsarChunk[chunkCount];
			for ( var i = 0; i < chunks.Length; i++ )
				chunks[i] = new Hd2DsarChunk( table.AsSpan( i * Hd2DsarChunk.StoredSize, Hd2DsarChunk.StoredSize ) );

			file = new Hd2DsarFile( path, stream, chunks, headerAndChunkInfoSize, uncompressedDataSize );
			return true;
		}
		catch ( Exception exception )
		{
			error = exception.Message;
			return false;
		}
	}

	/// <summary>Index of the chunk whose uncompressed range contains <paramref name="uncompressedOffset"/>.</summary>
	public int FindChunk( ulong uncompressedOffset )
	{
		var low = 0;
		var high = Chunks.Length - 1;
		var result = -1;

		while ( low <= high )
		{
			var middle = ( low + high ) >> 1;
			if ( Chunks[middle].UncompressedOffset <= uncompressedOffset )
			{
				result = middle;
				low = middle + 1;
			}
			else
			{
				high = middle - 1;
			}
		}

		return result;
	}

	public byte[] ReadChunk( int index )
	{
		var chunk = Chunks[index];
		var compressed = new byte[chunk.CompressedSize];

		lock ( _stream )
		{
			_stream.Seek( (long)chunk.CompressedOffset, SeekOrigin.Begin );
			if ( _stream.ReadAtLeast( compressed, compressed.Length, false ) != compressed.Length )
				throw new IOException( $"Truncated DSAR chunk {index} in '{Name}'." );
		}

		System.Threading.Interlocked.Increment( ref ChunksRead );

		if ( chunk.Compression == Hd2Compression.None )
		{
			System.Threading.Interlocked.Add( ref BytesDecompressed, compressed.Length );
			return compressed;
		}

		if ( chunk.Compression != Hd2Compression.Lz4 )
			throw new NotSupportedException( $"Unsupported DSAR compression id {(byte)chunk.Compression} in '{Name}'." );

		var result = new byte[chunk.UncompressedSize];
		if ( !Lz4Block.TryDecompress( compressed, result, out var written ) || written != result.Length )
			throw new IOException( $"LZ4 block {index} in '{Name}' did not decode to its declared size." );

		System.Threading.Interlocked.Add( ref BytesDecompressed, result.Length );
		return result;
	}

	/// <summary>
	/// Copies <paramref name="destination"/>.Length bytes starting at <paramref name="uncompressedOffset"/>,
	/// walking as many chunks as the range spans.
	/// </summary>
	public bool TryRead( ulong uncompressedOffset, Span<byte> destination )
	{
		if ( destination.Length == 0 ) return true;

		var chunkIndex = FindChunk( uncompressedOffset );
		if ( chunkIndex < 0 ) return false;

		var consumed = 0UL;
		while ( consumed < (ulong)destination.Length )
		{
			if ( chunkIndex >= Chunks.Length ) return false;

			var chunk = Chunks[chunkIndex];
			var chunkStart = chunk.UncompressedOffset;
			var chunkEnd = chunkStart + chunk.UncompressedSize;
			var position = uncompressedOffset + consumed;

			if ( position < chunkStart || position >= chunkEnd ) return false;

			var available = (int)Math.Min( chunkEnd - position, (ulong)destination.Length - consumed );
			var source = ReadChunk( chunkIndex );

			var inside = (int)( position - chunkStart );
			if ( inside + available > source.Length ) return false;

			source.AsSpan( inside, available ).CopyTo( destination[(int)consumed..] );
			consumed += (ulong)available;
			chunkIndex++;
		}

		return true;
	}

	public bool TryRead( ulong uncompressedOffset, int count, out byte[] data )
	{
		data = new byte[count];
		if ( TryRead( uncompressedOffset, data ) ) return true;

		data = null;
		return false;
	}

	public void Dispose()
	{
		_stream?.Dispose();
		_stream = null;
	}
}
