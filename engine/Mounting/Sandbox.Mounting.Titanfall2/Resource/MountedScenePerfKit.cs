// MountedScenePerfKit — reusable rendering-performance primitives for mounted-game maps.
//
// One file, no dependency on any particular game format: the caller supplies vertices and
// materials it has already decoded. Copy this whole file into the next Sandbox.Mounting.*
// project (or keep it in the mount assembly) and call it from the streamer/loader.
//
// 三个可独立使用的单元：
//   1. MountedAggregateBuilder — 世界几何按材质聚合成 AggregateRenderer（提交对象数下降，
//      draw 数不变；每个空间分区仍是独立 fragment，可单独剔除）
//   2. MountedInstanceBatch    — 显式 LOD + 可替换绘制子集的 instanced 静态批次
//   3. MountedInstanceCull     — 实例级距离剔除，零分配快路径，只在绘制集变化时重建
//
// 设计约定（都是实机踩过的坑，别改）：
//   - 合并进同一个顶点缓冲后，AddSubMesh 的 startVertex 必须传 0：索引已经重基过，
//     再传偏移会叠两次，表现为材质错乱/缺失。
//   - 一个 aggregate 最多 AggregateRenderer.MaxFragments 个 fragment；超过就切片，绝不丢弃
//     （丢掉等于该材质整片几何不可见）。
//   - 实例数组视为不可变：渲染线程每帧读取，替换引用而不是原地写。
using System;
using System.Collections.Generic;

namespace Sandbox.Mounting.PerfKit;

/// <summary>
/// 一段几何：同一个材质、同一个空间分区（cell）里的顶点与索引。
/// <typeparamref name="TVertex"/> 是调用方自己的顶点结构（必须 unmanaged）。
/// </summary>
public readonly record struct MountedGeometryPart<TVertex>(
	List<TVertex> Vertices,
	List<int> Indices,
	BBox Bounds ) where TVertex : unmanaged;

/// <summary>一个材质在若干空间分区里的全部几何。</summary>
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
/// 一个 fragment 的 CPU 侧数据：bound，以及它在合并后的顶点/索引缓冲里占多少。
/// </summary>
public readonly record struct MountedAggregateFragment( BBox Bounds, int VertexCount, int IndexCount );

/// <summary>
/// 已经拼接好的聚合体几何。主线程只负责把它变成 <see cref="Mesh"/>/<see cref="Model"/>，
/// 所以顶点拼接可以放到后台线程/有界流水线里做。
/// </summary>
public sealed class MountedAggregateGeometry<TVertex> where TVertex : unmanaged
{
	public List<TVertex> Vertices { get; init; } = new();
	public List<int> Indices { get; init; } = new();
	public BBox Bounds { get; init; }
	public List<MountedAggregateFragment> Fragments { get; init; } = new();
}

/// <summary>
/// 聚合结果：一个材质一个模型，每个空间分区一个 fragment（= 一次 draw、一个独立剔除边界）。
/// </summary>
public sealed record MountedAggregate(
	Material Material,
	Model Model,
	List<AggregateFragmentInfo> Fragments );

/// <summary>
/// 把“按 (空间分区, 材质) 分组的几何”重组成“每个材质一个模型、每个分区一个 draw”。
///
/// 为什么要聚合：场景主线程的渲染成本几乎完全由提交对象数决定，而不是 draw 数或三角形数。
/// 实测等价写法（Titanfall 2 / mp_angel_city）：774 个 ModelRenderer / 4444 draws 聚合成
/// 334 个 AggregateRenderer / 1091 draws，主线程 Render 17.4ms → 13.1ms，画面一致。
/// </summary>
public static class MountedAggregateBuilder
{
	/// <summary>每个 aggregate 允许的最大 fragment 数；0 表示取引擎上限。</summary>
	public static int DefaultMaxFragments => AggregateRenderer.MaxFragments;

	/// <summary>
	/// 构建 aggregate 列表。<paramref name="resourceNamePrefix"/> 是资源名前缀（例如
	/// <c>titanfall2/map/mp_angel_city_world</c>），生成的名字是 <c>..._aggregate_N</c>。
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

			// 表面上看每个材质一个模型；分区数超上限时切成多个模型，但一个都不丢。
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
	/// 把若干段同材质、同分区的几何拼成一份 CPU 侧数据。纯托管、不碰引擎对象，可以放到
	/// 后台线程；调用方负责在数量上做有界流水线（见 Titanfall 2 的 world 聚合器）。
	/// </summary>
	public static MountedAggregateGeometry<TVertex> PrepareGeometry<TVertex>(
		IReadOnlyList<MountedGeometryPart<TVertex>> parts,
		int start,
		int count ) where TVertex : unmanaged
	{
		if ( parts is null ) throw new ArgumentNullException( nameof( parts ) );
		if ( count <= 0 || start < 0 || start + count > parts.Count )
			throw new ArgumentOutOfRangeException( nameof( start ) );

		// 先算总量再分配：一次载入会拼几百 MB 顶点，List 反复扩容的拷贝是真实成本。
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
			// 预分配后这里只是加法；仍然逐项重基，不能整段拷贝（每个分区的索引都从 0 开始）。
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
	/// 把一份已经拼好的 CPU 几何变成 <see cref="Mesh"/>/<see cref="Model"/>。必须在主线程调用。
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
				// 第一个 fragment 用整段范围；后续 fragment 依次追加 sub mesh。
				mesh.SetVertexRange( 0, fragment.VertexCount );
				mesh.SetIndexRange( 0, fragment.IndexCount );
			}
			else
			{
				// base vertex 必须为 0 - 索引已经重基到合并后的顶点缓冲，再传偏移会叠两次。
				mesh.AddSubMesh( material, indexCursor, fragment.IndexCount );
			}

			// 世界几何已经是世界空间，fragment 只需要自己的 bound（identity transform）。
			fragments.Add( new AggregateFragmentInfo( fragment.Bounds ) );
			indexCursor += fragment.IndexCount;
		}

		var model = Model.Builder.WithName( name ).AddMesh( mesh ).Create();
		return new MountedAggregate( material, model, fragments );
	}

	/// <summary>给一个 GameObject 挂上 aggregate 渲染器（子对象、IsStatic）。</summary>
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
/// 一条 instanced 静态批次：一次 DrawModelInstanced 画多个永久实例，
/// 可选显式 LOD（模型里已含全部 LOD 时用），并允许运行期替换“实际绘制的实例子集”。
///
/// 关键行为：显式 LOD 用“离相机最近的实例原点”而不是批次 bounds 来选级。bounds 是全体成员的并集，
/// 最近点通常落在最近实例内部，用 bounds 会系统性地选到更精细的一级，让 A/B 失真。
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

/// <summary>实例批次的提交/绘制开关。默认值 = 静态挂载地图的常用组合。</summary>
public sealed class MountedInstanceBatchOptions
{
	public bool CastShadows { get; init; } = true;
	public bool Batchable { get; init; } = true;
	public bool IncludeInCubemap { get; init; }
	public bool NeedsEnvironmentMap { get; init; }
	public bool NeedsLightProbe { get; init; } = true;
	public Color? Tint { get; init; }
	/// <summary>给定时按“最近实例距离”选 LOD；为 null 时不传显式级别（单级模型 / 引擎自动选级）。</summary>
	public Func<float, int> SelectLod { get; init; }
}

/// <inheritdoc />
public class MountedInstanceBatch : SceneCustomObject, IMountedInstanceBatch
{
	readonly Model _model;
	readonly Func<float, int> _selectLod;
	sealed record DrawSet( Transform[] Transforms, int[] SourceIndices );
	DrawSet _drawSet;
	static long _submittedInstances, _frustumRejectedInstances, _instanceDraws;
	// Native object culling remains active. Extra per-instance CPU frustum tests
	// are opt-in: small, dense batches did not improve total frame time in A/B.
	public bool FrustumCullingEnabled { get; set; }
	public bool PerInstanceLodEnabled { get; set; } = true;
	public static (long Submitted, long FrustumRejected, long Draws) GetStatistics() =>
		(System.Threading.Interlocked.Read( ref _submittedInstances ), System.Threading.Interlocked.Read( ref _frustumRejectedInstances ), System.Threading.Interlocked.Read( ref _instanceDraws ));

	public int InstanceCount => _drawSet.Transforms.Length;
	public Transform[] DrawTransforms => _drawSet.Transforms;

	public bool CastShadows
	{
		get => Flags.CastShadows;
		set => Flags.CastShadows = value;
	}

	/// <summary>
	/// 创建一个 instanced 批次。<paramref name="transforms"/> 交给批次后必须视为不可变：
	/// 渲染线程每帧读取，替换引用而不是原地修改。空数组或无效模型会抛异常，调用方负责回退。
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

	/// <summary>替换绘制实例集。数组被视为不可变（渲染线程每帧读取）。</summary>
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
		if ( FrustumCullingEnabled || (_selectLod is not null && PerInstanceLodEnabled) )
		{
			DrawVisibleInstances( drawSet );
			return;
		}
		System.Threading.Interlocked.Add( ref _submittedInstances, all.Length );
		System.Threading.Interlocked.Increment( ref _instanceDraws );

		if ( _selectLod is null )
		{
			DrawInstances( all, drawSet.SourceIndices, -1 );
			return;
		}

		var camera = Graphics.CameraPosition;
		var nearest = float.MaxValue;
		foreach ( var transform in all )
			nearest = MathF.Min( nearest, camera.Distance( transform.Position ) );
		// 非有限值只可能来自非有限 transform；当作最近一级，保持可见。
		if ( !float.IsFinite( nearest ) ) nearest = 0f;
		DrawInstances( all, drawSet.SourceIndices, _selectLod( nearest ) );
	}

	void DrawVisibleInstances( DrawSet drawSet )
	{
		var all = drawSet.Transforms;
		// A single-instance scene object's native bounds already cover exactly that
		// instance. Avoid fetching six frustum planes and allocating scratch spans.
		if ( all.Length == 1 )
		{
			DrawInstances( all, drawSet.SourceIndices,
				_selectLod is null ? -1 : _selectLod( Graphics.CameraPosition.Distance( all[0].Position ) ) );
			System.Threading.Interlocked.Increment( ref _submittedInstances );
			System.Threading.Interlocked.Increment( ref _instanceDraws );
			return;
		}
		// Small batches use only stack storage. Large batches rent scratch space per
		// invocation, so recording multiple views cannot race on a shared scratch array.
		Transform[] rentedTransforms = null;
		byte[] rentedLods = null;
		int[] rentedSourceIndices = null;
		Span<Transform> visible = all.Length <= 64 ? stackalloc Transform[all.Length]
			: (rentedTransforms = System.Buffers.ArrayPool<Transform>.Shared.Rent( all.Length )).AsSpan( 0, all.Length );
		Span<byte> levels = all.Length <= 64 ? stackalloc byte[all.Length]
			: (rentedLods = System.Buffers.ArrayPool<byte>.Shared.Rent( all.Length )).AsSpan( 0, all.Length );
		Span<int> sourceIndices = all.Length <= 64 ? stackalloc int[all.Length]
			: (rentedSourceIndices = System.Buffers.ArrayPool<int>.Shared.Rent( all.Length )).AsSpan( 0, all.Length );
		try
		{
			var frustum = FrustumCullingEnabled ? Graphics.Frustum : default;
			var camera = Graphics.CameraPosition;
			var bounds = _model.Bounds;
			var radius = bounds.Size.Length * 0.5f;
			Span<int> counts = stackalloc int[4];
			counts.Clear();
			var total = 0;
			var sharedLod = 0;
			if ( _selectLod is not null && !PerInstanceLodEnabled )
			{
				var nearest = float.MaxValue;
				foreach ( var tx in all ) nearest = MathF.Min( nearest, camera.Distance( tx.Position ) );
				sharedLod = Math.Clamp( _selectLod( nearest ), 0, 3 );
			}
			for ( var i = 0; i < all.Length; i++ )
			{
				var tx = all[i];
				var scale = MathF.Max( MathF.Abs( tx.Scale.x ), MathF.Max( MathF.Abs( tx.Scale.y ), MathF.Abs( tx.Scale.z ) ) );
				if ( FrustumCullingEnabled && !frustum.IsInside( tx.PointToWorld( bounds.Center ), radius * scale, partially: true ) )
				{
					levels[i] = byte.MaxValue;
					continue;
				}
				var lod = _selectLod is null ? 0 : PerInstanceLodEnabled ? Math.Clamp( _selectLod( camera.Distance( tx.Position ) ), 0, 3 ) : sharedLod;
				levels[i] = (byte)lod;
				counts[lod]++;
				total++;
			}
			System.Threading.Interlocked.Add( ref _frustumRejectedInstances, all.Length - total );
			System.Threading.Interlocked.Add( ref _submittedInstances, total );
			Span<int> offsets = stackalloc int[4];
			offsets[0] = 0;
			for ( var i = 1; i < 4; i++ ) offsets[i] = offsets[i - 1] + counts[i - 1];
			Span<int> writes = stackalloc int[4];
			offsets.CopyTo( writes );
			for ( var i = 0; i < all.Length; i++ )
			{
				if ( levels[i] == byte.MaxValue ) continue;
				var write = writes[levels[i]]++;
				visible[write] = all[i];
				sourceIndices[write] = drawSet.SourceIndices[i];
			}
			for ( var lod = 0; lod < 4; lod++ )
			{
				if ( counts[lod] == 0 ) continue;
				var group = visible.Slice( offsets[lod], counts[lod] );
				DrawInstances( group, sourceIndices.Slice( offsets[lod], counts[lod] ), _selectLod is null ? -1 : lod );
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
/// 实例级距离剔除：只保留半径内的实例。
///
/// 为什么不是按批次/按 cell 剔除：一个批次覆盖一个 cell（2048 单位）时，整批一起消失会
/// 看到成片 props 突然没了；逐实例剔除减少整片同时消失，但并不提供透明淡出。
///
/// 全部保留时直接复用完整数组；部分保留时，只有成员或输入数组改变才重建子集。
/// 调用方只在 <see cref="Update"/> 返回 true 时替换绘制集。
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

	/// <summary>是否已经执行过一次 <see cref="Update"/>；批次重建后用它决定要不要恢复子集。</summary>
	public bool IsInitialized { get; private set; }

	/// <summary>
	/// 重新计算应绘制的实例集。返回 true 表示绘制集发生变化，调用方应把
	/// <see cref="Subset"/> 交给批次；<see cref="KeptCount"/> 为 0 时整批可以隐藏。
	/// </summary>
	/// <remarks>
	/// 可选的 keepDistances 是逐实例保留距离；提供时覆盖 keepDistance，无穷大表示永不按距离剔除。
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

		// 数量相同不代表成员相同；观察点移动后必须比较实际成员。
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

	/// <summary>把当前子集应用到刚重建的批次上（LOD 换模型时用，避免闪一帧全量实例）。</summary>
	public void ApplyTo( IMountedInstanceBatch batch )
	{
		if ( batch is null || !IsInitialized ) return;
		batch.SetDrawInstances( _subset, _subsetIndices );
	}

	/// <summary>
	/// 强制下一次 <see cref="Update"/> 重建子集，即使保留数量没变。批次被隐藏一段时间后重新可见时用：
	/// 缓存的子集是按旧 anchor 算的，可能含半径外实例、也可能缺刚进入半径的实例。
	/// </summary>
	public void Invalidate()
	{
		_source = null;
		_kept = -1;
	}

	/// <summary>清空状态；下次 <see cref="Update"/> 会重新扫描。剔除被运行时关闭时用。</summary>
	public void Reset()
	{
		IsInitialized = false;
		_source = null;
		_subset = Array.Empty<Transform>();
		_subsetIndices = Array.Empty<int>();
		_kept = 0;
	}
}
