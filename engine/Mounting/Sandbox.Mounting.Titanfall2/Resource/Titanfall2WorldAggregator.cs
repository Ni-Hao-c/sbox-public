using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using Sandbox.Mounting.PerfKit;

/// <summary>
/// 把 Titanfall 2 的 BSP 世界几何接到可复用 PerfKit 的聚合构建器上。
///
/// TF2 的 BSP 在解析阶段已经把所有顶点读进内存，所以这里能并行化的不是“读文件”，而是
/// “按材质把各分区的顶点/索引拼成聚合体”。这一段是纯托管计算，放到后台线程用有界队列
/// 预先生成，主线程只负责创建 Material/Mesh/Model（引擎对象必须在主线程碰）。
/// 这样材质解析/贴图 IO 与顶点拼接可以重叠，同时把预生成的几何数量限制在固定窗口内。
/// </summary>
static class Titanfall2WorldAggregator
{
	sealed record MaterialBatch(
		WorldMaterialKey Key,
		List<MountedGeometryPart<TitanfallMapVertex>> Parts );

	readonly record struct Slice(
		WorldMaterialKey Key,
		List<MountedGeometryPart<TitanfallMapVertex>> Parts,
		int Start,
		int Count );

	readonly record struct Prepared(
		WorldMaterialKey Key,
		MountedAggregateGeometry<TitanfallMapVertex> Geometry );

	/// <summary>
	/// 每个材质一个模型、每个空间分区一个 fragment。返回的列表可直接交给
	/// <see cref="MountedAggregateBuilder.Attach"/> 挂到场景里；<paramref name="totalMeshCount"/>
	/// 保留原来的语义（模型数，含切片）。
	/// </summary>
	public static List<MountedAggregate> Build(
		Dictionary<WorldRenderCell, Dictionary<WorldMaterialKey, MapMeshGroup>> chunks,
		string mapName,
		string category,
		string resourcePath,
		Sandbox.Diagnostics.Logger log,
		Func<WorldMaterialKey, int, Material> materialFactory,
		out int totalMeshCount )
	{
		var byMaterial = new Dictionary<WorldMaterialKey, List<(WorldRenderCell Cell, MapMeshGroup Group)>>();
		foreach ( var (cell, materials) in chunks )
		{
			foreach ( var (materialKey, group) in materials )
			{
				if ( group.Vertices.Count == 0 || group.Indices.Count == 0 ) continue;
				if ( !byMaterial.TryGetValue( materialKey, out var bucket ) )
				{
					bucket = new List<(WorldRenderCell, MapMeshGroup)>();
					byMaterial[materialKey] = bucket;
				}
				bucket.Add( (cell, group) );
			}
		}

		var batches = new List<MaterialBatch>( byMaterial.Count );
		var sourceVertexCount = 0;
		foreach ( var (materialKey, cells) in byMaterial
			.OrderBy( static pair => pair.Key.MaterialName, StringComparer.OrdinalIgnoreCase )
			.ThenBy( static pair => pair.Key.LightmapPage ) )
		{
			if ( cells.Count == 0 ) continue;

			var parts = new List<MountedGeometryPart<TitanfallMapVertex>>( cells.Count );
			var batchVertexCount = 0;
			foreach ( var (_, source) in cells )
			{
				parts.Add( new MountedGeometryPart<TitanfallMapVertex>( source.Vertices, source.Indices, source.Bounds ) );
				batchVertexCount += source.Vertices.Count;
			}
			sourceVertexCount += batchVertexCount;
			batches.Add( new MaterialBatch( materialKey, parts ) );
		}

		var maxFragments = AggregateRenderer.MaxFragments;
		var slices = new List<Slice>();
		foreach ( var batch in batches )
		{
			for ( var start = 0; start < batch.Parts.Count; start += maxFragments )
			{
				var count = Math.Min( maxFragments, batch.Parts.Count - start );
				slices.Add( new Slice( batch.Key, batch.Parts, start, count ) );
			}
		}

		var result = new List<MountedAggregate>( slices.Count );
		if ( !Titanfall2StreamingSettings.ParallelAggregateBuild )
		{
			var sequentialTimer = Stopwatch.StartNew();
			for ( var index = 0; index < slices.Count; index++ )
			{
				var slice = slices[index];
				var material = materialFactory( slice.Key, index );
				var geometry = MountedAggregateBuilder.PrepareGeometry( slice.Parts, slice.Start, slice.Count );
				var name = $"{BuildResourceNamePrefix( mapName, category )}_aggregate_{index}";
				result.Add( MountedAggregateBuilder.CreateAggregate( material, geometry, name ) );
			}
			totalMeshCount = result.Count;
			log.Info( $"Titanfall 2 world aggregates: {resourcePath} ({result.Count} models/{totalMeshCount} meshes, "
				+ $"{sourceVertexCount} vertices; sequential build {sequentialTimer.Elapsed.TotalSeconds:0.00}s; "
				+ "set SBOX_TITANFALL2_PARALLEL_AGGREGATE=1 to A/B the bounded pipeline)" );
			return result;
		}

		var prepareTimer = new Stopwatch();
		var producerError = default( Exception );
		var wallTimer = Stopwatch.StartNew();

		// 预生成窗口：一个 chunk 的几何 + 队列里最多两个 chunk。队列有界，所以主线程慢时
		// 后台线程会等待，而不是把所有材质的合并数组一次性铺满内存。
		var chunkSize = Math.Max( 1, Environment.ProcessorCount / 2 );
		var prepareOptions = new ParallelOptions
		{
			MaxDegreeOfParallelism = Math.Max( 1, Environment.ProcessorCount - 1 ),
		};
		using var queue = new BlockingCollection<Prepared>( 2 );
		using var cancellation = new CancellationTokenSource();
		prepareOptions.CancellationToken = cancellation.Token;

		var producer = Task.Run( () =>
		{
			try
			{
				for ( var baseIndex = 0; baseIndex < slices.Count; baseIndex += chunkSize )
				{
					cancellation.Token.ThrowIfCancellationRequested();
					var count = Math.Min( chunkSize, slices.Count - baseIndex );
					var prepared = new Prepared[count];

					prepareTimer.Start();
					Parallel.For( 0, count, prepareOptions, index =>
					{
						var slice = slices[baseIndex + index];
						prepared[index] = new Prepared(
							slice.Key,
							MountedAggregateBuilder.PrepareGeometry( slice.Parts, slice.Start, slice.Count ) );
					} );
					prepareTimer.Stop();

					// Enqueue in slice order so aggregate names and material resolution stay deterministic.
					foreach ( var item in prepared )
						queue.Add( item, cancellation.Token );
				}
			}
			catch ( Exception exception )
			{
				producerError = exception;
			}
			finally
			{
				queue.CompleteAdding();
			}
		} );

		var materialTime = TimeSpan.Zero;
		var meshTime = TimeSpan.Zero;
		var stageTimer = new Stopwatch();
		var modelIndex = 0;

		// The consumer runs on the calling (main) thread: Material.Load/Create and Model.Builder
		// are engine objects. While it waits for the next slice, the producer keeps merging.
		try
		{
			foreach ( var prepared in queue.GetConsumingEnumerable() )
			{
				stageTimer.Restart();
				var material = materialFactory( prepared.Key, modelIndex );
				materialTime += stageTimer.Elapsed;

				stageTimer.Restart();
				var name = $"{BuildResourceNamePrefix( mapName, category )}_aggregate_{modelIndex}";
				result.Add( MountedAggregateBuilder.CreateAggregate( material, prepared.Geometry, name ) );
				meshTime += stageTimer.Elapsed;
				modelIndex++;
			}
		}
		finally
		{
			// If material/mesh creation throws, the producer may be waiting on a
			// full queue. Cancel and join it before disposing the queue and data.
			cancellation.Cancel();
			producer.GetAwaiter().GetResult();
		}

		if ( producerError is not null )
			throw new InvalidOperationException( "Titanfall 2 world aggregate preparation failed.", producerError );

		wallTimer.Stop();
		totalMeshCount = result.Count;
		var fragmentCount = result.Sum( static aggregate => aggregate.Fragments.Count );
		log.Info( $"Titanfall 2 world aggregates: {resourcePath} ({result.Count} models/{totalMeshCount} meshes, "
			+ $"{fragmentCount} cell fragments, {sourceVertexCount} vertices; "
			+ $"materials {materialTime.TotalSeconds:0.00}s, "
			+ $"vertex/index prepare {prepareTimer.Elapsed.TotalSeconds:0.00}s, "
			+ $"mesh build {meshTime.TotalSeconds:0.00}s, total {wallTimer.Elapsed.TotalSeconds:0.00}s)" );
		return result;
	}

	static string BuildResourceNamePrefix( string mapName, string category ) => $"titanfall2/map/{mapName}_{category}";
}
