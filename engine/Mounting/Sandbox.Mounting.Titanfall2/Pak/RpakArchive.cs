using System.Buffers.Binary;
using System.Text;

/// <summary>A lazily decompressed Titanfall 2 RPAK v7 package.</summary>
sealed class RpakArchive
{
	const uint Magic = 0x6B615052;
	const ushort PakFileCompressed = 1 << 8;
	const int HeaderSize = 0x58;
	const int AssetSize = 0x48;
	const int CacheVersion = 3;
	const uint CacheMagic = 0x58493254; // T2IX
	static readonly object StrongBufferCacheLock = new();
	static readonly Dictionary<RpakArchive, StrongBufferEntry> StrongBuffers = new();
	static readonly LinkedList<RpakArchive> StrongBufferLru = new();
	static long StrongBufferBytes;
	static long StrongBufferHits;
	static long StrongBufferMisses;
	static long WeakBufferHits;
	static long BufferDecodes;
	static long BufferEvictions;
	static long IndexCacheHits;
	static long IndexCacheMisses;
	static long StrongBufferCacheLimit => (long)Titanfall2StreamingSettings.RpakBufferBudgetMb * 1024 * 1024;

	readonly object _bufferLock = new();
	WeakReference<byte[]> _bufferCache;
	readonly RpakPage[] _pages;
	readonly string[] _streamingFiles;

	public string FilePath { get; }
	public IReadOnlyList<RpakAsset> Assets { get; }
	public IReadOnlyList<string> StreamingFiles => _streamingFiles;

	RpakArchive( string filePath, RpakPage[] pages, string[] streamingFiles, List<RpakAsset> assets, byte[] initialBuffer )
	{
		FilePath = filePath;
		_pages = pages;
		_streamingFiles = streamingFiles;
		Assets = assets;
		if ( initialBuffer is not null ) _bufferCache = new WeakReference<byte[]>( initialBuffer );
	}

	public static bool TryOpen( string path, out RpakArchive archive, out string error )
	{
		archive = null;
		error = null;
		try
		{
			if ( TryReadCache( path, out archive ) )
			{
				System.Threading.Interlocked.Increment( ref IndexCacheHits );
				return true;
			}
			System.Threading.Interlocked.Increment( ref IndexCacheMisses );
			var fileBytes = File.ReadAllBytes( path );
			if ( !TryDecodeFile( fileBytes, out var bytes, out error ) ) return false;
			if ( bytes.Length < HeaderSize || U32( bytes, 0 ) != Magic || U16( bytes, 4 ) != 7 )
			{
				error = "Unsupported or invalid RPAK v7 header.";
				return false;
			}

			var streamPathBytes = U16( bytes, 0x38 );
			var segmentCount = U16( bytes, 0x3A );
			var pageCount = U16( bytes, 0x3C );
			var patchCount = I16( bytes, 0x3E );
			var pointerCount = I32( bytes, 0x40 );
			var assetCount = I32( bytes, 0x44 );
			var guidReferenceCount = I32( bytes, 0x48 );
			var dependencyCount = I32( bytes, 0x4C );
			var externalReferenceCount = I32( bytes, 0x50 );
			var externalReferenceBytes = I32( bytes, 0x54 );
			if ( pointerCount < 0 || assetCount < 0 || guidReferenceCount < 0 || dependencyCount < 0
				|| externalReferenceCount < 0 || externalReferenceBytes < 0 )
			{
				error = "RPAK metadata contains a negative count.";
				return false;
			}

			var patchDataBytes = 0;
			var firstContainedPage = 0;
			long offset = HeaderSize;
			if ( patchCount > 0 )
			{
				EnsureRange( bytes, offset, 8L + patchCount * 18L );
				patchDataBytes = I32( bytes, (int)offset );
				firstContainedPage = I32( bytes, (int)offset + 4 );
				if ( patchDataBytes < 0 || firstContainedPage < 0 || firstContainedPage > pageCount )
				{
					error = "RPAK patch header contains an invalid stream size or page count.";
					return false;
				}
				offset += 8L + patchCount * 16L + patchCount * 2L;
			}
			EnsureRange( bytes, offset, streamPathBytes );
			var streamingFiles = ReadNullTerminatedList( bytes.AsSpan( (int)offset, streamPathBytes ) );
			offset += streamPathBytes;

			EnsureRange( bytes, offset, (long)segmentCount * 0x10 );
			offset += (long)segmentCount * 0x10;
			var pages = new RpakPage[pageCount];
			for ( var index = 0; index < pageCount; index++ )
			{
				var pageOffset = checked((int)(offset + index * 0x0C));
				pages[index] = new RpakPage( I32( bytes, pageOffset ), I32( bytes, pageOffset + 4 ), U32( bytes, pageOffset + 8 ), 0 );
			}
			offset += (long)pageCount * 0x0C;
			offset += (long)pointerCount * 8;
			var assetsOffset = offset;
			offset += (long)assetCount * AssetSize;
			offset += (long)guidReferenceCount * 8;
			offset += (long)dependencyCount * 4;
			offset += (long)externalReferenceCount * 4;
			offset += externalReferenceBytes;
			EnsureRange( bytes, 0, offset );

			var pageDataOffset = offset + patchDataBytes;
			EnsureRange( bytes, offset, patchDataBytes );
			for ( var index = 0; index < pages.Length; index++ )
			{
				if ( index < firstContainedPage )
				{
					pages[index] = pages[index] with { FileOffset = -1 };
					continue;
				}
				pages[index] = pages[index] with { FileOffset = pageDataOffset };
				pageDataOffset += pages[index].Size;
				EnsureRange( bytes, pages[index].FileOffset, pages[index].Size );
			}

			var assets = new List<RpakAsset>( assetCount );
			for ( var index = 0; index < assetCount; index++ )
			{
				var assetOffset = checked((int)(assetsOffset + index * AssetSize));
				var guid = U64( bytes, assetOffset );
				var head = ReadPagePointer( bytes, assetOffset + 0x10 );
				var data = ReadPagePointer( bytes, assetOffset + 0x18 );
				var starpakOffset = I64( bytes, assetOffset + 0x20 );
				var headerStructSize = U32( bytes, assetOffset + 0x3C );
				var version = I32( bytes, assetOffset + 0x40 );
				var typeValue = U32( bytes, assetOffset + 0x44 );
				var type = FourCc( typeValue );
				if ( type is not ("txtr" or "matl" or "shds" or "shdr") ) continue;

				if ( !TryResolve( pages, head, bytes.Length, out var headerOffset ) ) continue;
				// TXTR and SHDS headers store their asset-name pointer at +0x08,
				// SHDR at +0x00, and MATL at +0x18. Keeping SHDS/SHDR entries lets
				// the material loader recover both feature flags and reflected constants.
				var namePointerOffset = type switch
				{
					"shdr" => headerOffset,
					"txtr" or "shds" => headerOffset + 8,
					_ => headerOffset + 24
				};
				if ( namePointerOffset + 8 > bytes.Length ) continue;
				var namePointer = ReadPagePointer( bytes, namePointerOffset );
				if ( !TryResolve( pages, namePointer, bytes.Length, out var nameOffset ) ) continue;
				var name = ReadCString( bytes, nameOffset );
				if ( string.IsNullOrWhiteSpace( name ) ) continue;

				assets.Add( new RpakAsset( guid, type, NormalizeAssetPath( name ), version, headerStructSize, head, data, starpakOffset ) );
			}

			archive = new RpakArchive( path, pages, streamingFiles, assets, bytes );
			TryWriteCache( archive );
			return true;
		}
		catch ( Exception exception )
		{
			error = exception.Message;
			return false;
		}
	}

	public bool TryReadAsset( RpakAsset asset, out RpakAssetData data, out string error )
	{
		data = default;
		if ( !TryGetBuffer( out var bytes, out error ) ) return false;
		if ( !TryResolve( _pages, asset.Head, bytes.Length, out var headerOffset ) )
		{
			error = "Asset header page pointer is invalid.";
			return false;
		}

		var headerSize = checked((int)asset.HeaderStructSize);
		if ( headerSize <= 0 || headerOffset + headerSize > bytes.Length )
		{
			error = $"Asset header size {headerSize} is invalid.";
			return false;
		}

		var header = new byte[headerSize];
		Buffer.BlockCopy( bytes, headerOffset, header, 0, headerSize );
		data = new RpakAssetData( header, null );
		return true;
	}

	public bool TryReadPagePointer( RpakPagePointer pointer, int length, out byte[] data, out string error )
	{
		data = null;
		if ( !TryGetBuffer( out var bytes, out error ) ) return false;
		if ( !TryResolve( _pages, pointer, bytes.Length, out var offset ) || length < 0 || offset + length > bytes.Length )
		{
			error = "RPAK page range is invalid.";
			return false;
		}
		data = new byte[length];
		Buffer.BlockCopy( bytes, offset, data, 0, length );
		return true;
	}

	/// <summary>
	/// Reads the payload referenced by an RPAK CPU-data descriptor. MATL and SHDR
	/// assets store a page pointer followed by the payload byte count at Asset.Data;
	/// Asset.Data itself is not the material constants or DXBC bytecode.
	/// </summary>
	public bool TryReadCpuData( RpakAsset asset, out byte[] data, out string error )
	{
		const int DescriptorSize = 16;
		const int MaximumCpuDataSize = 64 * 1024 * 1024;
		data = null;
		if ( !TryReadPagePointer( asset.Data, DescriptorSize, out var descriptor, out error ) ) return false;

		var pointer = ReadPagePointer( descriptor, 0 );
		var length = I32( descriptor, 8 );
		if ( length < 0 || length > MaximumCpuDataSize )
		{
			error = $"RPAK CPU-data size {length} is invalid.";
			return false;
		}
		if ( length == 0 )
		{
			data = Array.Empty<byte>();
			error = null;
			return true;
		}

		return TryReadPagePointer( pointer, length, out data, out error );
	}

	public string GetStreamingFilePath( int index )
	{
		if ( index < 0 || index >= _streamingFiles.Length ) return null;
		var relative = _streamingFiles[index].Replace( '/', Path.DirectorySeparatorChar ).Replace( '\\', Path.DirectorySeparatorChar );
		var directory = Path.GetDirectoryName( FilePath )!;
		var candidates = new[]
		{
			Path.Combine( directory, relative ),
			Path.Combine( directory, Path.GetFileName( relative ) ),
			Path.Combine( directory, "..", "..", relative )
		};
		return candidates.Select( Path.GetFullPath ).FirstOrDefault( File.Exists );
	}

	bool TryGetBuffer( out byte[] bytes, out string error )
	{
		if ( TryGetStrongBuffer( this, out bytes ) )
		{
			error = null;
			return true;
		}

		lock ( _bufferLock )
		{
			if ( _bufferCache is not null && _bufferCache.TryGetTarget( out bytes ) )
			{
				System.Threading.Interlocked.Increment( ref WeakBufferHits );
				KeepStrongBuffer( this, bytes );
				error = null;
				return true;
			}

			try
			{
				var fileBytes = File.ReadAllBytes( FilePath );
				if ( !TryDecodeFile( fileBytes, out bytes, out error ) ) return false;
				_bufferCache = new WeakReference<byte[]>( bytes );
				KeepStrongBuffer( this, bytes );
				return true;
			}
			catch ( Exception exception )
			{
				bytes = null;
				error = exception.Message;
				return false;
			}
		}
	}

	static bool TryGetStrongBuffer( RpakArchive archive, out byte[] bytes )
	{
		lock ( StrongBufferCacheLock )
		{
			if ( !StrongBuffers.TryGetValue( archive, out var entry ) )
			{
				System.Threading.Interlocked.Increment( ref StrongBufferMisses );
				bytes = null;
				return false;
			}
			System.Threading.Interlocked.Increment( ref StrongBufferHits );
			StrongBufferLru.Remove( entry.Node );
			StrongBufferLru.AddFirst( entry.Node );
			bytes = entry.Bytes;
			return true;
		}
	}

	static void KeepStrongBuffer( RpakArchive archive, byte[] bytes )
	{
		if ( bytes is null || bytes.LongLength > StrongBufferCacheLimit ) return;
		lock ( StrongBufferCacheLock )
		{
			if ( StrongBuffers.TryGetValue( archive, out var existing ) )
			{
				StrongBufferLru.Remove( existing.Node );
				StrongBufferLru.AddFirst( existing.Node );
				return;
			}

			var node = StrongBufferLru.AddFirst( archive );
			StrongBuffers.Add( archive, new StrongBufferEntry( bytes, node ) );
			StrongBufferBytes += bytes.LongLength;
			while ( StrongBufferBytes > StrongBufferCacheLimit && StrongBufferLru.Last is { } last )
			{
				StrongBufferLru.RemoveLast();
				if ( !StrongBuffers.Remove( last.Value, out var removed ) ) continue;
				StrongBufferBytes -= removed.Bytes.LongLength;
				System.Threading.Interlocked.Increment( ref BufferEvictions );
			}
		}
	}

	sealed record StrongBufferEntry( byte[] Bytes, LinkedListNode<RpakArchive> Node );

	static bool TryDecodeFile( byte[] fileBytes, out byte[] bytes, out string error )
	{
		bytes = null;
		error = null;
		if ( fileBytes.Length < HeaderSize || U32( fileBytes, 0 ) != Magic )
		{
			error = "Invalid RPAK magic.";
			return false;
		}

		var flags = U16( fileBytes, 6 );
		if ( (flags & PakFileCompressed) != 0 )
		{
			System.Threading.Interlocked.Increment( ref BufferDecodes );
			return PakFileDecoder.TryDecompress( fileBytes, out bytes, out error );
		}
		bytes = fileBytes;
		return true;
	}

	internal static RpakCacheStatistics GetCacheStatistics()
	{
		lock ( StrongBufferCacheLock )
		{
			return new RpakCacheStatistics(
				StrongBuffers.Count,
				StrongBufferBytes,
				StrongBufferCacheLimit,
				System.Threading.Interlocked.Read( ref StrongBufferHits ),
				System.Threading.Interlocked.Read( ref StrongBufferMisses ),
				System.Threading.Interlocked.Read( ref WeakBufferHits ),
				System.Threading.Interlocked.Read( ref BufferDecodes ),
				System.Threading.Interlocked.Read( ref BufferEvictions ),
				System.Threading.Interlocked.Read( ref IndexCacheHits ),
				System.Threading.Interlocked.Read( ref IndexCacheMisses ) );
		}
	}

	internal static void ClearStrongBufferCache()
	{
		lock ( StrongBufferCacheLock )
		{
			StrongBuffers.Clear();
			StrongBufferLru.Clear();
			StrongBufferBytes = 0;
		}
	}

	static bool TryReadCache( string sourcePath, out RpakArchive archive )
	{
		archive = null;
		try
		{
			var source = new FileInfo( sourcePath );
			var cachePath = GetCachePath( sourcePath );
			if ( !File.Exists( cachePath ) ) return false;
			using var stream = new FileStream( cachePath, FileMode.Open, FileAccess.Read, FileShare.Read );
			using var reader = new BinaryReader( stream, Encoding.UTF8, false );
			if ( reader.ReadUInt32() != CacheMagic || reader.ReadInt32() != CacheVersion
				|| reader.ReadInt64() != source.Length || reader.ReadInt64() != source.LastWriteTimeUtc.Ticks ) return false;

			var pageCount = ReadCacheCount( reader, 1_000_000, "page" );
			var pages = new RpakPage[pageCount];
			for ( var index = 0; index < pageCount; index++ )
				pages[index] = new RpakPage( reader.ReadInt32(), reader.ReadInt32(), reader.ReadUInt32(), reader.ReadInt64() );

			var streamCount = ReadCacheCount( reader, 4096, "stream" );
			var streamingFiles = new string[streamCount];
			for ( var index = 0; index < streamCount; index++ ) streamingFiles[index] = reader.ReadString();

			var assetCount = ReadCacheCount( reader, 10_000_000, "asset" );
			var assets = new List<RpakAsset>( assetCount );
			for ( var index = 0; index < assetCount; index++ )
			{
				assets.Add( new RpakAsset(
					reader.ReadUInt64(), reader.ReadString(), reader.ReadString(), reader.ReadInt32(), reader.ReadUInt32(),
					new RpakPagePointer( reader.ReadInt32(), reader.ReadInt32() ),
					new RpakPagePointer( reader.ReadInt32(), reader.ReadInt32() ), reader.ReadInt64() ) );
			}
			if ( stream.Position != stream.Length ) return false;
			archive = new RpakArchive( sourcePath, pages, streamingFiles, assets, null );
			return true;
		}
		catch
		{
			archive = null;
			return false;
		}
	}

	static void TryWriteCache( RpakArchive archive )
	{
		try
		{
			var source = new FileInfo( archive.FilePath );
			var cachePath = GetCachePath( archive.FilePath );
			System.IO.Directory.CreateDirectory( Path.GetDirectoryName( cachePath )! );
			var temporaryPath = cachePath + ".tmp";
			using ( var stream = new FileStream( temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None ) )
			using ( var writer = new BinaryWriter( stream, Encoding.UTF8, false ) )
			{
				writer.Write( CacheMagic );
				writer.Write( CacheVersion );
				writer.Write( source.Length );
				writer.Write( source.LastWriteTimeUtc.Ticks );
				writer.Write( archive._pages.Length );
				foreach ( var page in archive._pages )
				{
					writer.Write( page.Segment );
					writer.Write( page.Alignment );
					writer.Write( page.Size );
					writer.Write( page.FileOffset );
				}
				writer.Write( archive._streamingFiles.Length );
				foreach ( var file in archive._streamingFiles ) writer.Write( file );
				writer.Write( archive.Assets.Count );
				foreach ( var asset in archive.Assets )
				{
					writer.Write( asset.Guid );
					writer.Write( asset.Type );
					writer.Write( asset.Name );
					writer.Write( asset.Version );
					writer.Write( asset.HeaderStructSize );
					writer.Write( asset.Head.Index );
					writer.Write( asset.Head.Offset );
					writer.Write( asset.Data.Index );
					writer.Write( asset.Data.Offset );
					writer.Write( asset.StarpakOffset );
				}
			}
			File.Move( temporaryPath, cachePath, true );
		}
		catch
		{
			// A cache failure must never prevent mounting the original package.
		}
	}

	static int ReadCacheCount( BinaryReader reader, int maximum, string kind )
	{
		var count = reader.ReadInt32();
		if ( count < 0 || count > maximum ) throw new InvalidDataException( $"Invalid cached {kind} count {count}." );
		return count;
	}

	static string GetCachePath( string sourcePath )
	{
		var root = Environment.GetFolderPath( Environment.SpecialFolder.LocalApplicationData );
		var hash = 14695981039346656037UL;
		foreach ( var character in Path.GetFullPath( sourcePath ).ToUpperInvariant() )
		{
			hash ^= character;
			hash *= 1099511628211UL;
		}
		return Path.Combine( root, "sbox", "mount-cache", "titanfall2", $"{hash:X16}.idx" );
	}

	static bool TryResolve( RpakPage[] pages, RpakPagePointer pointer, int bufferLength, out int offset )
	{
		offset = 0;
		if ( pointer.Index < 0 || pointer.Index >= pages.Length || pointer.Offset < 0 ) return false;
		var page = pages[pointer.Index];
		if ( page.FileOffset < 0 ) return false;
		if ( (uint)pointer.Offset > page.Size ) return false;
		var resolved = page.FileOffset + pointer.Offset;
		if ( resolved < 0 || resolved > bufferLength ) return false;
		offset = checked((int)resolved);
		return true;
	}

	static RpakPagePointer ReadPagePointer( byte[] bytes, int offset ) => new( I32( bytes, offset ), I32( bytes, offset + 4 ) );
	static string[] ReadNullTerminatedList( ReadOnlySpan<byte> bytes ) => Encoding.UTF8.GetString( bytes ).Split( '\0', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries );
	static string ReadCString( byte[] bytes, int offset )
	{
		var end = offset;
		while ( end < bytes.Length && bytes[end] != 0 && end - offset < 4096 ) end++;
		return Encoding.UTF8.GetString( bytes, offset, end - offset );
	}
	static string NormalizeAssetPath( string path ) => path.Replace( '\\', '/' ).Trim( '/', ' ', '\0' );
	static string FourCc( uint value ) => new( [(char)(value & 0xFF), (char)((value >> 8) & 0xFF), (char)((value >> 16) & 0xFF), (char)(value >> 24)] );
	static void EnsureRange( byte[] bytes, long offset, long length )
	{
		if ( offset < 0 || length < 0 || offset > bytes.Length || length > bytes.Length - offset )
			throw new InvalidDataException( $"RPAK metadata range {offset}+{length} exceeds {bytes.Length}." );
	}

	static short I16( byte[] bytes, int offset ) => BinaryPrimitives.ReadInt16LittleEndian( bytes.AsSpan( offset, 2 ) );
	static ushort U16( byte[] bytes, int offset ) => BinaryPrimitives.ReadUInt16LittleEndian( bytes.AsSpan( offset, 2 ) );
	static int I32( byte[] bytes, int offset ) => BinaryPrimitives.ReadInt32LittleEndian( bytes.AsSpan( offset, 4 ) );
	static uint U32( byte[] bytes, int offset ) => BinaryPrimitives.ReadUInt32LittleEndian( bytes.AsSpan( offset, 4 ) );
	static long I64( byte[] bytes, int offset ) => BinaryPrimitives.ReadInt64LittleEndian( bytes.AsSpan( offset, 8 ) );
	static ulong U64( byte[] bytes, int offset ) => BinaryPrimitives.ReadUInt64LittleEndian( bytes.AsSpan( offset, 8 ) );
}

readonly record struct RpakPage( int Segment, int Alignment, uint Size, long FileOffset );
readonly record struct RpakPagePointer( int Index, int Offset );
readonly record struct RpakAsset( ulong Guid, string Type, string Name, int Version, uint HeaderStructSize, RpakPagePointer Head, RpakPagePointer Data, long StarpakOffset );
readonly record struct RpakAssetData( byte[] Header, byte[] Cpu );

readonly record struct RpakCacheStatistics(
	int StrongBuffers,
	long StrongBufferBytes,
	long StrongBufferLimit,
	long StrongHits,
	long StrongMisses,
	long WeakHits,
	long Decodes,
	long Evictions,
	long IndexHits,
	long IndexMisses );
