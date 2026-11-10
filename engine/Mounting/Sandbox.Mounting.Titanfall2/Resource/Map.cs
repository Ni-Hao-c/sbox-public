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
	bool pureWorld = false ) : SceneLoader<Titanfall2Mount>, ITitanfall2SceneResource
{
	const float DecalNormalOffset = 0.25f;
	// Seconds after load before the deferred navmesh build starts.
	const float NavMeshStartDelaySeconds = 3f;
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

	/// <summary>
	/// Drops the cached scene together with the world models it names; one without the
	/// other would reopen as error models. Called only when no live scene uses this map.
	/// </summary>
	public bool ReleaseSceneResource()
	{
		// A load may still be producing the scene. The owner retries later instead of
		// tearing down something that is being built.
		if ( IsLoading ) return false;
		var releasedCache = ReleaseCachedResult();
		// Destroying the cached SceneFile is what makes a later reopen rebuild the map:
		// SceneFile.Load checks the resource library first, so otherwise it would return
		// the stale serialized scene whose generated models and lightmaps we are about to
		// drop (see the field comment above).
		var releasedScene = ReleaseCachedScene();
		var releasedWorldBuild = _cachedWorldBuild is not null;
		if ( !releasedCache && !releasedScene && !releasedWorldBuild ) return false;
		DiscardWorldBuild();
		return true;
	}

	/// <summary>True while a load is in flight, so a release has to wait instead of tearing it down.</summary>
	public bool IsReleaseDeferred => IsLoading;

	/// <summary>
	/// Drops this loader's own build state. Lightmap pages and probe atlases are this build's
	/// textures and nothing else holds them, so they are disposed here.
	/// </summary>
	void DiscardWorldBuild()
	{
		_cachedWorldBuild?.Dispose();
		_cachedWorldBuild = null;
		_classifiedWorldModels.Clear();
		_materialPrefetch = null;
		_environmentEntities = null;
		_externalEnvironmentEntities = null;
	}

	protected override void BuildScene()
	{
		try
		{
			BuildSceneCore();
		}
		catch
		{
			// The scene is thrown away, so nothing will ever reference - or release - the
			// textures and models this partial build created. Roll back to an empty state
			// so the memory is returned and a later attempt starts from scratch.
			DiscardWorldBuild();
			throw;
		}
	}

	void BuildSceneCore()
	{
		// The game instance replaces Game.TypeLibrary after mount discovery. Register
		// against the current instance before custom components are added/serialized.
		Host.EnsureRuntimeTypesRegistered();
		Host.BeginMapBuild( Game.ActiveScene );
		Host.ReleaseSceneResourcesBeforeBuild( this );
		var timer = System.Diagnostics.Stopwatch.StartNew();
		// Authoritative rBSP collision is always parsed. The render-geometry fallback is
		// only built when that collision is missing or incomplete.
		var server = Application.IsDedicatedServer;
		var lightmaps = !server && Titanfall2StreamingSettings.UseBspLightmaps;
		// The parse result is derived data keyed on the mount's content fingerprint and this
		// source, so a later build of the same map can skip the read, the decompress and the
		// decode. The read starts here so it runs on a worker while this thread does the
		// bookkeeping below; a missing entry costs one file probe.
		var parseIdentity = Titanfall2MapCache.ParseIdentity( Host, _source, lightmaps );
		var cachedParse = Titanfall2MapCache.BeginRead( parseIdentity );
		// The full and the pure-world scene of one map parse identical data, so they share a
		// single reference counted parse; the last of them to finish building frees it.
		using var import = Host.BeginMapImport( $"{_source.CacheIdentity}|lightmaps:{lightmaps}", () => ReadBsp( cachedParse ) );
		if ( import.Error is not null )
		{
			Log.Warning( $"Failed to read Titanfall 2 BSP '{Path}': {import.Error}" );
			return;
		}
		var parsed = import.Parsed;

		(Titanfall2BspReader.ParsedBsp, string) ReadBsp( Task<Titanfall2MapCache.MapParseCacheRead> pendingParse )
		{
			// A cached parse is plain managed data, so it takes part in the shared import and its
			// lease counting (MapImport.cs) exactly like a freshly decoded one. Anything the cache
			// cannot vouch for is reported as a miss and the map is decoded normally.
			if ( Titanfall2MapCache.TryTake( pendingParse, out var cached ) )
			{
				Log.Info( $"Titanfall 2 map parse cache: hit, {cached.FileBytes / (1024.0 * 1024.0):0.0} MB payload "
					+ $"decoded in {cached.Seconds:0.00}s ({Path})." );
				return (cached.Parsed, null);
			}

			Log.Info( $"Titanfall 2 map parse cache: miss ({cached.MissReason}) ({Path})." );
			if ( !_source.TryReadAllBytes( out var bytes, out var readError ) ) return (null, readError);
			if ( !Titanfall2BspReader.TryRead( bytes, System.IO.Path.GetFileNameWithoutExtension( _source.Description ), ReadCollisionLumpOverrides(),
				parseWorldCollision: true, parseLightmaps: lightmaps,
				parseStaticPropLightProbes: lightmaps, out var result, out var parseError, allCollisionContents: true,
				environmentEntities: ReadExternalEnvironmentEntities() ) )
				return (null, $"parse failed: {parseError}");
			// Written by a worker from the parse this build already owns: a failed or skipped write
			// never changes what this load uses.
			Titanfall2MapCache.BeginWrite( parseIdentity, result, _source, Path, Log );
			return (result, null);
		}
		Log.Info( $"Titanfall 2 BSP read/parse {timer.Elapsed.TotalSeconds:0.00}s ({Path})." );
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
		var hasOriginalWorldCollision = parsed.WorldCollision is { IsEmpty: false, RejectedPrimitiveCount: 0 };
		if ( server && !hasOriginalWorldCollision ) throw new InvalidDataException( $"Dedicated server requires complete rBSP CM collision: {Path}" );
		_cachedWorldBuild = server ? new WorldBuildResult( [], [], [], [], [], null, [] )
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
		// editor/play clone. The retained build owns them until the map's resources are
		// released (no live scene shows the map) or the mount is.
		world.AddComponent<Titanfall2SceneResourceOwner>().Configure( Host.Ident );
		foreach ( var model in worldBuild.TranslucentChunks
			.Concat( worldBuild.DecalChunks ).Concat( worldBuild.GodrayChunks ).Concat( worldBuild.WaterChunks ).Select( static chunk => chunk.Model ) )
		{
			// Retain the managed material wrappers too: VMT proxy registrations are
			// weak, and a cached generated model does not rerun its material loader.
			_ = model.Materials;
		}
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
			// PerfKit owns the wiring (child object, IsStatic, AggregateRenderer) so each mount
			// does not repeat it.
			MountedAggregateBuilder.Attach( world, $"world_aggregate_{aggregateIndex}", aggregate,
				worldBuild.WorldAggregateCastShadows is { } casters && aggregateIndex < casters.Count
					? casters[aggregateIndex]
					: Titanfall2StreamingSettings.MapShadows );
			aggregateIndex++;
		}
		if ( worldBuild.WorldMaterialSources is { Length: > 0 } )
		{
			var controller = world.AddComponent<Titanfall2WorldMaterialTableController>();
			controller.SourceKey = worldBuild.WorldMaterialSourceKey;
		}
		// Depth-only world shadow casters: same triangles as the forward aggregates, but drawn from
		// one mesh per spatial cell with a single shared material and never in the camera views.
		foreach ( var chunk in worldBuild.WorldShadowChunks ?? [] )
		{
			var shadow = new GameObject( world, true, $"titanfall2_world_shadow_{chunk.Cell}" );
			shadow.IsStatic = true;
			var shadowRenderer = shadow.AddComponent<ModelRenderer>();
			shadowRenderer.Model = chunk.Model;
			shadowRenderer.RenderType = ModelRenderer.ShadowRenderType.ShadowsOnly;
		}
		if ( !server && Titanfall2StreamingSettings.MapShadows )
		{
			var shadowChunkCount = worldBuild.WorldShadowChunks?.Count ?? 0;
			var directCasterCount = worldBuild.WorldAggregateCastShadows is { } casterFlags
				? casterFlags.Count( static cast => cast )
				: worldBuild.WorldAggregates.Count;
			Log.Info( shadowChunkCount > 0
				? $"Titanfall 2 world shadow source: shadow-only geometry ({shadowChunkCount} chunks, {directCasterCount} alpha-tested aggregates still casting) ({Path})."
				: $"Titanfall 2 world shadow source: {directCasterCount} world aggregate models cast directly ({Path})." );
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
		// Light blockers: shadow casters the camera never draws.
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
				Titanfall2StreamingSettings.NavMeshOnDemandRadius, NavMeshStartDelaySeconds,
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
		var dynamicPropCount = _pureWorld ? 0 : CreateDynamicProps( world, parsed );
		var worldProps = parsed.StaticProps.Where( static prop => !IsVistaModelPath( prop.ModelPath ) ).ToArray();
		var streamerObject = new GameObject( true, "titanfall2_static_prop_streamer" );
		var propStreamer = streamerObject.AddComponent<Titanfall2StaticPropStreamer>();
		propStreamer.KeepAllCollisions = _pureWorld || server || Titanfall2StreamingSettings.ResidentWorld;
		propStreamer.Configure( Host.Ident, Path, streamingAnchor, worldProps, parsed.Visibility, ReadBakedSkyColor( parsed ),
				Titanfall2StreamingSettings.UseBspLightmaps ? parsed.WorldLights : null,
				server ? null : ResolvePropFadeDistances( worldProps ) );
		if ( propStreamer.KeepAllCollisions )
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
