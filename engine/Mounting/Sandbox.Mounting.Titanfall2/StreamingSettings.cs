/// <summary>Runtime budgets for Titanfall 2 map and texture streaming.</summary>
static class Titanfall2StreamingSettings
{
	// 512 was safe for loading every static prop, but visibly discarded the first
	// useful mip on most 1K/2K Titanfall materials. 1K is the balanced default;
	// the environment override can still select 512 for low-VRAM maps or 2048
	// while inspecting a small number of models.
	public static int TextureMaxDimension { get; } = GetInt( "SBOX_TITANFALL2_TEXTURE_MAX", 1024, 64, 2048 );
	// Decompressed package buffers are only a read accelerator. Keep a bounded
	// strong LRU; the lightweight on-disk index remains available after eviction.
	public static int RpakBufferBudgetMb { get; } = GetInt( "SBOX_TITANFALL2_RPAK_BUFFER_MB", 512, 64, 4096 );
	public static float PropCellSize { get; } = GetFloat( "SBOX_TITANFALL2_PROP_CELL_SIZE", 512f, 256f, 8192f );
	public static float PropLoadRadius { get; } = GetFloat( "SBOX_TITANFALL2_PROP_LOAD_RADIUS", 1024f, 512f, 32768f );
	// Every static prop and its model remain resident after the loading screen, but
	// distant scene objects do not need to participate in render-list generation.
	// A generous radius plus each model's bounds keeps large landmarks visible and
	// makes activation immediate without any VPK/RPAK work while the player moves.
	public static float PropRenderDistance { get; } = GetFloat( "SBOX_TITANFALL2_PROP_RENDER_DISTANCE", 4096f, 512f, 65536f );
	public static int PropsPerFrame { get; } = GetInt( "SBOX_TITANFALL2_PROPS_PER_FRAME", 16, 1, 256 );
	public static float PropFrameBudgetMilliseconds { get; } = GetFloat( "SBOX_TITANFALL2_PROP_FRAME_BUDGET_MS", 3f, 0.5f, 20f );
	// Scene loading waits for every static prop. Work is still split across yields
	// so the loading screen and platform event loop remain responsive.
	public static int PropPreloadPerYield { get; } = GetInt( "SBOX_TITANFALL2_PROP_PRELOAD_PER_YIELD", 128, 1, 2048 );
	public static float PropPreloadBudgetMilliseconds { get; } = GetFloat( "SBOX_TITANFALL2_PROP_PRELOAD_BUDGET_MS", 12f, 1f, 50f );
	// Static BSP props stay resident, but distant/small props do not need to be
	// submitted into every directional-light cascade. Large props receive a
	// radius allowance so buildings keep useful silhouettes beyond this range.
	public static float PropShadowDistance { get; } = GetFloat( "SBOX_TITANFALL2_PROP_SHADOW_DISTANCE", 896f, 0f, 32768f );
	public static float PropShadowMinimumRadius { get; } = GetFloat( "SBOX_TITANFALL2_PROP_SHADOW_MIN_RADIUS", 48f, 0f, 2048f );
	// World render chunks remain visible at every distance; only their expensive
	// directional-light shadow submission is culled with hysteresis.
	public static float WorldShadowEnableDistance { get; } = GetFloat( "SBOX_TITANFALL2_WORLD_SHADOW_ENABLE_DISTANCE", 2560f, 512f, 32768f );
	public static float WorldShadowDisableDistance { get; } = GetFloat( "SBOX_TITANFALL2_WORLD_SHADOW_DISABLE_DISTANCE", 3072f, 512f, 65536f );
	// Temporarily suppress the imported env_fog_controller while lighting and
	// effect matching are being tuned. Keep the entity data available so this can
	// be restored without changing the BSP reader.
	public static bool MapFog { get; } = false;
	// Billboarded steam/mist PCFs and model/BSP fog cards still need individual
	// Source operator and blend validation. Temporarily omit those atmospheric
	// cards while retaining fire, sparks, drips and the rest of the FX catalog.
	public static bool AtmosphericCardEffects { get; } = false;
	// Mounted models now prefer their compact VPHY hulls (and fall back to
	// skeletal hitboxes), so enabling prop colliders no longer duplicates every
	// render triangle into physics by default.
	public static bool PropCollisions { get; } = GetBool( "SBOX_TITANFALL2_PROP_COLLISIONS", true );
	public static bool NavMeshStaticProps { get; } = GetBool( "SBOX_TITANFALL2_NAVMESH_STATIC_PROPS", false );
	// Models without VPHY or hitboxes may fall back to their render mesh. Keep
	// small props accurate, but replace complex render meshes with one convex
	// bounds hull so a decorative asset cannot inject tens of thousands of
	// triangles into physics and trace acceleration structures.
	public static int ModelCollisionMeshMaxTriangles { get; } = GetInt( "SBOX_TITANFALL2_MODEL_COLLISION_MAX_TRIANGLES", 8192, 64, 131072 );
	public static float CollisionCellSize { get; } = GetFloat( "SBOX_TITANFALL2_COLLISION_CELL_SIZE", 512f, 256f, 8192f );
	// World geometry is partitioned before model creation so frustum culling can
	// reject local chunks instead of submitting material meshes spanning the map.
	public static float WorldRenderCellSize { get; } = GetFloat( "SBOX_TITANFALL2_WORLD_RENDER_CELL_SIZE", 2048f, 512f, 8192f );
	public static int WorldMaterialsPerFrame { get; } = GetInt( "SBOX_TITANFALL2_WORLD_MATERIALS_PER_FRAME", 1, 1, 16 );
	public static float WorldMaterialFrameBudgetMilliseconds { get; } = GetFloat( "SBOX_TITANFALL2_WORLD_MATERIAL_FRAME_BUDGET_MS", 2f, 0.25f, 20f );
	// PCF libraries and map particle instances are deliberately streamed in two
	// independent stages. Parsing every library while the BSP scene is built can
	// otherwise turn several hundred small effects into a long loading-screen stall.
	public static int ParticleLibrariesPerFrame { get; } = GetInt( "SBOX_TITANFALL2_PARTICLE_LIBRARIES_PER_FRAME", 8, 1, 32 );
	public static float ParticleLibraryFrameBudgetMilliseconds { get; } = GetFloat( "SBOX_TITANFALL2_PARTICLE_LIBRARY_BUDGET_MS", 2f, 0.25f, 20f );
	public static int ParticleInstancesPerFrame { get; } = GetInt( "SBOX_TITANFALL2_PARTICLE_INSTANCES_PER_FRAME", 4, 1, 64 );
	public static float ParticleInstanceFrameBudgetMilliseconds { get; } = GetFloat( "SBOX_TITANFALL2_PARTICLE_INSTANCE_BUDGET_MS", 2f, 0.25f, 20f );
	public static float ParticleActivationRadius { get; } = GetFloat( "SBOX_TITANFALL2_PARTICLE_ACTIVATION_RADIUS", 4096f, 256f, 65536f );
	public static int ParticleMaxParticlesPerSystem { get; } = GetInt( "SBOX_TITANFALL2_PARTICLE_MAX_PER_SYSTEM", 256, 1, 4096 );
	public static int ParticleMaxLightsPerSystem { get; } = GetInt( "SBOX_TITANFALL2_PARTICLE_MAX_LIGHTS", 4, 0, 32 );
	// Source PCFs can drive radius/velocity through scalar graphs. Until every
	// Titanfall operator is represented exactly, cap those values so one malformed
	// trail or steam system cannot cover the map with kilometre-sized cards.
	public static float ParticleMaximumRadius { get; } = GetFloat( "SBOX_TITANFALL2_PARTICLE_MAX_RADIUS", 256f, 8f, 4096f );
	public static float ParticleMaximumEmitterExtent { get; } = GetFloat( "SBOX_TITANFALL2_PARTICLE_MAX_EMITTER_EXTENT", 2048f, 64f, 16384f );
	public static float ParticleMaximumVelocity { get; } = GetFloat( "SBOX_TITANFALL2_PARTICLE_MAX_VELOCITY", 2048f, 64f, 32768f );
	// Temporarily force navigation generation off for mounted Titanfall 2 maps.
	// Keep this independent of the environment so a stale setting cannot re-enable it.
	public static bool DeferredNavMesh { get; } = false;
	public static float NavMeshStartDelaySeconds { get; } = GetFloat( "SBOX_TITANFALL2_NAVMESH_DELAY_SECONDS", 3f, 0f, 120f );

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
