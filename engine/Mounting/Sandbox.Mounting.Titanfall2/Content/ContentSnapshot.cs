using System.Collections.Frozen;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;

/// <summary>
/// One immutable directory entry. Raw dependencies and logical RPAK assets share the
/// index, but only the catalog publishes engine-loadable resources. Order is priority.
/// </summary>
sealed record Titanfall2ContentEntry(
	string Path, string Kind, string SourceId, string SourceIdentity,
	ITitanfall2AssetSource Source = null, RpakArchive Archive = null, RpakAsset Asset = default,
	MilesBankArchive Bank = null, MilesSoundAsset Sound = null )
{
	internal string Key => $"{Kind}:{Path}";
}

/// <summary>
/// Fixed mount-session resolution context. No directory enumeration or host lookup on
/// reads. Bound sources retain this snapshot, including after a mount is retired.
/// File contents are not copied: changed loose files fail instead of switching versions.
/// </summary>
sealed class Titanfall2ContentSnapshot
{
	readonly FrozenDictionary<string, Titanfall2ContentEntry> _winners;
	readonly FrozenDictionary<string, ReadOnlyCollection<Titanfall2ContentEntry>> _candidates;
	readonly FrozenDictionary<(string Kind, ulong Id), Titanfall2ContentEntry> _nativeIds;
	internal string Revision { get; }
	internal string SelectedMap { get; }
	internal IReadOnlyList<Titanfall2ContentEntry> Entries { get; }
	internal IReadOnlyList<Titanfall2ContentEntry> Winners { get; }
	internal IReadOnlyList<string> Diagnostics { get; }

	internal Titanfall2ContentSnapshot( IEnumerable<Titanfall2ContentEntry> entries, IEnumerable<string> identities, IEnumerable<string> diagnostics, string selectedMap = null )
	{
		SelectedMap = selectedMap;
		var all = entries.ToArray();
		var groups = all.GroupBy( entry => entry.Key, StringComparer.OrdinalIgnoreCase ).ToArray();
		_winners = groups.ToFrozenDictionary( group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase );
		_candidates = groups.Where( group => group.Skip( 1 ).Any() ).ToFrozenDictionary(
			group => group.Key, group => Array.AsReadOnly( group.ToArray() ), StringComparer.OrdinalIgnoreCase );
		_nativeIds = all.Where( entry => entry.Archive is not null ).GroupBy( entry => (entry.Kind, entry.Asset.Guid) )
			.ToFrozenDictionary( group => group.Key, group => group.First() );
		Entries = Array.AsReadOnly( all );
		Winners = Array.AsReadOnly( all.Where( entry => ReferenceEquals( _winners[entry.Key], entry ) ).ToArray() );
		Diagnostics = Array.AsReadOnly( diagnostics.ToArray() );
		// Length-prefix each field: filenames and manifest strings can contain delimiters.
		using var digest = IncrementalHash.CreateHash( HashAlgorithmName.SHA256 );
		void Add( string value )
		{
			var bytes = Encoding.UTF8.GetBytes( value );
			digest.AppendData( BitConverter.GetBytes( bytes.Length ) );
			digest.AppendData( bytes );
		}
		Add( "tf2-content-v1" );
		foreach ( var identity in identities ) Add( identity );
		foreach ( var entry in all )
		{
			Add( entry.Key ); Add( entry.SourceId ); Add( entry.SourceIdentity );
			Add( entry.Archive is null ? "" : entry.Asset.Guid.ToString( "x16" ) );
		}
		Revision = Convert.ToHexString( digest.GetHashAndReset() );
	}

	internal bool TryGet( string path, out Titanfall2ContentEntry entry, string kind = "file" )
	{
		entry = null;
		return TryNormalize( path, out var normalized ) && _winners.TryGetValue( $"{kind}:{normalized}", out entry );
	}

	internal bool TryGetNative( string kind, ulong id, out Titanfall2ContentEntry entry ) => _nativeIds.TryGetValue( (kind, id), out entry );

	internal IReadOnlyList<Titanfall2ContentEntry> Explain( string path, string kind = "file" )
	{
		if ( !TryGet( path, out var winner, kind ) ) return Array.Empty<Titanfall2ContentEntry>();
		return _candidates.TryGetValue( winner.Key, out var candidates ) ? candidates : Array.AsReadOnly( new[] { winner } );
	}

	internal ITitanfall2AssetSource GetSource( string path ) => TryGet( path, out var entry ) ? new BoundSource( this, entry ) : null;

	internal static bool TryNormalize( string path, out string normalized )
	{
		normalized = null;
		if ( string.IsNullOrWhiteSpace( path ) || path[0] is '/' or '\\' || path.Contains( ':' ) || path.Contains( '\0' ) ) return false;
		var parts = new List<string>();
		foreach ( var part in path.Replace( '\\', '/' ).Split( '/', StringSplitOptions.RemoveEmptyEntries ) )
		{
			if ( part == "." ) continue;
			if ( part == ".." )
			{
				if ( parts.Count == 0 ) return false;
				parts.RemoveAt( parts.Count - 1 );
			}
			else parts.Add( part );
		}
		if ( parts.Count == 0 ) return false;
		normalized = string.Join( '/', parts ).ToLowerInvariant();
		return true;
	}

	sealed class BoundSource( Titanfall2ContentSnapshot snapshot, Titanfall2ContentEntry entry )
		: ITitanfall2AssetSource, ITitanfall2PathAssetSource, ITitanfall2SiblingAssetSource, ITitanfall2PrefixAssetSource
	{
		public string Description => entry.Path;
		public string CacheIdentity => $"{snapshot.Revision}:{entry.Key}";
		public bool TryReadAllBytes( out byte[] bytes, out string error ) => entry.Source.TryReadAllBytes( out bytes, out error );
		public bool TryReadPath( string path, out byte[] bytes, out string error )
		{
			var source = snapshot.GetSource( path );
			if ( source is not null ) return source.TryReadAllBytes( out bytes, out error );
			bytes = null; error = $"Content not found in snapshot {snapshot.Revision}: {path}"; return false;
		}
		public bool TryReadSibling( string suffix, out byte[] bytes, out string error ) => TryReadPath( entry.Path + suffix, out bytes, out error );
		public bool TryReadPrefix( int minimumBytes, out byte[] bytes, out string error )
		{
			if ( entry.Source is ITitanfall2PrefixAssetSource prefix ) return prefix.TryReadPrefix( minimumBytes, out bytes, out error );
			return TryReadAllBytes( out bytes, out error );
		}
	}
}

/// <summary>Captures loose bytes by digest, without retaining the payload in memory.</summary>
sealed class Titanfall2SnapshotFile : ITitanfall2AssetSource, ITitanfall2PrefixAssetSource
{
	readonly string _root, _path, _digest;
	readonly long _length;
	public string Description => _path;
	public string CacheIdentity => $"{_path}:{_length}:{_digest}";

	internal Titanfall2SnapshotFile( string root, string path )
	{
		_root = Path.GetFullPath( root ); _path = Path.GetFullPath( path );
		ValidatePath();
		using var stream = File.OpenRead( _path );
		_length = stream.Length;
		if ( _length > 1L << 30 ) throw new InvalidDataException( "Loose content exceeds the 1 GiB read limit." );
		_digest = Convert.ToHexString( SHA256.HashData( stream ) );
	}

	void ValidatePath()
	{
		var prefix = Path.TrimEndingDirectorySeparator( _root ) + Path.DirectorySeparatorChar;
		if ( !_path.StartsWith( prefix, StringComparison.OrdinalIgnoreCase ) ) throw new InvalidDataException( "Content escaped its root." );
		for ( var current = _path; current is not null; current = Path.GetDirectoryName( current ) )
		{
			if ( (File.GetAttributes( current ) & FileAttributes.ReparsePoint) != 0 ) throw new InvalidDataException( $"Reparse point in content path: {current}" );
		}
	}

	public bool TryReadAllBytes( out byte[] bytes, out string error )
	{
		bytes = null;
		try
		{
			ValidatePath();
			using var stream = File.OpenRead( _path );
			if ( stream.Length != _length || _length > 1L << 30 ) throw new InvalidDataException( "Loose content length changed or exceeds the read limit." );
			var data = new byte[checked((int)_length)];
			stream.ReadExactly( data );
			if ( Convert.ToHexString( SHA256.HashData( data ) ) != _digest ) throw new InvalidDataException( "Loose content changed; start a new mount session." );
			bytes = data; error = null; return true;
		}
		catch ( Exception exception ) { error = exception.Message; return false; }
	}

	public bool TryReadPrefix( int minimumBytes, out byte[] bytes, out string error )
	{
		// Verify the complete digest, even for a header request. No unverified replacement
		// may alter the model catalog or companion count after snapshot publication.
		if ( !TryReadAllBytes( out bytes, out error ) ) return false;
		if ( minimumBytes >= 0 && bytes.Length > minimumBytes ) Array.Resize( ref bytes, minimumBytes );
		return true;
	}
}
