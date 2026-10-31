using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;

/// <summary>
/// Disposable derived data, never engine/GPU objects. Source fingerprints and the
/// converter assembly ID invalidate old output; incomplete/corrupt files fall back
/// to the original importer. Each file is compressed and atomically published.
/// </summary>
static class Titanfall2ProcessedCache
{
	const uint Magic = 0x43463254; // T2FC
	const int Version = 1;
	static readonly Lock TrimLock = new();
	// The mtime touch feeds Trim()'s LRU. Once per file per session is enough for that,
	// and it keeps a map load from issuing thousands of metadata writes.
	static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> Touched = new( StringComparer.OrdinalIgnoreCase );
	internal static readonly JsonSerializerOptions JsonOptions = new()
	{
		IncludeFields = true,
		IgnoreReadOnlyProperties = true,
		// Imported BSP metadata can contain non-finite lighting-origin values.
		// Preserve them losslessly; validating probe coordinates is a separate
		// concern and must not prevent the entire map's cache from being written.
		NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals
	};
	static long _mapHits, _mapMisses, _modelHits, _modelMisses, _textureHits, _textureMisses, _materialHits, _materialMisses, _writes, _rejected, _writeFailures;

	internal static string Root => Path.GetFullPath( Environment.GetEnvironmentVariable( "SBOX_TITANFALL2_CACHE_PATH" )
		?? Path.Combine( Environment.GetFolderPath( Environment.SpecialFolder.LocalApplicationData ), "sbox", "mount-cache", "titanfall2", "processed" ) );

	internal static string Key( string identity ) => Convert.ToHexString( SHA256.HashData( Encoding.UTF8.GetBytes( identity ) ) );
	internal static string GetPath( string kind, string identity ) => Path.Combine( Root, $"{kind}-{Key( identity )}.t2cache" );

	internal static bool TryRead( string kind, string identity, Action<CacheReader> read )
	{
		var hit = false;
		if ( Titanfall2StreamingSettings.ProcessedCache )
		{
			string path = null;
			try
			{
				path = GetPath( kind, identity );
				if ( File.Exists( path ) )
				{
					using var file = File.OpenRead( path );
					using var header = new BinaryReader( file, Encoding.UTF8, leaveOpen: true );
					if ( header.ReadUInt32() != Magic || header.ReadInt32() != Version
						|| !header.ReadBytes( 32 ).AsSpan().SequenceEqual( Convert.FromHexString( Key( identity ) ) ) )
						throw new InvalidDataException( "Cache header mismatch." );
					using var compressed = new GZipStream( file, CompressionMode.Decompress );
					using var reader = new CacheReader( compressed );
					read( reader );
					// Read through the gzip footer as well, validating the CRC/truncation.
					if ( compressed.ReadByte() != -1 ) throw new InvalidDataException( "Trailing cache data." );
					hit = true;
				}
			}
			catch ( Exception ) { Interlocked.Increment( ref _rejected ); }
			if ( hit )
			{
				if ( Touched.TryAdd( path, 0 ) )
				{
					try { File.SetLastWriteTimeUtc( path, DateTime.UtcNow ); } catch ( Exception ) { }
				}
			}
		}
		if ( kind == "map" )
		{
			if ( hit ) Interlocked.Increment( ref _mapHits ); else Interlocked.Increment( ref _mapMisses );
		}
		else if ( kind == "model" )
		{
			if ( hit ) Interlocked.Increment( ref _modelHits ); else Interlocked.Increment( ref _modelMisses );
		}
		else if ( kind == "texture" )
		{
			if ( hit ) Interlocked.Increment( ref _textureHits ); else Interlocked.Increment( ref _textureMisses );
		}
		else if ( kind == "material" )
		{
			if ( hit ) Interlocked.Increment( ref _materialHits ); else Interlocked.Increment( ref _materialMisses );
		}
		return hit;
	}

	internal static void Write( string kind, string identity, Action<BinaryWriter> write )
	{
		if ( !Titanfall2StreamingSettings.ProcessedCache ) return;
		string temporary = null;
		try
		{
			var path = GetPath( kind, identity );
			System.IO.Directory.CreateDirectory( Root );
			temporary = path + $".{Guid.NewGuid():N}.tmp";
			using ( var file = File.Create( temporary ) )
			{
				using var header = new BinaryWriter( file, Encoding.UTF8, leaveOpen: true );
				header.Write( Magic ); header.Write( Version ); header.Write( Convert.FromHexString( Key( identity ) ) );
				using var compressed = new GZipStream( file, CompressionLevel.Fastest, leaveOpen: true );
				using var writer = new BinaryWriter( compressed, Encoding.UTF8, leaveOpen: true );
				write( writer );
			}
			File.Move( temporary, path, overwrite: true );
			temporary = null;
			if ( Interlocked.Increment( ref _writes ) % 64 == 1 || kind == "map" ) Trim();
		}
		catch ( Exception exception )
		{
			if ( Interlocked.Increment( ref _writeFailures ) <= 2 )
				Titanfall2Log.Warning( $"Titanfall 2 derived cache unavailable; using original importer: {exception.Message}" );
		}
		finally
		{
			if ( temporary is not null ) { try { File.Delete( temporary ); } catch ( IOException ) { } }
		}
	}

	static void Trim()
	{
		lock ( TrimLock )
		{
			var root = Root;
			var files = new DirectoryInfo( root ).GetFiles( "*.t2cache", SearchOption.TopDirectoryOnly )
				.OrderByDescending( file => file.LastWriteTimeUtc ).ToArray();
			var kept = 0L;
			var budget = (long)Titanfall2StreamingSettings.ProcessedCacheBudgetMb * 1024 * 1024;
			foreach ( var file in files )
			{
				kept += file.Length;
				if ( kept <= budget ) continue;
				// Only our flat derived-cache directory is eligible for eviction.
				if ( !string.Equals( file.DirectoryName, root, StringComparison.OrdinalIgnoreCase ) ) continue;
				try { file.Delete(); } catch ( IOException ) { }
			}
		}
	}

	internal static void WriteString( BinaryWriter writer, string value )
	{
		var bytes = Encoding.UTF8.GetBytes( value ?? string.Empty );
		writer.Write( bytes.Length ); writer.Write( bytes );
	}
	internal static void WriteArray<T>( BinaryWriter writer, T[] values ) where T : unmanaged
	{
		writer.Write( values.Length ); writer.Write( MemoryMarshal.AsBytes( values.AsSpan() ) );
	}
	internal static void WriteJson<T>( BinaryWriter writer, T value ) =>
		WriteArray( writer, JsonSerializer.SerializeToUtf8Bytes( value, JsonOptions ) );

	internal static CacheStatistics GetStatistics() => new(
		Interlocked.Read( ref _mapHits ), Interlocked.Read( ref _mapMisses ),
		Interlocked.Read( ref _modelHits ), Interlocked.Read( ref _modelMisses ),
		Interlocked.Read( ref _textureHits ), Interlocked.Read( ref _textureMisses ),
		Interlocked.Read( ref _materialHits ), Interlocked.Read( ref _materialMisses ),
		Interlocked.Read( ref _writes ), Interlocked.Read( ref _rejected ), Interlocked.Read( ref _writeFailures ) );
	internal readonly record struct CacheStatistics( long MapHits, long MapMisses, long ModelHits, long ModelMisses,
		long TextureHits, long TextureMisses, long MaterialHits, long MaterialMisses, long Writes, long Rejected, long WriteFailures );

	internal sealed class CacheReader( Stream stream ) : BinaryReader( stream, Encoding.UTF8, leaveOpen: true )
	{
		long _remaining = 768L * 1024 * 1024;
		internal int Count( int maximum )
		{
			var count = ReadInt32();
			if ( count < 0 || count > maximum ) throw new InvalidDataException( "Cache count outside supported range." );
			return count;
		}
		internal T[] Array<T>( int maximum = 16_000_000 ) where T : unmanaged
		{
			var count = Count( maximum );
			var bytes = (long)count * Marshal.SizeOf<T>();
			_remaining -= bytes;
			if ( _remaining < 0 ) throw new InvalidDataException( "Cache allocation budget exceeded." );
			var result = new T[count];
			BaseStream.ReadExactly( MemoryMarshal.AsBytes( result.AsSpan() ) );
			return result;
		}
		internal string Text() => Encoding.UTF8.GetString( Array<byte>( 16384 ) );
		internal T Json<T>() => JsonSerializer.Deserialize<T>( Array<byte>( 64 * 1024 * 1024 ), JsonOptions )
			?? throw new InvalidDataException( "Missing cache metadata." );
	}
}

public partial class Titanfall2Mount
{
	string _contentFingerprint;
	internal string ContentFingerprint => _contentFingerprint ??= BuildContentFingerprint();
	string BuildContentFingerprint()
	{
		var identity = new StringBuilder( typeof( Titanfall2Mount ).Assembly.ManifestModule.ModuleVersionId.ToString() );
		identity.Append( _root );
		try
		{
			if ( !string.IsNullOrEmpty( _root ) )
			{
				foreach ( var folder in new[] { Path.Combine( _root, "vpk" ), Path.Combine( _root, "r2", "paks", "Win64" ) } )
					foreach ( var path in System.IO.Directory.EnumerateFiles( folder ).Where( path =>
						path.EndsWith( ".vpk", StringComparison.OrdinalIgnoreCase ) || path.EndsWith( ".rpak", StringComparison.OrdinalIgnoreCase )
						|| path.EndsWith( ".starpak", StringComparison.OrdinalIgnoreCase ) ).Order( StringComparer.OrdinalIgnoreCase ) )
					{
						var info = new FileInfo( path );
						identity.Append( Path.GetFullPath( path ) ).Append( ':' ).Append( info.Length ).Append( ':' ).Append( info.LastWriteTimeUtc.Ticks ).Append( '\n' );
					}
			}
		}
		catch ( IOException ) { identity.Append( Guid.NewGuid() ); }
		catch ( UnauthorizedAccessException ) { identity.Append( Guid.NewGuid() ); }
		return Titanfall2ProcessedCache.Key( identity.ToString() );
	}
}
