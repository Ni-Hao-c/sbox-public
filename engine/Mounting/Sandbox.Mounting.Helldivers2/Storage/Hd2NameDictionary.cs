namespace Sandbox.Mounting.Helldivers2;

/// <summary>
/// Helldivers 2 containers store no readable resource names: everything is a 64-bit hash. This
/// loads the community name databases shipped next to the mount and turns them into a hash to name
/// map, so resources and resource types can be reported by name.
///
/// Two line formats are accepted:
/// <list type="bullet">
/// <item><c>0x0123456789abcdef,content/path/name</c> - an explicit hash to name pair.</item>
/// <item><c>content/path/name</c> - a bare name, hashed with the same function the data compiler used.</item>
/// </list>
/// Blank lines and lines starting with <c>//</c> or <c>#</c> are ignored. Pairs are listed after
/// bare names in the shipped file, so an explicit hash always wins over a computed one.
/// </summary>
internal sealed class Hd2NameDictionary
{
	readonly Dictionary<ulong, string> _namesByHash = [];
	readonly Dictionary<uint, string> _namesByThinHash = [];
	bool _thinIndexBuilt;

	/// <summary>How many candidate lines were read, before filtering.</summary>
	public int Loaded { get; private set; }

	/// <summary>How many of those were explicit hash/name pairs.</summary>
	public int Pairs { get; private set; }

	public int Count => _namesByHash.Count;

	public string SourcePath { get; private set; }

	public static Hd2NameDictionary Empty() => new();

	public static Hd2NameDictionary Load( string path )
	{
		var dictionary = new Hd2NameDictionary { SourcePath = path };

		try
		{
			if ( string.IsNullOrWhiteSpace( path ) || !File.Exists( path ) ) return dictionary;

			foreach ( var raw in File.ReadLines( path ) )
			{
				var line = raw.Trim();
				if ( line.Length == 0 ) continue;
				if ( line.StartsWith( "//", StringComparison.Ordinal ) || line.StartsWith( "#", StringComparison.Ordinal ) ) continue;

				dictionary.Loaded++;

				var separator = line.IndexOf( ',' );
				if ( separator > 0 )
				{
					var name = line[( separator + 1 )..].Trim();
					if ( name.Length is 0 or > 192 ) continue;
					if ( !Hd2Hash.TryParse( line[..separator].Trim(), out var explicitHash ) ) continue;

					dictionary._namesByHash[explicitHash.Value] = name;
					dictionary.Pairs++;
					continue;
				}

				// The bare-name lists are scraped from several sources, so they also contain prose
				// fragments. Anything with whitespace or quotes cannot be a resource path, and adding
				// it would only put noise in the hash table.
				if ( line.Length > 192 ) continue;
				if ( line.IndexOfAny( [' ', '\t', '"', '\''] ) >= 0 ) continue;

				dictionary._namesByHash[Hd2Hash.Sum( line ).Value] = line;
			}
		}
		catch ( Exception exception )
		{
			Hd2Log.Warning( exception, $"Unable to read the Helldivers 2 name dictionary at '{path}'." );
		}

		return dictionary;
	}

	public bool TryGetName( Hd2Hash hash, out string name ) => _namesByHash.TryGetValue( hash.Value, out name );

	/// <summary>
	/// Resolves a Stingray thin hash (the high 32 bits of a full hash), which is what mesh names and
	/// material texture slots are stored as. Thin hashes share a 32-bit space, so a collision keeps
	/// whichever name was indexed first; that is a cosmetic risk, never a correctness one.
	/// </summary>
	public bool TryGetName( Hd2ThinHash hash, out string name )
	{
		EnsureThinIndex();
		return _namesByThinHash.TryGetValue( hash.Value, out name );
	}

	void EnsureThinIndex()
	{
		if ( _thinIndexBuilt ) return;

		_thinIndexBuilt = true;

		foreach ( var pair in _namesByHash )
		{
			var thin = (uint)( pair.Key >> 32 );
			_namesByThinHash.TryAdd( thin, pair.Value );
		}
	}

	/// <summary>The readable name for a hash, or its hex form when the dictionary does not know it.</summary>
	public string NameOrHash( Hd2Hash hash ) => _namesByHash.TryGetValue( hash.Value, out var name ) ? name : hash.ToString();
}
