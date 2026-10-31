using System.Buffers;
using System.Text;

/// <summary>
/// Read-only view of a Titanfall 2 Respawn VPK directory and its payload archives.
/// The directory tree is parsed once at mount time; entry bytes are read only when a loader needs them.
/// </summary>
sealed class VpkArchive
{
	const uint Signature = 0x55AA1234;
	const uint Version = 196610;
	const ushort ChunkTerminator = ushort.MaxValue;
	const ushort EmbeddedArchiveIndex = 0x7FFF;
	// Chunk sizes come from the directory tree and are never cross-checked against a file
	// length, so one corrupt header must not be able to demand a multi-gigabyte buffer.
	// The largest shipped entry is well under this (maps are a few hundred MB unpacked).
	const long MaximumEntryBytes = 1L << 30;

	readonly string _directoryPath;
	readonly uint _treeSize;
	readonly uint _dataSize;
	readonly IReadOnlyDictionary<string, VpkEntry> _entries;
	readonly System.Collections.Concurrent.ConcurrentDictionary<ushort, string> _payloadPaths = new();

	VpkArchive( string directoryPath, uint treeSize, uint dataSize, Dictionary<string, VpkEntry> entries )
	{
		_directoryPath = directoryPath;
		_treeSize = treeSize;
		_dataSize = dataSize;
		_entries = entries;
	}

	public IEnumerable<VpkEntry> Entries => _entries.Values;

	/// <summary>Looks up an entry by its normalized path inside this VPK.</summary>
	public bool TryGetEntry( string path, out VpkEntry entry )
	{
		entry = null;
		if ( string.IsNullOrWhiteSpace( path ) ) return false;
		return _entries.TryGetValue( NormalizePath( path ), out entry );
	}

	/// <summary>Reads an entry directly by its path inside this VPK.</summary>
	public bool TryReadAllBytes( string path, out byte[] bytes, out string error )
	{
		if ( !TryGetEntry( path, out var entry ) )
		{
			bytes = null;
			error = $"VPK entry not found: {path}";
			return false;
		}

		return TryReadAllBytes( entry, out bytes, out error );
	}

	public static bool TryOpen( string directoryPath, out VpkArchive archive, out string error )
	{
		archive = null;
		error = null;
		try
		{
			using var stream = new FileStream( directoryPath, FileMode.Open, FileAccess.Read, FileShare.Read );
			using var reader = new BinaryReader( stream, Encoding.ASCII, leaveOpen: true );
			if ( reader.ReadUInt32() != Signature )
			{
				error = "Unsupported VPK signature.";
				return false;
			}

			if ( reader.ReadUInt32() != Version )
			{
				error = "Unsupported VPK version.";
				return false;
			}

			var treeSize = reader.ReadUInt32();
			var dataSize = reader.ReadUInt32();
			var treeEnd = 16L + treeSize;
			if ( treeEnd > stream.Length || dataSize > stream.Length - treeEnd )
				throw new InvalidDataException( "VPK tree or embedded data block exceeds the directory file." );
			var entries = new Dictionary<string, VpkEntry>( StringComparer.OrdinalIgnoreCase );

			// Each branch must end inside the declared tree. Reaching its boundary
			// without a terminator is truncation, even if payload bytes follow it.
			while ( true )
			{
				var extension = ReadString( reader, treeEnd );
				if ( extension.Length == 0 ) break;

				while ( true )
				{
					var folder = ReadString( reader, treeEnd );
					if ( folder.Length == 0 ) break;

					while ( true )
					{
						var name = ReadString( reader, treeEnd );
						if ( name.Length == 0 ) break;

						EnsureTreeRange( reader, treeEnd, 6 );
						_ = reader.ReadUInt32(); // CRC
						var preloadBytes = reader.ReadUInt16();
						var chunks = new List<VpkChunk>();
						while ( true )
						{
							EnsureTreeRange( reader, treeEnd, 2 );
							var archiveIndex = reader.ReadUInt16();
							if ( archiveIndex == ChunkTerminator ) break;
							EnsureTreeRange( reader, treeEnd, 30 );
							chunks.Add( new VpkChunk(
								archiveIndex,
								reader.ReadUInt16(),
								reader.ReadUInt32(),
								reader.ReadUInt64(),
								reader.ReadUInt64(),
								reader.ReadUInt64() ) );
						}

						var preloadOffset = preloadBytes == 0 ? -1 : stream.Position;
						EnsureTreeRange( reader, treeEnd, preloadBytes );
						var path = BuildPath( folder, name, extension );
						entries[path] = new VpkEntry( path, preloadOffset, preloadBytes, chunks );
						stream.Position += preloadBytes;
					}
				}
			}

			if ( stream.Position != treeEnd )
				throw new InvalidDataException( "VPK tree ended before its declared size." );
			archive = new VpkArchive( directoryPath, treeSize, dataSize, entries );
			return true;
		}
		catch ( Exception e )
		{
			error = e.Message;
			return false;
		}
	}

	public bool TryReadAllBytes( VpkEntry entry, out byte[] bytes, out string error )
	{
		bytes = null;
		error = null;
		try
		{
			// Pre-size the output: growing from zero reallocated/copied large
			// multi-chunk entries (models, BSP lumps) several times before ToArray.
			var capacity = (long)entry.PreloadBytes;
			foreach ( var chunk in entry.Chunks )
			{
				if ( chunk.CompressedSize > int.MaxValue || chunk.UncompressedSize > int.MaxValue )
					throw new InvalidDataException( $"VPK chunk is too large: {entry.Path}" );
				capacity += (long)chunk.UncompressedSize;
			}
			if ( capacity > Array.MaxLength || capacity > MaximumEntryBytes )
				throw new InvalidDataException( $"VPK entry is too large: {entry.Path} ({capacity} bytes)." );
			using var output = new MemoryStream( (int)capacity );
			if ( entry.PreloadBytes > 0 )
				WriteRange( output, _directoryPath, entry.PreloadOffset, entry.PreloadBytes );

			foreach ( var chunk in entry.Chunks )
			{
				if ( chunk.CompressedSize > int.MaxValue || chunk.UncompressedSize > int.MaxValue )
					throw new InvalidDataException( $"VPK chunk is too large: {entry.Path}" );

				var data = ReadChunk( entry, chunk );
				if ( chunk.CompressedSize != chunk.UncompressedSize )
				{
					if ( !LzhamDecoder.TryDecompress( data, checked((int)chunk.UncompressedSize), out data, out var decodeError ) )
						throw new InvalidDataException( $"LZHAM decompression failed for '{entry.Path}': {decodeError}" );
				}

				output.Write( data );
			}

			bytes = output.ToArray();
			return true;
		}
		catch ( Exception e )
		{
			error = e.Message;
			return false;
		}
	}

	/// <summary>
	/// Reads at least <paramref name="minimumBytes"/> from the start of an entry (or the whole
	/// entry if it is shorter), decoding only the chunks that cover that prefix. Used for MDL
	/// header fields, which would otherwise decompress every chunk of a large model.
	/// </summary>
	public bool TryReadPrefix( VpkEntry entry, int minimumBytes, out byte[] bytes, out string error )
	{
		bytes = null;
		error = null;
		try
		{
			using var output = new MemoryStream();
			if ( entry.PreloadBytes > 0 )
				WriteRange( output, _directoryPath, entry.PreloadOffset, entry.PreloadBytes );

			foreach ( var chunk in entry.Chunks )
			{
				if ( output.Length >= minimumBytes ) break;
				if ( chunk.CompressedSize > int.MaxValue || chunk.UncompressedSize > int.MaxValue )
					throw new InvalidDataException( $"VPK chunk is too large: {entry.Path}" );

				var data = ReadChunk( entry, chunk );
				if ( chunk.CompressedSize != chunk.UncompressedSize
					&& !LzhamDecoder.TryDecompress( data, checked((int)chunk.UncompressedSize), out data, out var decodeError ) )
					throw new InvalidDataException( $"LZHAM decompression failed for '{entry.Path}': {decodeError}" );
				output.Write( data );
			}

			bytes = output.ToArray();
			return true;
		}
		catch ( Exception e )
		{
			error = e.Message;
			return false;
		}
	}

	byte[] ReadChunk( VpkEntry entry, VpkChunk chunk )
	{
		if ( chunk.ArchiveIndex == EmbeddedArchiveIndex )
		{
			if ( chunk.Offset > _dataSize || chunk.CompressedSize > _dataSize - chunk.Offset )
				throw new InvalidDataException( $"VPK embedded chunk range is invalid for '{entry.Path}'." );
			var directoryOffset = checked(16L + _treeSize + (long)chunk.Offset);
			if ( TryReadRange( _directoryPath, directoryOffset, checked((int)chunk.CompressedSize), out var embedded ) )
				return embedded;
			throw new InvalidDataException( $"VPK embedded chunk payload is missing for '{entry.Path}'." );
		}

		var payloadPath = ResolvePayloadPath( chunk.ArchiveIndex );
		if ( TryReadRange( payloadPath, checked((long)chunk.Offset), checked((int)chunk.CompressedSize), out var bytes ) )
			return bytes;

		// An external volume failure must not substitute unrelated directory data.
		throw new InvalidDataException( $"VPK chunk payload is missing or truncated for '{entry.Path}' in '{payloadPath}'." );
	}

	// Every chunk (1 MiB) resolved its payload with a List, LINQ Distinct and File.Exists.
	string ResolvePayloadPath( ushort archiveIndex ) => _payloadPaths.GetOrAdd( archiveIndex, ResolvePayloadPathUncached );

	string ResolvePayloadPathUncached( ushort archiveIndex )
	{
		var directory = Path.GetDirectoryName( _directoryPath )!;
		var name = Path.GetFileName( _directoryPath );
		if ( !name.EndsWith( "_dir.vpk", StringComparison.OrdinalIgnoreCase ) )
			throw new InvalidDataException( $"Unable to resolve external VPK volumes from directory filename '{name}'." );

		var candidates = new List<string>
		{
			name.Replace( "_dir.vpk", $"_{archiveIndex:D3}.vpk", StringComparison.OrdinalIgnoreCase )
		};

		if ( name.StartsWith( "englishclient_", StringComparison.OrdinalIgnoreCase ) )
			candidates.Add( "client_" + name["englishclient_".Length..].Replace( "_dir.vpk", $"_{archiveIndex:D3}.vpk", StringComparison.OrdinalIgnoreCase ) );

		foreach ( var candidate in candidates.Distinct( StringComparer.OrdinalIgnoreCase ) )
		{
			var fullPath = Path.Combine( directory, candidate );
			if ( File.Exists( fullPath ) ) return fullPath;
		}

		return Path.Combine( directory, candidates[0] );
	}

	static void WriteRange( Stream output, string path, long offset, int length )
	{
		if ( !TryReadRange( path, offset, length, out var data ) )
			throw new InvalidDataException( $"Unable to read VPK range from '{path}'." );
		output.Write( data );
	}

	static bool TryReadRange( string path, long offset, int length, out byte[] data )
	{
		data = null;
		if ( offset < 0 || length < 0 || !File.Exists( path ) ) return false;
		using var stream = new FileStream( path, FileMode.Open, FileAccess.Read, FileShare.Read );
		if ( offset > stream.Length || length > stream.Length - offset ) return false;
		stream.Position = offset;
		data = GC.AllocateUninitializedArray<byte>( length );
		var read = 0;
		while ( read < data.Length )
		{
			var count = stream.Read( data, read, data.Length - read );
			if ( count == 0 ) return false;
			read += count;
		}
		return true;
	}

	static string BuildPath( string folder, string name, string extension )
	{
		var fileName = name == " " ? string.Empty : name;
		if ( extension != " " ) fileName += "." + extension;
		return folder == " " ? fileName : folder.Replace( '\\', '/' ).Trim( '/' ) + "/" + fileName;
	}

	static string NormalizePath( string path ) => path.Replace( '\\', '/' ).Trim().TrimStart( '/' );

	static void EnsureTreeRange( BinaryReader reader, long treeEnd, int length )
	{
		if ( length > treeEnd - reader.BaseStream.Position )
			throw new InvalidDataException( "VPK directory entry exceeds the declared tree." );
	}

	static string ReadString( BinaryReader reader, long treeEnd )
	{
		var buffer = new ArrayBufferWriter<byte>();
		while ( true )
		{
			EnsureTreeRange( reader, treeEnd, 1 );
			var value = reader.ReadByte();
			if ( value == 0 ) break;
			buffer.GetSpan( 1 )[0] = value;
			buffer.Advance( 1 );
		}
		return buffer.WrittenCount == 0 ? string.Empty : Encoding.ASCII.GetString( buffer.WrittenSpan );
	}
}

sealed class VpkEntry
{
	public string Path { get; }
	public long PreloadOffset { get; }
	public int PreloadBytes { get; }
	public IReadOnlyList<VpkChunk> Chunks { get; }

	public VpkEntry( string path, long preloadOffset, int preloadBytes, IReadOnlyList<VpkChunk> chunks )
	{
		Path = path;
		PreloadOffset = preloadOffset;
		PreloadBytes = preloadBytes;
		Chunks = chunks;
	}
}

readonly record struct VpkChunk( ushort ArchiveIndex, ushort LoadFlags, uint TextureFlags, ulong Offset, ulong CompressedSize, ulong UncompressedSize );
