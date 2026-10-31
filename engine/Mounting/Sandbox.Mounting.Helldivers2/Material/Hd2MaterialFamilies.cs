namespace Sandbox.Mounting.Helldivers2;

/// <summary>
/// Maps the material slot and setting names recovered from the community name database onto the
/// parameters the engine's <c>shaders/complex.shader</c> exposes.
///
/// Slot names are packed ("color_roughness", "normal_ao_roughness", "normal_specular_ao"), so a
/// name is matched word by word and the families are tried in a deliberate order: normal first, then
/// colour, then roughness, then ambient occlusion, then metalness. That order is what keeps
/// "normal_ao_roughness" a normal map and "color_roughness" an albedo, which is how the reference
/// implementation treats them.
///
/// The parameter constants are the runtime sampler names (<c>g_t*</c>), not the attribute names the
/// <c>.vmat</c> files use. Measured live in the editor: <c>Material.Set</c> accepts any name and always
/// returns true, but reading back <c>TextureColor</c> yields a 0x0 placeholder while <c>g_tColor</c>
/// yields the texture that was actually bound. Textures bound under the attribute names are never
/// sampled, which is what made every mounted model render untextured.
/// </summary>
internal static class Hd2MaterialFamilies
{
	public const string ColorParameter = "g_tColor";
	public const string NormalParameter = "g_tNormal";
	public const string RoughnessParameter = "g_tRoughness";
	public const string AmbientOcclusionParameter = "g_tAmbientOcclusion";
	public const string MetalnessTextureParameter = "g_tMetalness";
	public const string MetalnessScalarParameter = "g_flMetalness";
	public const string RoughnessScaleParameter = "g_flRoughnessScaleFactor";
	public const string ColorTintParameter = "g_vColorTint";

	static readonly string[] NormalTokens = ["normal", "normals", "norm", "nac", "nar"];
	static readonly string[] ColorTokens = ["color", "colour", "albedo", "diffuse", "basecolor", "input_image"];
	static readonly string[] RoughnessTokens = ["rough", "roughness", "gloss", "glossiness", "smoothness"];
	static readonly string[] OcclusionTokens = ["ao", "occlusion", "ambientocclusion"];
	static readonly string[] MetalnessTokens = ["metal", "metalness", "metallic"];
	static readonly string[] EmissiveTokens = ["emissive", "emission", "emit"];

	/// <summary>A setting's colour family; narrower than the texture one, which also accepts "input_image".</summary>
	static readonly string[] SettingColorTokens = ["color", "colour", "tint", "basecolor"];

	static readonly char[] WordSeparators = ['_', '-', '.', '/', ' '];

	/// <summary>
	/// Appends every engine texture parameter a resolved slot name feeds, in family priority order.
	///
	/// Packed names deliberately match more than one family: "normal_ao_roughness" is a normal map, a
	/// roughness map and an ambient occlusion map at once, and the complex shader samples each of
	/// those from its own parameter. Binding only the first match left hard-surface models without a
	/// normal map, which is what made them render flat.
	/// </summary>
	public static void MatchTextureParameters( string name, List<string> parameters )
	{
		if ( string.IsNullOrWhiteSpace( name ) ) return;

		if ( Matches( name, NormalTokens ) ) parameters.Add( NormalParameter );
		if ( Matches( name, ColorTokens ) ) parameters.Add( ColorParameter );
		if ( Matches( name, RoughnessTokens ) ) parameters.Add( RoughnessParameter );
		if ( Matches( name, OcclusionTokens ) ) parameters.Add( AmbientOcclusionParameter );
		if ( Matches( name, MetalnessTokens ) ) parameters.Add( MetalnessTextureParameter );
	}

	/// <summary>
	/// Maps a resolved setting name onto a parameter. These are the value parameters, which the
	/// engine sets through the <c>Vector4</c> overload and therefore never rewrites with a
	/// <c>g_t</c> prefix. Emissive belongs to no parameter the complex shader has and stays
	/// unmapped, reported as unsupported.
	///
	/// Measured coverage is sparse rather than wrong: over the first 64 shipped materials, 622
	/// settings carried only 6 distinct recoverable names (base_color, color, emissive,
	/// emissive_intensity, width, height). Where a name does resolve, this is the only colour a
	/// texture-less material has.
	/// </summary>
	public static bool TryMatchSettingParameter( string name, out string parameter )
	{
		parameter = null;
		if ( string.IsNullOrWhiteSpace( name ) ) return false;
		if ( IsUnsupported( name ) ) return false;

		if ( Matches( name, MetalnessTokens ) )
		{
			parameter = MetalnessScalarParameter;
			return true;
		}

		if ( Matches( name, RoughnessTokens ) )
		{
			parameter = RoughnessScaleParameter;
			return true;
		}

		if ( Matches( name, SettingColorTokens ) )
		{
			parameter = ColorTintParameter;
			return true;
		}

		return false;
	}

	/// <summary>True when the name reads as a family the complex shader has no parameter for.</summary>
	public static bool IsUnsupported( string name ) => !string.IsNullOrWhiteSpace( name ) && Matches( name, EmissiveTokens );

	/// <summary>True when any token appears as a whole word of the name, or as the whole name.</summary>
	static bool Matches( string name, string[] tokens )
	{
		foreach ( var token in tokens )
		{
			if ( name.Equals( token, StringComparison.OrdinalIgnoreCase ) ) return true;
			if ( ContainsWord( name, token ) ) return true;
		}

		return false;
	}

	static bool ContainsWord( string name, string token )
	{
		var start = 0;
		while ( start <= name.Length )
		{
			var end = name.IndexOfAny( WordSeparators, start );
			var length = ( end < 0 ? name.Length : end ) - start;

			if ( length == token.Length && string.Compare( name, start, token, 0, token.Length, StringComparison.OrdinalIgnoreCase ) == 0 ) return true;
			if ( end < 0 ) return false;

			start = end + 1;
		}

		return false;
	}
}
