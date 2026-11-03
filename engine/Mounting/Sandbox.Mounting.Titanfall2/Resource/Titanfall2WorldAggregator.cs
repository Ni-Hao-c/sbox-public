using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using Sandbox.Mounting.PerfKit;

/// <summary>
/// Feeds Titanfall 2 BSP world geometry into the reusable PerfKit aggregate builder.
///
/// The BSP parse already holds every vertex in memory, so the parallel work is merging each
/// material's per-cell vertices/indices into aggregates, not file reading. That merge is purely
/// managed and runs ahead on background threads through a bounded queue, while the main thread
/// only creates Material/Mesh/Model (engine objects must be touched on the main thread). Material
/// resolution and texture IO therefore overlap the vertex merging, and the amount of prepared
/// geometry stays within a fixed window.
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
	/// One model per material, one fragment per spatial cell. The returned list can be passed
	/// straight to <see cref="MountedAggregateBuilder.Attach"/>; <paramref name="totalMeshCount"/>
	/// is the number of models, including split slices. <paramref name="cellSize"/> is only logged,
	/// so an A/B run can be told apart in the load log. <paramref name="aggregateKeys"/> gives the
	/// material key each returned aggregate was built from, in the same order; the caller needs it
	/// to decide which aggregates still have to cast shadows themselves.
	/// </summary>
	public static List<MountedAggregate> Build(
		Dictionary<WorldRenderCell, Dictionary<WorldMaterialKey, MapMeshGroup>> chunks,
		string mapName,
		string category,
		float cellSize,
		string resourcePath,
		Sandbox.Diagnostics.Logger log,
		Func<WorldMaterialKey, int, Material> materialFactory,
		out int totalMeshCount,
		out IReadOnlyList<WorldMaterialKey> aggregateKeys )
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
		// Largest fragment that will be drawn: one part is one (cell, material) draw.
		var largestPartVertices = 0;
		var largestPartIndices = 0;
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
				largestPartVertices = Math.Max( largestPartVertices, source.Vertices.Count );
				largestPartIndices = Math.Max( largestPartIndices, source.Indices.Count );
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
		var prepareTimer = new Stopwatch();
		var producerError = default( Exception );
		var wallTimer = Stopwatch.StartNew();

		// Prepare-ahead window: one chunk being merged plus at most two queued slices. The queue is
		// bounded, so a slow main thread makes the producer wait instead of filling memory with
		// every material's merged arrays.
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
		var keys = new List<WorldMaterialKey>( slices.Count );

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
				keys.Add( prepared.Key );
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
		aggregateKeys = keys;
		var fragmentCount = result.Sum( static aggregate => aggregate.Fragments.Count );
		log.Info( $"Titanfall 2 world aggregates: {resourcePath} ({result.Count} models/{totalMeshCount} meshes from "
			+ $"{batches.Count} materials, {fragmentCount} cell fragments, {sourceVertexCount} vertices; "
			+ $"cell {cellSize:0}u, up to {maxFragments} fragments/aggregate, largest fragment {largestPartVertices}V/{largestPartIndices}I; "
			+ $"materials {materialTime.TotalSeconds:0.00}s, "
			+ $"vertex/index prepare {prepareTimer.Elapsed.TotalSeconds:0.00}s, "
			+ $"mesh build {meshTime.TotalSeconds:0.00}s, total {wallTimer.Elapsed.TotalSeconds:0.00}s)" );
		return result;
	}

	static string BuildResourceNamePrefix( string mapName, string category ) => $"titanfall2/map/{mapName}_{category}";
}
