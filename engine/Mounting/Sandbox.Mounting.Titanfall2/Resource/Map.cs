using System.Runtime.InteropServices;
using Titanfall2;

/// <summary>Builds a runnable scene directly from Titanfall 2 rBSP geometry.</summary>
class MapLoader(
	ITitanfall2AssetSource source,
	ITitanfall2AssetSource spawnEntitySource = null,
	ITitanfall2AssetSource environmentEntitySource = null,
	ITitanfall2AssetSource particleEntitySource = null ) : SceneLoader<Titanfall2Mount>
{
	const float DecalNormalOffset = 0.25f;
	readonly ITitanfall2AssetSource _source = source;
	readonly ITitanfall2AssetSource _spawnEntitySource = spawnEntitySource;
	readonly ITitanfall2AssetSource _environmentEntitySource = environmentEntitySource;
	readonly ITitanfall2AssetSource _particleEntitySource = particleEntitySource;

	[StructLayout( LayoutKind.Sequential )]
	struct TitanfallMapVertex
	{
		[VertexLayout.Position] public Vector3 Position;
		[VertexLayout.Normal] public Vector3 Normal;
		[VertexLayout.TexCoord] public Vector2 TexCoord;
		[VertexLayout.Color] public Color32 Color;
	}

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

		var collisionLumps = Titanfall2StreamingSettings.DeferredNavMesh
			? ReadCollisionLumpOverrides()
			: new Dictionary<int, byte[]>();
		if ( !Titanfall2BspReader.TryRead( bytes, System.IO.Path.GetFileNameWithoutExtension( _source.Description ), collisionLumps,
			Titanfall2StreamingSettings.DeferredNavMesh, out var parsed, out var parseError ) )
		{
			Log.Warning( $"Failed to parse Titanfall 2 BSP '{Path}': {parseError}" );
			return;
		}
		var collisionSummary = Titanfall2StreamingSettings.DeferredNavMesh
			? $"{parsed.WorldCollision.BrushCount} brushes, {parsed.WorldCollision.TricollCount} tricolls, {parsed.WorldCollision.TriangleCount} collision triangles"
			: "NavMesh collision skipped";
		Log.Info( $"Titanfall 2 map parsed: {Path} ({parsed.World.Meshes.Count} world meshes, {collisionSummary}, "
			+ $"{parsed.StaticProps.Count} static props, {timer.Elapsed.TotalSeconds:0.00}s)" );

		var worldBuild = BuildWorldModel( parsed );
		if ( worldBuild is null )
		{
			Log.Warning( $"Titanfall 2 BSP contains no usable world geometry: {Path}" );
			return;
		}

		var world = new GameObject( true, "worldspawn" );
		world.IsStatic = true;
		world.AddComponent<Titanfall2MaterialAnimator>();
		foreach ( var chunk in worldBuild.WorldChunks )
		{
			var chunkObject = new GameObject( world, true, $"world_{chunk.Cell}" );
			chunkObject.IsStatic = true;
			chunkObject.AddComponent<ModelRenderer>().Model = chunk.Model;
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
		if ( worldBuild.CollisionModel is not null )
		{
			var fallbackWorldCollider = world.AddComponent<ModelCollider>();
			fallbackWorldCollider.Model = worldBuild.CollisionModel;
			fallbackWorldCollider.Static = true;
		}

		if ( Titanfall2StreamingSettings.DeferredNavMesh && BuildWorldCollisionModel( parsed ) is { } collisionModel )
		{
			var collisionObject = new GameObject( true, "titanfall2_world_collision" );
			collisionObject.Tags.Add( Titanfall2DeferredNavMeshBuilder.NavMeshBodyTag );
			var worldCollider = collisionObject.AddComponent<ModelCollider>();
			worldCollider.Model = collisionModel;
			worldCollider.Static = true;
		}
		else if ( Titanfall2StreamingSettings.DeferredNavMesh )
		{
			Log.Warning( $"Titanfall 2 BSP has no usable brush/tricoll world collision: {Path}. "
				+ "Render geometry will not be submitted to physics or NavMesh as a fallback." );
		}
		world.AddComponent<Titanfall2DeferredNavMeshBuilder>()
			.Configure( Titanfall2StreamingSettings.DeferredNavMesh, Titanfall2StreamingSettings.NavMeshStartDelaySeconds );
		CreateDefaultLighting();
		var cubemapProbeCount = CreateCubemapProbes( parsed );
		var skyboxModelCount = CreateSkybox( parsed.StaticProps );
		Log.Info( $"Titanfall 2 map world created: {Path} ({timer.Elapsed.TotalSeconds:0.00}s)" );

		var spawnCount = CreateSpawnPoints( parsed, out var streamingAnchor );
		var particleCount = CreateParticleSystems( streamingAnchor );
		var worldProps = parsed.StaticProps.Where( static prop => !IsVistaModelPath( prop.ModelPath ) ).ToArray();
		var streamerObject = new GameObject( true, "titanfall2_static_prop_streamer" );
		streamerObject.AddComponent<Titanfall2StaticPropStreamer>()
			.Configure( Host.Ident, Path, streamingAnchor, worldProps );
		Log.Info( $"Titanfall 2 map loaded: {Path} ({parsed.World.Meshes.Count} world meshes, "
			+ $"{worldProps.Length} world props registered for loading-stage preload, "
			+ $"{skyboxModelCount} vista models moved to the 3D skybox, {cubemapProbeCount} environment probes, "
			+ $"{particleCount} particle systems queued)" );
		Log.Info( $"Titanfall 2 map spawn points created: {Path} ({spawnCount})" );
	}

	WorldBuildResult BuildWorldModel( Titanfall2BspReader.ParsedBsp parsed )
	{
		var vertexCount = parsed.World.Meshes.Sum( static mesh => mesh?.Vertices.Length ?? 0 );
		var indexCount = parsed.World.Meshes.Sum( static mesh => mesh?.Indices.Length ?? 0 );
		if ( vertexCount == 0 || indexCount == 0 ) return null;

		var groups = new Dictionary<WorldRenderCell, Dictionary<string, MapMeshGroup>>();
		var decalGroups = new Dictionary<WorldRenderCell, Dictionary<string, MapMeshGroup>>();
		var godrayGroups = new Dictionary<WorldRenderCell, Dictionary<string, MapMeshGroup>>();
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
				var group = GetRenderGroup( targetGroups, cell, materialName );
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
				if ( !isGodray && !isDecal )
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
			+ $"{decalMeshCount} decal and {godrayMeshCount} Godray meshes excluded from collision)" );
		var renderTimer = System.Diagnostics.Stopwatch.StartNew();
		var worldChunks = BuildRenderChunks(
			groups, parsed.MapName, "world",
			(materialName, meshIndex) => LoadWorldMaterial( materialName )
				?? MaterialLoader.CreateRuntimeMaterial( $"{Path}/world_fallback_{meshIndex}", GetMaterialMetadata( materialName ) ),
			out var worldRenderMeshCount );
		var decalChunks = BuildRenderChunks(
			decalGroups, parsed.MapName, "decals",
			(materialName, meshIndex) => LoadWorldMaterial( materialName )
				?? MaterialLoader.CreateDecalMaterial( $"{Path}/decal_fallback_{meshIndex}", GetMaterialMetadata( materialName ) ),
			out var decalRenderMeshCount );
		var godrayChunks = BuildRenderChunks(
			godrayGroups, parsed.MapName, "godrays",
			(materialName, meshIndex) => LoadWorldMaterial( materialName )
				?? MaterialLoader.CreateGodrayMaterial( $"{Path}/godray_fallback_{meshIndex}", materialName ),
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
		return new WorldBuildResult( worldChunks, decalChunks, godrayChunks, collisionModel );
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
			Color = new Color32( sourceVertex.Color.R, sourceVertex.Color.G, sourceVertex.Color.B, sourceVertex.Color.A )
		};
	}

	static MapMeshGroup GetRenderGroup(
		Dictionary<WorldRenderCell, Dictionary<string, MapMeshGroup>> chunks,
		WorldRenderCell cell,
		string materialName )
	{
		if ( !chunks.TryGetValue( cell, out var materials ) )
		{
			materials = new Dictionary<string, MapMeshGroup>( StringComparer.OrdinalIgnoreCase );
			chunks.Add( cell, materials );
		}
		if ( materials.TryGetValue( materialName, out var group ) ) return group;
		group = new MapMeshGroup();
		materials.Add( materialName, group );
		return group;
	}

	List<WorldRenderChunk> BuildRenderChunks(
		Dictionary<WorldRenderCell, Dictionary<string, MapMeshGroup>> chunks,
		string mapName,
		string category,
		Func<string, int, Material> materialFactory,
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
			foreach ( var (materialName, group) in materials.OrderBy( static pair => pair.Key, StringComparer.OrdinalIgnoreCase ) )
			{
				if ( group.Vertices.Count == 0 || group.Indices.Count == 0 ) continue;
				var material = materialFactory( materialName, totalMeshCount );
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

		var candidates = new List<SkyboxCandidate>();
		foreach ( var values in ReadEntities( _environmentEntitySource ) )
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
		foreach ( var values in ReadEntities( _particleEntitySource ) )
		{
			if ( !values.TryGetValue( "classname", out var className )
				|| !className.Equals( "info_particle_system", StringComparison.OrdinalIgnoreCase ) ) continue;
			if ( values.TryGetValue( "start_active", out var startActive )
				&& (startActive.Equals( "0", StringComparison.OrdinalIgnoreCase )
					|| startActive.Equals( "false", StringComparison.OrdinalIgnoreCase )) ) continue;
			if ( !values.TryGetValue( "effect_name", out var effectName ) || string.IsNullOrWhiteSpace( effectName ) ) continue;
			if ( !values.TryGetValue( "origin", out var originText ) || !TryParseVector3( originText, out var origin ) ) continue;
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
		if ( entities.Count == 0 ) return 0;
		var streamerObject = new GameObject( true, "titanfall2_particle_streamer" );
		streamerObject.AddComponent<Titanfall2ParticleStreamer>()
			.Configure( Host.Ident, Path, streamingAnchor, entities );
		return entities.Count;
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
			|| materialName.Contains( "toolsinvisible", StringComparison.OrdinalIgnoreCase );
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

	static void CreateDefaultLighting()
	{
		var gameObject = new GameObject( true, "titanfall2_environment_light" );
		gameObject.WorldRotation = new Angles( 50f, -35f, 0f );
		var light = gameObject.AddComponent<DirectionalLight>();
		light.LightColor = new Color( 1.0f, 0.95f, 0.86f );
		light.SkyColor = new Color( 0.38f, 0.48f, 0.62f );
	}

	static Vector3 ToSandbox( System.Numerics.Vector3 value ) => new( value.X, value.Y, value.Z );
	static Vector2 ToSandbox( System.Numerics.Vector2 value ) => new( value.X, value.Y );

	sealed class MapMeshGroup
	{
		public List<TitanfallMapVertex> Vertices { get; } = new();
		public List<int> Indices { get; } = new();
		public BBox Bounds = new() { Mins = float.MaxValue, Maxs = float.MinValue };
	}

	readonly record struct WorldRenderCell( int X, int Y, int Z )
	{
		public static WorldRenderCell FromTriangle( Vector3 first, Vector3 second, Vector3 third, float size )
		{
			var center = (first + second + third) / 3f;
			return new WorldRenderCell(
				(int)MathF.Floor( center.x / size ),
				(int)MathF.Floor( center.y / size ),
				(int)MathF.Floor( center.z / size ) );
		}

		public override string ToString() => $"{X}_{Y}_{Z}";
	}

	sealed record WorldRenderChunk( WorldRenderCell Cell, Model Model );

	readonly record struct CollisionCell( int X, int Y );

	sealed class CollisionChunk
	{
		readonly Dictionary<int, int> _sourceToLocal = new();
		public List<Vector3> Vertices { get; } = new();
		public List<int> Indices { get; } = new();

		public int RemapVertex( int sourceIndex, IReadOnlyList<System.Numerics.Vector3> sourceVertices )
		{
			if ( _sourceToLocal.TryGetValue( sourceIndex, out var localIndex ) ) return localIndex;
			localIndex = Vertices.Count;
			_sourceToLocal.Add( sourceIndex, localIndex );
			Vertices.Add( ToSandbox( sourceVertices[sourceIndex] ) );
			return localIndex;
		}
	}

	sealed record SkyboxCandidate(
		string Name,
		Vector3 Origin,
		Angles Rotation,
		float Scale,
		Titanfall2BspReader.StaticPropInstance[] Models );

	readonly record struct VistaOriginKey( int X, int Y, int Z )
	{
		const float Quantization = 8f;

		public static VistaOriginKey FromPosition( System.Numerics.Vector3 position ) => new(
			(int)MathF.Round( position.X / Quantization ),
			(int)MathF.Round( position.Y / Quantization ),
			(int)MathF.Round( position.Z / Quantization ) );
	}

	sealed record WorldBuildResult(
		IReadOnlyList<WorldRenderChunk> WorldChunks,
		IReadOnlyList<WorldRenderChunk> DecalChunks,
		IReadOnlyList<WorldRenderChunk> GodrayChunks,
		Model CollisionModel );
}
