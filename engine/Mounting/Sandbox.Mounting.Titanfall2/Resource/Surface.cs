/// <summary>
/// Maps Titanfall 2 surface property names onto the engine's <see cref="Surface"/> resources.
/// <para>
/// The mount reads surface properties wherever the formats expose them (MDL model/bone
/// surface props and legacy VMT <c>$surfaceprop</c>). Retail rBSP material surface names are
/// stored in the MATL asset header as fixed-up string pointers, which the mount does not
/// resolve yet; those materials keep the engine default surface.
/// </para>
/// </summary>
static class Titanfall2Surface
{
	/// <summary>The engine surface used when a name is unknown or not exported by the game.</summary>
	internal const string FallbackName = "default";

	/// <summary>
	/// Titanfall 2 (and Source) names that have no engine surface with the same name.
	/// Keep this list conservative: a wrong mapping changes friction and footstep sounds.
	/// </summary>
	static readonly Dictionary<string, string> Aliases = new( StringComparer.OrdinalIgnoreCase )
	{
		// Solid mineral surfaces: the engine has no concrete/stone surface.
		["concrete"] = "dirt",
		["concrete_block"] = "dirt",
		["rock"] = "dirt",
		["stone"] = "dirt",
		["gravel"] = "dirt",
		["asphalt"] = "dirt",
		["sand"] = "dirt",
		["mud"] = "dirt",
		["snow"] = "dirt",
		["tile"] = "ceramic",
		// Wood variants.
		["wood_panel"] = "wood",
		["wood_panel_solid"] = "wood",
		["wood_solid"] = "wood",
		// Metal variants.
		["metal_panel"] = "metal",
		["metal_grate"] = "metal",
		["metal_solid"] = "metal",
		["metal_sheet_solid"] = "metal.sheet",
		["metal_vent"] = "metal",
		// Cloth and soft surfaces.
		["cloth"] = "fabric",
		["carpet"] = "fabric",
		["paper"] = "cardboard",
		["cardboard_box"] = "cardboard",
		// Organic.
		["body"] = "flesh",
		["blood"] = "flesh",
		// Liquids.
		["water"] = "water",
		["slime"] = "water",
	};

	static readonly Dictionary<string, Surface> Resolved = new( StringComparer.OrdinalIgnoreCase );
	static readonly HashSet<string> Warned = new( StringComparer.OrdinalIgnoreCase );
	static readonly object Sync = new();

	/// <summary>
	/// Normalizes a source surface name into the engine's resource-name form. Paths, quotes,
	/// extensions and a <c>surfaceproperties/</c> prefix are removed; aliases are applied last.
	/// </summary>
	internal static string NormalizeName( string name )
	{
		if ( string.IsNullOrWhiteSpace( name ) )
			return FallbackName;

		var value = name.Replace( '\\', '/' ).Trim().Trim( '"' ).Trim( '\'' ).TrimStart( '/' );
		var slash = value.LastIndexOf( '/' );
		if ( slash >= 0 )
			value = value[(slash + 1)..];

		if ( value.EndsWith( ".surface", StringComparison.OrdinalIgnoreCase ) )
			value = value[..^8];
		else if ( value.EndsWith( ".surf", StringComparison.OrdinalIgnoreCase ) )
			value = value[..^5];

		value = value.Trim().ToLowerInvariant();
		if ( value.Length == 0 )
			return FallbackName;

		return Aliases.TryGetValue( value, out var alias ) ? alias : value;
	}

	/// <summary>
	/// Resolves a source surface name to an engine surface. Unknown names fall back to
	/// <c>default</c>; each unknown name is reported once so a map does not flood the log.
	/// </summary>
	internal static Surface Resolve( string name, string source )
	{
		var normalized = NormalizeName( name );
		lock ( Sync )
		{
			if ( Resolved.TryGetValue( normalized, out var cached ) && cached.IsValid() )
				return cached;
		}

		var surface = Surface.FindByName( normalized );
		if ( surface is not null )
		{
			lock ( Sync )
			{
				Resolved[normalized] = surface;
			}
			return surface;
		}

		if ( !normalized.Equals( FallbackName, StringComparison.OrdinalIgnoreCase ) )
		{
			var warn = false;
			lock ( Sync )
			{
				warn = Warned.Add( normalized );
			}
			if ( warn )
				Titanfall2Log.Trace( $"{source ?? "Titanfall 2 mount"}: surface '{name}' has no engine equivalent; using '{FallbackName}'." );
		}

		var fallback = Surface.FindByName( FallbackName );
		if ( fallback is not null )
		{
			lock ( Sync )
			{
				Resolved[FallbackName] = fallback;
			}
		}
		return fallback;
	}

	/// <summary>The engine resource name for a source surface name, or <c>default</c>.</summary>
	internal static string ResolveName( string name, string source )
	{
		return Resolve( name, source )?.ResourceName ?? FallbackName;
	}

	/// <summary>
	/// Applies the surface to a material's <c>PhysicsSurfaceProperties</c> attribute so the
	/// engine's material surface lookup (impacts, footsteps) reports the mapped surface.
	/// </summary>
	internal static void ApplyTo( Material material, string name, string source )
	{
		if ( material is null || string.IsNullOrWhiteSpace( name ) )
			return;

		var surface = Resolve( name, source );
		if ( surface is null || string.IsNullOrWhiteSpace( surface.ResourceName ) )
			return;

		material.Attributes.Set( "PhysicsSurfaceProperties", surface.ResourceName );
	}
}
