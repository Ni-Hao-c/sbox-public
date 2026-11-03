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

	// Upload the two largest lightmap channels as 16-bit textures: SkyB as RGB565 and RealTimeB as
	// BGRA4444. The shader keeps sampling float4, so only precision changes (retail RealTimeB is
	// 93.7-96.7% binary, SkyB loses ~1% of sky visibility) while the lightmap block drops from 17 to
	// 13 bytes per texel - the bandwidth the world shader reads every pixel. 0 restores RGBA8888.
	public static bool Lightmap16Bit { get; } = GetBool( "SBOX_TITANFALL2_LIGHTMAP_16BIT", true );

	// Frame-rate preset: one switch for the knobs that trade distant detail for submitted work.
	// 0 leaves every knob at its own default, 60 is those same defaults, 120 raises the values
	// that shrink the shadow/depth passes and the per-frame submission count. An explicit
	// SBOX_TITANFALL2_* variable always wins over the preset, so a single knob can be tuned back.
	public static int FpsPreset { get; } = GetInt( "SBOX_TITANFALL2_FPS_PRESET", 0, 0, 240 );
	static bool AggressiveFps => FpsPreset >= 120;

	// Lightmap mips: dilate each chart outward and upload a short chain (L0..L2) for the three
	// channels the world shader samples with Tex2DS. Off by default because it softens chart edges
	// slightly at distance; the 120 fps preset turns it on for the bandwidth win. An explicit
	// SBOX_TITANFALL2_LIGHTMAP_MIPS overrides either default.
	public static bool LightmapMips { get; } = GetBool( "SBOX_TITANFALL2_LIGHTMAP_MIPS", AggressiveFps );

	// Per-instance distance cull for static props; large landmarks are exempt. 0 disables it.
	public static float PropRenderDistance { get; } = GetFloat( "SBOX_TITANFALL2_PROP_RENDER_DISTANCE", AggressiveFps ? 6144f : 8192f, 0f, 65536f );
	// Multiplies the fade distances authored on each static prop.
	public static float PropAuthoredFadeScale { get; } = GetFloat( "SBOX_TITANFALL2_PROP_AUTHORED_FADE_SCALE", 1f, 0.25f, 16f );
	// Render-submission granularity: how many prop cells per axis share one instanced batch.
	// A larger block merges more instances of one model into a single submitted object, which is
	// what the render main thread pays for. The cluster is also the unit at which a batch
	// aggregates its members' PVS cells and shadow flag, so raising it coarsens those two rules:
	// a batch is drawn when any member prop is PVS-visible and casts shadows when any member is
	// large enough. 4 is the measured baseline and keeps the current behaviour; 1 gives one
	// batch per cell for A/B, 8 and 16 merge further.
	public static int PropBatchClusterCells { get; } = GetClusterCells( "SBOX_TITANFALL2_BATCH_CLUSTER", AggressiveFps ? 8 : 4 );
	// Camera distance past which a prop batch stops casting directional shadows. A batch shares
	// one shadow flag, so one large member otherwise drags every instance in the cluster into the
	// shadow pass; on a retail map that is thousands of props the cascades do not cover anyway.
	// Off by default: toggling the flag while walking makes whole batches enter and leave the
	// shadow pass in bursts (hundreds of native flag changes per visibility sweep), which reads
	// as hitching. The 120 fps preset turns it on; 0 keeps every batch casting like before.
	public static float PropShadowDistance { get; } = GetFloat( "SBOX_TITANFALL2_PROP_SHADOW_DISTANCE", AggressiveFps ? 1024f : 0f, 0f, 65536f );
	// Multiplies the static prop LOD switch distances. Below 1 falls to a cheaper LOD sooner,
	// which cuts triangles in the forward and depth passes at the cost of distant silhouettes.
	// 1 keeps the authored 1536/4096/8192 so a default run matches the previous behaviour.
	public static float PropLodDistanceScale { get; } = GetFloat( "SBOX_TITANFALL2_PROP_LOD_SCALE", AggressiveFps ? 0.5f : 1f, 0.1f, 4f );
	// Edge length of the spatial cells world geometry is grouped into. Larger cells submit fewer
	// fragments (fewer draws), at the cost of a coarser frustum cull; 2048 is the measured default.
	public static int WorldRenderCellSize { get; } = GetInt( "SBOX_TITANFALL2_WORLD_CELL_SIZE", AggressiveFps ? 4096 : 2048, 512, 8192 );

	// World lightmaps, prop probes, local lights and atmospheric radiance share one linear
	// HDR path before the map's display transform. 0 uses s&box native lighting instead,
	// for diagnosing an unsupported map.
	public static bool UseBspLightmaps { get; } = GetBool( "SBOX_TITANFALL2_BSP_LIGHTMAPS", true );
	// Imported world and large props cast directional shadows alongside their baked lighting by default.
	public static bool MapShadows { get; } = GetBool( "SBOX_TITANFALL2_MAP_SHADOWS", true );
	// Draw a dedicated depth-only copy of the opaque world (one mesh per spatial cell, all sharing a
	// single runtime material) and stop the forward aggregates from casting. The world keeps
	// projecting through two cascades, but the shadow pass binds one shader and issues one draw per
	// cell instead of one per (cell, material) fragment with 300+ materials. Off restores the old
	// behaviour (aggregates cast with their own materials) for A/B; independent of SHADOW_CASCADES.
	public static bool ShadowGeometry { get; } = GetBool( "SBOX_TITANFALL2_SHADOW_GEOMETRY", true );
	// Directional shadow cascade count. Every cascade renders all shadow casters into its own depth
	// target, so this multiplies the whole shadow pass: 1 halves it, 3 suits very long shadow
	// distances. Forced to 1 when shadows are off; 2 keeps the shipped look.
	public static int ShadowCascadeCount { get; } = MapShadows
		? GetInt( "SBOX_TITANFALL2_SHADOW_CASCADES", AggressiveFps ? 1 : 2, 1, 3 )
		: 1;
	public static bool PropShadows { get; } = GetBool( "SBOX_TITANFALL2_PROP_SHADOWS", true );
	// Screen-space contact shadows supplement directional shadows on small details. The 120 fps
	// preset turns them off: they are a full-screen depth pass on top of the cascades.
	public static bool NativeContactShadows { get; } = GetBool( "SBOX_TITANFALL2_CONTACT_SHADOWS", !AggressiveFps );
	// Bias in stops on top of the map's retail exposure calibration.
	public static float BakedExposureCompensation { get; } = GetFloat( "SBOX_TITANFALL2_EXPOSURE_EV", 0f, -5f, 5f );
	// Godray cards are additive and camera-distance faded. They are enabled by default;
	// turn them off when diagnosing bright streaks or flashing while moving. The 120 fps preset
	// drops them: they are translucent additive cards that cost fill rate for little geometry.
	public static bool Godrays { get; } = GetBool( "SBOX_TITANFALL2_GODRAYS", !AggressiveFps );

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

	// Batching is only measured on a 1/2/4/8/16 cell grid. A value in between snaps down to the
	// nearest measured grid instead of inventing a layout nobody has profiled.
	static int GetClusterCells( string name, int fallback )
	{
		var value = GetInt( name, fallback, 1, 16 );
		return value switch
		{
			>= 16 => 16,
			>= 8 => 8,
			>= 4 => 4,
			>= 2 => 2,
			_ => 1
		};
	}

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
