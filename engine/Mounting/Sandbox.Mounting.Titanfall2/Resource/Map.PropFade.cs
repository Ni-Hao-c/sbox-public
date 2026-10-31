using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Titanfall2;
using Titanfall2.Formats;

partial class MapLoader
{
	/// <summary>
	/// The distance at which the original game stops drawing each static prop, or 0 when the
	/// prop never fades. Respawn's v13 sprp record holds a fade distance where Source 1 kept
	/// forcedFadeScale: retail maps store -1 ("use the model's default") or a distance of
	/// 300 to 10,000,000 units, so a positive value wins and -1 falls back to the MDL header's
	/// DefaultFadeDistance, which is -1 again for models that never fade.
	/// </summary>
	float[] ResolvePropFadeDistances( IReadOnlyList<Titanfall2BspReader.StaticPropInstance> props )
	{
		var result = new float[props.Count];
		var models = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
		for ( var index = 0; index < props.Count; index++ )
		{
			var authored = props[index].ForcedFadeScale;
			if ( float.IsFinite( authored ) && authored > 0f ) result[index] = authored;
			else if ( !string.IsNullOrWhiteSpace( props[index].ModelPath ) ) models.Add( props[index].ModelPath );
		}

		var modelFades = ReadModelFadeDistances( models );
		for ( var index = 0; index < props.Count; index++ )
		{
			if ( result[index] > 0f || string.IsNullOrWhiteSpace( props[index].ModelPath ) ) continue;
			if ( modelFades.TryGetValue( props[index].ModelPath, out var fade ) && float.IsFinite( fade ) && fade > 0f )
				result[index] = fade;
		}
		return result;
	}

	/// <summary>
	/// Reads DefaultFadeDistance from each model's header. Only the first VPK chunk of a model
	/// is decoded, in parallel, and the result is cached per map: about 230 ms for the 732
	/// models of sp_sewers1 on a miss, a single small file read on a hit.
	/// </summary>
	Dictionary<string, float> ReadModelFadeDistances( IReadOnlyCollection<string> models )
	{
		var result = new Dictionary<string, float>( StringComparer.OrdinalIgnoreCase );
		if ( models.Count == 0 ) return result;
		var identity = $"{Titanfall2MapCache.Identity( Host, _source )}|prop-fade-v1";
		if ( Titanfall2ProcessedCache.TryRead( "propfade", identity, reader =>
		{
			var count = reader.Count( 1_000_000 );
			for ( var index = 0; index < count; index++ )
			{
				var path = reader.Text();
				result[path] = reader.ReadSingle();
			}
		} ) && models.All( result.ContainsKey ) ) return result;

		result.Clear();
		var headerSize = Unsafe.SizeOf<Mdl53StudioHeader>();
		var read = new ConcurrentDictionary<string, float>( StringComparer.OrdinalIgnoreCase );
		Parallel.ForEach( models, model =>
		{
			var fade = -1f;
			try
			{
				if ( Host.TryGetModelSource( model, out var source ) )
				{
					byte[] bytes = null;
					var ok = source is ITitanfall2PrefixAssetSource prefix
						? prefix.TryReadPrefix( headerSize, out bytes, out _ )
						: source.TryReadAllBytes( out bytes, out _ );
					if ( ok && bytes is not null && bytes.Length >= headerSize )
					{
						var header = Titanfall2Mdl53Binary.ReadStruct<Mdl53StudioHeader>( bytes, 0 );
						if ( header.Version == Titanfall2Mdl53Constants.StudioHeaderVersion ) fade = header.DefaultFadeDistance;
					}
				}
			}
			catch ( Exception ) { }
			read[model] = fade;
		} );
		foreach ( var pair in read ) result[pair.Key] = pair.Value;

		Titanfall2ProcessedCache.Write( "propfade", identity, writer =>
		{
			writer.Write( result.Count );
			foreach ( var pair in result )
			{
				Titanfall2ProcessedCache.WriteString( writer, pair.Key );
				writer.Write( pair.Value );
			}
		} );
		return result;
	}
}
