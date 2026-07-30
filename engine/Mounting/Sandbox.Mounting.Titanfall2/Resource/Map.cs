using System.Runtime.InteropServices;
using Titanfall2;

/// <summary>Builds a runnable scene directly from Titanfall 2 rBSP geometry.</summary>
class MapLoader(
	ITitanfall2AssetSource source,
	ITitanfall2AssetSource spawnEntitySource = null,
	ITitanfall2AssetSource environmentEntitySource = null,
	ITitanfall2AssetSource particleEntitySource = null,
	ITitanfall2AssetSource scriptEntitySource = null ) : SceneLoader<Titanfall2Mount>
{
	const float DecalNormalOffset = 0.25f;
	readonly ITitanfall2AssetSource _source = source;
	readonly ITitanfall2AssetSource _spawnEntitySource = spawnEntitySource;
	readonly ITitanfall2AssetSource _environmentEntitySource = environmentEntitySource;
	readonly ITitanfall2AssetSource _particleEntitySource = particleEntitySource;
	readonly ITitanfall2AssetSource _scriptEntitySource = scriptEntitySource;

	protected override void BuildScene()
	{
		// The game instance replaces Game.TypeLibrary after mount discovery. Register
		// against the current instance before custom components are added/serialized.
		Host.EnsureRuntimeTypesRegistered();
		var timer = System.Diagnostics.Stopwatch.StartNew();
		// Imported BSP geometry can contain millions of collision triangles. Initialize
		// navigation during scene loading, but defer tile generation until the mounted
		// map is running so it cannot hold the loading screen open for minutes.
		if ( Game.ActiveScene is { } scene )
		{
			scene.NavMesh.IsEnabled = Titanfall2StreamingSettings.DeferredNavMesh;
			// Keep automatic generation deferred in both modes. A runtime controller
			// either leaves navigation disabled or explicitly queues generation later.
			scene.NavMesh.DeferGeneration = true;
		}
		if ( !_source.TryReadAllBytes( out var bytes, out var readError ) )
		{
			Log.Warning( $"Failed to read Titanfall 2 BSP '{Path}': {readError}" );
			return;
		}

		// The imported brush/tricoll path is retained for future NavMesh work, but
		// it is not yet reliable enough to replace gameplay collision on every R2
		// map. Keep the previously stable filtered render-geometry collision while
		// navigation is disabled.
		var parseOriginalWorldCollision = Titanfall2StreamingSettings.DeferredNavMesh;
		var collisionLumps = parseOriginalWorldCollision
			? ReadCollisionLumpOverrides()
			: new Dictionary<int, byte[]>();
		if ( !Titanfall2BspReader.TryRead( bytes, System.IO.Path.GetFileNameWithoutExtension( _source.Description ), collisionLumps,
			parseWorldCollision: parseOriginalWorldCollision, out var parsed, out var parseError ) )
		{
			Log.Warning( $"Failed to parse Titanfall 2 BSP '{Path}': {parseError}" );
			return;
		}
		var collisionSummary = parsed.WorldCollision.SourcePrimitiveCount > 0
			? $"{parsed.WorldCollision.PrimitiveCount}/{parsed.WorldCollision.SourcePrimitiveCount} player-solid primitives, "
				+ $"{parsed.WorldCollision.WaterPrimitiveCount} water "
				+ $"({parsed.WorldCollision.BlockingWaterPrimitiveCount} player-blocking), "
				+ $"{parsed.WorldCollision.TriangleCount} collision triangles"
			: "original collision lumps unavailable";
		Log.Info( $"Titanfall 2 map parsed: {Path} ({parsed.World.Meshes.Count} world meshes, {collisionSummary}, "
			+ $"{parsed.StaticProps.Count} static props, {timer.Elapsed.TotalSeconds:0.00}s)" );

		var hasOriginalWorldCollision = parseOriginalWorldCollision
			&& parsed.WorldCollision is { IsEmpty: false };
		var worldBuild = BuildWorldModel( parsed, createFallbackCollision: !hasOriginalWorldCollision );
		if ( worldBuild is null )
		{
			Log.Warning( $"Titanfall 2 BSP contains no usable world geometry: {Path}" );
			return;
		}

		var world = new GameObject( true, "worldspawn" );
		world.IsStatic = true;
		world.AddComponent<Titanfall2MaterialAnimator>();
		world.AddComponent<Titanfall2SceneResourceOwner>().Configure( Host.Ident, worldBuild.OwnedResources );
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
		if ( Titanfall2StreamingSettings.MapShadows )
			world.AddComponent<Titanfall2WorldShadowController>();
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
		var originalCollisionModel = hasOriginalWorldCollision
			? BuildWorldCollisionModel( parsed )
			: null;
		if ( originalCollisionModel is not null )
		{
			var collisionObject = new GameObject( world, true, "titanfall2_world_collision" );
			collisionObject.IsStatic = true;
			if ( Titanfall2StreamingSettings.DeferredNavMesh )
				collisionObject.Tags.Add( Titanfall2DeferredNavMeshBuilder.NavMeshBodyTag );
			var worldCollider = collisionObject.AddComponent<ModelCollider>();
			worldCollider.Model = originalCollisionModel;
			worldCollider.Static = true;
		}
		else if ( worldBuild.CollisionModel is not null )
		{
			var fallbackWorldCollider = world.AddComponent<ModelCollider>();
			fallbackWorldCollider.Model = worldBuild.CollisionModel;
			fallbackWorldCollider.Static = true;
			Log.Warning( $"Titanfall 2 BSP is using render-geometry collision because authoritative "
				+ $"rBSP collision was unavailable: {Path}." );
		}

		if ( Titanfall2StreamingSettings.DeferredNavMesh && originalCollisionModel is null )
		{
			Log.Warning( $"Titanfall 2 BSP has no usable brush/tricoll world collision: {Path}. "
				+ "Render geometry will not be submitted to physics or NavMesh as a fallback." );
		}
		world.AddComponent<Titanfall2DeferredNavMeshBuilder>()
			.Configure( Titanfall2StreamingSettings.DeferredNavMesh, Titanfall2StreamingSettings.NavMeshStartDelaySeconds );
		CreateEnvironment( parsed );
		var cubemapProbeCount = CreateCubemapProbes( parsed );
		var skyboxModelCount = CreateSkybox( parsed.StaticProps );
		Log.Info( $"Titanfall 2 map world created: {Path} ({timer.Elapsed.TotalSeconds:0.00}s)" );

		var spawnCount = CreateSpawnPoints( parsed, out var streamingAnchor );
		var particleCount = CreateParticleSystems( streamingAnchor );
		var dynamicPropCount = CreateDynamicProps( world );
		var worldProps = parsed.StaticProps.Where( static prop => !IsVistaModelPath( prop.ModelPath ) ).ToArray();
		var streamerObject = new GameObject( true, "titanfall2_static_prop_streamer" );
		streamerObject.AddComponent<Titanfall2StaticPropStreamer>()
			.Configure( Host.Ident, Path, streamingAnchor, worldProps );
		Log.Info( $"Titanfall 2 map loaded: {Path} ({parsed.World.Meshes.Count} world meshes, "
			+ $"{worldProps.Length} world props registered for loading-stage preload, "
			+ $"{skyboxModelCount} vista models moved to the 3D skybox, {cubemapProbeCount} environment probes, "
			+ $"{dynamicPropCount} dynamic props, {particleCount} particle systems queued)" );
		Log.Info( $"Titanfall 2 map spawn points created: {Path} ({spawnCount})" );
	}

	WorldBuildResult BuildWorldModel( Titanfall2BspReader.ParsedBsp parsed, bool createFallbackCollision )
	{
		var vertexCount = parsed.World.Meshes.Sum( static mesh => mesh?.Vertices.Length ?? 0 );
		var indexCount = parsed.World.Meshes.Sum( static mesh => mesh?.Indices.Length ?? 0 );
		if ( vertexCount == 0 || indexCount == 0 ) return null;

		var groups = new Dictionary<WorldRenderCell, Dictionary<WorldMaterialKey, MapMeshGroup>>();
		var decalGroups = new Dictionary<WorldRenderCell, Dictionary<WorldMaterialKey, MapMeshGroup>>();
		var godrayGroups = new Dictionary<WorldRenderCell, Dictionary<WorldMaterialKey, MapMeshGroup>>();
		var lightmaps = CreateLightmapTextures( parsed.Lightmaps );
		var worldMaterials = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
		var decalMaterials = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
		var godrayMaterials = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
		var cutoutMaterials = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
		var translucentMaterials = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
		var waterMaterials = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
		var collisionPositions = new List<Vector3>( vertexCount );
		var collisionIndices = new List<int>( indexCount );
		var skippedToolMeshes = 0;
		var decalMeshCount = 0;
		var godrayMeshCount = 0;
		var visualEffectCollisionMeshCount = 0;
		foreach ( var sourceMesh in parsed.World.Meshes )
		{
			if ( sourceMesh is null || sourceMesh.Vertices.Length == 0 || sourceMesh.Indices.Length < 3 ) continue;
			var materialName = NormalizeMaterialName( sourceMesh.MaterialName );
			if ( ShouldSkipWorldMaterial( materialName ) )
			{
				skippedToolMeshes++;
				continue;
			}
			var isGodray = MaterialLoader.IsGodrayMaterial( materialName );
			var materialMetadata = GetMaterialMetadata( materialName );
			var isDecal = !isGodray && materialMetadata.IsDecal;
			var allowsFallbackCollision = Titanfall2CollisionFilter.ShouldIncludeMaterial( materialName, materialMetadata );
			var includeCollision = createFallbackCollision && allowsFallbackCollision;
			if ( !isDecal && materialMetadata.Mode == Titanfall2MaterialMode.Cutout ) cutoutMaterials.Add( materialName );
			if ( !isDecal && materialMetadata.IsTranslucent ) translucentMaterials.Add( materialName );
			if ( !isDecal && materialMetadata.IsWater ) waterMaterials.Add( materialName );
			var targetGroups = isGodray ? godrayGroups : isDecal ? decalGroups : groups;
			var targetMaterials = isGodray ? godrayMaterials : isDecal ? decalMaterials : worldMaterials;
			targetMaterials.Add( materialName );
			if ( isGodray ) godrayMeshCount++;
			if ( isDecal ) decalMeshCount++;

			for ( var index = 0; index + 2 < sourceMesh.Indices.Length; index += 3 )
			{
				var sourceIndex0 = sourceMesh.Indices[index];
				var sourceIndex1 = sourceMesh.Indices[index + 1];
				var sourceIndex2 = sourceMesh.Indices[index + 2];
				if ( (uint)sourceIndex0 >= sourceMesh.Vertices.Length
					|| (uint)sourceIndex1 >= sourceMesh.Vertices.Length
					|| (uint)sourceIndex2 >= sourceMesh.Vertices.Length ) continue;

				var vertex0 = CreateWorldVertex( sourceMesh.Vertices[sourceIndex0], isDecal );
				var vertex1 = CreateWorldVertex( sourceMesh.Vertices[sourceIndex1], isDecal );
				var vertex2 = CreateWorldVertex( sourceMesh.Vertices[sourceIndex2], isDecal );
				var cell = WorldRenderCell.FromTriangle(
					vertex0.Position, vertex1.Position, vertex2.Position,
					Titanfall2StreamingSettings.WorldRenderCellSize );
				var lightmapPage = !isGodray && !isDecal ? sourceMesh.LightmapPage : -1;
				var group = GetRenderGroup( targetGroups, cell, new WorldMaterialKey( materialName, lightmapPage ) );
				var baseVertex = group.Vertices.Count;
				group.Vertices.Add( vertex0 );
				group.Vertices.Add( vertex1 );
				group.Vertices.Add( vertex2 );
				group.Indices.Add( baseVertex );
				group.Indices.Add( baseVertex + 2 );
				group.Indices.Add( baseVertex + 1 );
				group.Bounds = group.Bounds.AddPoint( vertex0.Position );
				group.Bounds = group.Bounds.AddPoint( vertex1.Position );
				group.Bounds = group.Bounds.AddPoint( vertex2.Position );
				if ( includeCollision )
				{
					var collisionBase = collisionPositions.Count;
					collisionPositions.Add( vertex0.Position );
					collisionPositions.Add( vertex1.Position );
					collisionPositions.Add( vertex2.Position );
					collisionIndices.Add( collisionBase );
					collisionIndices.Add( collisionBase + 2 );
					collisionIndices.Add( collisionBase + 1 );
				}
			}
			if ( createFallbackCollision && !allowsFallbackCollision && !isGodray && !isDecal )
				visualEffectCollisionMeshCount++;
		}

		if ( groups.Count == 0 ) return null;
		var worldGroupCount = groups.Values.Sum( static cell => cell.Count );
		var decalGroupCount = decalGroups.Values.Sum( static cell => cell.Count );
		var godrayGroupCount = godrayGroups.Values.Sum( static cell => cell.Count );
		Log.Info( $"Titanfall 2 map geometry grouped: {Path} ({worldMaterials.Count} world materials across "
			+ $"{groups.Count} spatial cells/{worldGroupCount} local meshes including "
			+ $"{cutoutMaterials.Count} cutout, {translucentMaterials.Count} translucent and {waterMaterials.Count} water; "
			+ $"{decalMaterials.Count} decal materials across {decalGroups.Count} cells/{decalGroupCount} local meshes, "
			+ $"{godrayMaterials.Count} Godray materials across {godrayGroups.Count} cells/{godrayGroupCount} local meshes, "
			+ $"{vertexCount} source vertices, {indexCount / 3} source triangles, {skippedToolMeshes} invisible tool meshes skipped, "
			+ $"{decalMeshCount} decal, {godrayMeshCount} Godray and {visualEffectCollisionMeshCount} "
			+ (createFallbackCollision
				? "visual FX meshes excluded from fallback collision)"
				: "render collision disabled; authoritative rBSP contents used)") );
		var renderTimer = System.Diagnostics.Stopwatch.StartNew();
		var worldChunks = BuildRenderChunks(
			groups, parsed.MapName, "world",
			(materialKey, meshIndex) => CreateWorldLightmappedMaterial(
				materialKey,
				meshIndex,
				lightmaps ),
			out var worldRenderMeshCount );
		var decalChunks = BuildRenderChunks(
			decalGroups, parsed.MapName, "decals",
			(materialKey, meshIndex) => LoadWorldMaterial( materialKey.MaterialName )
				?? MaterialLoader.CreateDecalMaterial( $"{Path}/decal_fallback_{meshIndex}", GetMaterialMetadata( materialKey.MaterialName ) ),
			out var decalRenderMeshCount );
		var godrayChunks = BuildRenderChunks(
			godrayGroups, parsed.MapName, "godrays",
			(materialKey, meshIndex) => LoadWorldMaterial( materialKey.MaterialName )
				?? MaterialLoader.CreateGodrayMaterial( $"{Path}/godray_fallback_{meshIndex}", materialKey.MaterialName ),
			out var godrayRenderMeshCount );
		if ( worldChunks.Count == 0 ) return null;
		Log.Info( $"Titanfall 2 spatial render models created: {Path} "
			+ $"({worldChunks.Count} world chunks/{worldRenderMeshCount} meshes, "
			+ $"{decalChunks.Count} decal chunks/{decalRenderMeshCount} meshes, "
			+ $"{godrayChunks.Count} Godray chunks/{godrayRenderMeshCount} meshes, "
			+ $"{renderTimer.Elapsed.TotalSeconds:0.00}s)" );

		Model collisionModel = null;
		if ( collisionPositions.Count > 0 && collisionIndices.Count > 0 )
		{
			var collisionBuilder = Model.Builder.WithName( $"titanfall2/map/{parsed.MapName}_collision" );
			collisionBuilder.AddCollisionMesh( collisionPositions.ToArray(), collisionIndices.ToArray() );
			collisionBuilder.AddTraceMesh( collisionPositions, collisionIndices );
			collisionModel = collisionBuilder.Create();
			Log.Info( $"Titanfall 2 fallback render collision submitted: {Path} ({collisionIndices.Count / 3} triangles)" );
		}
		return new WorldBuildResult( worldChunks, decalChunks, godrayChunks, collisionModel, lightmaps?.OwnedResources ?? Array.Empty<IDisposable>() );
	}

	static TitanfallMapVertex CreateWorldVertex( Titanfall2BspReader.WorldVertex sourceVertex, bool isDecal )
	{
		var normal = ToSandbox( sourceVertex.Normal ).Normal;
		var position = ToSandbox( sourceVertex.Position );
		if ( isDecal ) position += normal * DecalNormalOffset;
		return new TitanfallMapVertex
		{
			Position = position,
			Normal = normal,
			TexCoord = ToSandbox( sourceVertex.TexCoord ),
			LightmapTexCoord = ToSandbox( sourceVertex.LightmapTexCoord ),
			Color = new Color32( sourceVertex.Color.R, sourceVertex.Color.G, sourceVertex.Color.B, sourceVertex.Color.A )
		};
	}

	static MapMeshGroup GetRenderGroup(
		Dictionary<WorldRenderCell, Dictionary<WorldMaterialKey, MapMeshGroup>> chunks,
		WorldRenderCell cell,
		WorldMaterialKey materialKey )
	{
		if ( !chunks.TryGetValue( cell, out var materials ) )
		{
			materials = new Dictionary<WorldMaterialKey, MapMeshGroup>();
			chunks.Add( cell, materials );
		}
		if ( materials.TryGetValue( materialKey, out var group ) ) return group;
		group = new MapMeshGroup();
		materials.Add( materialKey, group );
		return group;
	}

	List<WorldRenderChunk> BuildRenderChunks(
		Dictionary<WorldRenderCell, Dictionary<WorldMaterialKey, MapMeshGroup>> chunks,
		string mapName,
		string category,
		Func<WorldMaterialKey, int, Material> materialFactory,
		out int totalMeshCount )
	{
		var result = new List<WorldRenderChunk>( chunks.Count );
		totalMeshCount = 0;
		foreach ( var (cell, materials) in chunks
			.OrderBy( static pair => pair.Key.X )
			.ThenBy( static pair => pair.Key.Y )
			.ThenBy( static pair => pair.Key.Z ) )
		{
			var builder = Model.Builder.WithName( $"titanfall2/map/{mapName}_{category}_{cell}" );
			var chunkMeshCount = 0;
			foreach ( var (materialKey, group) in materials
				.OrderBy( static pair => pair.Key.MaterialName, StringComparer.OrdinalIgnoreCase )
				.ThenBy( static pair => pair.Key.LightmapPage ) )
			{
				if ( group.Vertices.Count == 0 || group.Indices.Count == 0 ) continue;
				var material = materialFactory( materialKey, totalMeshCount );
				var mesh = new Mesh( $"{Path}_{category}_{cell}_{totalMeshCount}", material );
				mesh.CreateVertexBuffer( group.Vertices.Count, group.Vertices );
				mesh.CreateIndexBuffer( group.Indices.Count, group.Indices );
				mesh.Bounds = group.Bounds;
				builder.AddMesh( mesh );
				chunkMeshCount++;
				totalMeshCount++;
			}
			if ( chunkMeshCount > 0 ) result.Add( new WorldRenderChunk( cell, builder.Create() ) );
		}
		return result;
	}

	Material CreateWorldLightmappedMaterial( WorldMaterialKey key, int meshIndex, RuntimeLightmaps lightmaps )
	{
		var sourceMaterial = LoadWorldMaterial( key.MaterialName )
			?? MaterialLoader.CreateRuntimeMaterial( $"{Path}/world_fallback_{meshIndex}", GetMaterialMetadata( key.MaterialName ) );
		var metadata = GetMaterialMetadata( key.MaterialName );
		if ( lightmaps is null || key.LightmapPage < 0 || key.LightmapPage >= lightmaps.Pages.Count
			|| metadata.Mode != Titanfall2MaterialMode.Opaque || metadata.IsUnlit || metadata.IsWater )
			return sourceMaterial;

		var page = lightmaps.Pages[key.LightmapPage];
		if ( page.SkyA is null || !page.SkyA.IsValid )
			return sourceMaterial;

		var material = Material.Create(
			$"{Path}/world_lightmapped_{meshIndex}_{key.LightmapPage}",
			"shaders/titanfall2_world_lightmapped.shader" );
		CopyWorldMaterialParameters( sourceMaterial, material );
		material.Set( "g_tLightmapSkyA", page.SkyA );
		if ( page.SkyB is not null ) material.Set( "g_tLightmapSkyB", page.SkyB );
		if ( page.RealTimeA is not null ) material.Set( "g_tLightmapRealTimeA", page.RealTimeA );
		if ( page.RealTimeB is not null ) material.Set( "g_tLightmapRealTimeB", page.RealTimeB );
		if ( page.RealTimeC is not null ) material.Set( "g_tLightmapRealTimeC", page.RealTimeC );
		material.Set( "g_flT2LightmapIntensity", 1.35f );
		material.Set( "g_flT2RealTimeLightContribution", 0f );
		return material;
	}

	static void CopyWorldMaterialParameters( Material source, Material target )
	{
		string[] textureNames =
		[
			"g_tAlbedo", "g_tNormal", "g_tGloss", "g_tSpecular", "g_tAO", "g_tOpacity",
			"g_tEmissive", "g_tDetail", "g_tDistortion", "g_tEnvironment"
		];
		foreach ( var name in textureNames )
		{
			var texture = source?.GetTexture( name );
			if ( texture is not null && texture.IsValid )
				target.Set( name, texture );
		}

		string[] vectorNames =
		[
			"g_flT2GlossScale", "g_vT2SpecularTint", "g_flT2EmissiveStrength",
			"g_flT2DetailBlend", "g_vT2UvDistortion", "g_flT2HasEnvironment",
			"g_flT2EnvironmentIntensity", "g_flT2FresnelStrength", "g_vT2Uv1RotScale",
			"g_vT2Uv1Translate", "g_vT2AlbedoTint", "g_vT2EmissiveTint",
			"g_flT2MaterialOpacity", "g_flT2AlphaTestReference"
		];
		foreach ( var name in vectorNames )
			target.Set( name, source?.GetVector4( name ) ?? default );
	}

	static RuntimeLightmaps CreateLightmapTextures( Titanfall2BspReader.BspLightmaps source )
	{
		if ( source is null || !source.IsValid ) return null;
		var pages = new List<RuntimeLightmapPage>( source.Pages.Count );
		foreach ( var page in source.Pages )
		{
			Texture Create( byte[] data, int width, int height )
			{
				if ( data is null || data.Length != width * height * 4 ) return null;
				// Respawn's atlas contains tightly packed lightmap charts. Generic
				// mip generation blends unrelated charts and unused black texels
				// together, producing large soft rectangular blotches at grazing
				// camera angles. Keep the authored base page only.
				return Texture.Create( width, height, ImageFormat.RGBA8888 )
					.WithData( data )
					.Finish();
			}

			pages.Add( new RuntimeLightmapPage(
				Create( page.SkyA, page.Width, page.Height ),
				Create( page.SkyB, page.Width, page.Height ),
				Create( page.RealTimeA, page.Width, page.Height ),
				Create( page.RealTimeB, page.Width, page.Height ),
				Create( page.RealTimeC, Math.Max( 1, page.Width / 2 ), Math.Max( 1, page.Height / 2 ) ) ) );
		}
		return new RuntimeLightmaps( pages );
	}

	Material LoadWorldMaterial( string materialName )
	{
		if ( string.IsNullOrWhiteSpace( materialName ) || !Host.TryGetMaterialPath( materialName, out var registeredPath ) )
			return null;
		var material = Material.Load( $"mount://{Host.Ident}/{registeredPath}.vmat" );
		return material is not null && material.IsValid ? material : null;
	}

	Titanfall2MaterialMetadata GetMaterialMetadata( string materialName )
	{
		if ( Host.TryGetMaterialMetadata( materialName, out var metadata ) ) return metadata;
		return MaterialLoader.InferMetadata( materialName );
	}

	Model BuildWorldCollisionModel( Titanfall2BspReader.ParsedBsp parsed )
	{
		var collision = parsed.WorldCollision;
		if ( collision is null || collision.IsEmpty ) return null;
		var chunks = BuildCollisionChunks( collision, Titanfall2StreamingSettings.CollisionCellSize );
		if ( chunks.Count == 0 ) return null;
		var builder = Model.Builder.WithName( $"titanfall2/map/{parsed.MapName}_collision" );
		var submittedVertices = 0;
		var submittedTriangles = 0;
		foreach ( var chunk in chunks.Values )
		{
			if ( chunk.Vertices.Count < 3 || chunk.Indices.Count < 3 ) continue;
			builder.AddCollisionMesh( chunk.Vertices.ToArray(), chunk.Indices.ToArray() );
			submittedVertices += chunk.Vertices.Count;
			submittedTriangles += chunk.Indices.Count / 3;
		}
		var model = builder.Create();
		Log.Info( $"Titanfall 2 real world collision built: {Path} ({collision.PrimitiveCount} primitives: "
			+ $"{collision.BrushCount} brushes/{collision.BrushTriangleCount} triangles + "
			+ $"{collision.TricollCount} tricolls/{collision.TricollTriangleCount} triangles; "
			+ $"{collision.NonBlockingPrimitiveCount} non-player-solid primitives skipped, "
			+ $"{collision.WaterPrimitiveCount} water primitives "
			+ $"({collision.BlockingWaterPrimitiveCount} player-blocking); "
			+ $"{chunks.Count} spatial chunks at {Titanfall2StreamingSettings.CollisionCellSize:0} units, "
			+ $"{submittedVertices} submitted vertices, {submittedTriangles} triangles)" );
		return model;
	}

	static Dictionary<CollisionCell, CollisionChunk> BuildCollisionChunks( Titanfall2BspReader.BspWorldCollision collision, float cellSize )
	{
		var chunks = new Dictionary<CollisionCell, CollisionChunk>();
		for ( var index = 0; index + 2 < collision.Indices.Length; index += 3 )
		{
			var sourceA = collision.Indices[index];
			var sourceB = collision.Indices[index + 1];
			var sourceC = collision.Indices[index + 2];
			if ( sourceA < 0 || sourceA >= collision.Vertices.Length
				|| sourceB < 0 || sourceB >= collision.Vertices.Length
				|| sourceC < 0 || sourceC >= collision.Vertices.Length ) continue;
			var center = (collision.Vertices[sourceA] + collision.Vertices[sourceB] + collision.Vertices[sourceC]) / 3f;
			var cell = new CollisionCell( (int)MathF.Floor( center.X / cellSize ), (int)MathF.Floor( center.Y / cellSize ) );
			if ( !chunks.TryGetValue( cell, out var chunk ) )
			{
				chunk = new CollisionChunk();
				chunks.Add( cell, chunk );
			}
			chunk.Indices.Add( chunk.RemapVertex( sourceA, collision.Vertices ) );
			chunk.Indices.Add( chunk.RemapVertex( sourceB, collision.Vertices ) );
			chunk.Indices.Add( chunk.RemapVertex( sourceC, collision.Vertices ) );
		}
		return chunks;
	}

	IReadOnlyDictionary<int, byte[]> ReadCollisionLumpOverrides()
	{
		var overrides = new Dictionary<int, byte[]>();
		if ( _source is not ITitanfall2SiblingAssetSource siblingSource ) return overrides;
		long totalBytes = 0;
		foreach ( var lumpId in Titanfall2BspReader.RequiredCollisionLumpIds )
		{
			byte[] lumpBytes;
			if ( !siblingSource.TryReadSibling( $".{lumpId:X4}.bsp_lump.client", out lumpBytes, out _ )
				&& !siblingSource.TryReadSibling( $".{lumpId:X4}.bsp_lump", out lumpBytes, out _ ) ) continue;
			overrides[lumpId] = lumpBytes;
			totalBytes += lumpBytes.LongLength;
		}

		Log.Info( $"Titanfall 2 BSP collision lumps loaded: {Path} ({overrides.Count}, "
			+ $"{totalBytes / (1024.0 * 1024.0):0.00} MB)" );
		return overrides;
	}

	static string NormalizeMaterialName( string name )
	{
		if ( string.IsNullOrWhiteSpace( name ) ) return string.Empty;
		var normalized = name.Replace( '\\', '/' ).Trim( '/' );
		foreach ( var extension in new[] { ".vmat", ".rpak", ".matl" } )
		{
			if ( normalized.EndsWith( extension, StringComparison.OrdinalIgnoreCase ) )
				normalized = normalized[..^extension.Length];
		}
		return normalized;
	}

	int CreateCubemapProbes( Titanfall2BspReader.ParsedBsp parsed )
	{
		if ( Application.IsHeadless ) return 0;
		var bounds = new BBox( ToSandbox( parsed.World.Mins ), ToSandbox( parsed.World.Maxs ) );
		var center = (bounds.Mins + bounds.Maxs) * 0.5f;
		var controllerObject = new GameObject( true, "titanfall2_cubemap_controller" );
		var controller = controllerObject.AddComponent<Titanfall2CubemapController>();

		if ( parsed.Cubemaps.Count > 0 && parsed.EmbeddedCubemapVtf.Length > 0 )
		{
			if ( VtfTextureDecoder.TryCreateCubeFrames( parsed.EmbeddedCubemapVtf, out var textures, out var error ) )
			{
				var positions = parsed.Cubemaps.Select( cubemap => ToSandbox( cubemap.Origin ) ).ToArray();
				var count = controller.ConfigureCustom( textures, positions, bounds );
				if ( textures.Length != parsed.Cubemaps.Count )
					Log.Warning( $"Titanfall 2 cubemap frame/location mismatch: {Path} "
						+ $"({textures.Length} frames, {parsed.Cubemaps.Count} locations; using {count})." );
				Log.Info( $"Titanfall 2 embedded cubemaps loaded: {Path} ({count} probes from "
					+ $"{parsed.EmbeddedCubemapVtf.Length / (1024.0 * 1024.0):0.00} MB VTF)." );
				return count;
			}
			Log.Warning( $"Unable to decode Titanfall 2 embedded cubemaps for '{Path}': {error}" );
		}

		controller.ConfigureDelayedFallback( center, bounds );
		Log.Info( $"Titanfall 2 map has no usable embedded cubemap; a delayed render-once fallback was queued: {Path}." );
		return 1;
	}

	int CreateSkybox( IReadOnlyList<Titanfall2BspReader.StaticPropInstance> staticProps )
	{
		var vistaProps = staticProps.Where( static prop => IsVistaModelPath( prop.ModelPath ) ).ToArray();
		if ( vistaProps.Length == 0 ) return 0;

		var environmentEntities = ReadEntities( _environmentEntitySource );
		var candidates = new List<SkyboxCandidate>();
		foreach ( var values in environmentEntities )
		{
			if ( !values.TryGetValue( "classname", out var className )
				|| !className.Equals( "sky_camera", StringComparison.OrdinalIgnoreCase )
				|| !values.TryGetValue( "origin", out var originText )
				|| !TryParseVector3( originText, out var origin ) ) continue;

			var rotation = default( Angles );
			if ( values.TryGetValue( "angles", out var anglesText ) && TryParseVector3( anglesText, out var angleVector ) )
				rotation = new Angles( angleVector.x, angleVector.y, angleVector.z );

			var skyScale = 1000f;
			if ( values.TryGetValue( "skyscale", out var skyScaleText ) && TryParseFloat( skyScaleText, out var parsedSkyScale ) )
				skyScale = parsedSkyScale;
			else if ( values.TryGetValue( "scale", out var scaleText ) && TryParseFloat( scaleText, out var parsedScale ) && parsedScale > 1f )
				skyScale = parsedScale;

			var matchingProps = vistaProps.Where( prop => DistanceSquared( prop.Origin, origin ) <= 16f * 16f ).ToArray();
			if ( matchingProps.Length == 0 ) continue;
			candidates.Add( new SkyboxCandidate(
				values.GetValueOrDefault( "targetname", "sky_camera" ),
				origin,
				rotation,
				Math.Clamp( skyScale, 1f, 100000f ),
				ResolveSkyboxFog( values, environmentEntities ),
				matchingProps ) );
		}

		SkyboxCandidate selected;
		if ( candidates.Count > 0 )
		{
			selected = candidates
				.OrderByDescending( static candidate => candidate.Name.Contains( "level", StringComparison.OrdinalIgnoreCase ) )
				.ThenByDescending( static candidate => candidate.Models.Count( prop => !IsPlanetVista( prop.ModelPath ) ) )
				.ThenBy( static candidate => candidate.Name.Contains( "intro", StringComparison.OrdinalIgnoreCase ) )
				.ThenByDescending( static candidate => candidate.Models.Length )
				.First();
		}
		else
		{
			// Some community VPKs omit the environment entity partition. Vista props are
			// still authored at the sky_camera origin, so retain a deterministic fallback.
			var group = vistaProps
				.GroupBy( static prop => VistaOriginKey.FromPosition( prop.Origin ) )
				.OrderByDescending( static group => group.Count( prop => !IsPlanetVista( prop.ModelPath ) ) )
				.ThenByDescending( static group => group.Count() )
				.First();
			var models = group.ToArray();
			selected = new SkyboxCandidate(
				"inferred_sky_camera",
				ToSandbox( models[0].Origin ),
				Angles.Zero,
				1000f,
				Titanfall2SkyboxFog.Disabled,
				models );
		}

		var skyboxModels = selected.Models.Select( static prop => new Titanfall2SkyboxModel(
			prop.ModelPath,
			ToSandbox( prop.Origin ),
			new Angles( prop.Angles.X, prop.Angles.Y, prop.Angles.Z ),
			MathF.Max( prop.Scale, 0.001f ) ) ).ToArray();
		var skyboxObject = new GameObject( true, "titanfall2_3d_skybox" );
		skyboxObject.AddComponent<Titanfall2Skybox3D>().Configure(
			Host.Ident,
			Path,
			selected.Name,
			selected.Origin,
			selected.Rotation,
			selected.Scale,
			selected.Fog,
			skyboxModels );

		Log.Info( $"Titanfall 2 3D skybox selected: {Path} ({selected.Name}, origin {selected.Origin}, "
			+ $"scale {selected.Scale:0.##}, {skyboxModels.Length}/{vistaProps.Length} vista models)" );
		return skyboxModels.Length;
	}

	int CreateSpawnPoints( Titanfall2BspReader.ParsedBsp parsed, out Vector3 firstPosition )
	{
		var count = 0;
		firstPosition = (ToSandbox( parsed.World.Mins ) + ToSandbox( parsed.World.Maxs )) * 0.5f + Vector3.Up * 128f;
		foreach ( var values in ReadEntities( _spawnEntitySource ) )
		{
			if ( !values.TryGetValue( "classname", out var className ) || className is not ("info_spawnpoint_human_start" or "info_spawnpoint_human" or "info_player_start") ) continue;
			if ( !values.TryGetValue( "origin", out var originText ) || !TryParseVector3( originText, out var origin ) ) continue;

			var gameObject = new GameObject( true, values.GetValueOrDefault( "targetname", className ) );
			gameObject.WorldPosition = origin;
			if ( count == 0 ) firstPosition = origin;
			if ( values.TryGetValue( "angles", out var anglesText ) && TryParseVector3( anglesText, out var angles ) )
				gameObject.WorldRotation = new Angles( angles.x, angles.y, angles.z );
			gameObject.AddComponent<SpawnPoint>();
			gameObject.Tags.Add( "spawn", "info_player_start", className );
			count++;
		}

		if ( count > 0 ) return count;
		CreateFallbackSpawn( firstPosition );
		return 1;
	}

	int CreateParticleSystems( Vector3 streamingAnchor )
	{
		var entities = new List<Titanfall2ParticleMapEntity>();
		var skyboxEffects = 0;
		var atmosphericEffects = 0;
		foreach ( var values in ReadEntities( _particleEntitySource ) )
		{
			if ( !values.TryGetValue( "classname", out var className )
				|| !className.Equals( "info_particle_system", StringComparison.OrdinalIgnoreCase ) ) continue;
			if ( values.TryGetValue( "start_active", out var startActive )
				&& (startActive.Equals( "0", StringComparison.OrdinalIgnoreCase )
					|| startActive.Equals( "false", StringComparison.OrdinalIgnoreCase )) ) continue;
			if ( !values.TryGetValue( "effect_name", out var effectName ) || string.IsNullOrWhiteSpace( effectName ) ) continue;
			if ( !values.TryGetValue( "origin", out var originText ) || !TryParseVector3( originText, out var origin ) ) continue;
			if ( !Titanfall2StreamingSettings.AtmosphericCardEffects
				&& Titanfall2CollisionFilter.IsAtmosphericCardName( effectName ) )
			{
				atmosphericEffects++;
				continue;
			}
			// 3D-skybox particles must be submitted to the isolated skybox SceneWorld.
			// A regular scene component would render them at their authored sky-camera
			// coordinates in the main world, so defer these few systems until the
			// skybox bridge gains a native particle path.
			if ( values.TryGetValue( "in_skybox", out var inSkybox ) && inSkybox == "1" )
			{
				skyboxEffects++;
				continue;
			}

			var rotation = Angles.Zero;
			if ( values.TryGetValue( "angles", out var anglesText ) && TryParseVector3( anglesText, out var angles ) )
				rotation = new Angles( angles.x, angles.y, angles.z );
			var scale = 1f;
			if ( values.TryGetValue( "scale", out var scaleText ) && TryParseFloat( scaleText, out var parsedScale ) )
				scale = Math.Clamp( parsedScale, 0.001f, 1000f );
			entities.Add( new Titanfall2ParticleMapEntity(
				effectName.Trim(),
				values.GetValueOrDefault( "targetname", string.Empty ),
				origin,
				rotation,
				scale ) );
		}

		if ( skyboxEffects > 0 )
			Log.Info( $"Titanfall 2 map FX deferred {skyboxEffects} 3D-skybox particle systems: {Path}." );
		if ( atmosphericEffects > 0 )
			Log.Info( $"Titanfall 2 map FX temporarily omitted {atmosphericEffects} billboarded "
				+ $"steam/mist/smoke systems: {Path}." );
		if ( entities.Count == 0 ) return 0;
		var streamerObject = new GameObject( true, "titanfall2_particle_streamer" );
		streamerObject.AddComponent<Titanfall2ParticleStreamer>()
			.Configure( Host.Ident, Path, streamingAnchor, entities );
		return entities.Count;
	}

	int CreateDynamicProps( GameObject world )
	{
		var entities = ReadEntities( _scriptEntitySource );
		if ( entities.Count == 0 ) return 0;

		var root = new GameObject( world, true, "titanfall2_dynamic_props" );
		root.IsStatic = false;
		var created = 0;
		var failed = 0;
		var animated = 0;
		var colliders = 0;
		var propsByGuid = new Dictionary<string, GameObject>( StringComparer.OrdinalIgnoreCase );
		foreach ( var values in entities )
		{
			if ( !values.TryGetValue( "classname", out var className )
				|| !(className.Equals( "prop_dynamic", StringComparison.OrdinalIgnoreCase )
					|| className.Equals( "prop_dynamic_lightweight", StringComparison.OrdinalIgnoreCase )
					|| className.Equals( "prop_dynamic_override", StringComparison.OrdinalIgnoreCase )) )
				continue;
			if ( IsEntityTrue( values.GetValueOrDefault( "StartDisabled" ) ) ) continue;
			if ( !values.TryGetValue( "model", out var modelPath )
				|| string.IsNullOrWhiteSpace( modelPath )
				|| modelPath[0] == '*' ) continue;
			if ( !values.TryGetValue( "origin", out var originText )
				|| !TryParseVector3( originText, out var position ) ) continue;

			var normalizedModelPath = NormalizeModelPath( modelPath );
			var resourcePath = $"mount://{Host.Ident}/{normalizedModelPath}.vmdl";
			var model = Model.Load( resourcePath );
			if ( !model.IsValid() || model == Model.Error )
			{
				if ( failed++ < 12 )
					Log.Warning( $"Unable to load Titanfall 2 dynamic prop '{resourcePath}'." );
				continue;
			}

			var gameObject = new GameObject( root, true,
				values.GetValueOrDefault( "targetname", System.IO.Path.GetFileNameWithoutExtension( normalizedModelPath ) ) );
			gameObject.IsStatic = false;
			gameObject.WorldPosition = position;
			if ( values.TryGetValue( "angles", out var anglesText ) && TryParseVector3( anglesText, out var angles ) )
				gameObject.WorldRotation = new Angles( angles.x, angles.y, angles.z );
			if ( values.TryGetValue( "scale", out var scaleText ) && TryParseFloat( scaleText, out var scale ) )
				gameObject.WorldScale = Vector3.One * Math.Clamp( scale, 0.001f, 1000f );

			var disableShadows = !Titanfall2StreamingSettings.PropShadows
				|| IsEntityTrue( values.GetValueOrDefault( "disableshadows" ) );
			if ( model.BoneCount > 0 || model.AnimationCount > 0 )
			{
				var renderer = gameObject.AddComponent<SkinnedModelRenderer>();
				renderer.Model = model;
				renderer.UseAnimGraph = false;
				renderer.RenderType = disableShadows
					? ModelRenderer.ShadowRenderType.Off
					: ModelRenderer.ShadowRenderType.On;
				var animation = ResolveDynamicAnimation( values, model );
				if ( !string.IsNullOrWhiteSpace( animation ) )
				{
					renderer.Sequence.Name = animation;
					renderer.Sequence.Looping = !IsEntityTrue( values.GetValueOrDefault( "HoldAnimation" ) );
					renderer.PlaybackRate = 1f;
				}
				animated++;
			}
			else
			{
				var renderer = gameObject.AddComponent<ModelRenderer>();
				renderer.Model = model;
				renderer.RenderType = disableShadows
					? ModelRenderer.ShadowRenderType.Off
					: ModelRenderer.ShadowRenderType.On;
			}

			var solid = values.TryGetValue( "solid", out var solidText )
				&& int.TryParse( solidText, out var solidType ) && solidType != 0;
			var collidable = solid
				|| IsEntityTrue( values.GetValueOrDefault( "collide_human" ) )
				|| IsEntityTrue( values.GetValueOrDefault( "collide_titan" ) );
			if ( collidable )
			{
				var collider = gameObject.AddComponent<ModelCollider>();
				collider.Model = model;
				collider.Static = true;
				colliders++;
			}
			if ( values.TryGetValue( "link_guid", out var linkGuid ) && !string.IsNullOrWhiteSpace( linkGuid ) )
				propsByGuid.TryAdd( linkGuid.Trim(), gameObject );
			created++;
		}

		if ( created == 0 )
		{
			root.Destroy();
			return 0;
		}

		var rotators = CreateRotatingMovers( root, entities, propsByGuid );
		Log.Info( $"Titanfall 2 scripted dynamic props created: {created} models, "
			+ $"{animated} skinned, {rotators} rotating movers, {colliders} colliders, "
			+ $"{failed} failures ({Path})." );
		return created;
	}

	static string ResolveDynamicAnimation(
		IReadOnlyDictionary<string, string> values,
		Model model )
	{
		foreach ( var key in new[] { "DefaultAnim", "animation", "sequence" } )
		{
			if ( values.TryGetValue( key, out var explicitAnimation )
				&& !string.IsNullOrWhiteSpace( explicitAnimation ) )
				return explicitAnimation.Trim();
		}

		if ( model is null || model.AnimationCount <= 0 ) return null;
		var names = model.AnimationNames
			.Where( static name => !string.IsNullOrWhiteSpace( name )
				&& !name.Equals( "bindpose", StringComparison.OrdinalIgnoreCase ) )
			.ToArray();
		return names.FirstOrDefault( static name =>
				name.Contains( "idle", StringComparison.OrdinalIgnoreCase )
				|| name.Contains( "spin", StringComparison.OrdinalIgnoreCase )
				|| name.Contains( "loop", StringComparison.OrdinalIgnoreCase ) )
			?? names.FirstOrDefault();
	}

	static int CreateRotatingMovers(
		GameObject root,
		IReadOnlyList<Dictionary<string, string>> entities,
		IReadOnlyDictionary<string, GameObject> propsByGuid )
	{
		var created = 0;
		foreach ( var values in entities )
		{
			if ( !values.TryGetValue( "rotate_forever_speed", out var speedText )
				|| !TryParseFloat( speedText, out var speed )
				|| MathF.Abs( speed ) <= 0.001f )
				continue;

			var isRotator = values.TryGetValue( "editorclass", out var editorClass )
				&& editorClass.Equals( "script_rotator", StringComparison.OrdinalIgnoreCase );
			isRotator |= values.TryGetValue( "classname", out var className )
				&& className.Equals( "script_mover_lightweight", StringComparison.OrdinalIgnoreCase )
				&& values.ContainsKey( "rotation_axis" );
			if ( !isRotator ) continue;

			if ( !values.TryGetValue( "origin", out var originText )
				|| !TryParseVector3( originText, out var pivot ) )
				continue;

			var moverRotation = Rotation.Identity;
			if ( values.TryGetValue( "angles", out var anglesText )
				&& TryParseVector3( anglesText, out var angles ) )
				moverRotation = new Angles( angles.x, angles.y, angles.z ).ToRotation();

			var axisName = values.GetValueOrDefault( "rotation_axis", "yaw" );
			var delay = GetEntityFloat( values, "start_delay", 0f );
			foreach ( var link in values.Where( static pair =>
				pair.Key.StartsWith( "link_to_guid_", StringComparison.OrdinalIgnoreCase ) ) )
			{
				if ( string.IsNullOrWhiteSpace( link.Value )
					|| !propsByGuid.TryGetValue( link.Value.Trim(), out var target )
					|| !target.IsValid() )
					continue;

				var basis = IsEntityTrue( values.GetValueOrDefault( "use_local_rotation" ) )
					? target.WorldRotation
					: moverRotation;
				var axis = axisName.ToLowerInvariant() switch
				{
					"pitch" => basis.Right,
					"roll" => basis.Forward,
					_ => basis.Up
				};
				target.AddComponent<Titanfall2RotatingMover>()
					.Configure( pivot, axis, speed, delay );
				created++;
			}
		}
		return created;
	}

	static IReadOnlyList<Dictionary<string, string>> ReadEntities( ITitanfall2AssetSource source )
	{
		if ( source is null || !source.TryReadAllBytes( out var bytes, out _ ) || bytes.Length == 0 )
			return Array.Empty<Dictionary<string, string>>();

		var entities = new List<Dictionary<string, string>>();
		var text = System.Text.Encoding.UTF8.GetString( bytes ).Replace( '\0', '\n' );
		foreach ( System.Text.RegularExpressions.Match block in System.Text.RegularExpressions.Regex.Matches(
			text,
			@"\{(?<body>.*?)\}",
			System.Text.RegularExpressions.RegexOptions.Singleline ) )
		{
			var values = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase );
			foreach ( System.Text.RegularExpressions.Match pair in System.Text.RegularExpressions.Regex.Matches(
				block.Groups["body"].Value,
				@"""(?<key>[^""]+)""\s*""(?<value>[^""]*)""" ) )
			{
				values[pair.Groups["key"].Value] = pair.Groups["value"].Value;
			}
			if ( values.Count > 0 ) entities.Add( values );
		}
		return entities;
	}

	static bool TryParseVector3( string value, out Vector3 result )
	{
		result = default;
		var components = value.Split( [' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries );
		if ( components.Length != 3 ) return false;
		var style = System.Globalization.NumberStyles.Float;
		var culture = System.Globalization.CultureInfo.InvariantCulture;
		if ( !float.TryParse( components[0], style, culture, out var x )
			|| !float.TryParse( components[1], style, culture, out var y )
			|| !float.TryParse( components[2], style, culture, out var z ) ) return false;
		result = new Vector3( x, y, z );
		return true;
	}

	static bool TryParseFloat( string value, out float result ) => float.TryParse(
		value,
		System.Globalization.NumberStyles.Float,
		System.Globalization.CultureInfo.InvariantCulture,
		out result );

	static bool IsVistaModelPath( string path )
	{
		if ( string.IsNullOrWhiteSpace( path ) ) return false;
		var normalized = path.Replace( '\\', '/' ).TrimStart( '/' );
		return normalized.StartsWith( "models/vistas/", StringComparison.OrdinalIgnoreCase )
			|| normalized.StartsWith( "vistas/", StringComparison.OrdinalIgnoreCase );
	}

	static bool IsPlanetVista( string path ) => path?.Contains( "planet_blue_sun", StringComparison.OrdinalIgnoreCase ) == true;

	static bool ShouldSkipWorldMaterial( string materialName )
	{
		if ( string.IsNullOrWhiteSpace( materialName ) ) return false;
		return materialName.Contains( "toolsskybox", StringComparison.OrdinalIgnoreCase )
			|| materialName.Contains( "toolsnodraw", StringComparison.OrdinalIgnoreCase )
			|| materialName.Contains( "toolsinvisible", StringComparison.OrdinalIgnoreCase )
			|| (!Titanfall2StreamingSettings.AtmosphericCardEffects
				&& Titanfall2CollisionFilter.IsAtmosphericCardName( materialName ));
	}

	static float DistanceSquared( System.Numerics.Vector3 source, Vector3 target )
	{
		var x = source.X - target.x;
		var y = source.Y - target.y;
		var z = source.Z - target.z;
		return x * x + y * y + z * z;
	}

	static void CreateFallbackSpawn( Vector3 position )
	{
		var spawn = new GameObject( true, "info_player_start" );
		spawn.WorldPosition = position;
		spawn.AddComponent<SpawnPoint>();
		spawn.Tags.Add( "spawn", "info_player_start" );
	}

	void CreateEnvironment( Titanfall2BspReader.ParsedBsp parsed )
	{
		var entities = ReadEntities( _environmentEntitySource );
		var center = (ToSandbox( parsed.World.Mins ) + ToSandbox( parsed.World.Maxs )) * 0.5f;
		var lightValues = entities
			.Where( values => values.TryGetValue( "classname", out var className )
				&& className.Equals( "light_environment", StringComparison.OrdinalIgnoreCase ) )
			.OrderBy( values => EntityDistanceSquared( values, center ) )
			.FirstOrDefault();

		var gameObject = new GameObject( true, "titanfall2_environment_light" );
		gameObject.WorldRotation = new Angles( 50f, -35f, 0f );
		if ( lightValues is not null
			&& lightValues.TryGetValue( "angles", out var angleText )
			&& TryParseVector3( angleText, out var angles ) )
			gameObject.WorldRotation = new Angles( angles.x, angles.y, angles.z );

		var light = gameObject.AddComponent<DirectionalLight>();
		light.LightColor = TryParseSourceLightColor( lightValues?.GetValueOrDefault( "_light" ), 0.78f,
			out var lightColor ) ? lightColor : new Color( 0.78f, 0.74f, 0.68f );
		light.SkyColor = TryParseSourceLightColor( lightValues?.GetValueOrDefault( "_ambient" ), 0.30f,
			out var skyColor ) ? skyColor : new Color( 0.22f, 0.28f, 0.36f );
		light.ShadowCascadeCount = Titanfall2StreamingSettings.MapShadows ? 2 : 1;
		light.ContactShadows = false;

		var fogValues = Titanfall2StreamingSettings.MapFog
			? entities
				.Where( values => values.TryGetValue( "classname", out var className )
					&& className.Equals( "env_fog_controller", StringComparison.OrdinalIgnoreCase )
					&& (!values.TryGetValue( "fogenable", out var enabledText ) || !IsEntityFalse( enabledText )) )
				.OrderBy( values => EntityDistanceSquared( values, center ) )
				.FirstOrDefault()
			: null;
		if ( fogValues is not null )
		{
			var fogObject = new GameObject( true, "titanfall2_environment_fog" );
			fogObject.WorldPosition = new Vector3( center.x, center.y, ToSandbox( parsed.World.Mins ).z - 1024f );
			var fog = fogObject.AddComponent<GradientFog>();
			fog.Height = MathF.Max( 2048f, ToSandbox( parsed.World.Maxs ).z - ToSandbox( parsed.World.Mins ).z + 2048f );
			fog.StartDistance = GetEntityFloat( fogValues, "fogdistoffset",
				GetEntityFloat( fogValues, "fogstart", 256f ) );
			fog.EndDistance = GetEntityFloat( fogValues, "foghalfdistbottom",
				GetEntityFloat( fogValues, "fogend", 6000f ) );
			if ( fog.EndDistance <= fog.StartDistance )
				fog.EndDistance = fog.StartDistance + 4096f;
			var opacity = Math.Clamp( GetEntityFloat( fogValues, "fogdensity",
				GetEntityFloat( fogValues, "fogmaxdensity", 0.28f ) ), 0.05f, 0.55f );
			fog.Color = TryParseRgbColor( fogValues.GetValueOrDefault( "fogcolor" ), opacity, out var fogColor )
				? fogColor
				: new Color( 0.66f, 0.72f, 0.78f, opacity );
			fog.FalloffExponent = 1.15f;
			fog.VerticalFalloffExponent = 0.25f;
		}

		var color = new GameObject( true, "titanfall2_color_adjustments" ).AddComponent<ColorAdjustments>();
		color.Blend = 1f;
		color.Saturation = 0.86f;
		color.Brightness = 0.94f;
		color.Contrast = 0.92f;

		Log.Info( $"Titanfall 2 environment created from map entities: "
			+ $"{(lightValues is null ? "fallback light" : "light_environment")}, "
			+ $"{(!Titanfall2StreamingSettings.MapFog ? "fog disabled" : fogValues is null ? "no fog entity" : "env_fog_controller")}, "
			+ $"2 shadow cascades ({Path})." );
	}

	static float EntityDistanceSquared( IReadOnlyDictionary<string, string> values, Vector3 center )
	{
		return values.TryGetValue( "origin", out var originText ) && TryParseVector3( originText, out var origin )
			? origin.DistanceSquared( center )
			: float.MaxValue;
	}

	static float GetEntityFloat( IReadOnlyDictionary<string, string> values, string key, float fallback )
		=> values.TryGetValue( key, out var text ) && TryParseFloat( text, out var value ) ? value : fallback;

	static Titanfall2SkyboxFog ResolveSkyboxFog(
		IReadOnlyDictionary<string, string> cameraValues,
		IReadOnlyList<Dictionary<string, string>> environmentEntities )
	{
		IReadOnlyDictionary<string, string> fogValues = cameraValues;
		if ( cameraValues.TryGetValue( "useworldfog", out var useWorldFog ) && IsEntityTrue( useWorldFog ) )
		{
			fogValues = environmentEntities.FirstOrDefault( static values =>
				values.TryGetValue( "classname", out var className )
				&& className.Equals( "env_fog_controller", StringComparison.OrdinalIgnoreCase )
				&& (!values.TryGetValue( "fogenable", out var enabled ) || !IsEntityFalse( enabled )) )
				?? cameraValues;
		}

		if ( fogValues.TryGetValue( "fogenable", out var fogEnabled ) && IsEntityFalse( fogEnabled ) )
			return Titanfall2SkyboxFog.Disabled;

		var start = MathF.Max( 0f, GetEntityFloat( fogValues, "fogdistoffset",
			GetEntityFloat( fogValues, "fogstart", 80f ) ) );
		var end = GetEntityFloat( fogValues, "foghalfdistbottom",
			GetEntityFloat( fogValues, "fogend", 6000f ) );
		if ( end <= start ) end = start + 4096f;

		var opacity = Math.Clamp( GetEntityFloat( fogValues, "fogdensity",
			GetEntityFloat( fogValues, "fogmaxdensity", 0.25f ) ), 0f, 0.65f );
		if ( opacity <= 0.001f ) return Titanfall2SkyboxFog.Disabled;

		var color = TryParseRgbColor( fogValues.GetValueOrDefault( "fogcolor" ), 1f, out var parsedColor )
			? parsedColor.WithAlpha( 1f )
			: new Color( 0.74f, 0.85f, 1f );
		return new Titanfall2SkyboxFog( true, color, start, end, opacity );
	}

	static bool TryParseSourceLightColor( string value, float maximumIntensity, out Color result )
	{
		result = default;
		if ( !TryParseFloatComponents( value, out var components ) || components.Length < 3 ) return false;
		var authoredIntensity = components.Length >= 4 ? components[3] : 255f;
		var intensity = Math.Clamp( authoredIntensity / 1020f, maximumIntensity * 0.35f, maximumIntensity );
		result = new Color(
			Math.Clamp( components[0] / 255f, 0f, 1f ) * intensity,
			Math.Clamp( components[1] / 255f, 0f, 1f ) * intensity,
			Math.Clamp( components[2] / 255f, 0f, 1f ) * intensity );
		return true;
	}

	static bool TryParseRgbColor( string value, float alpha, out Color result )
	{
		result = default;
		if ( !TryParseFloatComponents( value, out var components ) || components.Length < 3 ) return false;
		result = new Color(
			Math.Clamp( components[0] / 255f, 0f, 1f ),
			Math.Clamp( components[1] / 255f, 0f, 1f ),
			Math.Clamp( components[2] / 255f, 0f, 1f ),
			Math.Clamp( alpha, 0f, 1f ) );
		return true;
	}

	static bool TryParseFloatComponents( string value, out float[] components )
	{
		components = Array.Empty<float>();
		if ( string.IsNullOrWhiteSpace( value ) ) return false;
		var parts = value.Split( [' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries );
		if ( parts.Length == 0 ) return false;
		components = new float[parts.Length];
		for ( var index = 0; index < parts.Length; index++ )
		{
			if ( !TryParseFloat( parts[index], out components[index] ) )
			{
				components = Array.Empty<float>();
				return false;
			}
		}
		return true;
	}

	static bool IsEntityTrue( string value ) => value is not null
		&& (value == "1" || value.Equals( "true", StringComparison.OrdinalIgnoreCase )
			|| value.Equals( "yes", StringComparison.OrdinalIgnoreCase ));

	static bool IsEntityFalse( string value ) => value is not null
		&& (value == "0" || value.Equals( "false", StringComparison.OrdinalIgnoreCase )
			|| value.Equals( "no", StringComparison.OrdinalIgnoreCase ));

	static string NormalizeModelPath( string path )
	{
		var normalized = path.Replace( '\\', '/' ).Trim().TrimStart( '/' );
		return normalized.EndsWith( ".vmdl", StringComparison.OrdinalIgnoreCase )
			? normalized[..^5]
			: normalized;
	}

	static Vector3 ToSandbox( System.Numerics.Vector3 value ) => new( value.X, value.Y, value.Z );
	static Vector2 ToSandbox( System.Numerics.Vector2 value ) => new( value.X, value.Y );

}
