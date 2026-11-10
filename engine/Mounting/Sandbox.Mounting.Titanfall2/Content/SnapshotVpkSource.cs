using System.Collections.Frozen;

/// <summary>One captured version table per archive, shared by all its entries.</summary>
sealed class Titanfall2VpkVersion
{
	readonly string _directory;
	readonly FrozenDictionary<ushort, (string Path, string Stamp)> _payloads;
	internal IReadOnlyList<string> Identities { get; }

	internal Titanfall2VpkVersion( VpkArchive archive )
	{
		_directory = Titanfall2ContentDiscovery.Stamp( archive.DirectoryPath );
		_payloads = archive.Entries.SelectMany( entry => entry.Chunks ).Select( chunk => chunk.ArchiveIndex )
			.Where( index => index != 0x7fff ).Distinct().ToFrozenDictionary( index => index, index =>
			{
				var path = archive.ResolvePayloadPath( index );
				return (path, Stamp( path ));
			} );
		Identities = Array.AsReadOnly( _payloads.OrderBy( pair => pair.Key ).Select( pair => pair.Value.Stamp ).Prepend( _directory ).ToArray() );
	}

	static string Stamp( string path ) => File.Exists( path ) ? Titanfall2ContentDiscovery.Stamp( path ) : $"missing:{path}";

	internal void Validate( VpkArchive archive, VpkEntry entry )
	{
		if ( Stamp( archive.DirectoryPath ) != _directory ) throw new InvalidDataException( "VPK directory changed; start a new mount session." );
		foreach ( var chunk in entry.Chunks )
		{
			if ( chunk.ArchiveIndex == 0x7fff ) continue;
			var expected = _payloads[chunk.ArchiveIndex];
			if ( Stamp( expected.Path ) != expected.Stamp ) throw new InvalidDataException( "VPK payload changed; start a new mount session." );
		}
	}
}

sealed class Titanfall2SnapshotVpkSource( VpkArchive archive, VpkEntry entry, Titanfall2VpkVersion version ) : ITitanfall2AssetSource, ITitanfall2PrefixAssetSource
{
	public string Description => entry.Path;
	public bool TryReadAllBytes( out byte[] bytes, out string error ) => Read( -1, out bytes, out error );
	public bool TryReadPrefix( int minimumBytes, out byte[] bytes, out string error ) => Read( minimumBytes, out bytes, out error );

	bool Read( int minimumBytes, out byte[] bytes, out string error )
	{
		bytes = null;
		try
		{
			version.Validate( archive, entry );
			return minimumBytes < 0 ? archive.TryReadAllBytes( entry, out bytes, out error ) : archive.TryReadPrefix( entry, minimumBytes, out bytes, out error );
		}
		catch ( Exception exception ) { error = exception.Message; return false; }
	}
}
