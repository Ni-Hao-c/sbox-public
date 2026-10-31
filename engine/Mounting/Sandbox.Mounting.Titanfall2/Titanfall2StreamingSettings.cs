/// <summary>
/// How a static prop's level of detail is selected.
/// <see cref="Native"/> keeps every LOD Respawn compiled into one Model and passes an
/// explicit level to the draw call. <see cref="Legacy"/> keeps one Model per LOD and swaps a
/// batch's model on the main thread whenever the camera crosses a distance band.
/// </summary>
enum Titanfall2PropLodMode
{
	Native,
	Legacy
}

/// <summary>Runtime budgets for Titanfall 2 map and texture streaming.</summary>
static class Titanfall2StreamingSettings
{
	public static bool ProcessedCache { get; } = GetBool( "SBOX_TITANFALL2_PROCESSED_CACHE", true );
	public static int ProcessedCacheBudgetMb { get; } = GetInt( "SBOX_TITANFALL2_CACHE_MB", 2048, 128, 8192 );
	// Experimental: bake rare, rigid props without useful LODs into BSP material
	// groups. Repeated and LOD-bearing models retain the instanced path.
	public static bool AggregateStaticProps { get; } = GetBool( "SBOX_TITANFALL2_AGGREGATE_STATIC_PROPS", false );
	public static int AggregatePropMaxCopies { get; } = GetInt( "SBOX_TITANFALL2_AGGREGATE_PROP_MAX_COPIES", 4, 1, 16 );
	public static int AggregatePropVertexBudget { get; } = GetInt( "SBOX_TITANFALL2_AGGREGATE_PROP_VERTICES", 300000, 0, 2000000 );
	public static bool PropInstanceFrustumCulling { get; } = GetBool( "SBOX_TITANFALL2_PROP_INSTANCE_FRUSTUM", false );
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
	// Static prop batches are keyed per BSP prop cell and model. On mp_angel_city that
	// produced 5458 batches for 18412 instances (3.4 instances per batch) and the
	// engine's own "draw calls over 3000" warning, because one model repeated across
	// neighbouring cells paid for one instanced draw per cell. Grouping an N x N block
	// of prop cells into one batch cluster keys batches per cluster and model instead.
	// Four cells per axis reduce CPU submissions on Angel City. LOD and distance
	// remain per instance; batch visibility/shadows conservatively keep a group when
	// any member needs it. Set 1 to restore the historical cell granularity for A/B.
	// Read when a scene's prop cells are initialized; changing it does not mutate live batches.
	public static int PropBatchClusterCells { get; set; } = GetInt( "SBOX_TITANFALL2_PROP_BATCH_CLUSTER_CELLS", 4, 1, 8 );
	// Every static prop and its model remain resident after the loading screen, but
	// distant scene objects do not need to participate in render-list generation.
	// A generous radius plus each model's bounds keeps large landmarks visible and
	// makes activation immediate without any VPK/RPAK work while the player moves.
	//
	// 0 disables distance culling entirely, which is the historical behaviour: this
	// budget was declared but never read until now, so a non-zero default would
	// silently change how many batches reach the renderer. Measured cost of the
	// status quo: 5458 instance batches, main-thread Render 13.1 ms at 92% of the
	// frame budget. Enable it per-machine to A/B the submission cost.
	// Settable at runtime on purpose: these three decide how much geometry reaches the
	// renderer, and each costs a full relaunch to change through the environment, which
	// makes a controlled A/B (same spot, same view, one variable) impractical. Everything
	// else here is read once from the environment and stays fixed.
	// Distance cull for props, now applied per instance (see UpdateClusterVisibilityState) rather
	// than per batch, so props drop out one at a time instead of a whole cell's worth disappearing
	// together. The longer default retains distant detail; large landmarks are exempt.
	// Set 0 to disable distance culling entirely.
	public static float PropRenderDistance { get; set; } = GetFloat( "SBOX_TITANFALL2_PROP_RENDER_DISTANCE", 8192f, 0f, 65536f );
	// Keep large silhouettes; PVS and per-view frustum culling still apply.
	public static float PropLandmarkRadius { get; } = GetFloat( "SBOX_TITANFALL2_PROP_LANDMARK_RADIUS", 256f, 0f, 8192f );
	// Counting-only mode: report how many instances distance culling would hide
	// without hiding anything. Use it to size the win before enabling the cull.
	public static bool PropRenderDistanceDryRun { get; set; } = GetBool( "SBOX_TITANFALL2_PROP_RENDER_DISTANCE_DRYRUN", false );
	// Extra retention band at the boundary; individual objects use hysteresis and
	// batch subsets use this expanded radius, always including each prop's own radius.
	public static float PropRenderDistanceHysteresis { get; } = GetFloat( "SBOX_TITANFALL2_PROP_RENDER_DISTANCE_HYSTERESIS", 512f, 0f, 16384f );
	// Respawn's own per-prop fade distance (sprp value, else the MDL default). A prop stops
	// drawing at the nearer of this and PropRenderDistance; props without one, and landmarks,
	// keep the global rule. Settable at runtime for A/B; the scale stretches every authored
	// distance (2 = twice as far) for players who prefer detail over frame rate.
	public static bool PropAuthoredFade { get; set; } = GetBool( "SBOX_TITANFALL2_PROP_AUTHORED_FADE", true );
	public static float PropAuthoredFadeScale { get; set; } = GetFloat( "SBOX_TITANFALL2_PROP_AUTHORED_FADE_SCALE", 1f, 0.25f, 16f );
	// rBSP stores a CellBSP tree plus portal-linked visibility cells. Keep the
	// portal walk deliberately conservative: objects without a valid cell mask
	// always remain visible, and batches remain visible when any member is.
	public static bool PropPvsCulling { get; } = GetBool( "SBOX_TITANFALL2_PROP_PVS", true );
	public static int PropPvsPortalDepth { get; set; } = GetInt( "SBOX_TITANFALL2_PROP_PVS_PORTAL_DEPTH", 16, 1, 64 );
	// These choose among Respawn's own compiled MDL/VTX LODs. The wide bands and
	// hysteresis avoid rebuilding an instanced batch while the camera sits near a
	// threshold. Set SBOX_TITANFALL2_PROP_LODS=0 to keep LOD0 everywhere.
	// Settable at runtime like PropRenderDistance: an on/off LOD A/B must not cost a relaunch.
	public static bool PropLods { get; set; } = GetBool( "SBOX_TITANFALL2_PROP_LODS", true );
	// The engine does NOT compare a model's switch distances against world distance. It compares
	// them against 50 / screenWidthInPixels (RenderMesh.LodForScreenSize), so a threshold written
	// in world units is effectively "never reached" and the model stays on LOD0 forever - which is
	// what this mount measured: 92% of prop models never drew a cheaper far LOD, PropLods changed
	// nothing, and the frame was GPU bound on triangles.
	//
	// screenWidthInPixels ~= 2 * radius * focal / distance, so a threshold that should fire at
	// world distance D is  25 * D / (radius * focal). radius is normally each model's own bounding
	// radius (computed from its LOD0 vertices), because prop sizes differ by more than an order of
	// magnitude on a BSP map. Setting this above zero overrides every model with one radius, which
	// is only useful for A/B; focal is the horizontal focal length in pixels (viewportWidth /
	// (2 * tan(fov/2)) - 960 is 1920 px at 90 degrees). The mount does not read the live viewport
	// because a model is built once per prop, not per frame.
	public static float PropLodNominalRadius { get; } = GetFloat( "SBOX_TITANFALL2_PROP_LOD_NOMINAL_RADIUS", 0f, 0f, 4096f );
	public static float PropLodFocalPixels { get; } = GetFloat( "SBOX_TITANFALL2_PROP_LOD_FOCAL_PX", 960f, 64f, 8192f );
	// Per-object submission knobs for the prop batches. The frame's main thread spends most of
	// its render time submitting objects (about 2 us each on the measured map), so these are worth
	// having: Batchable lets the engine merge submissions, and the two pass flags stop every prop
	// batch from being submitted again for the cubemap and environment-map passes, which on this
	// map run in addition to the main view (SceneViewsRendered = 4).
	public static bool PropBatchable { get; } = GetBool( "SBOX_TITANFALL2_PROP_BATCHABLE", true );
	public static bool PropIncludeInCubemap { get; } = GetBool( "SBOX_TITANFALL2_PROP_IN_CUBEMAP", false );
	public static bool PropNeedsEnvironmentMap { get; } = GetBool( "SBOX_TITANFALL2_PROP_NEEDS_ENVMAP", false );
	// Groups the world geometry the other way round: one model per material with one draw call per
	// spatial cell, drawn through an AggregateRenderer, instead of one model per cell with one draw
	// call per material inside it. Draw calls stay the same, but submissions collapse to one object
	// per material (measured on mp_angel_city: 774 renderers / 3892 draws became 334 aggregates /
	// 1091 draws, -72%, with the same picture), and each cell is still culled on its own fragment
	// bound. Default on since 2026-10-21; set the env var to 0 to fall back to per-cell renderers.
	public static bool WorldAggregate { get; } = GetBool( "SBOX_TITANFALL2_WORLD_AGGREGATE", true );
	// Which mechanism consumes those bands. Native builds one Model that already contains
	// every decoded LOD and asks the draw call for a level, so nothing is loaded and no
	// batch is rebuilt while the camera moves; legacy loads one model per LOD and rebuilds
	// an instance batch on every band change, which is what this project measured as
	// main-thread model reads during movement (see PropLodSwapsPerFrame and friends).
	// Settable at runtime like PropRenderDistance: an A/B at the same spot and view must not
	// cost a relaunch. A change is picked up by the prop streamer's next frame, which
	// rebuilds the batches that already exist; PropLods=0 still forces LOD0 in both modes.
	public static Titanfall2PropLodMode PropLodMode { get; set; } = GetLodMode( "SBOX_TITANFALL2_PROP_LOD_MODE", Titanfall2PropLodMode.Native );
	public static float PropLod1Distance { get; } = GetFloat( "SBOX_TITANFALL2_PROP_LOD1_DISTANCE", 1536f, 256f, 65536f );
	public static float PropLod2Distance { get; } = GetFloat( "SBOX_TITANFALL2_PROP_LOD2_DISTANCE", 4096f, 512f, 65536f );
	public static float PropLod3Distance { get; } = GetFloat( "SBOX_TITANFALL2_PROP_LOD3_DISTANCE", 8192f, 1024f, 131072f );
	// 256 crossed a band on almost every refresh while sprinting: the measured swap queue
	// peaked at 3552 entries against a drain rate of one per frame. 1024 exceeds the distance
	// a player covers between two cell refreshes, so a batch changes band only when it
	// settles there. The env override still accepts 0-4096, including the previous 256.
	public static float PropLodHysteresis { get; } = GetFloat( "SBOX_TITANFALL2_PROP_LOD_HYSTERESIS", 1024f, 0f, 4096f );
	// Loading a new MDL LOD and rebuilding an instance batch happens on the main
	// thread.  Spread those transitions over frames so crossing an LOD band never
	// turns into one large movement hitch.
	public static int PropLodSwapsPerFrame { get; } = GetInt( "SBOX_TITANFALL2_PROP_LOD_SWAPS_PER_FRAME", 1, 1, 8 );
	public static float PropLodSwapBudgetMilliseconds { get; } = GetFloat( "SBOX_TITANFALL2_PROP_LOD_SWAP_BUDGET_MS", 1.5f, 0.25f, 10f );
	// Swaps are produced (one per batch whose band changed) faster than one per frame can be
	// consumed, so the pending queue needs a bound. Reaching the limit SKIPS the LOD evaluation
	// pass instead of dropping an entry: dropping cleared the batch's queued flag, the next sweep
	// queued it again, and the queue churned at ~664 operations per second while the dropped
	// batch's LOD never converged. Skipping is one comparison, keeps the queue bounded, and each
	// queued batch still gets its swap at one per frame. 0 keeps the unbounded behaviour for A/B.
	public static int PropLodSwapQueueLimit { get; } = GetInt( "SBOX_TITANFALL2_PROP_LOD_SWAP_QUEUE_LIMIT", 256, 0, 65536 );
	// Position-driven render, shadow and collision state is also applied in a
	// bounded queue. Walking must not synchronously touch every BSP prop cell.
	public static int PropRuntimeCellsPerFrame { get; } = GetInt( "SBOX_TITANFALL2_PROP_RUNTIME_CELLS_PER_FRAME", 16, 1, 256 );
	public static float PropRuntimeCellBudgetMilliseconds { get; } = GetFloat( "SBOX_TITANFALL2_PROP_RUNTIME_CELL_BUDGET_MS", 1.5f, 0.25f, 10f );
	public static int PropsPerFrame { get; } = GetInt( "SBOX_TITANFALL2_PROPS_PER_FRAME", 16, 1, 256 );
	public static float PropFrameBudgetMilliseconds { get; } = GetFloat( "SBOX_TITANFALL2_PROP_FRAME_BUDGET_MS", 3f, 0.5f, 20f );
	// Scene loading waits for every static prop. Work is still split across yields
	// so the loading screen and platform event loop remain responsive.
	public static int PropPreloadPerYield { get; } = GetInt( "SBOX_TITANFALL2_PROP_PRELOAD_PER_YIELD", 512, 1, 2048 );
	public static float PropPreloadBudgetMilliseconds { get; } = GetFloat( "SBOX_TITANFALL2_PROP_PRELOAD_BUDGET_MS", 35f, 1f, 100f );
	// CPU-only texture preparation runs ahead of main-thread GPU uploads.
	public static int MaterialPrefetchWindow { get; } = GetInt( "SBOX_TITANFALL2_MATERIAL_PREFETCH", 4, 0, 8 );
	// Titanfall maps ship baked lightmap/light-probe data. Until that data is
	// decoded into the mounted world shader, prefer stable frame time over
	// resubmitting the entire imported map and its props to directional CSM.
	// Set SBOX_TITANFALL2_MAP_SHADOWS=1 to restore the previous dynamic shadows.
	public static bool MapShadows { get; } = GetBool( "SBOX_TITANFALL2_MAP_SHADOWS", false );
	// Keep world lightmaps, prop probes, local lights and atmospheric radiance
	// in one linear HDR path before the map's display transform. Set 0 to use
	// the native-lighting fallback while diagnosing an unsupported map.
	public static bool UseBspLightmaps { get; } = GetBool( "SBOX_TITANFALL2_BSP_LIGHTMAPS", true );
	// Retail display curve and exposure for the linear HDR lighting path.
	// Disable while comparing raw radiance. This is not the retail colour LUT.
	public static bool BakedTonemapping { get; } = GetBool( "SBOX_TITANFALL2_BAKED_TONEMAPPING", true );
	// Bias in stops, added to the imported display pipeline. Zero preserves
	// retail exposure calibration; the old Hable baseline used an extra 1.5 EV.
	public static float BakedExposureCompensation { get; } = GetFloat( "SBOX_TITANFALL2_EXPOSURE_EV", 0f, -5f, 5f );
	// Authored global display-RGB lookups apply to both native and baked lighting.
	// This does not select a tone curve or force the experimental lightmap path.
	public static bool BakedColorCorrection { get; } = GetBool( "SBOX_TITANFALL2_BAKED_COLOR_CORRECTION", true );
	// Native s&box screen-space contact shadows restore small-scale grounding
	// without enabling the expensive world CSM pass. Disable with 0 if a GPU
	// budget is tighter than the visual improvement.
	public static bool NativeContactShadows { get; } = GetBool( "SBOX_TITANFALL2_CONTACT_SHADOWS", true );
	// A restrained Titanfall grade on top of the native renderer. These are
	// intentionally configurable because display HDR/exposure varies by GPU.
	public static float NativeColorSaturation { get; } = GetFloat( "SBOX_TITANFALL2_COLOR_SATURATION", 1.05f, 0f, 2f );
	public static float NativeColorBrightness { get; } = GetFloat( "SBOX_TITANFALL2_COLOR_BRIGHTNESS", 0.98f, 0f, 2f );
	public static float NativeColorContrast { get; } = GetFloat( "SBOX_TITANFALL2_COLOR_CONTRAST", 1.08f, 0f, 2f );
	// Imported prop shadows used a short distance gate, visibly popping as the
	// player moved. Default to no mounted casters; native contact shading remains.
	public static bool PropShadows { get; } = GetBool( "SBOX_TITANFALL2_PROP_SHADOWS", false );
	// Static BSP props stay resident, but distant/small props do not need to be
	// submitted into every directional-light cascade. Large props receive a
	// radius allowance so buildings keep useful silhouettes beyond this range.
	// 0 means unlimited when shadows are explicitly enabled. A positive radius
	// restores the optional distance gate for machines that prefer its tradeoff.
	public static float PropShadowDistance { get; } = GetFloat( "SBOX_TITANFALL2_PROP_SHADOW_DISTANCE", 0f, 0f, 32768f );
	public static float PropShadowMinimumRadius { get; } = GetFloat( "SBOX_TITANFALL2_PROP_SHADOW_MIN_RADIUS", 48f, 0f, 2048f );
	// World render chunks remain visible at every distance; only their expensive
	// directional-light shadow submission is culled with hysteresis.
	public static float WorldShadowEnableDistance { get; } = GetFloat( "SBOX_TITANFALL2_WORLD_SHADOW_ENABLE_DISTANCE", 0f, 0f, 32768f );
	public static float WorldShadowDisableDistance { get; } = GetFloat( "SBOX_TITANFALL2_WORLD_SHADOW_DISABLE_DISTANCE", 0f, 0f, 65536f );
	// Restore authored fog and atmospheric content. These switches remain
	// available for A/B diagnosis without discarding the BSP/PCF source data.
	public static bool MapFog { get; } = GetBool( "SBOX_TITANFALL2_MAP_FOG", true );
	public static bool AtmosphericCardEffects { get; } = GetBool( "SBOX_TITANFALL2_ATMOSPHERIC_CARD_EFFECTS", true );
	public static float AtmosphereOpacityScale { get; } = GetFloat( "SBOX_TITANFALL2_ATMOSPHERE_OPACITY", 1f, 0f, 1f );
	// Map ambience from `_snd.ent` (ambient_generic point emitters + trigger_soundscape zones).
	// Retail authors looping ambience subtly; this is a global scale because display volume varies.
	// 3D skybox draw scale. Titanfall's sky_camera entities author "scale" (1 on every shipped
	// map) while "skyscale" is 1000; the engine multiplies skybox content by this value, so 1000
	// pushes the vista models far past the camera's far plane and they vanish. 0 = use the map.
	public static float SkyboxScale { get; } = GetFloat( "SBOX_TITANFALL2_SKYBOX_SCALE", 0f, 0f, 4096f );
	// Global fill-light multiplier for the map's light_environment ambient term. Baked lightmaps
	// are off by default, so shadowed surfaces rely on this ambient colour; retail is brighter
	// than the raw _ambient value once exposure/tonemapping is applied.
	// Keep authored dimensions in the isolated sky world. Once the view is pinned to sky_camera,
	// enlarging the panorama changes no viewing angle, but pushes its surface through the sky
	// view's far clip plane (20x produced the circular holes visible while turning).
	public static float SkyboxModelScale { get; } = GetFloat( "SBOX_TITANFALL2_SKYBOX_MODEL_SCALE", 1f, 1f, 1000f );
	// Retail vista pixel shaders (uberUnlitSamp2*) divide the sampled colour by
	// max(peak channel, c_maxLightingValue) and multiply by c_maxLightingValue. The
	// constant is an engine per-camera value, not stored in any asset; 1 is calibrated
	// against this mount's display chain (sp_beacon_spoke0's BC6H sky stays blue, not white).
	public static float VistaMaxLighting { get; } = GetFloat( "SBOX_TITANFALL2_VISTA_MAX_LIGHTING", 1f, 0.05f, 65504f );
	// How the imported sky sphere is drawn:
	//   0 = 2D sky only
	//   1 = engine 3D skybox world (retail vista, drawn in its authored direction; default)
	//   2 = camera-following dome geometry (experimental: the vista is a directional sector, so
	//       wrapping it around the camera shows the sector from inside and reads inverted)
	// Measured on mp_angel_city: models/vistas/angel_city_se.mdl is the south-east skyline sector
	// (4053 x 4053 x 2818, authored at the sky_camera), not a 360 sky shell. The engine 3D skybox is
	// the correct way to draw it; the 2D sky behind it covers every direction the sector does not.
	public static int SkyboxMode { get; } = GetInt( "SBOX_TITANFALL2_SKYBOX_MODE", 1, 0, 2 );

	// The engine's 3D skybox view is placed at Origin + mainPos / Scale, so the authored value
	// (skyscale 1000) puts the camera inside the sector. Auto scaling pushes the vista content to
	// this apparent distance instead, which is what makes it read as a distant skyline.
	public static float SkyboxTargetDistance { get; } = GetFloat( "SBOX_TITANFALL2_SKYBOX_TARGET_DISTANCE", 25000f, 1000f, 1000000f );
	// A 2D sky can fill directions not covered by a particular map's vista panorama.
	// Optional background for maps whose vista does not cover all directions. Keep it
	// opt-in: a generic cubemap can visibly disagree with the map's authored panorama.
	// Missing vista geometry must be fixed in its camera/bounds, not hidden with another sky.
	public static bool Sky2D { get; } = GetBool( "SBOX_TITANFALL2_2D_SKY", false );
	public static string SkyMaterial { get; } = GetString( "SBOX_TITANFALL2_SKY_MATERIAL", "mount://titanfall2/skies/skycube.vmat" );
	// Sky lit ambient (IBL) from the 2D sky; also fills the unlit side of world geometry.
	public static bool SkyIndirectLighting { get; } = GetBool( "SBOX_TITANFALL2_SKY_INDIRECT", true );
	public static float AmbientScale { get; } = GetFloat( "SBOX_TITANFALL2_AMBIENT_SCALE", 1.35f, 0.1f, 4f );
	// Godray materials are additive camera-distance-faded cards. They are the usual suspect for
	// "bright flashes while moving", so they get their own switch for A/B.
	// Off by default: in the imported maps the light shafts resolve as additive camera-distance
	// faded cards, which read as bright vertical streaks that flash while moving.
	public static bool Godrays { get; } = GetBool( "SBOX_TITANFALL2_GODRAYS", false );
	public static bool MapAudio { get; } = GetBool( "SBOX_TITANFALL2_MAP_AUDIO", true );
	public static float MapAudioVolume { get; } = GetFloat( "SBOX_TITANFALL2_AUDIO_VOLUME", 0.7f, 0.05f, 2f );
	// ambient_generic.radius is authored as 0 on every shipped map (attenuation lives in the
	// sound itself), so this is the fallback audible radius for those emitters and the radius
	// used by trigger_soundscape zones that omit triggerNearbyRadius.
	public static float MapAudioEmitterRadius { get; } = GetFloat( "SBOX_TITANFALL2_AUDIO_EMITTER_RADIUS", 1024f, 64f, 16384f );
	public static int MapAudioMaxEmitters { get; } = GetInt( "SBOX_TITANFALL2_AUDIO_MAX_EMITTERS", 12, 1, 128 );
	// Mounted models now prefer their compact VPHY hulls (and fall back to
	// skeletal hitboxes), so enabling prop colliders no longer duplicates every
	// render triangle into physics by default.
	public static bool PropCollisions { get; } = GetBool( "SBOX_TITANFALL2_PROP_COLLISIONS", true );
	// Rendering stays resident for the whole mounted map, but a ModelCollider
	// creates native physics bodies and broad-phase entries for every instance.
	// Keep only nearby prop collision active. The larger disable radius provides
	// hysteresis so crossing a cell boundary cannot repeatedly rebuild physics.
	public static bool StreamPropCollisions { get; } = GetBool( "SBOX_TITANFALL2_STREAM_PROP_COLLISIONS", true );
	public static float PropCollisionEnableDistance { get; } = GetFloat( "SBOX_TITANFALL2_PROP_COLLISION_ENABLE_DISTANCE", 1536f, 128f, 32768f );
	// A 256 unit gap between enable and disable was narrower than the noise of a sprinting
	// player, so props at the boundary were built and torn down repeatedly (measured 90-130
	// creations per second, with a comparable number of releases). 1024 units of hysteresis
	// keeps a collider alive until the player is decisively gone. Setting the disable radius
	// back to 1792 restores the previous gap for an A/B.
	public static float PropCollisionDisableDistance { get; } = GetFloat( "SBOX_TITANFALL2_PROP_COLLISION_DISABLE_DISTANCE", 2560f, 128f, 65536f );
	public static bool NavMeshStaticProps { get; } = GetBool( "SBOX_TITANFALL2_NAVMESH_STATIC_PROPS", false );
	// Models without VPHY or hitboxes may fall back to their render mesh. Keep
	// small props accurate, but replace complex render meshes with one convex
	// bounds hull so a decorative asset cannot inject tens of thousands of
	// triangles into physics and trace acceleration structures.
	public static int ModelCollisionMeshMaxTriangles { get; } = GetInt( "SBOX_TITANFALL2_MODEL_COLLISION_MAX_TRIANGLES", 8192, 64, 131072 );
	// A Source body part is a choice: exactly one of its models is active. The extractor used to
	// import every model of every body part, so models with several choices drew all variants on
	// top of each other. With no packed body override, keep model 0 of each part;
	// Mdl53BodyPart.Base is a packing multiplier, not a default index. The env
	// override restores the all-choices behaviour for A/B.
	public static bool ImportAllBodyGroupChoices { get; } = GetBool( "SBOX_TITANFALL2_ALL_BODYGROUP_CHOICES", false );
	public static float CollisionCellSize { get; } = GetFloat( "SBOX_TITANFALL2_COLLISION_CELL_SIZE", 512f, 256f, 8192f );
	// World geometry is partitioned before model creation so frustum culling can
	// reject local chunks instead of submitting material meshes spanning the map.
	public static float WorldRenderCellSize { get; } = GetFloat( "SBOX_TITANFALL2_WORLD_RENDER_CELL_SIZE", 2048f, 512f, 8192f );
	public static int WorldMaterialsPerFrame { get; } = GetInt( "SBOX_TITANFALL2_WORLD_MATERIALS_PER_FRAME", 1, 1, 16 );
	public static float WorldMaterialFrameBudgetMilliseconds { get; } = GetFloat( "SBOX_TITANFALL2_WORLD_MATERIAL_FRAME_BUDGET_MS", 2f, 0.25f, 20f );
	// The world aggregate concatenation is pure CPU work, so it runs in bounded chunks on a
	// background task while the main thread resolves materials and creates meshes. Set to 0
	// to build everything synchronously on the main thread for a controlled A/B.
	public static bool ParallelAggregateBuild { get; } = GetBool( "SBOX_TITANFALL2_PARALLEL_AGGREGATE", true );
	// PCF libraries and map particle instances are deliberately streamed in two
	// independent stages. Parsing every library while the BSP scene is built can
	// otherwise turn several hundred small effects into a long loading-screen stall.
	public static int ParticleLibrariesPerFrame { get; } = GetInt( "SBOX_TITANFALL2_PARTICLE_LIBRARIES_PER_FRAME", 8, 1, 32 );
	public static float ParticleLibraryFrameBudgetMilliseconds { get; } = GetFloat( "SBOX_TITANFALL2_PARTICLE_LIBRARY_BUDGET_MS", 2f, 0.25f, 20f );
	public static int ParticleInstancesPerFrame { get; } = GetInt( "SBOX_TITANFALL2_PARTICLE_INSTANCES_PER_FRAME", 4, 1, 64 );
	public static float ParticleInstanceFrameBudgetMilliseconds { get; } = GetFloat( "SBOX_TITANFALL2_PARTICLE_INSTANCE_BUDGET_MS", 2f, 0.25f, 20f );
	public static float ParticleActivationRadius { get; } = GetFloat( "SBOX_TITANFALL2_PARTICLE_ACTIVATION_RADIUS", 4096f, 256f, 65536f );
	public static int ParticleMaxParticlesPerSystem { get; } = GetInt( "SBOX_TITANFALL2_PARTICLE_MAX_PER_SYSTEM", 256, 1, 4096 );
	// Honours each PCF's "maximum draw distance": a map effect is suspended once the
	// camera is farther away than the largest draw distance in its system tree.
	public static bool ParticleDrawDistance { get; } = GetBool( "SBOX_TITANFALL2_PARTICLE_DRAW_DISTANCE", true );
	// Honours each PCF's "time to sleep when not drawn": an effect outside the view
	// frustum for that long is suspended until it can be seen again.
	public static bool ParticleSleepWhenNotDrawn { get; } = GetBool( "SBOX_TITANFALL2_PARTICLE_SLEEP", true );
	// A suspended effect loses its particles. On waking, looping effects are stepped
	// forward by up to this many seconds so they reappear filled instead of empty.
	public static float ParticleWakeWarmupSeconds { get; } = GetFloat( "SBOX_TITANFALL2_PARTICLE_WAKE_WARMUP", 1f, 0f, 8f );
	public static float ParticleWakeWarmupBudgetMilliseconds { get; } = GetFloat( "SBOX_TITANFALL2_PARTICLE_WAKE_WARMUP_BUDGET_MS", 1.5f, 0f, 20f );
	public static int ParticleMaxLightsPerSystem { get; } = GetInt( "SBOX_TITANFALL2_PARTICLE_MAX_LIGHTS", 4, 0, 32 );
	// Source PCFs can drive radius/velocity through scalar graphs. Until every
	// Titanfall operator is represented exactly, cap those values so one malformed
	// trail or steam system cannot cover the map with kilometre-sized cards.
	public static float ParticleMaximumRadius { get; } = GetFloat( "SBOX_TITANFALL2_PARTICLE_MAX_RADIUS", 256f, 8f, 4096f );
	// Authored cloud cards are legitimately thousands of units wide. Keep the
	// general guard for other effects without shrinking the map's cloud layers.
	public static float ParticleAtmosphereMaximumRadius { get; } = GetFloat( "SBOX_TITANFALL2_ATMOSPHERE_MAX_RADIUS", 8192f, 256f, 32768f );
	public static float ParticleMaximumEmitterExtent { get; } = GetFloat( "SBOX_TITANFALL2_PARTICLE_MAX_EMITTER_EXTENT", 2048f, 64f, 16384f );
	public static float ParticleMaximumVelocity { get; } = GetFloat( "SBOX_TITANFALL2_PARTICLE_MAX_VELOCITY", 2048f, 64f, 32768f );
	// Opt in to delayed navigation generation independently of the physics source.
	public static bool DeferredNavMesh { get; } = GetBool( "SBOX_TITANFALL2_DEFERRED_NAVMESH", false );
	// Importing original brush/tricoll physics must not require generating navigation.
	public static bool UseOriginalWorldCollision { get; } = GetBool( "SBOX_TITANFALL2_ORIGINAL_WORLD_COLLISION", true );
	public static float NavMeshStartDelaySeconds { get; } = GetFloat( "SBOX_TITANFALL2_NAVMESH_DELAY_SECONDS", 3f, 0f, 120f );
	// A host scene with navigation enabled otherwise generates every tile of the map
	// from every collider before loading finishes (about 1.5-2 minutes on angel_city and
	// timeshift). On demand skips that pass, uses only tagged world collision, and builds
	// tiles near players and NavMesh agents. Off restores the engine's whole-map pass.
	public static bool NavMeshOnDemand { get; } = GetBool( "SBOX_TITANFALL2_NAVMESH_ON_DEMAND", true );
	public static float NavMeshOnDemandRadius { get; } = GetFloat( "SBOX_TITANFALL2_NAVMESH_RADIUS", 2048f, 512f, 65536f );

	// Runtime probing. The engine performance overlay reports a hitch as one number
	// for the whole frame, so it cannot say which streaming path spent the time. Any
	// single unit of work that exceeds this threshold logs its own path and duration.
	public static float SpikeThresholdMilliseconds { get; } = GetFloat( "SBOX_TITANFALL2_SPIKE_THRESHOLD_MS", 2f, 0.1f, 1000f );
	// Mount warnings are limited by Titanfall2Log, but a per-frame spike report needs
	// its own budget: a sustained hitch would otherwise be reported hundreds of times.
	public static int SpikeLogLimit { get; } = GetInt( "SBOX_TITANFALL2_SPIKE_LOG_LIMIT", 32, 0, 4096 );
	// Collision activation builds a native physics body plus its broad-phase entries, and the
	// model load in that path is a synchronous main-thread read. Isolated measurement puts one
	// collider at 0.02-0.69 ms (see PropCollisionFrameBudgetMilliseconds), so the cost that
	// matters during movement is not one expensive collider but how many are churned per
	// second: 8 per frame allowed 90-130 creations per second at 60 fps. 2 per frame keeps the
	// same work reachable while bounding the churn; the per-frame instance cap is checked
	// before every collider, and an interrupted cell is resumed on a later frame.
	public static int PropCollisionInstancesPerFrame { get; } = GetInt( "SBOX_TITANFALL2_PROP_COLLISION_INSTANCES_PER_FRAME", 2, 1, 4096 );
	// A time budget, checked before each collider rather than after a batch of them, so one
	// slow activation cannot drag a whole frame past the budget. Isolated measurements of the
	// "most expensive" models on mp_angel_city are 0.02-0.69 ms each (an earlier spike probe
	// reported 3.6-22 ms for the same models, which turned out to be GC pauses attributed to
	// this path); 1 ms therefore fits several activations while keeping the pass bounded.
	// 0 disables this budget and falls back to the historical cell budget.
	public static float PropCollisionFrameBudgetMilliseconds { get; } = GetFloat( "SBOX_TITANFALL2_PROP_COLLISION_FRAME_BUDGET_MS", 1f, 0f, 20f );
	// The loading pass creates a collider for every prop near the spawn anchor. One collider
	// is cheap in isolation (0.02-0.69 ms measured; the historical "2-22 ms" came from GC
	// pauses being attributed to this path), but there are many of them. This caps how many a
	// single yield may start; the rest are left to the runtime cell pass, which activates them
	// as the player approaches. 0 defers all of them, trading instant spawn-area collision for
	// a shorter loading screen.
	public static int PropPreloadCollisionBodiesPerYield { get; } = GetInt( "SBOX_TITANFALL2_PROP_PRELOAD_COLLISION_BODIES", 4, 0, 4096 );
	// A model that failed to load is not cached by the resource loader, so every
	// retry pays the full read and parse again. Remember failures for this long
	// instead of re-attempting them on every cell refresh.
	public static float ModelFailureRetrySeconds { get; } = GetFloat( "SBOX_TITANFALL2_MODEL_FAILURE_RETRY_SECONDS", 30f, 0.5f, 900f );

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

	static string GetString( string name, string fallback )
		=> Environment.GetEnvironmentVariable( name ) is { Length: > 0 } value ? value : fallback;

	static bool GetBool( string name, bool fallback )
	{
		var value = Environment.GetEnvironmentVariable( name )?.Trim();
		if ( string.IsNullOrWhiteSpace( value ) ) return fallback;
		return value == "1"
			|| value.Equals( "true", StringComparison.OrdinalIgnoreCase )
			|| value.Equals( "yes", StringComparison.OrdinalIgnoreCase )
			|| value.Equals( "on", StringComparison.OrdinalIgnoreCase );
	}

	// Only the two documented words select a mode. Anything else, including a typo, keeps
	// the default: an uncertain A/B setting must not silently pick the path being measured.
	static Titanfall2PropLodMode GetLodMode( string name, Titanfall2PropLodMode fallback )
	{
		var value = Environment.GetEnvironmentVariable( name )?.Trim();
		if ( string.IsNullOrWhiteSpace( value ) ) return fallback;
		if ( value.Equals( "legacy", StringComparison.OrdinalIgnoreCase ) ) return Titanfall2PropLodMode.Legacy;
		if ( value.Equals( "native", StringComparison.OrdinalIgnoreCase ) ) return Titanfall2PropLodMode.Native;
		return fallback;
	}
}
