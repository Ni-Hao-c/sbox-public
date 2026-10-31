using Sandbox.Mounting.PerfKit;
using Titanfall2;

/// <summary>Builds a runnable scene directly from Titanfall 2 rBSP geometry.</summary>
partial class MapLoader(
	ITitanfall2AssetSource source,
	ITitanfall2AssetSource spawnEntitySource = null,
	ITitanfall2AssetSource environmentEntitySource = null,
	ITitanfall2AssetSource particleEntitySource = null,
	ITitanfall2AssetSource scriptEntitySource = null,
	ITitanfall2AssetSource soundEntitySource = null,
	bool pureWorld = false ) : SceneLoader<Titanfall2Mount>
{
	const float DecalNormalOffset = 0.25f;
	readonly ITitanfall2AssetSource _source = source;
	readonly ITitanfall2AssetSource _spawnEntitySource = spawnEntitySource;
	readonly ITitanfall2AssetSource _environmentEntitySource = environmentEntitySource;
	readonly ITitanfall2AssetSource _particleEntitySource = particleEntitySource;
	readonly ITitanfall2AssetSource _scriptEntitySource = scriptEntitySource;
	readonly ITitanfall2AssetSource _soundEntitySource = soundEntitySource;
	readonly bool _pureWorld = pureWorld;
	// SceneLoader caches a serialized SceneFile, whose generated model paths have
	// no disk loader. Keep their native resources alive for that same cache lifetime;
	// otherwise closing the last scene lets GC turn a later reopen into error models.
	WorldBuildResult _cachedWorldBuild;

	protected override void BuildScene()
	{
		// The game instance replaces Game.TypeLibrary after mount discovery. Register
		// against the current instance before custom components are added/serialized.
		Host.EnsureRuntimeTypesRegistered();
		var timer = System.Diagnostics.Stopwatch.StartNew();
		// Physics import is independent of navigation generation. Keep the stable
		// render-geometry fallback unless authoritative collision is requested.
		var server = Application.IsDedicatedServer;
		var parseOriginalWorldCollision = server || Titanfall2StreamingSettings.UseOriginalWorldCollision;
		var cacheable = Titanfall2StreamingSettings.ProcessedCache && !parseOriginalWorldCollision && !Titanfall2StreamingSettings.UseBspLightmaps;
		var cacheIdentity = cacheable ? Titanfall2MapCache.Identity( Host, _source ) : null;
		Titanfall2BspReader.ParsedBsp parsed = null;
		var cached = cacheable && Titanfall2MapCache.TryRead( cacheIdentity, out parsed );
		if ( !cached )
		{
			if ( !_source.TryReadAllBytes( out var bytes, out var readError ) )
			{
				Log.Warning( $"Failed to read Titanfall 2 BSP '{Path}': {readError}" );
				return;
			}
			var collisionLumps = parseOriginalWorldCollision ? ReadCollisionLumpOverrides() : new Dictionary<int, byte[]>();
			if ( !Titanfall2BspReader.TryRead( bytes, System.IO.Path.GetFileNameWithoutExtension( _source.Description ), collisionLumps,
				parseWorldCollision: parseOriginalWorldCollision, parseLightmaps: !server && Titanfall2StreamingSettings.UseBspLightmaps,
				parseStaticPropLightProbes: !server && Titanfall2StreamingSettings.UseBspLightmaps, out parsed, out var parseError, allCollisionContents: true ) )
			{
				Log.Warning( $"Failed to parse Titanfall 2 BSP '{Path}': {parseError}" );
				return;
			}
			if ( cacheable ) Titanfall2MapCache.Write( cacheIdentity, parsed );
		}
		Log.Info( $"Titanfall 2 map processed cache: {(cached ? "hit" : cacheable ? "miss/rebuilt" : "disabled for this import mode")}; "
			+ $"BSP read/parse/cache {timer.Elapsed.TotalSeconds:0.00}s ({Path})." );
		var collisionSummary = parsed.WorldCollision.SourcePrimitiveCount > 0
			? $"{parsed.WorldCollision.PrimitiveCount}/{parsed.WorldCollision.SourcePrimitiveCount} player-solid primitives, "
				+ $"{parsed.WorldCollision.WaterPrimitiveCount} water "
				+ $"({parsed.WorldCollision.BlockingWaterPrimitiveCount} player-blocking), "
				+ $"{parsed.WorldCollision.TriangleCount} collision triangles, {parsed.WorldCollision.RejectedPrimitiveCount} rejected primitives"
			: "original collision lumps unavailable";
		var visibilitySummary = parsed.Visibility.IsValid
			? $"PVS {parsed.Visibility.CellCount} cells/{parsed.Visibility.Portals.Count} portals"
			: "PVS unavailable";
		Log.Info( $"Titanfall 2 map parsed: {Path} ({parsed.World.Meshes.Count} world meshes, {collisionSummary}, "
			+ $"{parsed.StaticProps.Count} static props, {visibilitySummary}, "
			+ $"lightmaps {(parsed.Lightmaps.IsValid ? $"{parsed.Lightmaps.Pages.Count} pages" : "not parsed")}, "
			+ $"{timer.Elapsed.TotalSeconds:0.00}s)" );

		LogBspEntities( parsed );
		var hasOriginalWorldCollision = parseOriginalWorldCollision
			&& parsed.WorldCollision is { IsEmpty: false, RejectedPrimitiveCount: 0 };
		if ( server && !hasOriginalWorldCollision ) throw new InvalidDataException( $"Dedicated server requires complete rBSP CM collision: {Path}" );
		_cachedWorldBuild = server ? new WorldBuildResult( [], [], [], [], [], [], null, [] )
			: BuildWorldModel( parsed, createFallbackCollision: !hasOriginalWorldCollision );
		var worldBuild = _cachedWorldBuild;
		if ( worldBuild is null )
		{
			Log.Warning( $"Titanfall 2 BSP contains no usable world geometry: {Path}" );
			return;
		}

		var world = new GameObject( true, "worldspawn" );
		world.IsStatic = true;
		if ( !server ) world.AddComponent<Titanfall2MaterialAnimator>();
		// Lightmap textures belong to the cached generated scene, not an individual
		// editor/play clone. The retained build owns them until the mount is released.
		world.AddComponent<Titanfall2SceneResourceOwner>().Configure( Host.Ident );
		foreach ( var model in worldBuild.WorldChunks.Concat( worldBuild.TranslucentChunks )
			.Concat( worldBuild.DecalChunks ).Concat( worldBuild.GodrayChunks ).Concat( worldBuild.WaterChunks ).Select( static chunk => chunk.Model ) )
		{
			// Retain the managed material wrappers too: VMT proxy registrations are
			// weak, and a cached generated model does not rerun its material loader.
			_ = model.Materials;
		}
		foreach ( var chunk in worldBuild.WorldChunks )
		{
			var chunkObject = new GameObject( world, true, $"world_{chunk.Cell}" );
			chunkObject.IsStatic = true;
			var renderer = chunkObject.AddComponent<ModelRenderer>();
			renderer.Model = chunk.Model;
			renderer.RenderType = Titanfall2StreamingSettings.MapShadows
				? ModelRenderer.ShadowRenderType.On
				: ModelRenderer.ShadowRenderType.Off;
		}
		if ( Titanfall2StreamingSettings.MapShadows && Titanfall2StreamingSettings.WorldShadowEnableDistance > 0f )
			world.AddComponent<Titanfall2WorldShadowController>();
		var aggregateIndex = 0;
		foreach ( var chunk in worldBuild.TranslucentChunks )
		{
			var child = new GameObject( world, true, $"titanfall2_world_translucent_{chunk.Cell}" );
			child.IsStatic = true;
			var renderer = child.AddComponent<ModelRenderer>();
			renderer.Model = chunk.Model;
			renderer.RenderType = ModelRenderer.ShadowRenderType.Off;
		}
		foreach ( var aggregate in worldBuild.WorldAggregates )
		{
			// 接线细节（子对象 / IsStatic / AggregateRenderer）复用 PerfKit，避免每个挂载项目
			// 再写一份同样的挂接代码。
			MountedAggregateBuilder.Attach( world, $"world_aggregate_{aggregateIndex}", aggregate, Titanfall2StreamingSettings.MapShadows );
			aggregateIndex++;
		}
		foreach ( var chunk in worldBuild.DecalChunks )
		{
			var decals = new GameObject( world, true, $"decals_{chunk.Cell}" );
			decals.IsStatic = true;
			var decalRenderer = decals.AddComponent<ModelRenderer>();
			decalRenderer.Model = chunk.Model;
			decalRenderer.RenderType = ModelRenderer.ShadowRenderType.Off;
		}
		foreach ( var chunk in worldBuild.GodrayChunks )
		{
			var godrays = new GameObject( world, true, $"godrays_{chunk.Cell}" );
			godrays.IsStatic = true;
			var godrayRenderer = godrays.AddComponent<ModelRenderer>();
			godrayRenderer.Model = chunk.Model;
			godrayRenderer.RenderType = ModelRenderer.ShadowRenderType.Off;
		}
		// Light blockers: shadow casters the camera never draws. Not named world_*, so the
		// distance shadow gate cannot switch them to a visible ShadowRenderType.On.
		foreach ( var chunk in worldBuild.ShadowOnlyChunks ?? [] )
		{
			var blocker = new GameObject( world, true, $"titanfall2_light_blocker_{chunk.Cell}" );
			blocker.IsStatic = true;
			var blockerRenderer = blocker.AddComponent<ModelRenderer>();
			blockerRenderer.Model = chunk.Model;
			blockerRenderer.RenderType = ModelRenderer.ShadowRenderType.ShadowsOnly;
		}
		foreach ( var chunk in worldBuild.WaterChunks )
		{
			var water = new GameObject( world, true, $"water_{chunk.Cell}" );
			water.IsStatic = true;
			var renderer = water.AddComponent<Titanfall2WaterRenderer>();
			renderer.Model = chunk.Model;
			renderer.RenderType = ModelRenderer.ShadowRenderType.Off;
		}
		ModelCollider worldCollision = null;
		if ( hasOriginalWorldCollision )
		{
			var classified = CreateClassifiedWorldCollision( world, parsed );
			// hasOriginalWorldCollision already proved the geometry decoded and nothing was
			// rejected. An empty result therefore means every primitive has empty contents:
			// the map would load with no solid world at all, so fail loudly instead.
			if ( classified.Length == 0 )
				throw new InvalidDataException( $"Authoritative rBSP collision contains no solid contents groups: {Path}" );
			// WorldReady verifies one body; prefer a solid collider over a trigger-only group.
			worldCollision = classified.FirstOrDefault( static collider => !collider.IsTrigger ) ?? classified[0];
			world.AddComponent<Titanfall2CollisionManifest>().Colliders = classified;
		}
		else if ( worldBuild.CollisionModel is not null )
		{
			world.Tags.Add( Titanfall2DeferredNavMeshBuilder.NavMeshBodyTag );
			var fallbackWorldCollider = world.AddComponent<ModelCollider>();
			fallbackWorldCollider.Model = worldBuild.CollisionModel;
			fallbackWorldCollider.Static = true;
			worldCollision = fallbackWorldCollider;
			var source = world.AddComponent<Titanfall2CollisionSource>();
			source.MountIdent = Host.Ident;
			source.MapPath = Path;
			source.GeometrySource = "filtered render-geometry fallback";
			Log.Warning( $"Titanfall 2 BSP is using render-geometry collision because authoritative "
				+ $"rBSP collision was unavailable or incomplete: {Path}." );
		}

		// Pure-world scenes are loaded into host scenes (MapInstance) that may enable
		// navigation themselves, so they need the on-demand takeover as well. Forcing
		// navigation on (DeferredNavMesh) stays limited to full mounted scenes.
		world.AddComponent<Titanfall2DeferredNavMeshBuilder>()
			.Configure( !_pureWorld && Titanfall2StreamingSettings.DeferredNavMesh, Titanfall2StreamingSettings.NavMeshOnDemand,
				Titanfall2StreamingSettings.NavMeshOnDemandRadius, Titanfall2StreamingSettings.NavMeshStartDelaySeconds,
				new BBox( ToSandbox( parsed.World.Mins ), ToSandbox( parsed.World.Maxs ) ) );
		if ( !server ) CreateEnvironment( parsed );
		if ( !server && Titanfall2StreamingSettings.UseBspLightmaps )
			world.AddComponent<Titanfall2LocalLightController>().Configure( parsed.WorldLights, parsed.Lightmaps.RealTimeLightPages );
		var cubemapProbeCount = server ? 0 : CreateCubemapProbes( parsed );
		var skyboxModelCount = server ? 0 : CreateSkybox( parsed );
		Log.Info( $"Titanfall 2 map world created: {Path} ({timer.Elapsed.TotalSeconds:0.00}s)" );

		var spawnCount = 0;
		var streamingAnchor = (ToSandbox( parsed.World.Mins ) + ToSandbox( parsed.World.Maxs )) * 0.5f;
		// Mounted scenes are read only: a camera added in the editor is dropped when Play reloads
		// the scene from the mount. Keep a fallback camera as part of the scene instead.
		if ( !_pureWorld )
		{
			spawnCount = CreateSpawnPoints( parsed, out streamingAnchor );
			if ( !server )
			{
				var mapCameraObject = new GameObject( true, "titanfall2_map_camera" );
				mapCameraObject.AddComponent<Titanfall2MapCamera>();
			}
		}
		var particleCount = server ? 0 : CreateParticleSystems( parsed, streamingAnchor );
		var audioCount = server ? 0 : CreateMapAudio( parsed, streamingAnchor );
		var dynamicPropCount = _pureWorld ? 0 : CreateDynamicProps( world );
		var worldProps = parsed.StaticProps.Where( static prop => !IsVistaModelPath( prop.ModelPath ) ).ToArray();
		var streamerObject = new GameObject( true, "titanfall2_static_prop_streamer" );
		var propStreamer = streamerObject.AddComponent<Titanfall2StaticPropStreamer>();
		propStreamer.KeepAllCollisions = _pureWorld || server;
		propStreamer.Configure( Host.Ident, Path, streamingAnchor, worldProps, parsed.Visibility, _aggregatedPropRadii, ReadBakedSkyColor( parsed ),
				Titanfall2StreamingSettings.UseBspLightmaps ? parsed.WorldLights : null,
				server ? null : ResolvePropFadeDistances( worldProps ) );
		if ( _pureWorld || server )
		{
			var ready = world.AddComponent<Titanfall2WorldReady>();
			ready.Props = propStreamer;
			ready.WorldCollision = worldCollision;
		}
		Log.Info( $"Titanfall 2 map loaded: {Path} ({parsed.World.Meshes.Count} world meshes, "
			+ $"{worldProps.Length} world props registered for loading-stage preload, "
			+ $"{skyboxModelCount} vista models moved to the 3D skybox, {cubemapProbeCount} environment probes, "
			+ $"{dynamicPropCount} dynamic props, {particleCount} particle systems queued, "
			+ $"{audioCount} audio sources)" );
		Log.Info( $"Titanfall 2 map spawn points created: {Path} ({spawnCount})" );
	}

	static Vector3 ToSandbox( System.Numerics.Vector3 value ) => new( value.X, value.Y, value.Z );
	static Vector2 ToSandbox( System.Numerics.Vector2 value ) => new( value.X, value.Y );
}
