/// <summary>Deferred byte source for a mounted Titanfall 2 asset.</summary>
interface ITitanfall2AssetSource
{
	string Description { get; }
	string CacheIdentity => Description;
	bool TryReadAllBytes( out byte[] bytes, out string error );
}

/// <summary>Byte source capable of resolving files adjacent to the primary asset.</summary>
interface ITitanfall2SiblingAssetSource
{
	bool TryReadSibling( string suffix, out byte[] bytes, out string error );
}

/// <summary>Byte source capable of resolving an arbitrary normalized game path.</summary>
interface ITitanfall2PathAssetSource
{
	bool TryReadPath( string assetPath, out byte[] bytes, out string error );
}

/// <summary>Byte source that can return the start of an asset without decoding all of it.</summary>
interface ITitanfall2PrefixAssetSource
{
	/// <summary>Returns at least <paramref name="minimumBytes"/> leading bytes, or the whole asset if it is shorter.</summary>
	bool TryReadPrefix( int minimumBytes, out byte[] bytes, out string error );
}

interface ITitanfall2TextureSource
{
	bool TryCreateTexture( out Texture texture, out string error );
}

sealed class LooseAssetSource( string path ) : ITitanfall2AssetSource, ITitanfall2SiblingAssetSource, ITitanfall2PathAssetSource, ITitanfall2PrefixAssetSource
{
	public bool TryReadPrefix( int minimumBytes, out byte[] bytes, out string error )
	{
		try
		{
			using var stream = new FileStream( path, FileMode.Open, FileAccess.Read, FileShare.Read );
			bytes = new byte[(int)Math.Min( stream.Length, Math.Max( 0, minimumBytes ) )];
			stream.ReadExactly( bytes );
			error = null;
			return true;
		}
		catch ( Exception e )
		{
			bytes = null;
			error = e.Message;
			return false;
		}
	}

	public string Description => path;
	public string CacheIdentity
	{
		get
		{
			var file = new FileInfo( path );
			return file.Exists ? $"{file.FullName}:{file.Length}:{file.LastWriteTimeUtc.Ticks}" : $"missing:{file.FullName}";
		}
	}

	public bool TryReadAllBytes( out byte[] bytes, out string error )
	{
		try
		{
			bytes = File.ReadAllBytes( path );
			error = null;
			return true;
		}
		catch ( Exception e )
		{
			bytes = null;
			error = e.Message;
			return false;
		}
	}

	public bool TryReadSibling( string suffix, out byte[] bytes, out string error )
	{
		var siblingPath = path + suffix;
		try
		{
			bytes = File.ReadAllBytes( siblingPath );
			error = null;
			return true;
		}
		catch ( Exception e )
		{
			bytes = null;
			error = e.Message;
			return false;
		}
	}

	public bool TryReadPath( string assetPath, out byte[] bytes, out string error )
	{
		bytes = null;
		error = null;
		if ( string.IsNullOrWhiteSpace( assetPath ) )
		{
			error = "Asset path is empty.";
			return false;
		}

		try
		{
			var normalized = assetPath.Replace( '\\', '/' );
			// Dependencies are game-relative paths, never host filesystem paths.
			// Reject traversal before trying the adjacent-file fallback as well.
			if ( Path.IsPathRooted( normalized ) || normalized.Contains( ':' )
				|| normalized.Split( '/' ).Any( part => part == ".." ) )
				throw new InvalidDataException( $"Loose asset path is not game-relative: {assetPath}" );
			normalized = normalized.Replace( '/', Path.DirectorySeparatorChar );
			var fullSourcePath = Path.GetFullPath( path );
			var sourceMarker = $"{Path.DirectorySeparatorChar}models{Path.DirectorySeparatorChar}";
			var markerIndex = fullSourcePath.IndexOf( sourceMarker, StringComparison.OrdinalIgnoreCase );
			var root = markerIndex >= 0 ? fullSourcePath[..(markerIndex + 1)] : Path.GetDirectoryName( fullSourcePath );
			var rootPrefix = Path.GetFullPath( root ?? string.Empty ).TrimEnd( Path.DirectorySeparatorChar ) + Path.DirectorySeparatorChar;
			var candidates = new[]
			{
				Path.Combine( root ?? string.Empty, normalized ),
				Path.Combine( Path.GetDirectoryName( fullSourcePath ) ?? string.Empty, Path.GetFileName( normalized ) )
			};
			foreach ( var candidate in candidates.Select( Path.GetFullPath ).Distinct( StringComparer.OrdinalIgnoreCase ) )
			{
				if ( !candidate.StartsWith( rootPrefix, StringComparison.OrdinalIgnoreCase ) ) continue;
				if ( !File.Exists( candidate ) ) continue;
				bytes = File.ReadAllBytes( candidate );
				return true;
			}
		}
		catch ( Exception exception )
		{
			error = exception.Message;
		}

		error ??= $"Loose asset not found: {assetPath}";
		return false;
	}
}

sealed class VpkPathResolver
{
	readonly Dictionary<string, (VpkArchive Archive, VpkEntry Entry)> _entries = new( StringComparer.OrdinalIgnoreCase );

	public VpkPathResolver( IEnumerable<VpkArchive> archives )
	{
		foreach ( var archive in archives )
		{
			foreach ( var entry in archive.Entries )
				_entries.TryAdd( Normalize( entry.Path ), (archive, entry) );
		}
	}

	public bool TryReadAllBytes( string assetPath, out byte[] bytes, out string error )
	{
		if ( !_entries.TryGetValue( Normalize( assetPath ), out var source ) )
		{
			bytes = null;
			error = $"Mounted VPK entry not found: {assetPath}";
			return false;
		}

		return source.Archive.TryReadAllBytes( source.Entry, out bytes, out error );
	}

	static string Normalize( string assetPath ) => assetPath?.Replace( '\\', '/' ).Trim().TrimStart( '/' ) ?? string.Empty;
}

sealed class VpkAssetSource( VpkArchive archive, VpkEntry entry, VpkPathResolver resolver = null )
	: ITitanfall2AssetSource, ITitanfall2SiblingAssetSource, ITitanfall2PathAssetSource, ITitanfall2PrefixAssetSource
{
	public string Description => entry.Path;
	public bool TryReadAllBytes( out byte[] bytes, out string error ) => archive.TryReadAllBytes( entry, out bytes, out error );
	public bool TryReadPrefix( int minimumBytes, out byte[] bytes, out string error ) => archive.TryReadPrefix( entry, minimumBytes, out bytes, out error );
	public bool TryReadSibling( string suffix, out byte[] bytes, out string error ) => archive.TryReadAllBytes( entry.Path + suffix, out bytes, out error );
	public bool TryReadPath( string assetPath, out byte[] bytes, out string error )
	{
		if ( archive.TryReadAllBytes( assetPath, out bytes, out error ) ) return true;
		return resolver?.TryReadAllBytes( assetPath, out bytes, out error ) ?? false;
	}
}

sealed class RpakTextureAssetSource( RpakArchive archive, RpakAsset asset ) : ITitanfall2AssetSource, ITitanfall2TextureSource
{
	public string Description => $"{Path.GetFileName( archive.FilePath )}:{asset.Name}";

	public bool TryReadAllBytes( out byte[] bytes, out string error )
	{
		bytes = null;
		error = "RPAK texture data is decoded directly into a runtime texture.";
		return false;
	}

	public bool TryCreateTexture( out Texture texture, out string error ) => RpakTextureDecoder.TryCreate( archive, asset, out texture, out error );
	internal bool TryReadTexture( out RpakTextureData data, out string error ) => RpakTextureDecoder.TryRead( archive, asset, out data, out error );
}
