using System.Text;

/// <summary>
/// Environment partitions supplement the BSP entity lump. Preserve authored duplicates
/// inside either source, but do not import their cross-partition copies a second time.
/// </summary>
static class Titanfall2EnvironmentEntities
{
	internal static IReadOnlyList<Dictionary<string, string>> Merge(
		IReadOnlyList<Dictionary<string, string>> external, IReadOnlyList<Dictionary<string, string>> embedded )
	{
		if ( external.Count == 0 ) return embedded;
		if ( embedded.Count == 0 ) return external;
		var result = new List<Dictionary<string, string>>( external );
		var copies = new Dictionary<string, Queue<int>>( StringComparer.Ordinal );
		for ( var i = 0; i < external.Count; ++i )
		{
			var identity = Identity( external[i] );
			if ( !copies.TryGetValue( identity, out var indices ) ) copies[identity] = indices = new();
			indices.Enqueue( i );
		}
		foreach ( var entity in embedded )
		{
			var identity = Identity( entity );
			if ( copies.TryGetValue( identity, out var indices ) && indices.TryDequeue( out var index ) )
			{
				// Generated convex metadata is not an authored difference. Transfer it to
				// the external copy without mutating either source dictionary.
				var added = entity.Where( pair => pair.Key.StartsWith( "*trigger_", StringComparison.Ordinal ) && !result[index].ContainsKey( pair.Key ) ).ToArray();
				if ( added.Length == 0 ) continue;
				var enriched = new Dictionary<string, string>( result[index], StringComparer.OrdinalIgnoreCase );
				foreach ( var pair in added ) enriched[pair.Key] = pair.Value;
				result[index] = enriched;
			}
			else result.Add( entity );
		}
		return result;
	}

	static string Identity( IReadOnlyDictionary<string, string> entity )
	{
		var identity = new StringBuilder();
		foreach ( var pair in entity.OrderBy( pair => pair.Key, StringComparer.OrdinalIgnoreCase ) )
		{
			if ( pair.Key.StartsWith( "*trigger_", StringComparison.Ordinal ) ) continue;
			var value = pair.Value ?? string.Empty;
			identity.Append( pair.Key.Length ).Append( ':' ).Append( pair.Key.ToLowerInvariant() )
				.Append( value.Length ).Append( ':' ).Append( value );
		}
		return identity.ToString();
	}
}
