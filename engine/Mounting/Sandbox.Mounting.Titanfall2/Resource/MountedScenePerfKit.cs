// MountedScenePerfKit — reusable rendering-performance primitives for mounted-game maps.
//
// One file, no dependency on any particular game format: the caller supplies vertices and
// materials it has already decoded. Copy this whole file into the next Sandbox.Mounting.*
// project (or keep it in the mount assembly) and call it from the streamer/loader.
//
// Three independent units:
//   1. MountedAggregateBuilder - aggregates world geometry per material into AggregateRenderers
//      (fewer submitted objects, same draw count; each spatial cell stays a separate fragment
//      that is culled on its own).
//   2. MountedInstanceBatch    - instanced static batch with per-instance LOD selection and a
//      replaceable draw subset.
//   3. MountedInstanceCull     - per-instance distance culling with an allocation-free fast path;
//      it rebuilds only when the draw set changes.
//
// Design rules (each one fixed a real bug; do not change):
//   - After merging into one vertex buffer, AddSubMesh must get startVertex 0. The indices are
//     already rebased, so an offset is applied twice and shows up as wrong or missing materials.
//   - An aggregate holds at most AggregateRenderer.MaxFragments fragments. Split into more
//     aggregates beyond that; never drop fragments (that hides the material's geometry).
//   - Instance arrays are immutable: the render thread reads them every frame, so replace the
//     reference instead of writing in place.
using System;
using System.Collections.Generic;

namespace Sandbox.Mounting.PerfKit;

/// <summary>
/// One piece of geometry: the vertices and indices of one material in one spatial cell.
/// <typeparamref name="TVertex"/> is the caller's own (unmanaged) vertex struct.
/// </summary>
public readonly record struct MountedGeometryPart<TVertex>(
	List<TVertex> Vertices,
	List<int> Indices,
	BBox Bounds ) where TVertex : unmanaged;

/// <summary>All geometry of one material across its spatial cells.</summary>
public sealed class MountedGeometryGroup<TVertex> where TVertex : unmanaged
{
	public Material Material { get; }
	public List<MountedGeometryPart<TVertex>> Parts { get; } = new();

	public MountedGeometryGroup( Material material )
	{
		Material = material ?? throw new ArgumentNullException( nameof( material ) );
	}

	public void Add( List<TVertex> vertices, List<int> indices, BBox bounds )
	{
		if ( vertices is null || indices is null ) throw new ArgumentNullException();
		if ( vertices.Count == 0 || indices.Count == 0 ) return;
		Parts.Add( new MountedGeometryPart<TVertex>( vertices, indices, bounds ) );
	}
}

/// <summary>
/// CPU-side data of one fragment: its bounds and its share of the merged vertex/index buffers.
/// </summary>
public readonly record struct MountedAggregateFragment( BBox Bounds, int VertexCount, int IndexCount );

/// <summary>
/// Merged aggregate geometry. The main thread only turns it into a <see cref="Mesh"/>/<see cref="Model"/>,
/// so the vertex merging can run on a background thread or a bounded pipeline.
/// </summary>
public sealed class MountedAggregateGeometry<TVertex> where TVertex : unmanaged
{
	public List<TVertex> Vertices { get; init; } = new();
	public List<int> Indices { get; init; } = new();
	public BBox Bounds { get; init; }
	public List<MountedAggregateFragment> Fragments { get; init; } = new();
}

/// <summary>
/// Aggregate result: one model per material, one fragment per spatial cell (one draw and one
/// culling bound each).
/// </summary>
public sealed record MountedAggregate(
	Material Material,
	Model Model,
	List<AggregateFragmentInfo> Fragments );

/// <summary>
/// Regroups geometry grouped by (spatial cell, material) into one model per material with one
/// draw per cell.
///
/// Aggregation pays off because the scene main thread's render cost is driven by the number of
/// submitted objects, not by draw or triangle counts.
/// </summary>
public static class MountedAggregateBuilder
{
	/// <summary>Maximum fragments per aggregate (the engine limit); a maxFragments of 0 uses this.</summary>
	public static int DefaultMaxFragments => AggregateRenderer.MaxFragments;

	/// <summary>
	/// Builds the aggregate list. <paramref name="resourceNamePrefix"/> is the resource name prefix
	/// (for example <c>titanfall2/map/mp_angel_city_world</c>); models are named <c>..._aggregate_N</c>.
	/// </summary>
	public static List<MountedAggregate> Build<TVertex>(
		IEnumerable<MountedGeometryGroup<TVertex>> groups,
		string resourceNamePrefix,
		int maxFragments = 0 ) where TVertex : unmanaged
	{
		if ( groups is null ) throw new ArgumentNullException( nameof( groups ) );
		if ( string.IsNullOrEmpty( resourceNamePrefix ) ) throw new ArgumentException( "A resource name prefix is required.", nameof( resourceNamePrefix ) );
		if ( maxFragments <= 0 ) maxFragments = DefaultMaxFragments;

		var result = new List<MountedAggregate>();
		var modelIndex = 0;

		foreach ( var group in groups )
		{
			if ( group?.Material is null || group.Parts.Count == 0 ) continue;

			// Normally one model per material. Above the fragment limit, split into several models
			// rather than dropping any cell.
			for ( var start = 0; start < group.Parts.Count; start += maxFragments )
			{
				var count = Math.Min( maxFragments, group.Parts.Count - start );
				var name = $"{resourceNamePrefix}_aggregate_{modelIndex}";
				var geometry = PrepareGeometry( group.Parts, start, count );
				var aggregate = CreateAggregate( group.Material, geometry, name );
				result.Add( aggregate );
				modelIndex++;
			}
		}

		return result;
	}

	/// <summary>
	/// Merges geometry parts of one material into one CPU-side buffer. Purely managed and free of
	/// engine objects, so it can run on a background thread; the caller bounds how much is prepared
	/// ahead (see the Titanfall 2 world aggregator).
	/// </summary>
	public static MountedAggregateGeometry<TVertex> PrepareGeometry<TVertex>(
		IReadOnlyList<MountedGeometryPart<TVertex>> parts,
		int start,
		int count ) where TVertex : unmanaged
	{
		if ( parts is null ) throw new ArgumentNullException( nameof( parts ) );
		if ( count <= 0 || start < 0 || start + count > parts.Count )
			throw new ArgumentOutOfRangeException( nameof( start ) );

		// Size the lists up front: a load merges hundreds of MB of vertices, and repeated List growth copies are a real cost.
		var vertexTotal = 0;
		var indexTotal = 0;
		for ( var index = start; index < start + count; index++ )
		{
			vertexTotal += parts[index].Vertices.Count;
			indexTotal += parts[index].Indices.Count;
		}

		var vertices = new List<TVertex>( vertexTotal );
		var indices = new List<int>( indexTotal );
		var fragments = new List<MountedAggregateFragment>( count );
		var bounds = parts[start].Bounds;

		for ( var index = start; index < start + count; index++ )
		{
			var part = parts[index];
			var vertexOffset = vertices.Count;
			vertices.AddRange( part.Vertices );

			var partIndices = part.Indices;
			// Rebase each index; a block copy is wrong because every cell's indices start at 0.
			for ( var i = 0; i < partIndices.Count; i++ )
				indices.Add( partIndices[i] + vertexOffset );

			if ( index > start ) bounds = bounds.AddBBox( part.Bounds );
			fragments.Add( new MountedAggregateFragment( part.Bounds, part.Vertices.Count, part.Indices.Count ) );
		}

		return new MountedAggregateGeometry<TVertex>
		{
			Vertices = vertices,
			Indices = indices,
			Bounds = bounds,
			Fragments = fragments,
		};
	}

	/// <summary>
	/// Turns merged CPU geometry into a <see cref="Mesh"/>/<see cref="Model"/>. Must be called on the main thread.
	/// </summary>
	public static MountedAggregate CreateAggregate<TVertex>(
		Material material,
		MountedAggregateGeometry<TVertex> geometry,
		string name ) where TVertex : unmanaged
	{
		if ( material is null ) throw new ArgumentNullException( nameof( material ) );
		if ( geometry is null ) throw new ArgumentNullException( nameof( geometry ) );
		if ( geometry.Vertices.Count == 0 || geometry.Indices.Count == 0 )
			throw new ArgumentException( "Aggregate geometry is empty.", nameof( geometry ) );

		var mesh = new Mesh( name, material );
		mesh.CreateVertexBuffer( geometry.Vertices.Count, geometry.Vertices );
		mesh.CreateIndexBuffer( geometry.Indices.Count, geometry.Indices );
		mesh.Bounds = geometry.Bounds;

		var fragments = new List<AggregateFragmentInfo>( geometry.Fragments.Count );
		var indexCursor = 0;
		for ( var index = 0; index < geometry.Fragments.Count; index++ )
		{
			var fragment = geometry.Fragments[index];
			if ( index == 0 )
			{
				// The first fragment uses the mesh's own range; later fragments are appended as sub meshes.
				mesh.SetVertexRange( 0, fragment.VertexCount );
				mesh.SetIndexRange( 0, fragment.IndexCount );
			}
			else
			{
				// Base vertex must be 0: the indices are already rebased to the merged buffer, so an offset would apply twice.
				mesh.AddSubMesh( material, indexCursor, fragment.IndexCount );
			}

			// World geometry is already in world space, so a fragment only needs its bounds (identity transform).
			fragments.Add( new AggregateFragmentInfo( fragment.Bounds ) );
			indexCursor += fragment.IndexCount;
		}

		var model = Model.Builder.WithName( name ).AddMesh( mesh ).Create();
		return new MountedAggregate( material, model, fragments );
	}

	/// <summary>Attaches an aggregate renderer to a static child of <paramref name="parent"/>.</summary>
	public static AggregateRenderer Attach( GameObject parent, string name, MountedAggregate aggregate, bool castShadows )
	{
		if ( parent is null || !parent.IsValid() ) throw new ArgumentNullException( nameof( parent ) );
		if ( aggregate is null ) throw new ArgumentNullException( nameof( aggregate ) );

		var child = new GameObject( parent, true, name );
		child.IsStatic = true;
		var renderer = child.AddComponent<AggregateRenderer>();
		// Set the fragment list before the model: the model setter rebuilds the native aggregate
		// when the component is already active, and an empty fragment list would rebuild to nothing.
		renderer.Fragments = aggregate.Fragments;
		renderer.Model = aggregate.Model;
		renderer.CastShadows = castShadows;
		return renderer;
	}
}

/// <summary>
/// An instanced static batch: DrawModelInstanced draws many permanent instances at once, and the
/// subset of instances actually drawn can be replaced at runtime.
///
/// LOD is chosen per instance from the distance between its own origin and the camera (not from
/// the batch bounds), and instances are drawn grouped by level.
/// </summary>
public interface IMountedInstanceBatch : IValid
{
	int InstanceCount { get; }
	Transform[] DrawTransforms { get; }
	void SetDrawTransforms( Transform[] transforms );
	void SetDrawInstances( Transform[] transforms, int[] sourceIndices ) => SetDrawTransforms( transforms );
	bool CastShadows { get; set; }
	bool RenderingEnabled { get; set; }
	void Delete();
}

/// <summary>Submission and draw flags for an instance batch. Defaults suit static mounted maps.</summary>
public sealed class MountedInstanceBatchOptions
{
	public bool CastShadows { get; init; } = true;
	public bool Batchable { get; init; } = true;
	public bool IncludeInCubemap { get; init; }
	public bool NeedsEnvironmentMap { get; init; }
	public bool NeedsLightProbe { get; init; } = true;
	public Color? Tint { get; init; }
	/// <summary>Maps an instance's camera distance to its LOD level. When null, no explicit level is passed to the draw.</summary>
	public Func<float, int> SelectLod { get; init; }
}

/// <inheritdoc />
public class MountedInstanceBatch : SceneCustomObject, IMountedInstanceBatch
{
	readonly Model _model;
	readonly Func<float, int> _selectLod;
	sealed record DrawSet( Transform[] Transforms, int[] SourceIndices );
	DrawSet _drawSet;
	static long _submittedInstances, _instanceDraws;
	public static (long Submitted, long Draws) GetStatistics() =>
		(System.Threading.Interlocked.Read( ref _submittedInstances ), System.Threading.Interlocked.Read( ref _instanceDraws ));

	/// <summary>
	/// Camera grid the per-instance LOD choice is snapped to, in world units. The LOD groups are
	/// rebuilt on every draw, and <see cref="Titanfall2ProbeInstanceBatch"/> re-uploads an index
	/// table whenever a group's membership changes: a camera that jitters around a LOD boundary
	/// would therefore re-upload group tables every single frame while walking. Snapping the camera
	/// to this grid gives every instance the same deadband and caps re-partitioning to one grid
	/// crossing. The LOD switch point moves by at most the camera's snap error (a few percent of the
	/// nearest switch distance, under 1% of the far ones), and every instance of a batch is judged
	/// with the same snapped camera in a given view.
	/// </summary>
	const float LodCameraGrid = 32f;

	static Vector3 QuantizedLodCamera( Vector3 camera )
	{
		const float inverseGrid = 1f / LodCameraGrid;
		return new Vector3(
			MathF.Floor( camera.x * inverseGrid ) * LodCameraGrid,
			MathF.Floor( camera.y * inverseGrid ) * LodCameraGrid,
			MathF.Floor( camera.z * inverseGrid ) * LodCameraGrid );
	}

	public int InstanceCount => _drawSet.Transforms.Length;
	public Transform[] DrawTransforms => _drawSet.Transforms;

	public bool CastShadows
	{
		get => Flags.CastShadows;
		set => Flags.CastShadows = value;
	}

	/// <summary>
	/// Creates an instanced batch. Treat <paramref name="transforms"/> as immutable once passed in:
	/// the render thread reads it every frame, so replace the reference instead of editing it. An empty
	/// array or invalid model throws; the caller handles the fallback.
	/// </summary>
	public MountedInstanceBatch(
		SceneWorld world,
		Model model,
		Transform[] transforms,
		MountedInstanceBatchOptions options = null )
		: base( world )
	{
		if ( !model.IsValid() ) throw new ArgumentException( "A valid model is required.", nameof( model ) );
		if ( transforms is null || transforms.Length == 0 ) throw new ArgumentException( "At least one instance transform is required.", nameof( transforms ) );

		options ??= new MountedInstanceBatchOptions();
		_model = model;
		SetDrawTransforms( transforms );
		_selectLod = options.SelectLod;

		var localBounds = model.Bounds;
		var worldBounds = localBounds.Transform( transforms[0] );
		for ( var index = 1; index < transforms.Length; index++ )
			worldBounds = worldBounds.AddBBox( localBounds.Transform( transforms[index] ) );

		Bounds = worldBounds;
		Batchable = options.Batchable;
		Flags.IsOpaque = true;
		Flags.IsTranslucent = false;
		Flags.IsStatic = true;
		Flags.CastShadows = options.CastShadows;
		Flags.WantsPrePass = true;
		Flags.NeedsLightProbe = options.NeedsLightProbe;
		Flags.NeedsEnvironmentMap = options.NeedsEnvironmentMap;
		Flags.IncludeInCubemap = options.IncludeInCubemap;
		if ( options.Tint is { } tint ) ColorTint = tint;
	}

	/// <summary>Replaces the drawn instance set. The array is treated as immutable (the render thread reads it every frame).</summary>
	public void SetDrawTransforms( Transform[] transforms ) => SetDrawInstances( transforms, null );

	/// <summary>Keep caller-owned per-instance data associated through subset changes.</summary>
	public void SetDrawInstances( Transform[] transforms, int[] sourceIndices )
	{
		transforms ??= Array.Empty<Transform>();
		if ( sourceIndices is not null && sourceIndices.Length != transforms.Length )
			throw new ArgumentException( "Each draw transform needs a source index.", nameof( sourceIndices ) );
		if ( sourceIndices is null )
		{
			sourceIndices = new int[transforms.Length];
			for ( var i = 0; i < sourceIndices.Length; ++i ) sourceIndices[i] = i;
		}
		_drawSet = new DrawSet( transforms, sourceIndices );
	}

	public new virtual void Delete() => base.Delete();

	/// <summary>Draw a view/LOD subset with its original instance identities.</summary>
	protected virtual void DrawInstances( Span<Transform> transforms, ReadOnlySpan<int> sourceIndices, int lodLevel )
	{
		if ( lodLevel < 0 ) Graphics.DrawModelInstanced( _model, transforms );
		else Graphics.DrawModelInstanced( _model, transforms, lodLevel );
	}

	public override void RenderSceneObject()
	{
		var drawSet = _drawSet; // One immutable snapshot for both arrays in this view/pass.
		var all = drawSet.Transforms;
		if ( !_model.IsValid() || all.Length == 0 ) return;
		if ( _selectLod is not null )
		{
			DrawPerInstanceLods( drawSet );
			return;
		}
		System.Threading.Interlocked.Add( ref _submittedInstances, all.Length );
		System.Threading.Interlocked.Increment( ref _instanceDraws );
		DrawInstances( all, drawSet.SourceIndices, -1 );
	}

	/// <summary>Groups the instances by their own LOD level and issues one draw per level.</summary>
	void DrawPerInstanceLods( DrawSet drawSet )
	{
		var all = drawSet.Transforms;
		// A single instance needs no grouping and no scratch spans.
		if ( all.Length == 1 )
		{
			DrawInstances( all, drawSet.SourceIndices, _selectLod( QuantizedLodCamera( Graphics.CameraPosition ).Distance( all[0].Position ) ) );
			System.Threading.Interlocked.Increment( ref _submittedInstances );
			System.Threading.Interlocked.Increment( ref _instanceDraws );
			return;
		}
		// Small batches use only stack storage. Large batches rent scratch space per
		// invocation, so recording multiple views cannot race on a shared scratch array.
		Transform[] rentedTransforms = null;
		byte[] rentedLods = null;
		int[] rentedSourceIndices = null;
		Span<Transform> grouped = all.Length <= 64 ? stackalloc Transform[all.Length]
			: (rentedTransforms = System.Buffers.ArrayPool<Transform>.Shared.Rent( all.Length )).AsSpan( 0, all.Length );
		Span<byte> levels = all.Length <= 64 ? stackalloc byte[all.Length]
			: (rentedLods = System.Buffers.ArrayPool<byte>.Shared.Rent( all.Length )).AsSpan( 0, all.Length );
		Span<int> sourceIndices = all.Length <= 64 ? stackalloc int[all.Length]
			: (rentedSourceIndices = System.Buffers.ArrayPool<int>.Shared.Rent( all.Length )).AsSpan( 0, all.Length );
		try
		{
			// Snapped camera: the LOD groups (and the probe index tables they upload) only change
			// when the camera crosses a grid line, not on every unit of movement.
			var camera = QuantizedLodCamera( Graphics.CameraPosition );
			Span<int> counts = stackalloc int[4];
			counts.Clear();
			for ( var i = 0; i < all.Length; i++ )
			{
				var lod = Math.Clamp( _selectLod( camera.Distance( all[i].Position ) ), 0, 3 );
				levels[i] = (byte)lod;
				counts[lod]++;
			}
			System.Threading.Interlocked.Add( ref _submittedInstances, all.Length );
			Span<int> offsets = stackalloc int[4];
			offsets[0] = 0;
			for ( var i = 1; i < 4; i++ ) offsets[i] = offsets[i - 1] + counts[i - 1];
			Span<int> writes = stackalloc int[4];
			offsets.CopyTo( writes );
			for ( var i = 0; i < all.Length; i++ )
			{
				var write = writes[levels[i]]++;
				grouped[write] = all[i];
				sourceIndices[write] = drawSet.SourceIndices[i];
			}
			for ( var lod = 0; lod < 4; lod++ )
			{
				if ( counts[lod] == 0 ) continue;
				var group = grouped.Slice( offsets[lod], counts[lod] );
				DrawInstances( group, sourceIndices.Slice( offsets[lod], counts[lod] ), lod );
				System.Threading.Interlocked.Increment( ref _instanceDraws );
			}
		}
		finally
		{
			if ( rentedTransforms is not null ) System.Buffers.ArrayPool<Transform>.Shared.Return( rentedTransforms );
			if ( rentedLods is not null ) System.Buffers.ArrayPool<byte>.Shared.Return( rentedLods );
			if ( rentedSourceIndices is not null ) System.Buffers.ArrayPool<int>.Shared.Return( rentedSourceIndices );
		}
	}
}

/// <summary>
/// Per-instance distance culling: keeps only the instances within range.
///
/// Culling per batch or per cell would make whole patches of props vanish at once; culling per
/// instance avoids that, but it does not fade instances out.
///
/// When every instance is kept the full array is reused; otherwise the subset is rebuilt only when
/// its membership or the input array changes. Callers replace the draw set only when
/// <see cref="Update"/> returns true.
/// </summary>
public sealed class MountedInstanceCull
{
	Transform[] _source;
	Transform[] _subset = Array.Empty<Transform>();
	int[] _subsetIndices = Array.Empty<int>();
	int _kept;

	public Transform[] Subset => _subset;
	public int[] SubsetIndices => _subsetIndices;
	public int KeptCount => _kept;

	/// <summary>Whether <see cref="Update"/> has run at least once.</summary>
	public bool IsInitialized { get; private set; }

	/// <summary>
	/// Recomputes the instances to draw. Returns true when the draw set changed, in which case the
	/// caller passes <see cref="Subset"/> to the batch; when <see cref="KeptCount"/> is 0 the whole batch can be hidden.
	/// </summary>
	/// <remarks>
	/// The optional keepDistances holds per-instance keep distances that override keepDistance;
	/// infinity means the instance is never distance-culled.
	/// </remarks>
	public bool Update( Transform[] all, Vector3 anchor, float keepDistance, IReadOnlyList<float> radii = null, float landmarkRadius = float.MaxValue,
		IReadOnlyList<float> keepDistances = null )
	{
		IsInitialized = true;

		if ( all is null || all.Length == 0 )
		{
			var changed = _kept != 0 || _subset.Length != 0;
			_source = all;
			_subset = Array.Empty<Transform>();
			_subsetIndices = Array.Empty<int>();
			_kept = 0;
			return changed;
		}

		var kept = 0;
		var membershipChanged = false;
		for ( var index = 0; index < all.Length; index++ )
		{
			var transform = all[index];
			if ( !Keeps( transform, index, anchor, keepDistance, radii, landmarkRadius, keepDistances ) ) continue;
			if ( kept >= _subset.Length || _subset[kept] != transform
				|| (_subsetIndices is not null && _subsetIndices[kept] != index) ) membershipChanged = true;
			kept++;
		}

		if ( kept == all.Length )
		{
			var changed = !ReferenceEquals( _subset, all ) || _kept != kept;
			_source = all;
			_subset = all;
			_subsetIndices = null; // The complete array has identity indices.
			_kept = kept;
			return changed;
		}

		if ( kept == 0 )
		{
			var changed = _kept != 0;
			_source = all;
			_subset = Array.Empty<Transform>();
			_subsetIndices = Array.Empty<int>();
			_kept = 0;
			return changed;
		}

		// The same count does not mean the same members; after the anchor moves, compare actual membership.
		var rebuild = !ReferenceEquals( _source, all ) || _kept != kept || membershipChanged;
		_source = all;
		_kept = kept;
		if ( !rebuild ) return false;

		var result = new Transform[kept];
		var resultIndices = new int[kept];
		var write = 0;
		for ( var index = 0; index < all.Length; index++ )
		{
			var transform = all[index];
			if ( Keeps( transform, index, anchor, keepDistance, radii, landmarkRadius, keepDistances ) )
			{
				result[write] = transform;
				resultIndices[write++] = index;
			}
		}

		_subset = result;
		_subsetIndices = resultIndices;
		return true;
	}

	static bool Keeps( Transform transform, int index, Vector3 anchor, float keepDistance, IReadOnlyList<float> radii,
		float landmarkRadius, IReadOnlyList<float> keepDistances )
	{
		var radius = radii is not null && index < radii.Count ? MathF.Max( 0, radii[index] ) : 0f;
		if ( landmarkRadius > 0 && radius >= landmarkRadius ) return true;
		var distance = (keepDistances is not null && index < keepDistances.Count ? keepDistances[index] : keepDistance) + radius;
		return float.IsPositiveInfinity( distance ) || anchor.DistanceSquared( transform.Position ) <= distance * distance;
	}

	/// <summary>Applies the current subset to a batch.</summary>
	public void ApplyTo( IMountedInstanceBatch batch )
	{
		if ( batch is null || !IsInitialized ) return;
		batch.SetDrawInstances( _subset, _subsetIndices );
	}

	/// <summary>
	/// Forces the next <see cref="Update"/> to rebuild the subset even if the kept count is unchanged.
	/// Use it when a batch becomes visible again after being hidden: the cached subset was computed
	/// for an old anchor and may include out-of-range instances or miss ones that came into range.
	/// </summary>
	public void Invalidate()
	{
		_source = null;
		_kept = -1;
	}
}
