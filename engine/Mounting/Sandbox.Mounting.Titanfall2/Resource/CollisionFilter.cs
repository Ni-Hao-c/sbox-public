/// <summary>
/// Filters visual-only Titanfall materials out of runtime physics. Titanfall's
/// rBSP render mesh contains water surfaces, fog cards, light shafts and other
/// effects that were never solid in the original game.
/// </summary>
static class Titanfall2CollisionFilter
{
	static readonly string[] VisualEffectTerms =
	[
		"godray",
		"atmosphere",
		"lightbeam",
		"light_beam",
		"/beam",
		"cloud",
		"/fog",
		"_fog",
		"smoke",
		"mist",
		"steam",
		"spray",
		"waterfall",
		"watersheet",
		"water_sheet",
		"heatwave",
		"distortion"
	];

	static readonly string[] AtmosphericCardTerms =
	[
		"/fog",
		"_fog",
		"fog_",
		"smoke",
		"mist",
		"steam",
		"humid"
	];

	internal static bool ShouldIncludeMaterial( string materialName, Titanfall2MaterialMetadata metadata )
	{
		if ( metadata.IsDecal || MaterialLoader.IsGodrayMaterial( materialName ) )
			return false;

		return !IsVisualEffectName( materialName );
	}

	internal static bool IsVisualEffectName( string value )
	{
		if ( string.IsNullOrWhiteSpace( value ) ) return false;
		var normalized = value.Replace( '\\', '/' );
		foreach ( var term in VisualEffectTerms )
		{
			if ( normalized.Contains( term, StringComparison.OrdinalIgnoreCase ) )
				return true;
		}
		return false;
	}

	internal static bool IsAtmosphericCardName( string value )
	{
		if ( string.IsNullOrWhiteSpace( value ) ) return false;
		var normalized = value.Replace( '\\', '/' );
		foreach ( var term in AtmosphericCardTerms )
		{
			if ( normalized.Contains( term, StringComparison.OrdinalIgnoreCase ) )
				return true;
		}
		return false;
	}
}
