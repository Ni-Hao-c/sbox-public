using Sandbox.Mounting.PerfKit;
using Titanfall2;

partial class MapLoader
{
	// Edge length of the spatial cells world triangles are grouped into.
	const float WorldRenderCellSize = 2048f;

	Titanfall2MaterialPrefetch _materialPrefetch;
	WorldBuildResult BuildWorldModel( Titanfall2BspReader.ParsedBsp parsed, bool createFallbackCollision )
	{
		var vertexCount = parsed.World.Meshes.Sum( static mesh => mesh?.Vertices.Length ?? 0 );
		var indexCount = parsed.World.Meshes.Sum( static mesh => mesh?.Indices.Length ?? 0 );
		if ( vertexCount == 0 || indexCount == 0 ) return null;

		var groups = new Dictionary<WorldRenderCell, Dictionary<WorldMaterialKey, MapMeshGroup>>();
		var translucentGroups = new Dictionary<WorldRenderCell, Dictionary<WorldMaterialKey, MapMeshGroup>>();
		var decalGroups = new Dictionary<WorldRenderCell, Dictionary<WorldMaterialKey, MapMeshGroup>>();
		var godrayGroups = new Dictionary<WorldRenderCell, Dictionary<WorldMaterialKey, MapMeshGroup>>();
		var waterGroups = new Dictionary<WorldRenderCell, Dictionary<WorldMaterialKey, MapMeshGroup>>();
		var lightmaps = Titanfall2StreamingSettings.UseBspLightmaps
			? CreateLightmapTextures( parsed )
			: null;
		var worldMaterials = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
		var decalMaterials = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
		var godrayMaterials = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
		var cutoutMaterials = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
		var translucentMaterials = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
		var waterMaterials = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
		var shadowOnlyGroups = new Dictionary<WorldRenderCell, Dictionary<WorldMaterialKey, MapMeshGroup>>();
		var shadowOnlyMaterials = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
		var shadowOnlyMeshCount = 0;
		var collisionPositions = new List<Vector3>( vertexCount );
		var collisionIndices = new List<int>( indexCount );
		var skippedToolMeshes = 0;
		var skippedGodrayMeshes = 0;
		var decalMeshCount = 0;
		var godrayMeshCount = 0;
		var visualEffectCollisionMeshCount = 0;
		foreach ( var sourceMesh in parsed.World.Meshes )
		{
			if ( sourceMesh is null || sourceMesh.Vertices.Length == 0 || sourceMesh.Indices.Length < 3 ) continue;
			var materialName = NormalizeMaterialName( sourceMesh.MaterialName );
			// tools/toolsblocklight is an invisible light blocker in the original game. Drawing it
			// showed green "LIGHT BLOCK" walls; it now only enters the shadow pass (or nothing,
			// when map shadows are off), so it still keeps sunlight out of the spaces it seals.
			var isShadowOnly = Titanfall2CollisionFilter.IsNonSolidToolName( materialName );
			if ( ShouldSkipWorldMaterial( materialName ) || (isShadowOnly && !Titanfall2StreamingSettings.MapShadows) )
			{
				skippedToolMeshes++;
				continue;
			}
			var isGodray = MaterialLoader.IsGodrayMaterial( materialName );
			if ( isGodray && !Titanfall2StreamingSettings.Godrays )
			{
				// Additive light-shaft cards: skipping them removes the bright cards that can
				// flash while the camera moves, without touching opaque world geometry.
				skippedGodrayMeshes++;
				continue;
			}
			var materialMetadata = GetMaterialMetadata( materialName );
			var isDecal = !isGodray && materialMetadata.IsDecal;
			var allowsFallbackCollision = Titanfall2CollisionFilter.ShouldIncludeMaterial( materialName, materialMetadata );
			var includeCollision = createFallbackCollision && allowsFallbackCollision;
			if ( !isDecal && materialMetadata.Mode == Titanfall2MaterialMode.Cutout ) cutoutMaterials.Add( materialName );
			if ( !isDecal && materialMetadata.IsTranslucent ) translucentMaterials.Add( materialName );
			if ( !isDecal && materialMetadata.IsWater ) waterMaterials.Add( materialName );
			// Aggregate scene objects do not supply framebuffer copies for water.
			// Keep refractive surfaces on a dedicated translucent renderer.
			// Native aggregate objects enter the opaque/prepass lists. Transparent
			// grids and screens need ordinary renderers to reach the translucent
			// pass with correct depth testing and sorting.
			var targetGroups = isShadowOnly ? shadowOnlyGroups : isGodray ? godrayGroups : isDecal ? decalGroups
				: materialMetadata.IsWater ? waterGroups : materialMetadata.IsTranslucent ? translucentGroups : groups;
			var targetMaterials = isShadowOnly ? shadowOnlyMaterials : isGodray ? godrayMaterials : isDecal ? decalMaterials : worldMaterials;
			if ( isShadowOnly ) shadowOnlyMeshCount++;
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
					WorldRenderCellSize );
				// Native s&box lighting does not need one material/draw group per
				// Respawn lightmap page. Merge those pages when the experimental
				// lightmap path is disabled to reduce both memory and draw calls.
				var lightmapPage = Titanfall2StreamingSettings.UseBspLightmaps && !isGodray && !isDecal && !isShadowOnly
					? sourceMesh.LightmapPage
					: -1;
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

		if ( groups.Count == 0 && translucentGroups.Count == 0 && waterGroups.Count == 0 ) return null;
		var worldGroupCount = groups.Values.Sum( static cell => cell.Count );
		var decalGroupCount = decalGroups.Values.Sum( static cell => cell.Count );
		var godrayGroupCount = godrayGroups.Values.Sum( static cell => cell.Count );
		Log.Info( $"Titanfall 2 map geometry grouped: {Path} ({worldMaterials.Count} world materials across "
			+ $"{groups.Count} spatial cells/{worldGroupCount} local meshes including "
			+ $"{cutoutMaterials.Count} cutout, {translucentMaterials.Count} translucent and {waterMaterials.Count} water; "
			+ $"{decalMaterials.Count} decal materials across {decalGroups.Count} cells/{decalGroupCount} local meshes, "
			+ $"{godrayMaterials.Count} Godray materials across {godrayGroups.Count} cells/{godrayGroupCount} local meshes, "
			+ $"{vertexCount} source vertices, {indexCount / 3} source triangles, {skippedToolMeshes} invisible tool meshes skipped, "
			+ $"{(skippedGodrayMeshes > 0 ? $"{skippedGodrayMeshes} godray meshes skipped by switch" : "godrays enabled")}, "
			+ $"{decalMeshCount} decal, {godrayMeshCount} Godray and {visualEffectCollisionMeshCount} "
			+ (createFallbackCollision
				? "visual FX meshes excluded from fallback collision)"
				: "render collision disabled; authoritative rBSP contents used)") );
		var renderTimer = System.Diagnostics.Stopwatch.StartNew();
		// Match consumption order. A queued water material held until the end must
		// not occupy every look-ahead slot while opaque materials load synchronously.
		var worldOrder = worldMaterials.Except( waterMaterials, StringComparer.OrdinalIgnoreCase )
			.Except( translucentMaterials, StringComparer.OrdinalIgnoreCase ).OrderBy( static name => name, StringComparer.OrdinalIgnoreCase );
		using var prefetch = new Titanfall2MaterialPrefetch( Host,
			worldOrder.Concat( EnumerateChunkMaterials( translucentGroups ) ).Concat( EnumerateChunkMaterials( decalGroups ) )
				.Concat( EnumerateChunkMaterials( godrayGroups ) ).Concat( EnumerateChunkMaterials( waterGroups ) )
				.Select( name => Host.TryGetMaterialPath( name, out var registered ) ? registered : name ) );
		_materialPrefetch = prefetch;
		var worldAggregates = Titanfall2WorldAggregator.Build(
			groups, parsed.MapName, "world", Path, Log,
			( materialKey, meshIndex ) => CreateWorldLightmappedMaterial(
				materialKey,
				meshIndex,
				lightmaps ),
			out var worldRenderMeshCount );
		// Per-phase timings show whether world geometry, decals or Godrays moved the load time.
		var worldPhase = renderTimer.Elapsed;
		var translucentChunks = BuildRenderChunks( translucentGroups, parsed.MapName, "translucent",
			( materialKey, meshIndex ) => LoadWorldMaterial( materialKey.MaterialName )
				?? MaterialLoader.CreateRuntimeMaterial( $"{Path}/translucent_fallback_{meshIndex}", GetMaterialMetadata( materialKey.MaterialName ) ),
			out var translucentRenderMeshCount );
		var translucentPhase = renderTimer.Elapsed - worldPhase;
		var decalChunks = BuildRenderChunks(
			decalGroups, parsed.MapName, "decals",
			( materialKey, meshIndex ) => LoadWorldMaterial( materialKey.MaterialName )
				?? MaterialLoader.CreateDecalMaterial( $"{Path}/decal_fallback_{meshIndex}", GetMaterialMetadata( materialKey.MaterialName ) ),
			out var decalRenderMeshCount );
		var decalPhase = renderTimer.Elapsed - worldPhase - translucentPhase;
		var godrayChunks = BuildRenderChunks(
			godrayGroups, parsed.MapName, "godrays",
			( materialKey, meshIndex ) => LoadWorldMaterial( materialKey.MaterialName )
				?? MaterialLoader.CreateGodrayMaterial( $"{Path}/godray_fallback_{meshIndex}", materialKey.MaterialName ),
			out var godrayRenderMeshCount );
		var godrayPhase = renderTimer.Elapsed - worldPhase - translucentPhase - decalPhase;
		var waterChunks = BuildRenderChunks( waterGroups, parsed.MapName, "water",
			( materialKey, meshIndex ) => LoadWorldMaterial( materialKey.MaterialName )
				?? MaterialLoader.CreateWaterMaterial( $"{Path}/water_fallback_{meshIndex}", GetMaterialMetadata( materialKey.MaterialName ) ),
			out var waterRenderMeshCount );
		var waterPhase = renderTimer.Elapsed - worldPhase - translucentPhase - decalPhase - godrayPhase;
		// Only depth reaches the shadow map, so a plain runtime material replaces the tool texture.
		var shadowOnlyChunks = BuildRenderChunks( shadowOnlyGroups, parsed.MapName, "shadow_only",
			( materialKey, meshIndex ) => MaterialLoader.CreateRuntimeMaterial( $"{Path}/shadow_only_{meshIndex}", GetMaterialMetadata( materialKey.MaterialName ) ),
			out var shadowOnlyRenderMeshCount );
		if ( worldAggregates.Count == 0 && translucentChunks.Count == 0 && waterChunks.Count == 0 ) return null;
		Log.Info( $"Titanfall 2 spatial render models created: {Path} "
			+ $"({worldAggregates.Count} world aggregates/{worldRenderMeshCount} meshes, "
			+ $"{translucentChunks.Count} translucent chunks/{translucentRenderMeshCount} meshes, "
			+ $"{decalChunks.Count} decal chunks/{decalRenderMeshCount} meshes, "
			+ $"{godrayChunks.Count} Godray chunks/{godrayRenderMeshCount} meshes; "
			+ $"{waterChunks.Count} water chunks/{waterRenderMeshCount} meshes, "
			+ $"{shadowOnlyChunks.Count} shadow-only light-blocker chunks/{shadowOnlyRenderMeshCount} meshes from {shadowOnlyMeshCount} source meshes; "
			+ $"world {worldPhase.TotalSeconds:0.00}s, translucent {translucentPhase.TotalSeconds:0.00}s, decals {decalPhase.TotalSeconds:0.00}s, "
			+ $"Godrays {godrayPhase.TotalSeconds:0.00}s, "
			+ $"water {waterPhase.TotalSeconds:0.00}s, "
			+ $"{renderTimer.Elapsed.TotalSeconds:0.00}s)" );

		Model collisionModel = null;
		if ( collisionPositions.Count > 0 && collisionIndices.Count > 0 )
		{
			var collisionBuilder = Model.Builder.WithName( $"titanfall2/map/{parsed.MapName}_collision" );
			// List overloads upload straight from the existing lists; ToArray() here copied 37MB of
			// collision data for nothing.
			collisionBuilder.AddCollisionMesh( collisionPositions, collisionIndices );
			collisionBuilder.AddTraceMesh( collisionPositions, collisionIndices );
			collisionModel = collisionBuilder.Create();
			Log.Info( $"Titanfall 2 fallback render collision submitted: {Path} ({collisionIndices.Count / 3} triangles)" );
		}
		return new WorldBuildResult( translucentChunks, decalChunks, godrayChunks, waterChunks, worldAggregates, collisionModel,
			lightmaps?.OwnedResources ?? Array.Empty<IDisposable>(), shadowOnlyChunks );
	}

	static IEnumerable<string> EnumerateChunkMaterials( Dictionary<WorldRenderCell, Dictionary<WorldMaterialKey, MapMeshGroup>> chunks ) =>
		chunks.OrderBy( pair => pair.Key.X ).ThenBy( pair => pair.Key.Y ).ThenBy( pair => pair.Key.Z )
			.SelectMany( pair => pair.Value.Keys.OrderBy( key => key.MaterialName, StringComparer.OrdinalIgnoreCase )
				.ThenBy( key => key.LightmapPage ) ).Select( key => key.MaterialName );

	internal static TitanfallMapVertex CreateWorldVertex( Titanfall2BspReader.WorldVertex sourceVertex, bool isDecal )
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
		if ( !Titanfall2StreamingSettings.UseBspLightmaps )
			return sourceMaterial;
		var metadata = GetMaterialMetadata( key.MaterialName );
		if ( lightmaps is null || key.LightmapPage < 0 || key.LightmapPage >= lightmaps.Pages.Count
			|| metadata.Mode != Titanfall2MaterialMode.Opaque || metadata.IsUnlit || metadata.IsWater )
			return sourceMaterial;

		var page = lightmaps.Pages[key.LightmapPage];
		if ( page.SkyA is null || !page.SkyA.IsValid || page.SkyB is null || !page.SkyB.IsValid )
			return sourceMaterial;

		return CreateLightmappedMaterial( $"{Path}/world_lightmapped_{meshIndex}_{key.LightmapPage}", sourceMaterial, page, lightmaps.SkyColor );
	}

	internal static Material CreateLightmappedMaterial( string name, Material sourceMaterial, RuntimeLightmapPage page, Color skyColor )
	{
		var material = Material.Create( name, "shaders/titanfall2_world_lightmapped.shader" );
		CopyWorldMaterialParameters( sourceMaterial, material );
		material.Set( "g_tLightmapSkyA", page.SkyA );
		material.Set( "g_tLightmapSkyB", page.SkyB );
		if ( page.RealTimeA is not null ) material.Set( "g_tLightmapRealTimeA", page.RealTimeA );
		if ( page.RealTimeB is not null ) material.Set( "g_tLightmapRealTimeB", page.RealTimeB );
		if ( page.RealTimeC is not null ) material.Set( "g_tLightmapRealTimeC", page.RealTimeC );
		if ( page.SunChannels is not null ) material.Set( "g_tLightmapSunChannels", page.SunChannels );
		material.Set( "g_flT2HasSunLightmap", page.SunChannels is not null ? 1f : 0f );
		material.Set( "g_flT2LightmapIntensity", 1f );
		material.Set( "g_vT2LightmapSkyColor", new Vector3( skyColor.r, skyColor.g, skyColor.b ) );
		material.Set( "g_flT2RealTimeLightContribution", 0f );
		return material;
	}

	static void CopyWorldMaterialParameters( Material source, Material target )
	{
		string[] textureNames =
		[
			"g_tAlbedo", "g_tNormal", "g_tGloss", "g_tSpecular", "g_tAO", "g_tCavity", "g_tOpacity",
			"g_tEmissive", "g_tEmissiveMultiply", "g_tDetail", "g_tDistortion", "g_tEnvironment",
			"g_tAlbedo2", "g_tNormal2", "g_tGloss2", "g_tSpecular2", "g_tLayerBlend"
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
			"g_flT2MaterialOpacity", "g_flT2AlphaTestReference", "g_flUseVertexColor",
			"g_flT2Layered", "g_flT2LayerBlendRamp", "g_flHasNormalMap", "g_flHasNormalMap2",
			"g_flT2SpecularWorkflow",
			"g_flT2HasCavity", "g_flT2CavityUvChannel",
			"g_flT2HasEmissiveMultiply", "g_flT2EmissiveMultiplyUvChannel", "g_vT2AuxUvChannels",
			"g_vT2SurfaceUvChannels", "g_vT2SurfaceUvChannels2", "g_flT2LayerBlendUvChannel",
			"g_vT2Uv2RotScale", "g_vT2Uv2Translate", "g_vT2Uv3RotScale", "g_vT2Uv3Translate",
			"g_flT2NormalSlope", "g_flT2NormalSlope2", "g_flT2NormalTwoChannel", "g_flT2NormalTwoChannel2"
		];
		foreach ( var name in vectorNames )
			target.Set( name, source?.GetVector4( name ) ?? default );
	}

	RuntimeLightmaps CreateLightmapTextures( Titanfall2BspReader.ParsedBsp parsed )
	{
		var source = parsed.Lightmaps;
		if ( source is null || !source.IsValid ) return null;
		var pages = new List<RuntimeLightmapPage>( source.Pages.Count );
		foreach ( var page in source.Pages )
		{
			pages.Add( CreateLightmapPage( page, source.RealTimeLightPages, parsed.WorldLights ) );
		}
		return new RuntimeLightmaps( pages, ReadBakedSkyColor( parsed ) );
	}

	internal static RuntimeLightmapPage CreateLightmapPage( Titanfall2BspReader.BspLightmapPage page,
		IReadOnlyList<short> lightPages, IReadOnlyList<Titanfall2WorldLight> worldLights )
	{
		static Texture Create( byte[] data, int width, int height )
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

		var sunChannels = Titanfall2WorldLight.CreateSunChannels( page.RealTimeA, lightPages, worldLights );
		var sunTexture = sunChannels.Length == page.Width * page.Height
			? Texture.Create( page.Width, page.Height, ImageFormat.I8 ).WithData( sunChannels ).Finish() : null;
		return new RuntimeLightmapPage(
			Create( page.SkyA, page.Width, page.Height ),
			Create( page.SkyB, page.Width, page.Height ),
			Create( page.RealTimeA, page.Width, page.Height ),
			Create( page.RealTimeB, page.Width, page.Height ),
			Create( page.RealTimeC, Math.Max( 1, page.Width / 2 ), Math.Max( 1, page.Height / 2 ) ), sunTexture );
	}

	Color ReadBakedSkyColor( Titanfall2BspReader.ParsedBsp parsed )
	{
		var entities = ReadEntities( _environmentEntitySource );
		return ReadBakedSkyColor( parsed, entities.Count == 0 ? parsed.Entities : entities );
	}

	internal static Color ReadBakedSkyColor( Titanfall2BspReader.ParsedBsp parsed, IReadOnlyList<Dictionary<string, string>> entities )
	{
		var lightValues = Titanfall2LightEnvironment.Default( entities );
		var compiled = SelectBakedEnvironmentLight( parsed, lightValues, Titanfall2WorldLight.SkyAmbientType );
		if ( compiled is not null ) return new Color( compiled.Intensity.X, compiled.Intensity.Y, compiled.Intensity.Z );
		var color = TryParseSourceLightColor( lightValues?.GetValueOrDefault( "_ambient" ), 0.30f, out var authored )
			? authored : new Color( 0.22f, 0.28f, 0.36f );
		return color * AmbientScale;
	}

	Material LoadWorldMaterial( string materialName )
	{
		if ( string.IsNullOrWhiteSpace( materialName ) || !Host.TryGetMaterialPath( materialName, out var registeredPath ) )
			return null;
		_materialPrefetch?.Wait( registeredPath );
		try
		{
			var material = Material.Load( $"mount://{Host.Ident}/{registeredPath}.vmat" );
			return material is not null && material.IsValid ? material : null;
		}
		finally { _materialPrefetch?.Consumed( registeredPath ); }
	}

	Titanfall2MaterialMetadata GetMaterialMetadata( string materialName )
	{
		if ( Host.TryGetMaterialMetadata( materialName, out var metadata ) ) return metadata;
		return MaterialLoader.InferMetadata( materialName );
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

	static bool ShouldSkipWorldMaterial( string materialName )
	{
		if ( string.IsNullOrWhiteSpace( materialName ) ) return false;
		return materialName.Contains( "toolsskybox", StringComparison.OrdinalIgnoreCase )
			|| materialName.Contains( "toolsnodraw", StringComparison.OrdinalIgnoreCase )
			|| materialName.Contains( "toolsinvisible", StringComparison.OrdinalIgnoreCase );
	}

	static float DistanceSquared( System.Numerics.Vector3 source, Vector3 target )
	{
		var x = source.X - target.x;
		var y = source.Y - target.y;
		var z = source.Z - target.z;
		return x * x + y * y + z * z;
	}
}
