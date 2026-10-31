using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace Titanfall2;

/// <summary>
/// Parses Source-style keyvalue entity text (the rBSP ENTITIES lump and the external
/// <c>*_spawn/_env/_fx/_script/_snd.ent</c> partitions all use the same syntax).
/// Kept in Formats/ so both the binary reader and the scene layer share one parser.
/// </summary>
public static class Titanfall2EntityText
{
	static readonly Regex BlockPattern = new( @"\{(?<body>.*?)\}", RegexOptions.Singleline | RegexOptions.Compiled,
		TimeSpan.FromSeconds( 5 ) );
	static readonly Regex PairPattern = new( @"""(?<key>[^""]+)""\s*""(?<value>[^""]*)""", RegexOptions.Compiled,
		TimeSpan.FromSeconds( 5 ) );

	public static List<Dictionary<string, string>> Parse( byte[] bytes )
		=> bytes is null || bytes.Length == 0
			? new List<Dictionary<string, string>>()
			: Parse( Encoding.UTF8.GetString( bytes ).Replace( '\0', '\n' ) );

	public static List<Dictionary<string, string>> Parse( string text )
	{
		var entities = new List<Dictionary<string, string>>();
		if ( string.IsNullOrEmpty( text ) ) return entities;

		foreach ( Match block in BlockPattern.Matches( text ) )
		{
			var values = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase );
			foreach ( Match pair in PairPattern.Matches( block.Groups["body"].Value ) )
				values[pair.Groups["key"].Value] = pair.Groups["value"].Value;

			if ( values.Count > 0 ) entities.Add( values );
		}

		return entities;
	}
}
