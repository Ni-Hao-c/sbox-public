using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Titanfall2.Formats;

/// <summary>Loads an embedded Titanfall 2 MDL53/RMDL as a runtime model.</summary>
class ModelLoader( ITitanfall2AssetSource source, bool staticInstance = false, int staticLod = 0 ) : ResourceLoader<Titanfall2Mount>
{
	internal const string StaticInstanceSuffix = ".t2static";
	internal const int StaticInstanceLodCount = Titanfall2Mdl53Constants.MaxLods;
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
		if ( !_source.TryReadAllBytes( out var data, out var error ) )
		{
			Titanfall2Log.Warning( $"Failed to read Titanfall 2 model '{Path}': {error}" );
			return null;
		}

		try
		{
			var parsed = Titanfall2Mdl53Reader.Parse( data );
			var lod = Titanfall2MeshExtractor.ExtractLod( parsed, _staticInstance ? _staticLod : 0 );
			if ( _staticInstance && lod.Meshes.Length == 0 && _staticLod != 0 )
			{
				// Some static props only ship LOD0.  Their virtual LOD resource still
				// resolves safely instead of becoming an error model at range.
				lod = Titanfall2MeshExtractor.ExtractLod( parsed, 0 );
			}
			return _staticInstance
				? BuildStaticInstanceModel( lod )
				: BuildModel( parsed, lod, data );
		}
		catch ( Exception exception )
		{
			Titanfall2Log.Warning( $"Failed to convert Titanfall 2 model '{Path}': {exception.Message}" );
			return null;
		}
	}

	internal static string GetStaticInstancePath( string sourceModelPath, int lod = 0 )
		=> lod <= 0
			? sourceModelPath + StaticInstanceSuffix
			: sourceModelPath + StaticInstanceSuffix + $".lod{Math.Clamp( lod, 1, StaticInstanceLodCount - 1 )}";

	internal static bool CanInstance( Model model )
		=> model.IsValid()
			&& StaticInstanceEligibility.TryGetValue( model, out var eligibility )
			&& eligibility.Eligible;

	Model BuildStaticInstanceModel( Titanfall2MeshExtractor.ExtractedLod lod )
	{
		var isVista = IsVistaModelPath( Path );
		var builder = Model.Builder.WithName( Path );
		var hasMesh = false;
		var canInstance = true;

		for ( var meshIndex = 0; meshIndex < lod.Meshes.Length; ++meshIndex )
		{
			var sourceMesh = lod.Meshes[meshIndex];
			if ( sourceMesh.Vertices.Length == 0 || sourceMesh.Indices.Length < 3 || ShouldSkipRenderMaterial( sourceMesh.MaterialName ) )
				continue;

			var materialName = NormalizeMaterialName( sourceMesh.MaterialName );
			var metadata = GetMaterialMetadata( materialName );
			if ( metadata.IsTranslucent
				|| metadata.IsDecal
				|| metadata.IsWater
				|| MaterialLoader.IsGodrayMaterial( materialName ) )
			{
				// SceneCustomObject has one pass classification for the entire
				// batch. Keep mixed/sorted/special materials as individual rigid
				// SceneObjects so their native material pass selection is retained.
				canInstance = false;
			}

			var meshBounds = new BBox { Mins = float.MaxValue, Maxs = float.MinValue };
			var indices = ReverseWinding( sourceMesh.Indices );
			var material = LoadMaterial( materialName )
				?? (isVista
					? MaterialLoader.CreateVistaMaterial( $"{Path}/fallback_{meshIndex}", materialName, metadata )
					: metadata.IsDecal
						? MaterialLoader.CreateDecalMaterial( $"{Path}/decal_fallback_{meshIndex}", metadata )
						: MaterialLoader.CreateRuntimeMaterial( $"{Path}/fallback_{meshIndex}", metadata ));
			var mesh = new Mesh( $"{Path}_{meshIndex}", material );
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

			mesh.CreateVertexBuffer( vertices.Length, vertices );
			mesh.CreateIndexBuffer( indices.Length, indices );
			mesh.Bounds = meshBounds;
			builder.AddMesh( mesh );
			hasMesh = true;
		}

		if ( !hasMesh )
		{
			Titanfall2Log.Warning( $"Titanfall 2 static instance model contains no LOD{_staticLod} geometry: {Path}" );
			return null;
		}

		var model = builder.Create();
		StaticInstanceEligibility.Add( model, new StaticInstanceEligibilityValue( canInstance ) );
		Titanfall2Log.Trace( $"Titanfall 2 static instance model loaded: {Path} "
			+ $"({lod.Meshes.Length} meshes, {lod.VertexCount} vertices, "
			+ $"{lod.TriangleCount} triangles, GPU instancing {(canInstance ? "enabled" : "disabled for special materials")})" );
		return model;
	}

	Model BuildModel( Titanfall2Mdl53Reader.ParsedModel parsed, Titanfall2MeshExtractor.ExtractedLod lod, byte[] data )
	{
		var isVista = IsVistaModelPath( Path );
		var builder = Model.Builder.WithName( Path );
		var skeleton = AddSkeleton( builder, parsed );
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
			var includeCollision = !isVista
				&& Titanfall2CollisionFilter.ShouldIncludeMaterial( materialName, metadata );
			if ( metadata.IsDecal ) decalMeshCount++;

			var meshBounds = new BBox { Mins = float.MaxValue, Maxs = float.MinValue };
			var collisionBase = collisionVertices.Count;
			var indices = ReverseWinding( sourceMesh.Indices );
			var material = LoadMaterial( materialName )
				?? (isVista
					? MaterialLoader.CreateVistaMaterial( $"{Path}/fallback_{meshIndex}", materialName, metadata )
					: metadata.IsDecal
						? MaterialLoader.CreateDecalMaterial( $"{Path}/decal_fallback_{meshIndex}", metadata )
						: MaterialLoader.CreateRuntimeMaterial( $"{Path}/fallback_{meshIndex}", metadata ));
			var mesh = new Mesh( $"{Path}_{meshIndex}", material );

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
			builder.AddMesh( mesh );
			hasMesh = true;
		}

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
			Titanfall2Log.Warning( $"Titanfall 2 model contains neither LOD0 geometry nor supported animations: {Path}" );
			return null;
		}

		Titanfall2Log.Trace( $"Titanfall 2 model loaded: {Path} ({lod.Meshes.Length} meshes, {decalMeshCount} decal meshes, "
			+ $"{lod.VertexCount} vertices, {lod.TriangleCount} triangles, {parsed.Bones.Length} bones, "
			+ $"{animationCount} animations, {collisionHullCount} {collisionDescription} collision hulls)" );
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
			Titanfall2Log.Warning( $"Titanfall 2 model '{Path}' skipped {failureCount - 5} additional invalid animation sequences." );
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
					Titanfall2Log.Warning( $"Unable to read included Titanfall 2 animation model '{path}' for '{Path}': {error}" );
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
					Titanfall2Log.Warning( $"Unable to parse included Titanfall 2 animation model '{path}' for '{Path}': {exception.Message}" );
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
						Titanfall2Log.Trace( $"Titanfall 2 ragdoll imported: {Path} ({bodyCount} bodies, {jointCount} joints)." );
					}
					else
					{
						AddCollisionHulls( builder, parsed, skeleton, hulls );
					}
					return ("VPHY", hulls.Length);
				}
				Titanfall2Log.Warning( $"Embedded Titanfall 2 VPHY collision for '{Path}' contained no finite, indexed convex hulls." );
			}
		}
		catch ( Exception exception )
		{
			Titanfall2Log.Warning( $"Embedded Titanfall 2 VPHY collision for '{Path}' was skipped: {exception.Message}" );
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
				if ( Titanfall2StreamingSettings.PropCollisions || Titanfall2StreamingSettings.NavMeshStaticProps )
					builder.AddCollisionMesh( renderVertices.ToArray(), renderIndices.ToArray() );
				return ("render-mesh fallback", 1);
			}

			if ( CreateBoundsHull( renderVertices ) is { } boundsHull )
			{
				AddCollisionHulls( builder, parsed, skeleton, [boundsHull] );
				Titanfall2Log.Trace( $"Titanfall 2 model collision simplified: {Path} ({triangleCount} render triangles -> one bounds hull)." );
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
		if ( !Host.TryGetMaterialPath( normalized, out var registeredPath ) ) return null;
		return Material.Load( $"mount://{Host.Ident}/{registeredPath}.vmat" );
	}

	Titanfall2MaterialMetadata GetMaterialMetadata( string materialName )
	{
		if ( Host.TryGetMaterialMetadata( materialName, out var metadata ) ) return metadata;
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
