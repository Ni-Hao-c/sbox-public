/// <summary>
/// The few environment switches of the Titanfall 2 mount. Each is read once at startup
/// from an SBOX_TITANFALL2_* variable; see ENVIRONMENT_SWITCHES.md. Streaming budgets and
/// other tuning values are constants next to the code that uses them.
/// </summary>
static class Titanfall2StreamingSettings
{
	public static bool ProcessedCache { get; } = GetBool( "SBOX_TITANFALL2_PROCESSED_CACHE", true );
	public static int ProcessedCacheBudgetMb { get; } = GetInt( "SBOX_TITANFALL2_CACHE_MB", 2048, 128, 8192 );
	// 1K keeps the first useful mip of most 1K/2K materials. 512 suits low-VRAM machines,
	// 2048 is for inspecting a few models up close.
	public static int TextureMaxDimension { get; } = GetInt( "SBOX_TITANFALL2_TEXTURE_MAX", 1024, 64, 2048 );

	// Per-instance distance cull for static props; large landmarks are exempt. 0 disables it.
	public static float PropRenderDistance { get; } = GetFloat( "SBOX_TITANFALL2_PROP_RENDER_DISTANCE", 8192f, 0f, 65536f );
	// Multiplies the fade distances authored on each static prop.
	public static float PropAuthoredFadeScale { get; } = GetFloat( "SBOX_TITANFALL2_PROP_AUTHORED_FADE_SCALE", 1f, 0.25f, 16f );

	// World lightmaps, prop probes, local lights and atmospheric radiance share one linear
	// HDR path before the map's display transform. 0 uses s&box native lighting instead,
	// for diagnosing an unsupported map.
	public static bool UseBspLightmaps { get; } = GetBool( "SBOX_TITANFALL2_BSP_LIGHTMAPS", true );
	// Lighting is baked, so the imported world and props skip the directional CSM pass by default.
	public static bool MapShadows { get; } = GetBool( "SBOX_TITANFALL2_MAP_SHADOWS", true );
	public static bool PropShadows { get; } = GetBool( "SBOX_TITANFALL2_PROP_SHADOWS", true );
	// Screen-space contact shadows ground small detail without the world CSM pass.
	public static bool NativeContactShadows { get; } = GetBool( "SBOX_TITANFALL2_CONTACT_SHADOWS", true );
	// Bias in stops on top of the map's retail exposure calibration.
	public static float BakedExposureCompensation { get; } = GetFloat( "SBOX_TITANFALL2_EXPOSURE_EV", 0f, -5f, 5f );
	// Godray cards are additive and camera-distance faded; in the imported maps they read as
	// bright streaks that flash while moving, so they are off by default.
	public static bool Godrays { get; } = GetBool( "SBOX_TITANFALL2_GODRAYS", true );

	public static bool MapAudio { get; } = GetBool( "SBOX_TITANFALL2_MAP_AUDIO", true );
	public static float MapAudioVolume { get; } = GetFloat( "SBOX_TITANFALL2_AUDIO_VOLUME", 0.7f, 0.05f, 2f );

	// A host scene with navigation enabled otherwise generates every tile of the map from
	// every collider before loading finishes (about 1.5-2 minutes on angel_city and timeshift).
	// On demand skips that pass, uses only tagged world collision, and builds tiles near
	// players and NavMesh agents. Off restores the engine's whole-map pass.
	public static bool NavMeshOnDemand { get; } = GetBool( "SBOX_TITANFALL2_NAVMESH_ON_DEMAND", true );
	public static float NavMeshOnDemandRadius { get; } = GetFloat( "SBOX_TITANFALL2_NAVMESH_RADIUS", 2048f, 512f, 65536f );
	// Enables navigation for a full map scene whose host did not, generated after loading.
	public static bool DeferredNavMesh { get; } = GetBool( "SBOX_TITANFALL2_DEFERRED_NAVMESH", false );
	public static bool NavMeshStaticProps { get; } = GetBool( "SBOX_TITANFALL2_NAVMESH_STATIC_PROPS", false );

	static int GetInt( string name, int fallback, int minimum, int maximum )
	{
		return int.TryParse( Environment.GetEnvironmentVariable( name ), out var value )
			? Math.Clamp( value, minimum, maximum )
			: fallback;
	}

	static float GetFloat( string name, float fallback, float minimum, float maximum )
	{
		return float.TryParse( Environment.GetEnvironmentVariable( name ), System.Globalization.NumberStyles.Float,
			System.Globalization.CultureInfo.InvariantCulture, out var value )
			? Math.Clamp( value, minimum, maximum )
			: fallback;
	}

	static bool GetBool( string name, bool fallback )
	{
		var value = Environment.GetEnvironmentVariable( name )?.Trim();
		if ( string.IsNullOrWhiteSpace( value ) ) return fallback;
		return value == "1"
			|| value.Equals( "true", StringComparison.OrdinalIgnoreCase )
			|| value.Equals( "yes", StringComparison.OrdinalIgnoreCase )
			|| value.Equals( "on", StringComparison.OrdinalIgnoreCase );
	}
}
