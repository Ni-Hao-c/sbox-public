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
		// Retail CM marks liquid volumes 0x20 and block-light tool faces 0x480; neither
		// contains a player-solid bit, so their render faces must not become floors or walls.
		if ( metadata.IsDecal || metadata.IsWater || MaterialLoader.IsGodrayMaterial( materialName )
			|| IsNonSolidToolName( materialName ) )
			return false;

		return !IsVisualEffectName( materialName );
	}

	internal static bool IsNonSolidToolName( string value )
	{
		if ( string.IsNullOrWhiteSpace( value ) ) return false;
		return value.Replace( '\\', '/' ).Contains( "tools/toolsblocklight", StringComparison.OrdinalIgnoreCase );
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
