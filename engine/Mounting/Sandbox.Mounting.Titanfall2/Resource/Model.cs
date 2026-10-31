using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Titanfall2.Formats;

/// <summary>Loads an embedded Titanfall 2 MDL53/RMDL as a runtime model.</summary>
partial class ModelLoader( ITitanfall2AssetSource source, bool staticInstance = false, int staticLod = 0, bool allLods = false, Titanfall2Mount variantHost = null, string variantPath = null, int skin = 0, int body = 0, bool collisionOnly = false ) : ResourceLoader<Titanfall2Mount>
{
	internal const string StaticInstanceSuffix = ".t2static";
	// The native path builds one Model that already holds every decoded LOD, so it needs a
	// resource path of its own. Sharing the legacy ".t2static" name would make the two
	// loaders fight over a single resource entry, and a runtime A/B between them would hand
	// out whichever loader happened to be registered, not the one the mode selected.
	internal const string StaticInstanceAllLodsSuffix = StaticInstanceSuffix + ".all";
	internal const int StaticInstanceLodCount = Titanfall2Mdl53Constants.MaxLods;
	// StaticInstanceLodCount is the MDL format's ceiling; this is how many bands the streamer
	// actually uses. Legacy has exactly four distance bands (the nearest one plus
	// PropLod1/2/3Distance) and neither SelectLod nor SelectLodByDistance ever returns a level
	// above 3, so only these four are decoded and only these four carry their own geometry.
	// Levels 4..7 register an alias of the deepest resolved band instead of being left empty: an
	// empty level could be selected by the engine's own distance calculation (the props that pass
	// no explicit level) and would then draw nothing at all. See BuildStaticInstanceAllLodsModel.
	internal const int StaticInstanceBandCount = 4;
	const float DecalNormalOffset = 0.05f;
	sealed class StaticInstanceEligibilityValue( bool eligible )
	{
		internal bool Eligible { get; } = eligible;
	}

	// Runtime Model resources are owned by the engine resource system. A regular
	// dictionary here kept every generated .t2static model alive after its scene
	// was destroyed, so returning to the menu could not reclaim their GPU data.
	static readonly ConditionalWeakTable<Model, StaticInstanceEligibilityValue> StaticInstanceEligibility = new();
	static long _collisionModelCount;
	static long _vphyModelCount;
	static long _hitboxModelCount;
	static long _renderMeshModelCount;
	static long _simplifiedModelCount;
	static long _noCollisionModelCount;
	static long _fallbackTriangleCount;
	readonly ITitanfall2AssetSource _source = source;
	readonly bool _staticInstance = staticInstance;
	readonly int _staticLod = Math.Clamp( staticLod, 0, StaticInstanceLodCount - 1 );
	readonly bool _allLods = allLods;
	bool ImportAllBodyGroups => variantHost is null && Titanfall2StreamingSettings.ImportAllBodyGroupChoices;
	Titanfall2Mount ImportHost => variantHost ?? Host;
	string ImportPath => variantPath ?? Path;
	readonly Dictionary<(int Skin, int Body, bool Collision), WeakReference<Model>> _variants = new();

	internal Model GetVariant( int requestedSkin, int requestedBody, bool onlyCollision = false )
	{
		ArgumentOutOfRangeException.ThrowIfNegative( requestedSkin );
		ArgumentOutOfRangeException.ThrowIfNegative( requestedBody );
		var key = (requestedSkin, requestedBody, onlyCollision);
		lock ( _variants )
		{
			if ( _variants.TryGetValue( key, out var weak ) && weak.TryGetTarget( out var cached ) && IsUsableModel( cached ) ) return cached;
			var loader = new ModelLoader( _source, _staticInstance && !onlyCollision, _staticLod, _allLods,
				ImportHost, $"{ImportPath}/skin{requestedSkin}_body{requestedBody}{(onlyCollision ? "_physics" : "")}", requestedSkin, requestedBody, onlyCollision );
			var model = loader.Load() as Model;
			if ( IsUsableModel( model ) ) _variants[key] = new WeakReference<Model>( model );
			foreach ( var expired in _variants.Where( pair => !pair.Value.TryGetTarget( out _ ) ).Select( pair => pair.Key ).ToArray() ) _variants.Remove( expired );
			return model;
		}
	}

	[StructLayout( LayoutKind.Sequential )]
	struct TitanfallVertex
	{
		[VertexLayout.Position] public Vector3 Position;
		[VertexLayout.Normal] public Vector3 Normal;
		[VertexLayout.TexCoord] public Vector2 TexCoord;
		[VertexLayout.Color] public Color32 Color;
	}

	[StructLayout( LayoutKind.Sequential )]
	struct TitanfallSkinnedVertex
	{
		[VertexLayout.Position] public Vector3 Position;
		[VertexLayout.Normal] public Vector3 Normal;
		[VertexLayout.TexCoord] public Vector2 TexCoord;
		[VertexLayout.Color] public Color32 Color;
		[VertexLayout.BlendIndices] public Color32 BlendIndices;
		[VertexLayout.BlendWeight] public Color32 BlendWeights;
	}

	sealed class SkeletonData
	{
		public string[] Names { get; init; } = Array.Empty<string>();
		public Transform[] LocalTransforms { get; init; } = Array.Empty<Transform>();
		public Transform[] ModelTransforms { get; init; } = Array.Empty<Transform>();
	}

	sealed class AnimationSourceData
	{
		public string Description { get; init; }
		public byte[] Data { get; init; }
		public Titanfall2Mdl53Reader.ParsedModel Model { get; init; }
	}

	sealed class CollisionHullData
	{
		public int SolidIndex { get; init; } = -1;
		public int BoneIndex { get; init; } = -1;
		public Vector3[] Points { get; init; } = Array.Empty<Vector3>();
		public int[] Indices { get; init; } = Array.Empty<int>();
	}

	sealed class RagdollBodyData
	{
		public int SolidIndex { get; init; }
		public int BoneIndex { get; init; }
		public float Mass { get; init; }
		public CollisionHullData[] Hulls { get; init; } = Array.Empty<CollisionHullData>();
	}

	protected override object Load()
	{
		var cacheIdentity = _staticInstance && !collisionOnly && Titanfall2StreamingSettings.ProcessedCache
			? $"{ImportHost.ContentFingerprint}|{_source.CacheIdentity}|static:{_allLods}:{_staticLod}:{skin}:{body}:{ImportAllBodyGroups}"
			: null;
		if ( cacheIdentity is not null && TryReadStaticCache( cacheIdentity, out var cachedLevels ) )
			return _allLods ? BuildStaticInstanceAllLodsModel( cachedLevels ) : BuildStaticInstanceModel( cachedLevels[0] );
		if ( !_source.TryReadAllBytes( out var data, out var error ) )
		{
			Titanfall2Log.Warning( $"Failed to read Titanfall 2 model '{ImportPath}': {error}" );
			return null;
		}

		try
		{
			var parsed = Titanfall2Mdl53Reader.Parse( data );
			if ( _staticInstance && _allLods )
			{
				var levels = Enumerable.Range( 0, StaticInstanceBandCount ).Select( level => PrepareStaticGeometry(
					Titanfall2MeshExtractor.ExtractLod( parsed, level, ImportAllBodyGroups, body, skin ) ) ).ToArray();
				if ( cacheIdentity is not null ) WriteStaticCache( cacheIdentity, levels );
				return BuildStaticInstanceAllLodsModel( levels );
			}
			var lod = Titanfall2MeshExtractor.ExtractLod( parsed, _staticInstance ? _staticLod : 0, ImportAllBodyGroups, body, skin );
			if ( _staticInstance && lod.Meshes.Length == 0 && _staticLod != 0 )
			{
				// Some static props only ship LOD0.  Their virtual LOD resource still
				// resolves safely instead of becoming an error model at range.
				lod = Titanfall2MeshExtractor.ExtractLod( parsed, 0, ImportAllBodyGroups, body, skin );
			}
			if ( collisionOnly ) return BuildCollisionOnly( parsed, lod, data );
			if ( !_staticInstance ) return BuildModel( parsed, lod, data );
			var geometry = PrepareStaticGeometry( lod );
			if ( cacheIdentity is not null ) WriteStaticCache( cacheIdentity, new[] { geometry } );
			return BuildStaticInstanceModel( geometry );
		}
		catch ( Exception exception )
		{
			Titanfall2Log.Warning( $"Failed to convert Titanfall 2 model '{ImportPath}': {exception.Message}" );
			return null;
		}
	}

	// CPU-only source for baking infrequent rigid props into the world's spatial
	// material groups. Keep models with useful LODs instanced so baking cannot
	// silently replace their inexpensive distant geometry with permanent LOD0.
	internal bool TryReadAggregateGeometry( out Titanfall2MeshExtractor.ExtractedLod lod )
	{
		lod = null;
		if ( !_source.TryReadAllBytes( out var data, out _ ) ) return false;
		try
		{
			var parsed = Titanfall2Mdl53Reader.Parse( data );
			if ( parsed.Bones.Length > 1 ) return false;
			var first = Titanfall2MeshExtractor.ExtractLod( parsed, 0, ImportAllBodyGroups, body, skin );
			if ( first.VertexCount == 0 || first.VertexCount > 32768 ) return false;
			for ( var level = 1; level < StaticInstanceBandCount; level++ )
			{
				var lower = Titanfall2MeshExtractor.ExtractLod( parsed, level, ImportAllBodyGroups, body, skin );
				if ( lower.TriangleCount > 0 && lower.TriangleCount < first.TriangleCount ) return false;
			}
			lod = first;
			return true;
		}
		catch ( Exception ) { return false; }
	}

	internal static string GetStaticInstancePath( string sourceModelPath, int lod = 0 )
		=> lod <= 0
			? sourceModelPath + StaticInstanceSuffix
			: sourceModelPath + StaticInstanceSuffix + $".lod{Math.Clamp( lod, 1, StaticInstanceLodCount - 1 )}";

	/// <summary>
	/// Resource path of the single model that carries every decoded LOD. It is deliberately a
	/// different resource from <see cref="GetStaticInstancePath"/>, because the legacy per-LOD
	/// resources stay registered: the LOD mode is a runtime switch and both sides have to be
	/// loadable in the same session.
	/// </summary>
	internal static string GetStaticInstanceAllLodsPath( string sourceModelPath )
		=> sourceModelPath + StaticInstanceAllLodsSuffix;

	/// <summary>
	/// The native path's draw-time level choice. It uses the same thresholds the legacy streamer
	/// used as distance bands, but without the legacy hysteresis: that hysteresis only exists to
	/// keep the legacy swap queue from churning while a batch sits on a threshold, and the native
	/// path has no queue and no per-batch level state - the level is recomputed from the batch
	/// bounds on every draw. The result is capped at the last band the model registers.
	/// </summary>
	internal static int SelectLodByDistance( float distance )
	{
		if ( !Titanfall2StreamingSettings.PropLods ) return 0;
		var lod = distance > Titanfall2StreamingSettings.PropLod3Distance ? 3
			: distance > Titanfall2StreamingSettings.PropLod2Distance ? 2
			: distance > Titanfall2StreamingSettings.PropLod1Distance ? 1
			: 0;
		return Math.Clamp( lod, 0, StaticInstanceBandCount - 1 );
	}

	/// <summary>
	/// The engine's own LOD switch distance for one band, taken from the numbers the legacy
	/// streamer used as distance bands. A draw that does not pass an explicit level (an
	/// individual non-instanceable prop, a batch fallback object, an editor preview) therefore
	/// resolves to the level the legacy streamer would have swapped in for that distance.
	/// Bands 4..7 carry band 3's geometry as an alias, and they are written to band 3's distance
	/// rather than left at the builder's 50-unit defaults, because the engine walks the whole
	/// switch distance array before it clamps to the bands the model uses: a small threshold at a
	/// level nobody selected deliberately would change where that walk stops. See
	/// <see cref="BuildStaticInstanceAllLodsModel"/>.
	/// </summary>
	internal static float GetLodSwitchDistance( int lod ) => lod switch
	{
		1 => Titanfall2StreamingSettings.PropLod1Distance,
		2 => Titanfall2StreamingSettings.PropLod2Distance,
		>= 3 => Titanfall2StreamingSettings.PropLod3Distance,
		_ => 0f
	};

	internal static bool CanInstance( Model model )
		=> model.IsValid()
			&& StaticInstanceEligibility.TryGetValue( model, out var eligibility )
			&& eligibility.Eligible;

	// A failed resource is a distinct wrapper with IsError=true, not necessarily
	// the Model.Error singleton. Accepting it bypassed retries and full-model fallbacks.
	internal static bool IsUsableModel( Model model ) =>
		model is not null && model.IsValid && !model.IsError && model != Model.Error;

	/// <summary>
	/// Bounding radius of one extracted LOD, used to convert a wanted world-space LOD switch
	/// distance into the screen-space metric the engine compares switch distances against.
	/// Zero when the LOD carries no vertices; callers clamp it.
	/// </summary>
	static float ComputeStaticInstanceRadius( Titanfall2MeshExtractor.ExtractedLod lod )
	{
		var min = new System.Numerics.Vector3( float.MaxValue, float.MaxValue, float.MaxValue );
		var max = new System.Numerics.Vector3( float.MinValue, float.MinValue, float.MinValue );
		var any = false;

		foreach ( var mesh in lod.Meshes )
		{
			foreach ( var vertex in mesh.Vertices )
			{
				any = true;
				min = System.Numerics.Vector3.Min( min, vertex.Position );
				max = System.Numerics.Vector3.Max( max, vertex.Position );
			}
		}

		return any ? (max - min).Length() * 0.5f : 0f;
	}

	/// <summary>
	/// Builds the drawable meshes of one extracted LOD. Both assemblers below share it so the
	/// legacy per-LOD model and the native all-LOD model cannot drift apart: a mesh that draws
	/// in one has to draw in the other, or an A/B between them would compare different geometry.
	/// </summary>
	StaticLodGeometry PrepareStaticGeometry( Titanfall2MeshExtractor.ExtractedLod lod )
	{
		var meshes = new List<StaticMeshGeometry>();

		for ( var meshIndex = 0; meshIndex < lod.Meshes.Length; ++meshIndex )
		{
			var sourceMesh = lod.Meshes[meshIndex];
			if ( sourceMesh.Vertices.Length == 0 || sourceMesh.Indices.Length < 3 || ShouldSkipRenderMaterial( sourceMesh.MaterialName ) )
				continue;

			var materialName = NormalizeMaterialName( sourceMesh.MaterialName );
			var metadata = GetMaterialMetadata( materialName );
			var meshBounds = new BBox { Mins = float.MaxValue, Maxs = float.MinValue };
			var indices = ReverseWinding( sourceMesh.Indices );
			var vertices = new TitanfallVertex[sourceMesh.Vertices.Length];

			for ( var vertexIndex = 0; vertexIndex < sourceMesh.Vertices.Length; ++vertexIndex )
			{
				var sourceVertex = sourceMesh.Vertices[vertexIndex];
				var normal = ToSandbox( sourceVertex.Normal ).Normal;
				var position = ToSandbox( sourceVertex.Position );
				if ( metadata.IsDecal ) position += normal * DecalNormalOffset;
				vertices[vertexIndex] = new TitanfallVertex
				{
					Position = position,
					Normal = normal,
					TexCoord = ToSandbox( sourceVertex.TexCoord ),
					Color = GetVertexColor( sourceVertex )
				};
				meshBounds = meshBounds.AddPoint( position );
			}

			meshes.Add( new StaticMeshGeometry( meshIndex, materialName, vertices, indices, meshBounds ) );
		}
		return new StaticLodGeometry( meshes.ToArray(), ComputeStaticInstanceRadius( lod ) );
	}

	List<Mesh> CreateStaticInstanceMeshes( StaticLodGeometry lod, bool isVista, ref bool canInstance )
	{
		var meshes = new List<Mesh>();
		foreach ( var source in lod.Meshes )
		{
			var metadata = GetMaterialMetadata( source.MaterialName );
			if ( metadata.IsTranslucent || metadata.IsDecal || metadata.IsWater || MaterialLoader.IsGodrayMaterial( source.MaterialName ) )
				canInstance = false;
			var material = LoadMaterial( source.MaterialName )
				?? (isVista ? MaterialLoader.CreateVistaMaterial( $"{ImportPath}/fallback_{source.Index}", source.MaterialName, metadata )
					: metadata.IsDecal ? MaterialLoader.CreateDecalMaterial( $"{ImportPath}/decal_fallback_{source.Index}", metadata )
						: MaterialLoader.CreateRuntimeMaterial( $"{ImportPath}/fallback_{source.Index}", metadata ));
			var mesh = new Mesh( $"{ImportPath}_{source.Index}", material );
			mesh.CreateVertexBuffer( source.Vertices.Length, source.Vertices );
			mesh.CreateIndexBuffer( source.Indices.Length, source.Indices );
			mesh.Bounds = source.Bounds;
			meshes.Add( mesh );
		}
		return meshes;
	}

	/// <summary>
	/// The legacy per-LOD model: one level's geometry, registered so that the engine's own LOD
	/// selection cannot pick a level this model does not contain. The streamer keeps a model
	/// like this resident per distance band and swaps the whole batch when the band changes.
	/// </summary>
	Model BuildStaticInstanceModel( StaticLodGeometry lod )
	{
		var isVista = IsVistaModelPath( ImportPath );
		var builder = Model.Builder.WithName( ImportPath );
		var canInstance = true;
		var meshes = CreateStaticInstanceMeshes( lod, isVista, ref canInstance );

		if ( meshes.Count == 0 )
		{
			Titanfall2Log.Warning( $"Titanfall 2 static instance model contains no LOD{_staticLod} geometry: {ImportPath}" );
			return null;
		}

		// Every mesh is visible at every level, which is what the legacy per-LOD model always
		// did: the model only holds one band's geometry, so which level the engine picks for it
		// is not this path's business.
		foreach ( var mesh in meshes )
			builder.AddMesh( mesh );

		var model = builder.Create();
		StaticInstanceEligibility.Add( model, new StaticInstanceEligibilityValue( canInstance ) );
		Titanfall2Log.Trace( $"Titanfall 2 static instance model loaded: {ImportPath} "
			+ $"({lod.Meshes.Length} meshes, {lod.VertexCount} vertices, "
			+ $"{lod.TriangleCount} triangles, GPU instancing {(canInstance ? "enabled" : "disabled for special materials")})" );
		return model;
	}

	/// <summary>
	/// The native model: every LOD the streamer can ask for (bands 0..3) as one resource, one
	/// instanced draw and one transform array, with the level passed explicitly to
	/// <c>Graphics.DrawModelInstanced</c> instead of a whole model being swapped per band.
	/// That removes the main-thread model load and the batch rebuild from movement entirely:
	/// crossing a threshold only changes a draw argument. Levels 4..7 are registered too, as an
	/// alias of the deepest resolved band, so that no level of this model can be empty - see the
	/// AddMesh loop below for why that matters. One visible consequence of registering them: an
	/// explicit LOD override is clamped to the model's highest used level
	/// (Sandbox.Tools\Scene\Compilation\SceneCompiler.Props.cs:27, Math.Clamp( … , model.MaxLodLevel )),
	/// so that clamp now reaches 7 instead of 3. It draws the aliased band 3 geometry, not nothing.
	///
	/// The cost is paid once, when the prop's model is first loaded: every band that has
	/// geometry is decoded and uploaded then, instead of one band's geometry per cold load.
	/// That is also why the decode is not conditioned on PropLods: PropLods is a runtime A/B
	/// switch, and a model that was built without the higher bands could not gain them later,
	/// so PropLods=0 keeps paying the load-time decode of all bands while every draw is
	/// guaranteed to be LOD0 (SelectLodByDistance returns 0 before it looks at any distance).
	/// </summary>
	Model BuildStaticInstanceAllLodsModel( StaticLodGeometry[] levelLods )
	{
		var isVista = IsVistaModelPath( ImportPath );
		var builder = Model.Builder.WithName( ImportPath );
		var canInstance = true;
		var levelMeshes = new List<Mesh>[StaticInstanceBandCount];
		for ( var level = 0; level < StaticInstanceBandCount; ++level )
		{
			levelMeshes[level] = CreateStaticInstanceMeshes( levelLods[level], isVista, ref canInstance );
		}

		if ( levelMeshes[0].Count == 0 )
		{
			// The legacy loader treated a model without LOD0 as unusable too: its band 0
			// resource failed to load and the streamer fell back to the full model, which
			// carries collision and animations. Keeping that outcome here means a malformed
			// model still gets one working object instead of a model with a hole at close range.
			Titanfall2Log.Warning( $"Titanfall 2 static instance model contains no LOD0 geometry: {ImportPath}" );
			return null;
		}

		// The deepest band this model actually resolved. Bands 4..7 alias it, and it must be the
		// coarsest band that exists rather than band 3 or a hard zero: a model that ships only
		// one or two bands would otherwise alias band 0 into every level.
		var deepestBand = 0;
		for ( var level = 0; level < StaticInstanceBandCount; ++level )
		{
			if ( levelMeshes[level].Count > 0 ) deepestBand = level;
		}

		// A band Respawn did not ship resolves to the coarsest band that does exist below it,
		// NOT to LOD0. This used to fall back to band 0, which made every model that ships fewer
		// than four bands draw its finest geometry at every distance - measured on mp_angel_city:
		// of 756 distinct prop models, 245 had a farthest band at least as heavy as their nearest
		// one (colony_fence_a was 324/252/252 then back to 324), so the frame ended up GPU bound
		// on triangles and PropLods changed nothing at all, because every level held the same
		// geometry. Carrying the coarsest band forward keeps far distance cheap and still draws
		// real geometry, so no level is ever empty and no level is finer than its neighbour.
		var coarsestSoFar = 0;
		for ( var level = 0; level < StaticInstanceBandCount; ++level )
		{
			if ( levelMeshes[level].Count > 0 ) coarsestSoFar = level;
			foreach ( var mesh in levelMeshes[coarsestSoFar] )
				builder.AddMesh( mesh, level );
		}

		// Bands 4..7 have no band of their own to map to, so they alias the deepest band this
		// model resolved (`deepestBand`, i.e. the coarsest band that actually has geometry, not
		// band 3 by assumption).
		// They exist so that no LOD level of this model is ever geometry-less. Which levels the
		// engine considers available is not ours to decide - Model.MaxLodLevel is a native call
		// (Model.cs:122-125) and LodCount is derived from it (Model.Info.cs:118) - and if it
		// counts the eight switch distances we write rather than the mesh masks, automatic
		// selection (a prop that passes no explicit level, i.e. the non-instanceable special
		// materials, plus editor previews) can reach 4..7. An empty level there yields no draws
		// and the prop disappears; an aliased one yields band 3's geometry, which is what legacy
		// drew past 8192. Cost is four extra registration entries per mesh (levels 4, 5, 6 and 7),
		// all pointing at Mesh objects that already exist, so no vertices or index buffers are
		// duplicated.
		for ( var level = StaticInstanceBandCount; level < StaticInstanceLodCount; ++level )
		{
			foreach ( var mesh in levelMeshes[deepestBand] )
				builder.AddMesh( mesh, level );
		}

		// The engine's LOD walk does NOT compare these thresholds against world distance. It builds
		// metric = 50 / screenWidthInPixels and takes the first level whose switch distance is below
		// that metric (Sandbox.SceneRenderer\World\Objects\RenderMesh.cs:282-295). A threshold written
		// in world units (1536/4096/8192) is therefore above every metric a prop can produce, the walk
		// never breaks, and the model draws LOD0 at every distance - measured here: 92% of prop models
		// had no cheaper far level and PropLods changed nothing at all.
		//
		// screenWidthInPixels ~= 2 * radius * focal / distance, so the threshold that fires at world
		// distance D is 25 * D / (radius * focal). radius is this model's own bounding radius, not a
		// single nominal value: prop sizes differ by more than an order of magnitude on a BSP map,
		// and a shared radius puts small props' switch distances far too late (a 32-unit radius on an
		// 8-unit prop makes every threshold 4x too far), which showed up as triangle counts well
		// above what the instanced path draws for the same instances.
		var nominalRadius = Titanfall2StreamingSettings.PropLodNominalRadius;
		var modelRadius = nominalRadius > 0f
			? nominalRadius
			: MathF.Max( levelLods[0].Radius, 0.25f );
		var lodScale = 25f / (modelRadius * Titanfall2StreamingSettings.PropLodFocalPixels);
		for ( var level = 1; level < StaticInstanceLodCount; ++level )
			builder.WithLodDistance( level, GetLodSwitchDistance( level ) * lodScale );

		var model = builder.Create();
		StaticInstanceEligibility.Add( model, new StaticInstanceEligibilityValue( canInstance ) );
		var levelCount = levelMeshes.Count( static meshes => meshes.Count > 0 );
		Titanfall2Log.Trace( $"Titanfall 2 static instance all-LOD model loaded: {ImportPath} "
			+ $"({levelCount}/{StaticInstanceBandCount} LOD bands with geometry of their own, "
			+ $"bands {StaticInstanceBandCount}..{StaticInstanceLodCount - 1} alias band {deepestBand}, "
			+ $"GPU instancing {(canInstance ? "enabled" : "disabled for special materials")})" );
		return model;
	}

	Model BuildModel( Titanfall2Mdl53Reader.ParsedModel parsed, Titanfall2MeshExtractor.ExtractedLod lod, byte[] data )
	{
		var isVista = IsVistaModelPath( ImportPath );
		var builder = Model.Builder.WithName( ImportPath );
		var skeleton = AddSkeleton( builder, parsed );
		AddModelMetadata( builder, parsed, skeleton );
		var animationCount = 0;
		if ( skeleton.Names.Length > 0 )
		{
			// Runtime SceneModel playback selects the first available sequence when
			// no animgraph or explicit sequence was supplied. Titanfall models often
			// put a one-frame "ref" clip first; that clip is an authored pose, not the
			// inverse-bind pose. Make the actual bind pose the deterministic default
			// while retaining every decoded Titanfall sequence for explicit playback.
			var bindPose = builder.AddAnimation( "bindpose", 1.0f ).WithLooping( true );
			bindPose.AddFrame( skeleton.LocalTransforms );
			animationCount = 1;
		}
		animationCount += AddAnimations( builder, parsed, skeleton, data );
		// Native body groups need every choice's geometry. The packed body value only picks
		// the default mask, so ModelRenderer.SetBodyGroup can switch without another model.
		// Only the body-0 model gets native groups: choice 0 is the native default, and packed
		// body variants keep their selected-only geometry for Titanfall2ModelVariant.
		var bodyGroups = ImportAllBodyGroups || body != 0 ? null : NativeBodyGroupNames( parsed );
		if ( bodyGroups is not null )
		{
			lod = Titanfall2MeshExtractor.ExtractLod( parsed, 0, true, body, skin );
			RegisterBodyGroups( builder, parsed, bodyGroups );
		}
		var skinSlots = new List<(Material Material, int SkinReference)>();
		var skinSlotConflict = false;
		var collisionVertices = new List<Vector3>();
		var collisionIndices = new List<int>();
		var hasMesh = false;
		var decalMeshCount = 0;

		for ( var meshIndex = 0; meshIndex < lod.Meshes.Length; ++meshIndex )
		{
			var sourceMesh = lod.Meshes[meshIndex];
			if ( sourceMesh.Vertices.Length == 0 || sourceMesh.Indices.Length < 3 || ShouldSkipRenderMaterial( sourceMesh.MaterialName ) )
				continue;

			var materialName = NormalizeMaterialName( sourceMesh.MaterialName );
			var metadata = GetMaterialMetadata( materialName );
			// Render-geometry fallback collision follows the selected body, not every choice.
			var includeCollision = !isVista
				&& IsSelectedBodyChoice( parsed, sourceMesh.BodyPartIndex, sourceMesh.ModelIndex, body )
				&& Titanfall2CollisionFilter.ShouldIncludeMaterial( materialName, metadata );
			if ( metadata.IsDecal ) decalMeshCount++;

			var meshBounds = new BBox { Mins = float.MaxValue, Maxs = float.MinValue };
			var collisionBase = collisionVertices.Count;
			var indices = ReverseWinding( sourceMesh.Indices );
			var material = LoadMaterial( materialName )
				?? (isVista
					? MaterialLoader.CreateVistaMaterial( $"{ImportPath}/fallback_{meshIndex}", materialName, metadata )
					: metadata.IsDecal
						? MaterialLoader.CreateDecalMaterial( $"{ImportPath}/decal_fallback_{meshIndex}", metadata )
						: MaterialLoader.CreateRuntimeMaterial( $"{ImportPath}/fallback_{meshIndex}", metadata ));
			var mesh = new Mesh( $"{ImportPath}_{meshIndex}", material );

			if ( skeleton.Names.Length > 0 )
			{
				var vertices = new TitanfallSkinnedVertex[sourceMesh.Vertices.Length];
				for ( var vertexIndex = 0; vertexIndex < sourceMesh.Vertices.Length; ++vertexIndex )
				{
					var sourceVertex = sourceMesh.Vertices[vertexIndex];
					var normal = ToSandbox( sourceVertex.Normal ).Normal;
					var position = ToSandbox( sourceVertex.Position );
					if ( metadata.IsDecal ) position += normal * DecalNormalOffset;
					EncodeSkinning( sourceVertex.BoneIds, sourceVertex.BoneWeights, skeleton.Names.Length, out var boneIndices, out var boneWeights );
					vertices[vertexIndex] = new TitanfallSkinnedVertex
					{
						Position = position,
						Normal = normal,
						TexCoord = ToSandbox( sourceVertex.TexCoord ),
						Color = GetVertexColor( sourceVertex ),
						BlendIndices = boneIndices,
						BlendWeights = boneWeights
					};
					meshBounds = meshBounds.AddPoint( position );
					if ( includeCollision ) collisionVertices.Add( position );
				}
				mesh.CreateVertexBuffer( vertices.Length, vertices );
			}
			else
			{
				var vertices = new TitanfallVertex[sourceMesh.Vertices.Length];
				for ( var vertexIndex = 0; vertexIndex < sourceMesh.Vertices.Length; ++vertexIndex )
				{
					var sourceVertex = sourceMesh.Vertices[vertexIndex];
					var normal = ToSandbox( sourceVertex.Normal ).Normal;
					var position = ToSandbox( sourceVertex.Position );
					if ( metadata.IsDecal ) position += normal * DecalNormalOffset;
					vertices[vertexIndex] = new TitanfallVertex
					{
						Position = position,
						Normal = normal,
						TexCoord = ToSandbox( sourceVertex.TexCoord ),
						Color = GetVertexColor( sourceVertex )
					};
					meshBounds = meshBounds.AddPoint( position );
					if ( includeCollision ) collisionVertices.Add( position );
				}
				mesh.CreateVertexBuffer( vertices.Length, vertices );
			}

			if ( includeCollision )
			{
				for ( var index = 0; index < indices.Length; ++index )
					collisionIndices.Add( collisionBase + indices[index] );
			}

			mesh.CreateIndexBuffer( indices.Length, indices );
			mesh.Bounds = meshBounds;
			var groupName = bodyGroups?[sourceMesh.BodyPartIndex];
			// The grouped overload takes one LOD. Keep every mesh on LOD0 alone when groups
			// exist, otherwise an all-LOD mesh would give the engine LODs that drop the grouped parts.
			if ( groupName is not null ) builder.AddMesh( mesh, 0, groupName, sourceMesh.ModelIndex );
			else if ( bodyGroups is not null ) builder.AddMesh( mesh, 0 );
			else builder.AddMesh( mesh );
			hasMesh = true;

			var slot = skinSlots.FindIndex( candidate => ReferenceEquals( candidate.Material, material ) );
			if ( slot < 0 ) skinSlots.Add( (material, sourceMesh.SkinReference) );
			else if ( skinSlots[slot].SkinReference != sourceMesh.SkinReference
				&& !SameSkinMaterials( parsed, skinSlots[slot].SkinReference, sourceMesh.SkinReference ) )
				skinSlotConflict = true;
		}

		AddSkinMaterialGroups( builder, parsed, skinSlots, skinSlotConflict );

		var collisionDescription = "none";
		var collisionHullCount = 0;
		if ( hasMesh && !isVista )
		{
			(collisionDescription, collisionHullCount) = AddCollision(
				builder, parsed, skeleton, data, collisionVertices, collisionIndices );
			RecordCollisionStatistics( collisionDescription, collisionIndices.Count / 3 );
		}

		if ( !hasMesh && animationCount == 0 )
		{
			Titanfall2Log.Warning( $"Titanfall 2 model contains neither LOD0 geometry nor supported animations: {ImportPath}" );
			return null;
		}

		Titanfall2Log.Trace( $"Titanfall 2 model loaded: {ImportPath} ({lod.Meshes.Length} meshes, {decalMeshCount} decal meshes, "
			+ $"{lod.VertexCount} vertices, {lod.TriangleCount} triangles, {parsed.Bones.Length} bones, "
			+ $"{animationCount} animations, {collisionHullCount} {collisionDescription} collision hulls)" );
		builder.WithData( new Titanfall2ModelCollisionData
		{
			SourcePath = ImportPath,
			GeometrySource = collisionDescription,
			HullCount = collisionHullCount,
			ModelContents = unchecked((uint)parsed.Header.Contents)
		} );
		return builder.Create();
	}

	static SkeletonData AddSkeleton( ModelBuilder builder, Titanfall2Mdl53Reader.ParsedModel parsed )
	{
		if ( parsed.Bones.Length == 0 ) return new SkeletonData();
		var names = new string[parsed.Bones.Length];
		var localTransforms = new Transform[parsed.Bones.Length];
		var modelTransforms = new Transform[parsed.Bones.Length];
		var usedNames = new HashSet<string>( StringComparer.OrdinalIgnoreCase );

		for ( var boneIndex = 0; boneIndex < parsed.Bones.Length; ++boneIndex )
		{
			var requestedName = parsed.Bones[boneIndex].Name;
			if ( string.IsNullOrWhiteSpace( requestedName ) ) requestedName = $"bone_{boneIndex}";
			var name = requestedName;
			for ( var suffix = 1; !usedNames.Add( name ); ++suffix ) name = $"{requestedName}_{suffix}";
			names[boneIndex] = name;

			var parent = GetBoneParent( parsed, boneIndex );
			var position = GetBonePosition( parsed, boneIndex );
			var rotation = GetBoneRotation( parsed, boneIndex );
			var scale = ToSandbox( parsed.Bones[boneIndex].Bone.Scale );
			if ( !IsFinite( scale ) ) scale = Vector3.One;
			localTransforms[boneIndex] = new Transform( position, rotation, scale );
			modelTransforms[boneIndex] = parent >= 0 && parent < boneIndex
				? modelTransforms[parent].ToWorld( localTransforms[boneIndex] )
				: localTransforms[boneIndex];

			// The runtime ModelBuilder native skeleton path derives its inverse-bind
			// matrices from model-space bone transforms, despite the managed API
			// documentation describing these arguments as parent-relative. Passing
			// Titanfall's local transforms here makes every descendant apply its
			// parent chain twice and visibly pulls weighted mesh parts apart.
			var modelTransform = modelTransforms[boneIndex];
			builder.AddBone( name, modelTransform.Position, modelTransform.Rotation,
				parent >= 0 && parent < names.Length ? names[parent] : null );
		}

		return new SkeletonData
		{
			Names = names,
			LocalTransforms = localTransforms,
			ModelTransforms = modelTransforms
		};
	}

	int AddAnimations(
		ModelBuilder builder,
		Titanfall2Mdl53Reader.ParsedModel targetModel,
		SkeletonData targetSkeleton,
		byte[] primaryData )
	{
		if ( targetSkeleton.Names.Length == 0 ) return 0;
		var sources = GetAnimationSources( primaryData, targetModel );
		var targetBones = new Dictionary<string, int>( StringComparer.OrdinalIgnoreCase );
		for ( var targetIndex = 0; targetIndex < targetModel.Bones.Length; ++targetIndex )
		{
			var name = targetModel.Bones[targetIndex].Name;
			if ( !string.IsNullOrWhiteSpace( name ) ) targetBones.TryAdd( name, targetIndex );
		}

		// "bindpose" is always installed before imported sequences and owns that
		// name, so an included model cannot replace the safe default pose.
		var addedNames = new HashSet<string>( StringComparer.OrdinalIgnoreCase ) { "bindpose" };
		var animationCount = 0;
		var failureCount = 0;
		foreach ( var animationSource in sources )
		{
			var sourceToTarget = new int[animationSource.Model.Bones.Length];
			Array.Fill( sourceToTarget, -1 );
			var matchingBones = 0;
			for ( var sourceIndex = 0; sourceIndex < animationSource.Model.Bones.Length; ++sourceIndex )
			{
				if ( targetBones.TryGetValue( animationSource.Model.Bones[sourceIndex].Name, out var targetIndex ) )
				{
					sourceToTarget[sourceIndex] = targetIndex;
					matchingBones++;
				}
			}
			if ( matchingBones == 0 ) continue;

			if ( animationSource.Model.Sequences.Length > 0 )
			{
				foreach ( var sequence in animationSource.Model.Sequences )
				{
					var animationIndex = sequence.AnimationIndices.FirstOrDefault( index => index >= 0 && index < animationSource.Model.Animations.Length, (short)-1 );
					if ( animationIndex < 0 ) continue;
					var name = string.IsNullOrWhiteSpace( sequence.Label )
						? animationSource.Model.Animations[animationIndex].Name
						: sequence.Label;
					TryAddAnimation( builder, targetSkeleton, animationSource, sourceToTarget,
						animationIndex, name, sequence.Sequence.Flags, addedNames, ref animationCount, ref failureCount );
				}
			}
			else
			{
				foreach ( var animation in animationSource.Model.Animations )
				{
					TryAddAnimation( builder, targetSkeleton, animationSource, sourceToTarget,
						animation.Index, animation.Name, animation.Animation.Flags, addedNames, ref animationCount, ref failureCount );
				}
			}
		}

		if ( failureCount > 5 )
			Titanfall2Log.Warning( $"Titanfall 2 model '{ImportPath}' skipped {failureCount - 5} additional invalid animation sequences." );
		return animationCount;
	}

	void TryAddAnimation(
		ModelBuilder builder,
		SkeletonData targetSkeleton,
		AnimationSourceData sourceData,
		int[] sourceToTarget,
		int animationIndex,
		string requestedName,
		int sequenceFlags,
		HashSet<string> addedNames,
		ref int animationCount,
		ref int failureCount )
	{
		var name = requestedName?.Trim().TrimStart( '@' );
		if ( string.IsNullOrWhiteSpace( name ) ) name = $"animation_{animationIndex}";
		if ( !addedNames.Add( name ) ) return;

		try
		{
			var decoded = Titanfall2Mdl53AnimationReader.Decode(
				sourceData.Data, sourceData.Model, animationIndex, name, sequenceFlags );
			var animation = builder.AddAnimation( decoded.Name, decoded.FramesPerSecond )
				.WithLooping( decoded.Looping )
				.WithDelta( decoded.Delta );

			foreach ( var sourceFrame in decoded.Frames )
			{
				var targetFrame = new Transform[targetSkeleton.Names.Length];
				if ( decoded.Delta ) Array.Fill( targetFrame, Transform.Zero );
				else Array.Copy( targetSkeleton.LocalTransforms, targetFrame, targetFrame.Length );

				for ( var sourceBone = 0; sourceBone < sourceFrame.Length && sourceBone < sourceToTarget.Length; ++sourceBone )
				{
					var targetBone = sourceToTarget[sourceBone];
					if ( targetBone < 0 ) continue;
					var pose = sourceFrame[sourceBone];
					targetFrame[targetBone] = new Transform(
						ToSandbox( pose.Position ),
						ToSandbox( pose.Rotation ),
						ToSandbox( pose.Scale ) );
				}
				animation.AddFrame( targetFrame );
			}
			animationCount++;
		}
		catch ( Exception exception )
		{
			addedNames.Remove( name );
			failureCount++;
			if ( failureCount <= 5 )
				Titanfall2Log.Warning( $"Titanfall 2 animation '{name}' from '{sourceData.Description}' was skipped: {exception.Message}" );
		}
	}

	List<AnimationSourceData> GetAnimationSources( byte[] primaryData, Titanfall2Mdl53Reader.ParsedModel primaryModel )
	{
		var sources = new List<AnimationSourceData>
		{
			new() { Description = _source.Description, Data = primaryData, Model = primaryModel }
		};
		if ( _source is not ITitanfall2PathAssetSource pathSource || primaryModel.IncludedModels.Length == 0 ) return sources;

		var visited = new HashSet<string>( StringComparer.OrdinalIgnoreCase ) { NormalizeAssetPath( _source.Description ) };
		for ( var sourceIndex = 0; sourceIndex < sources.Count && sources.Count < 64; ++sourceIndex )
		{
			foreach ( var includedModel in sources[sourceIndex].Model.IncludedModels )
			{
				var path = NormalizeAssetPath( includedModel.Name );
				if ( string.IsNullOrWhiteSpace( path ) || !visited.Add( path ) ) continue;
				if ( !pathSource.TryReadPath( path, out var bytes, out var error ) )
				{
					Titanfall2Log.Warning( $"Unable to read included Titanfall 2 animation model '{path}' for '{ImportPath}': {error}" );
					continue;
				}

				try
				{
					sources.Add( new AnimationSourceData
					{
						Description = path,
						Data = bytes,
						Model = Titanfall2Mdl53Reader.Parse( bytes )
					} );
				}
				catch ( Exception exception )
				{
					Titanfall2Log.Warning( $"Unable to parse included Titanfall 2 animation model '{path}' for '{ImportPath}': {exception.Message}" );
				}
			}
		}
		return sources;
	}

	(string Description, int HullCount) AddCollision(
		ModelBuilder builder,
		Titanfall2Mdl53Reader.ParsedModel parsed,
		SkeletonData skeleton,
		byte[] data,
		List<Vector3> renderVertices,
		List<int> renderIndices )
	{
		try
		{
			var physics = Titanfall2Mdl53PhysicsReader.ParseData( data, parsed.Header, parsed.Bones.Length );
			if ( physics.Hulls.Length > 0 )
			{
				var solidNames = physics.Solids
					.Where( solid => solid.Index >= 0 && !string.IsNullOrWhiteSpace( solid.Name ) )
					.GroupBy( solid => solid.Index )
					.ToDictionary( group => group.Key, group => group.First().Name );
				var hulls = physics.Hulls.Where( IsValidPhysicsHull ).Select( hull => new CollisionHullData
				{
					SolidIndex = hull.SolidIndex,
					BoneIndex = solidNames.TryGetValue( hull.SolidIndex, out var boneName )
						? FindBoneIndex( parsed, boneName )
						: hull.BoneIndex,
					Points = hull.Points.Select( ToSandbox ).ToArray(),
					Indices = hull.Indices
				} ).ToArray();
				if ( hulls.Length > 0 )
				{
					if ( TryAddRagdollCollision(
						builder, parsed, skeleton, hulls, physics.Solids, physics.Constraints,
						out var bodyCount, out var jointCount ) )
					{
						Titanfall2Log.Trace( $"Titanfall 2 ragdoll imported: {ImportPath} ({bodyCount} bodies, {jointCount} joints)." );
					}
					else
					{
						AddCollisionHulls( builder, parsed, skeleton, hulls );
					}
					return ("VPHY", hulls.Length);
				}
				Titanfall2Log.Warning( $"Embedded Titanfall 2 VPHY collision for '{ImportPath}' contained no finite, indexed convex hulls." );
			}
		}
		catch ( Exception exception )
		{
			Titanfall2Log.Warning( $"Embedded Titanfall 2 VPHY collision for '{ImportPath}' was skipped: {exception.Message}" );
		}

		var hitboxHulls = CreateHitboxHulls( parsed );
		if ( hitboxHulls.Length > 0 )
		{
			AddCollisionHulls( builder, parsed, skeleton, hitboxHulls );
			return ("hitbox", hitboxHulls.Length);
		}

		if ( renderVertices.Count > 0 && renderIndices.Count > 0 )
		{
			var triangleCount = renderIndices.Count / 3;
			if ( triangleCount <= Titanfall2StreamingSettings.ModelCollisionMeshMaxTriangles )
			{
				builder.AddTraceMesh( renderVertices, renderIndices );
				if ( collisionOnly || Titanfall2StreamingSettings.PropCollisions || Titanfall2StreamingSettings.NavMeshStaticProps )
					builder.AddCollisionMesh( renderVertices.ToArray(), renderIndices.ToArray() );
				return ("render-mesh fallback", 1);
			}

			if ( CreateBoundsHull( renderVertices ) is { } boundsHull )
			{
				AddCollisionHulls( builder, parsed, skeleton, [boundsHull] );
				Titanfall2Log.Trace( $"Titanfall 2 model collision simplified: {ImportPath} ({triangleCount} render triangles -> one bounds hull)." );
				return ("simplified-box", 1);
			}
		}
		return ("none", 0);
	}

	static bool IsValidPhysicsHull( Titanfall2Mdl53PhysicsReader.PhysicsHull hull )
	{
		if ( hull?.Points is null || hull.Indices is null || hull.Points.Length < 4 || hull.Indices.Length < 3 ) return false;
		if ( hull.Points.Any( point => !float.IsFinite( point.X ) || !float.IsFinite( point.Y ) || !float.IsFinite( point.Z ) ) ) return false;
		if ( hull.Indices.Any( index => index < 0 || index >= hull.Points.Length ) ) return false;
		return hull.Points.Distinct().Take( 4 ).Count() == 4;
	}

	static CollisionHullData CreateBoundsHull( IReadOnlyList<Vector3> vertices )
	{
		if ( vertices is null || vertices.Count == 0 ) return null;
		var mins = new Vector3( float.MaxValue, float.MaxValue, float.MaxValue );
		var maxs = new Vector3( float.MinValue, float.MinValue, float.MinValue );
		foreach ( var vertex in vertices )
		{
			if ( !IsFinite( vertex ) ) continue;
			mins = Vector3.Min( mins, vertex );
			maxs = Vector3.Max( maxs, vertex );
		}
		if ( !IsFinite( mins ) || !IsFinite( maxs ) ) return null;
		const float MinimumThickness = 0.5f;
		if ( maxs.x - mins.x < MinimumThickness ) { mins.x -= MinimumThickness; maxs.x += MinimumThickness; }
		if ( maxs.y - mins.y < MinimumThickness ) { mins.y -= MinimumThickness; maxs.y += MinimumThickness; }
		if ( maxs.z - mins.z < MinimumThickness ) { mins.z -= MinimumThickness; maxs.z += MinimumThickness; }
		return new CollisionHullData
		{
			Points =
			[
				new( mins.x, mins.y, mins.z ), new( maxs.x, mins.y, mins.z ),
				new( maxs.x, maxs.y, mins.z ), new( mins.x, maxs.y, mins.z ),
				new( mins.x, mins.y, maxs.z ), new( maxs.x, mins.y, maxs.z ),
				new( maxs.x, maxs.y, maxs.z ), new( mins.x, maxs.y, maxs.z )
			],
			Indices =
			[
				0, 1, 2, 0, 2, 3, 4, 6, 5, 4, 7, 6,
				0, 4, 5, 0, 5, 1, 1, 5, 6, 1, 6, 2,
				2, 6, 7, 2, 7, 3, 3, 7, 4, 3, 4, 0
			]
		};
	}

	static void RecordCollisionStatistics( string description, int renderTriangleCount )
	{
		var total = System.Threading.Interlocked.Increment( ref _collisionModelCount );
		switch ( description )
		{
			case "VPHY": System.Threading.Interlocked.Increment( ref _vphyModelCount ); break;
			case "hitbox": System.Threading.Interlocked.Increment( ref _hitboxModelCount ); break;
			case "render-mesh fallback":
				System.Threading.Interlocked.Increment( ref _renderMeshModelCount );
				System.Threading.Interlocked.Add( ref _fallbackTriangleCount, renderTriangleCount );
				break;
			case "simplified-box":
				System.Threading.Interlocked.Increment( ref _simplifiedModelCount );
				System.Threading.Interlocked.Add( ref _fallbackTriangleCount, renderTriangleCount );
				break;
			default: System.Threading.Interlocked.Increment( ref _noCollisionModelCount ); break;
		}
		if ( total % 100 != 0 ) return;
		Log.Info( $"Titanfall 2 collision audit: {total} models; VPHY={_vphyModelCount}, hitbox={_hitboxModelCount}, "
			+ $"render-fallback={_renderMeshModelCount}, simplified={_simplifiedModelCount}, none={_noCollisionModelCount}; "
			+ $"{_fallbackTriangleCount} source fallback triangles inspected." );
	}

	static void AddCollisionHulls(
		ModelBuilder builder,
		Titanfall2Mdl53Reader.ParsedModel parsed,
		SkeletonData skeleton,
		IReadOnlyList<CollisionHullData> hulls )
	{
		var mass = parsed.Header.Mass > 0.0f && float.IsFinite( parsed.Header.Mass )
			? parsed.Header.Mass
			: 0.0f;
		var rootBoneIndex = -1;
		for ( var boneIndex = 0; boneIndex < skeleton.Names.Length; boneIndex++ )
		{
			if ( GetBoneParent( parsed, boneIndex ) >= 0 ) continue;
			rootBoneIndex = boneIndex;
			break;
		}
		var rootPose = rootBoneIndex >= 0 ? skeleton.ModelTransforms[rootBoneIndex] : Transform.Zero;
		var body = builder.AddBody( mass, boneName: rootBoneIndex >= 0 ? skeleton.Names[rootBoneIndex] : null );
		if ( rootBoneIndex >= 0 ) body.SetBindPose( rootPose );
		var traceVertices = new List<Vector3>();
		var traceIndices = new List<int>();

		foreach ( var hull in hulls )
		{
			var boneIndex = hull.BoneIndex;
			var validBone = boneIndex >= 0 && boneIndex < skeleton.Names.Length;
			var bindPose = validBone ? skeleton.ModelTransforms[boneIndex] : Transform.Zero;
			var modelPoints = new Vector3[hull.Points.Length];
			for ( var index = 0; index < hull.Points.Length; index++ )
			{
				modelPoints[index] = bindPose.PointToWorld( hull.Points[index] );
			}

			var bodyPoints = rootBoneIndex >= 0
				? modelPoints.Select( point => rootPose.PointToLocal( point ) ).ToArray()
				: modelPoints;
			body.AddHull( bodyPoints );
			var traceBase = traceVertices.Count;
			traceVertices.AddRange( modelPoints );
			for ( var index = 0; index + 2 < hull.Indices.Length; index += 3 )
			{
				traceIndices.Add( traceBase + hull.Indices[index] );
				traceIndices.Add( traceBase + hull.Indices[index + 2] );
				traceIndices.Add( traceBase + hull.Indices[index + 1] );
			}
		}

		if ( traceVertices.Count > 0 && traceIndices.Count > 0 )
			builder.AddTraceMesh( traceVertices, traceIndices );
	}

	static bool TryAddRagdollCollision(
		ModelBuilder builder,
		Titanfall2Mdl53Reader.ParsedModel parsed,
		SkeletonData skeleton,
		IReadOnlyList<CollisionHullData> hulls,
		IReadOnlyList<Titanfall2Mdl53PhysicsReader.PhysicsSolid> physicsSolids,
		IReadOnlyList<Titanfall2Mdl53PhysicsReader.RagdollConstraint> physicsConstraints,
		out int bodyCount,
		out int jointCount )
	{
		bodyCount = 0;
		jointCount = 0;
		if ( physicsSolids.Count < 2 || physicsConstraints.Count == 0 ) return false;

		var solids = physicsSolids
			.Where( solid => solid.Index >= 0 )
			.GroupBy( solid => solid.Index )
			.ToDictionary( group => group.Key, group => group.First() );
		var bodies = new Dictionary<int, RagdollBodyData>();
		foreach ( var group in hulls.Where( hull => hull.SolidIndex >= 0 ).GroupBy( hull => hull.SolidIndex ) )
		{
			if ( !solids.TryGetValue( group.Key, out var solid ) ) continue;
			var groupedHulls = group.ToArray();
			var boneIndex = groupedHulls.Select( hull => hull.BoneIndex )
				.FirstOrDefault( index => index >= 0 && index < skeleton.Names.Length, -1 );
			if ( boneIndex < 0 ) boneIndex = FindBoneIndex( parsed, solid.Name );
			if ( boneIndex < 0 || boneIndex >= skeleton.Names.Length ) continue;
			bodies.Add( group.Key, new RagdollBodyData
			{
				SolidIndex = group.Key,
				BoneIndex = boneIndex,
				Mass = solid.Mass,
				Hulls = groupedHulls
			} );
		}

		if ( bodies.Count < 2 || bodies.Values.Select( body => body.BoneIndex ).Distinct().Count() != bodies.Count )
			return false;

		var constraints = physicsConstraints
			.Where( constraint => constraint.ParentIndex != constraint.ChildIndex
				&& bodies.ContainsKey( constraint.ParentIndex )
				&& bodies.ContainsKey( constraint.ChildIndex )
				&& IsFinite( constraint ) )
			.GroupBy( constraint => (constraint.ParentIndex, constraint.ChildIndex) )
			.Select( group => group.First() )
			.ToArray();
		if ( constraints.Length == 0 || !IsConnectedRagdoll( bodies.Keys, constraints ) ) return false;

		var bodyIndices = new Dictionary<int, int>();
		var traceVertices = new List<Vector3>();
		var traceIndices = new List<int>();
		var fallbackMass = parsed.Header.Mass > 0.0f && float.IsFinite( parsed.Header.Mass )
			? parsed.Header.Mass / bodies.Count
			: 0.0f;

		foreach ( var sourceBody in bodies.Values.OrderBy( body => body.SolidIndex ) )
		{
			var mass = sourceBody.Mass > 0.0f && float.IsFinite( sourceBody.Mass )
				? sourceBody.Mass
				: fallbackMass;
			var body = builder.AddBody( mass, boneName: skeleton.Names[sourceBody.BoneIndex] );
			var bindPose = skeleton.ModelTransforms[sourceBody.BoneIndex];
			body.SetBindPose( bindPose );
			bodyIndices.Add( sourceBody.SolidIndex, bodyIndices.Count );

			foreach ( var hull in sourceBody.Hulls )
			{
				body.AddHull( hull.Points );
				var traceBase = traceVertices.Count;
				foreach ( var point in hull.Points ) traceVertices.Add( bindPose.PointToWorld( point ) );
				for ( var index = 0; index + 2 < hull.Indices.Length; index += 3 )
				{
					traceIndices.Add( traceBase + hull.Indices[index] );
					traceIndices.Add( traceBase + hull.Indices[index + 2] );
					traceIndices.Add( traceBase + hull.Indices[index + 1] );
				}
			}
		}

		foreach ( var constraint in constraints )
		{
			var child = bodies[constraint.ChildIndex];
			var parent = bodies[constraint.ParentIndex];
			AddRagdollJoint(
				builder,
				bodyIndices[constraint.ChildIndex],
				bodyIndices[constraint.ParentIndex],
				skeleton.ModelTransforms[child.BoneIndex],
				skeleton.ModelTransforms[parent.BoneIndex],
				constraint );
			jointCount++;
		}

		if ( traceVertices.Count > 0 && traceIndices.Count > 0 )
			builder.AddTraceMesh( traceVertices, traceIndices );
		bodyCount = bodies.Count;
		return true;
	}

	static bool IsConnectedRagdoll(
		IEnumerable<int> bodyIndices,
		IReadOnlyList<Titanfall2Mdl53PhysicsReader.RagdollConstraint> constraints )
	{
		var remaining = bodyIndices.ToHashSet();
		if ( remaining.Count == 0 ) return false;
		var pending = new Queue<int>();
		var first = remaining.First();
		remaining.Remove( first );
		pending.Enqueue( first );
		while ( pending.TryDequeue( out var current ) )
		{
			foreach ( var constraint in constraints )
			{
				var adjacent = constraint.ParentIndex == current
					? constraint.ChildIndex
					: constraint.ChildIndex == current
						? constraint.ParentIndex
						: -1;
				if ( adjacent >= 0 && remaining.Remove( adjacent ) ) pending.Enqueue( adjacent );
			}
		}
		return remaining.Count == 0;
	}

	static void AddRagdollJoint(
		ModelBuilder builder,
		int childBodyIndex,
		int parentBodyIndex,
		Transform childPose,
		Transform parentPose,
		Titanfall2Mdl53PhysicsReader.RagdollConstraint constraint )
	{
		var limits = new[]
		{
			new Vector2( constraint.XMin, constraint.XMax ),
			new Vector2( constraint.YMin, constraint.YMax ),
			new Vector2( constraint.ZMin, constraint.ZMax )
		};
		var movingAxes = Enumerable.Range( 0, 3 )
			.Where( axis => MathF.Abs( limits[axis].y - limits[axis].x ) > 0.01f )
			.ToArray();

		if ( movingAxes.Length == 0 )
		{
			CreateJointFrames( childPose, parentPose, Rotation.Identity, out var childFrame, out var parentFrame );
			builder.AddFixedJoint( childBodyIndex, parentBodyIndex, childFrame, parentFrame );
			return;
		}

		if ( movingAxes.Length == 1 )
		{
			var sourceAxis = movingAxes[0];
			var axis = sourceAxis switch
			{
				0 => Vector3.Forward,
				1 => Vector3.Left,
				_ => Vector3.Up
			};
			var jointRotation = Rotation.FromToRotation( Vector3.Up, axis );
			CreateJointFrames( childPose, parentPose, jointRotation, out var childFrame, out var parentFrame );
			var limit = limits[sourceAxis];
			builder.AddHingeJoint( childBodyIndex, parentBodyIndex, childFrame, parentFrame )
				.WithTwistLimit( -limit.y, -limit.x );
			return;
		}

		// Source VPhysics stores independent Euler limits. The s&box ball joint
		// exposes one twist range and one conical swing range, so preserve X as the
		// twist axis and conservatively contain both Y/Z ranges in the swing cone.
		var ballRotation = Rotation.FromToRotation( Vector3.Up, Vector3.Forward );
		CreateJointFrames( childPose, parentPose, ballRotation, out var ballChildFrame, out var ballParentFrame );
		var swing = MathF.Max(
			MathF.Max( MathF.Abs( constraint.YMin ), MathF.Abs( constraint.YMax ) ),
			MathF.Max( MathF.Abs( constraint.ZMin ), MathF.Abs( constraint.ZMax ) ) );
		builder.AddBallJoint( childBodyIndex, parentBodyIndex, ballChildFrame, ballParentFrame )
			.WithSwingLimit( Math.Clamp( swing, 0.0f, 179.0f ) )
			.WithTwistLimit(
				Math.Clamp( MathF.Min( constraint.XMin, constraint.XMax ), -179.0f, 179.0f ),
				Math.Clamp( MathF.Max( constraint.XMin, constraint.XMax ), -179.0f, 179.0f ) );
	}

	static void CreateJointFrames(
		Transform childPose,
		Transform parentPose,
		Rotation localRotation,
		out Transform childFrame,
		out Transform parentFrame )
	{
		var childBindPose = new Transform( childPose.Position, childPose.Rotation );
		var parentBindPose = new Transform( parentPose.Position, parentPose.Rotation );
		childFrame = new Transform( Vector3.Zero, localRotation );
		parentFrame = parentBindPose.ToLocal( childBindPose.ToWorld( childFrame ) );
	}

	static bool IsFinite( Titanfall2Mdl53PhysicsReader.RagdollConstraint constraint )
		=> float.IsFinite( constraint.XMin ) && float.IsFinite( constraint.XMax )
			&& float.IsFinite( constraint.YMin ) && float.IsFinite( constraint.YMax )
			&& float.IsFinite( constraint.ZMin ) && float.IsFinite( constraint.ZMax );

	static CollisionHullData[] CreateHitboxHulls( Titanfall2Mdl53Reader.ParsedModel parsed )
	{
		var set = parsed.HitboxSets.FirstOrDefault( candidate => candidate.Hitboxes.Length > 0 );
		if ( set is null ) return Array.Empty<CollisionHullData>();
		var hulls = new List<CollisionHullData>();
		foreach ( var hitbox in set.Hitboxes )
		{
			var box = hitbox.Hitbox;
			if ( box.Bone < 0 || box.Bone >= parsed.Bones.Length ) continue;
			var mins = ToSandbox( box.BoundingBoxMin );
			var maxs = ToSandbox( box.BoundingBoxMax );
			if ( !IsFinite( mins ) || !IsFinite( maxs ) || mins == maxs ) continue;
			var points = new[]
			{
				new Vector3( mins.x, mins.y, mins.z ), new Vector3( maxs.x, mins.y, mins.z ),
				new Vector3( maxs.x, maxs.y, mins.z ), new Vector3( mins.x, maxs.y, mins.z ),
				new Vector3( mins.x, mins.y, maxs.z ), new Vector3( maxs.x, mins.y, maxs.z ),
				new Vector3( maxs.x, maxs.y, maxs.z ), new Vector3( mins.x, maxs.y, maxs.z )
			};
			hulls.Add( new CollisionHullData
			{
				BoneIndex = box.Bone,
				Points = points,
				Indices =
				[
					0, 1, 2, 0, 2, 3,
					4, 6, 5, 4, 7, 6,
					0, 4, 5, 0, 5, 1,
					1, 5, 6, 1, 6, 2,
					2, 6, 7, 2, 7, 3,
					3, 7, 4, 3, 4, 0
				]
			} );
		}
		return hulls.ToArray();
	}

	static int FindBoneIndex( Titanfall2Mdl53Reader.ParsedModel parsed, string name )
	{
		if ( string.IsNullOrWhiteSpace( name ) ) return -1;
		for ( var boneIndex = 0; boneIndex < parsed.Bones.Length; boneIndex++ )
		{
			if ( string.Equals( parsed.Bones[boneIndex].Name, name, StringComparison.OrdinalIgnoreCase ) )
				return boneIndex;
		}
		return -1;
	}

	static int GetBoneParent( Titanfall2Mdl53Reader.ParsedModel parsed, int boneIndex )
		=> boneIndex < parsed.LinearBones.Length ? parsed.LinearBones[boneIndex].Parent : parsed.Bones[boneIndex].Bone.Parent;

	static Vector3 GetBonePosition( Titanfall2Mdl53Reader.ParsedModel parsed, int boneIndex )
		=> ToSandbox( boneIndex < parsed.LinearBones.Length ? parsed.LinearBones[boneIndex].Position : parsed.Bones[boneIndex].Bone.Position );

	static Rotation GetBoneRotation( Titanfall2Mdl53Reader.ParsedModel parsed, int boneIndex )
		=> ToSandbox( boneIndex < parsed.LinearBones.Length
			? parsed.LinearBones[boneIndex].RotationQuaternion
			: parsed.Bones[boneIndex].Bone.RotationQuaternion );

	static Color32 GetVertexColor( Titanfall2MeshExtractor.ExtractedVertex vertex )
		=> vertex.HasColor
			? new Color32( vertex.Color.R, vertex.Color.G, vertex.Color.B, vertex.Color.A )
			: new Color32( 255, 255, 255, 255 );

	static void EncodeSkinning(
		IReadOnlyList<byte> sourceIndices,
		IReadOnlyList<float> sourceWeights,
		int boneCount,
		out Color32 indices,
		out Color32 weights )
	{
		Span<byte> encodedIndices = stackalloc byte[4];
		Span<float> normalizedWeights = stackalloc float[4];
		var count = 0;
		for ( var sourceIndex = 0; sourceIndex < sourceIndices.Count && sourceIndex < sourceWeights.Count && count < 4; ++sourceIndex )
		{
			var boneIndex = sourceIndices[sourceIndex];
			var weight = sourceWeights[sourceIndex];
			if ( boneIndex >= boneCount || !float.IsFinite( weight ) || weight <= 0.0f ) continue;
			encodedIndices[count] = boneIndex;
			normalizedWeights[count] = weight;
			count++;
		}

		if ( count == 0 )
		{
			indices = new Color32( 0, 0, 0, 0 );
			weights = new Color32( 255, 0, 0, 0 );
			return;
		}

		var sum = 0.0f;
		for ( var index = 0; index < count; ++index ) sum += normalizedWeights[index];
		Span<byte> encodedWeights = stackalloc byte[4];
		var remaining = 255;
		for ( var index = 0; index < count; ++index )
		{
			var value = index == count - 1
				? remaining
				: Math.Clamp( (int)MathF.Round( normalizedWeights[index] / sum * 255.0f ), 0, remaining );
			encodedWeights[index] = (byte)value;
			remaining -= value;
		}

		indices = new Color32( encodedIndices[0], encodedIndices[1], encodedIndices[2], encodedIndices[3] );
		weights = new Color32( encodedWeights[0], encodedWeights[1], encodedWeights[2], encodedWeights[3] );
	}

	static bool IsVistaModelPath( string path )
	{
		if ( string.IsNullOrWhiteSpace( path ) ) return false;
		var normalized = path.Replace( '\\', '/' );
		return normalized.Contains( "/models/vistas/", StringComparison.OrdinalIgnoreCase )
			|| normalized.Contains( "/vistas/", StringComparison.OrdinalIgnoreCase );
	}

	Material LoadMaterial( string name )
	{
		var normalized = NormalizeMaterialName( name );
		if ( string.IsNullOrWhiteSpace( normalized ) ) return null;
		if ( !ImportHost.TryGetMaterialPath( normalized, out var registeredPath ) ) return null;
		return Material.Load( $"mount://{ImportHost.Ident}/{registeredPath}.vmat" );
	}

	Titanfall2MaterialMetadata GetMaterialMetadata( string materialName )
	{
		if ( ImportHost.TryGetMaterialMetadata( materialName, out var metadata ) ) return metadata;
		return MaterialLoader.InferMetadata( materialName );
	}

	static string NormalizeMaterialName( string name )
	{
		if ( string.IsNullOrWhiteSpace( name ) ) return string.Empty;
		var normalized = string.Join( '/', name.Replace( '\\', '/' ).Split( '/', StringSplitOptions.RemoveEmptyEntries ) );
		foreach ( var extension in new[] { ".vmat", ".rpak", ".matl" } )
		{
			if ( normalized.EndsWith( extension, StringComparison.OrdinalIgnoreCase ) )
				normalized = normalized[..^extension.Length];
		}
		return normalized;
	}

	static string NormalizeAssetPath( string name )
	{
		if ( string.IsNullOrWhiteSpace( name ) ) return string.Empty;
		return string.Join( '/', name.Trim().Replace( '\\', '/' ).Split( '/', StringSplitOptions.RemoveEmptyEntries ) );
	}

	static bool ShouldSkipRenderMaterial( string name )
	{
		if ( string.IsNullOrWhiteSpace( name ) ) return false;
		var normalized = name.Replace( '\\', '/' );
		return normalized.Contains( "toolsnodraw", StringComparison.OrdinalIgnoreCase )
			|| normalized.Contains( "toolsinvisible", StringComparison.OrdinalIgnoreCase )
			|| normalized.Contains( "toolsskybox", StringComparison.OrdinalIgnoreCase )
			|| Titanfall2CollisionFilter.IsNonSolidToolName( normalized )
			|| (!Titanfall2StreamingSettings.AtmosphericCardEffects
				&& Titanfall2CollisionFilter.IsAtmosphericCardName( normalized ));
	}

	static int[] ReverseWinding( IReadOnlyList<int> source )
	{
		var result = new int[source.Count];
		for ( var index = 0; index + 2 < source.Count; index += 3 )
		{
			result[index] = source[index];
			result[index + 1] = source[index + 2];
			result[index + 2] = source[index + 1];
		}
		return result;
	}

	static Vector3 ToSandbox( System.Numerics.Vector3 value ) => new( value.X, value.Y, value.Z );
	static Vector2 ToSandbox( System.Numerics.Vector2 value ) => new( value.X, value.Y );
	static Rotation ToSandbox( System.Numerics.Quaternion value ) => new Rotation( value.X, value.Y, value.Z, value.W ).Normal;
	static bool IsFinite( Vector3 value ) => float.IsFinite( value.x ) && float.IsFinite( value.y ) && float.IsFinite( value.z );
}
