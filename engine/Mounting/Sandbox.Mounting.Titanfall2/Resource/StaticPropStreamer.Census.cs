public sealed partial class Titanfall2StaticPropStreamer
{
	sealed record ModelCensus( string Name, int MaterialBindings, ModelLoader.StaticGeometryStatistics Geometry );
	sealed record RenderCensus( int ActualMaterialIdentities, int[] BatchSizeUpperBounds, int[] BatchSizeCounts, ModelCensus[] Models );
	RenderCensus _renderCensus;
	readonly Guid _sceneGeneration = Guid.NewGuid();

	void CompileRenderCensus()
	{
		var upperBounds = new[] { 1, 4, 8, 16, 32, 64, 128, int.MaxValue };
		var counts = new int[upperBounds.Length];
		var models = new HashSet<Model>( _individualModels );
		foreach ( var cluster in _clusters.Values )
		{
			foreach ( var batch in cluster.Batches.Values )
			{
				models.Add( batch.Model );
				var bucket = Array.FindIndex( upperBounds, maximum => batch.Transforms.Count <= maximum );
				counts[bucket]++;
			}
		}
		var materials = new HashSet<Material>();
		var census = new List<ModelCensus>();
		foreach ( var model in models.OrderBy( model => model.Name, StringComparer.Ordinal ) )
		{
			foreach ( var material in model.Materials ) materials.Add( material );
			ModelLoader.TryGetGeometryStatistics( model, out var geometry );
			census.Add( new( model.Name, model.Materials.Length, geometry ) );
		}
		_renderCensus = new( materials.Count, upperBounds, counts, census.ToArray() );
	}

	/// <summary>
	/// Explicit diagnostic snapshot. No serialization, model enumeration or file I/O happens in
	/// the frame loop. Material identities are a census, not a promise of final MDI group count.
	/// </summary>
	public object GetCompiledSceneStatistics() => new
	{
		MapPath,
		SceneGeneration = _sceneGeneration,
		Configuration = Titanfall2EffectiveConfiguration.Capture(),
		RuntimeState = _runtimePhase.ToString(),
		CpuRenderClusters = _cpuRenderClusters,
		PayloadVersion = DataVersion,
		CompiledLayout = _compiledScene?.FromCompiledPayload ?? false,
		Instances = _compiledScene?.Props.Length ?? 0,
		ModelSkinGroups = _compiledScene?.Layout.Models.Length ?? 0,
		SpatialCells = _compiledScene?.Layout.Cells.Length ?? 0,
		PvsCells = _compiledVisibility?.CellCount ?? 0,
		PvsTableBytes = (_compiledVisibility?.CellCount ?? 0) * 16,
		KeepAllCollisions,
		PreparationComplete,
		ResourcesPrepared,
		ResidentCollisionsReady,
		ResidentCollisionCount,
		CollidableProps = _collidableProps,
		FilteredCollisions = _collisionFilteredProps,
		MissingCollisionModels = _colliderFailures,
		UnresolvedCollisions = Math.Max( 0, _collidableProps - _collidersCreated - _collisionFilteredProps - _colliderFailures ),
		IndividualSpecialMaterialProps = _plainStaticProps,
		FullModelFallbackProps = _plainFallbackProps,
		FrozenSkinnedProps = _skinnedProps,
		FailedProps = _failedProps,
		FullGpuInstances = FullGpuInstanceCount,
		MixedGpuInstances = _mixedGpuInstanceCount,
		GpuRenderingEnabled = _gpuScene.IsValid(),
		Render = _renderCensus,
		GpuScene = _gpuScene?.Statistics
	};
}
