using static Titanfall2StaticPropSceneData;
using System.Diagnostics;
using Sandbox.Mounting.PerfKit;
using Titanfall2;

public sealed partial class Titanfall2StaticPropStreamer
{
	/// <summary>
	/// Restores the CellBSP tree when the component was recreated from scene state.
	/// Rendering a mounted map instantiates the prop streamer twice: the first instance
	/// is configured directly and receives the tree by reference, the second is built
	/// from serialized properties and only sees <see cref="EncodedVisibility"/>. The
	/// measured symptom of not restoring it was a PVS diagnosis reporting
	/// cells=0/planes=0/nodes=0 while the map itself had parsed 74 cells and 712 portals.
	/// </summary>
	void EnsureVisibility()
	{
		if ( _visibility is { IsValid: true } )
		{
			_compiledVisibility ??= Titanfall2CompiledVisibility.Compile( _visibility, PvsPortalDepth );
			return;
		}

		if ( string.IsNullOrWhiteSpace( EncodedVisibility ) )
		{
			_visibilityRestoreFailed = true;
			return;
		}

		try
		{
			_visibility = ReadVisibility( EncodedVisibility, out _compiledVisibility );
			_visibilityRestoreFailed = _visibility is null;
		}
		catch ( Exception exception )
		{
			_visibilityRestoreFailed = true;
			Log.Warning( exception, $"Unable to restore Titanfall 2 PVS data for '{MapPath}'." );
		}
	}

	static string Encode( IReadOnlyList<Titanfall2BspReader.StaticPropInstance> props, Titanfall2BspReader.BspVisibility visibility )
		=> Titanfall2StaticPropSceneData.EncodeWithFade( props, visibility, null );

	static IEnumerable<StreamedProp> Decode( string encoded ) => Titanfall2StaticPropSceneData.ReadCompiledScene( encoded ).Props;

	void InitializeCells()
	{
		if ( _initialized ) return;
		_initialized = true;
		EnsureVisibility();
		if ( string.IsNullOrWhiteSpace( EncodedProps ) ) return;

		try
		{
			_propsWithPvsCells = 0;
			_compiledScene ??= ReadCompiledScene( EncodedProps );
			foreach ( var compiledCell in _compiledScene.Layout.Cells )
			{
				var coordinate = new CellCoordinate( compiledCell.X, compiledCell.Y );
				var clusterCoordinate = ClusterCoordinate.FromCell( coordinate, BatchClusterCells );
				if ( !_clusters.TryGetValue( clusterCoordinate, out var cluster ) )
				{
					cluster = new PropCluster( clusterCoordinate );
					_clusters.Add( clusterCoordinate, cluster );
				}
				var cell = new PropCell( coordinate, cluster );
				_cells.Add( coordinate, cell );
				cluster.Cells.Add( cell );
				for ( var i = compiledCell.First; i < compiledCell.First + compiledCell.Count; ++i )
				{
					var prop = _compiledScene.Props[_compiledScene.Layout.CellInstances[i]];
					cell.Props.Add( prop );
					if ( !prop.PvsCells.IsEmpty ) _propsWithPvsCells++;
				}
			}
			_totalProps = _compiledScene.Props.Length;
			Log.Info( $"Titanfall 2 compiled static scene: {_totalProps} stable instances, "
				+ $"{_compiledScene.Layout.Models.Length} model/skin groups, {_compiledScene.Layout.Cells.Length} spatial cells "
				+ $"({(_compiledScene.FromCompiledPayload ? "imported layout" : "legacy layout compiled during load")}; {MapPath})." );

			_refreshTimer.Start();
			_statusTimer.Start();
			Log.Info( $"Titanfall 2 prop streamer ready: {_totalProps} props in "
				+ $"{_cells.Count} cells; prioritize within {LoadRadius:0}, "
				+ "then load the full map and retain all loaded props until scene teardown, "
				+ $"{PropsPerFrame} props/frame, "
				+ $"{FrameBudgetMilliseconds:0.0} ms budget ({MapPath})." );
			Log.Info( $"Titanfall 2 prop batch clustering: {_cells.Count} prop cells grouped into "
				+ $"{_clusters.Count} clusters of up to {BatchClusterCells}x{BatchClusterCells} cells each ({MapPath})." );
		}
		catch ( Exception exception )
		{
			_initializationFailed = true;
			_cells.Clear();
			_clusters.Clear();
			_totalProps = 0;
			_compiledScene = null;
			Log.Warning( $"Failed to initialize Titanfall 2 prop streaming for '{MapPath}': {exception.Message}" );
		}
	}

	void RefreshCells( Vector3 anchor )
	{
		_lastAnchor = anchor;
		_hasAnchor = true;
		_refreshTimer.Restart();
		var loadDistanceSquared = LoadRadius * LoadRadius;
		_visiblePvsCells = ResolveVisiblePvsCells( anchor );

		// Distance culling is applied per instance and per batch (UpdateClusterVisibilityState),
		// never per cell: a cell can contain a large landmark that must stay visible while its
		// neighbours are culled.
		foreach ( var cluster in _clusters.Values )
			cluster.DistanceSquared = float.MaxValue;

		foreach ( var cell in _cells.Values )
		{
			cell.DistanceSquared = cell.Coordinate.DistanceSquaredToCell( anchor, CellSize );
			cell.Wanted = cell.DistanceSquared <= loadDistanceSquared;

			var cluster = cell.Cluster;
			if ( cell.DistanceSquared < cluster.DistanceSquared ) cluster.DistanceSquared = cell.DistanceSquared;
		}

		// Instance distances can cross the boundary without changing a cell or PVS.
		_runtimePvsUpdatePending = true;
		QueueRuntimeCellRefresh();

		if ( _completionLogged ) return;
		_workQueue.Clear();
		_workQueue.AddRange( _cells.Values.Where( static cell => cell.NextProp < cell.Props.Count ) );
		_workQueue.Sort( static ( left, right ) =>
		{
			var priority = right.Wanted.CompareTo( left.Wanted );
			return priority != 0 ? priority : left.DistanceSquared.CompareTo( right.DistanceSquared );
		} );
	}

	// The state pass is never rewound while it is walking: its inputs (cell distance and
	// render state) are already updated in place by RefreshCells, so only the visit order
	// is deferred. Restarting it from the nearest cluster on every refresh — which a player
	// moving 128 units triggers — kept it from ever reaching the far end of the map and
	// left _runtimePvsUpdatePending stuck at true.

	void RebuildRuntimeClusterQueue()
	{
		_runtimeClusterQueue.Clear();
		// Built in cell order before sorting, so each cluster appears once.
		foreach ( var cell in _cells.Values )
		{
			var cluster = cell.Cluster;
			if ( !cluster.HasCpuRenderWork ) continue;
			if ( cluster.QueueMarked ) continue;
			cluster.QueueMarked = true;
			_runtimeClusterQueue.Add( cluster );
		}
		foreach ( var cluster in _runtimeClusterQueue ) cluster.QueueMarked = false;
		_runtimeClusterQueue.Sort( static ( left, right ) => left.DistanceSquared.CompareTo( right.DistanceSquared ) );
		_runtimeClusterCursor = 0;
		_runtimeQueueRefreshPending = false;
		if ( _runtimeClusterQueue.Count == 0 ) _runtimePvsUpdatePending = false;
	}

	void LogCompletion()
	{
		if ( _completionLogged || _totalProps <= 0 || _liveProps + _failedProps < _totalProps ) return;
		BuildPendingBatches();
		_completionLogged = true;
		// The visible/culled counters in this line are only meaningful if they are
		// actually computed: this call was missing, so the line always reported 0/0
		// and made it look as though culling ran and found nothing to hide.
		UpdatePvsStatistics();
		Log.Info( $"Titanfall 2 prop streaming complete: {_liveProps}/{_totalProps} retained in "
			+ $"{_activeCells}/{_cells.Count} cells, {_gpuScene?.InstanceCount ?? 0} GPU-resident, "
			+ $"{_instancedProps - FullGpuInstanceCount} CPU-instanced in {_batchCount} batches, "
			+ $"{_skinnedProps} frozen-skinned fallbacks, {_plainProps} individual rigid, "
			+ (_gpuScene is null ? $"{_renderingProps} rendering, {_shadowCastingProps} casting shadows, "
				: "GPU visible/caster counts evaluated per view without readback, ")
			+ $"{Collisions.Created}/{Collisions.Expected} colliders active "
			+ $"({Collisions.Released} released, {Collisions.Filtered} zero-contents, {Collisions.Failures} unavailable, {Collisions.Deferred} deferred to runtime, "
			+ $"{Collisions.Resumes} cell passes resumed), "
			+ $"PVS cell {_pvsCameraCell} ({_pvsVisibleProps} visible/{_pvsCulledProps} culled), "
			+ $"{_failedProps} skipped ({MapPath})." );
		LogRenderDistanceSummary();
		LogModelFailureSummary();
	}

	/// <summary>
	/// Loads a mounted model and remembers failures. The resource loader does not cache
	/// a failed load, so without this every retry re-reads and re-parses the model: a
	/// model referenced by many instances paid that cost once per instance, and again on
	/// every cell refresh. Failures back off for a while instead of being written off
	/// permanently, so a transient problem can recover.
	/// </summary>
	Model LoadModelWithRetryCache( string resourcePath, int skin = 0, bool collisionOnly = false )
	{
		var failureKey = $"{resourcePath}|{skin}|{collisionOnly}";
		if ( _modelFailureRetryAfter.TryGetValue( failureKey, out var retryAfter ) )
		{
			if ( Titanfall2Log.ElapsedSeconds < retryAfter ) return null;
			_modelFailureRetryAfter.Remove( failureKey );
		}

		Model model;
		try
		{
			model = Sandbox.Mounting.Directory.Get( MountIdent ) is Titanfall2Mount mount
				? mount.LoadModelVariant( resourcePath, skin, collisionOnly: collisionOnly ) : null;
		}
		catch ( Exception exception )
		{
			Log.Warning( exception, $"Unable to load Titanfall 2 prop model '{resourcePath}'." );
			model = null;
		}

		if ( !ModelLoader.IsUsableModel( model ) )
		{
			_modelFailureRetryAfter[failureKey] = Titanfall2Log.ElapsedSeconds + ModelLoader.FailureRetrySeconds;
			return null;
		}

		return model;
	}

	Titanfall2CollisionModelResult LoadCollisionModelWithRetryCache( string resourcePath, int skin )
	{
		var failureKey = $"{resourcePath}|{skin}|True";
		if ( _modelFailureRetryAfter.TryGetValue( failureKey, out var retryAfter ) )
		{
			if ( Titanfall2Log.ElapsedSeconds < retryAfter ) return default;
			_modelFailureRetryAfter.Remove( failureKey );
		}
		Titanfall2CollisionModelResult result;
		try
		{
			result = Sandbox.Mounting.Directory.Get( MountIdent ) is Titanfall2Mount mount
				? mount.LoadCollisionModelVariant( resourcePath, skin ) : default;
		}
		catch ( Exception exception )
		{
			Log.Warning( exception, $"Unable to load Titanfall 2 prop collision '{resourcePath}'." );
			result = default;
		}
		if ( result.Status == Titanfall2CollisionModelStatus.Unavailable )
			_modelFailureRetryAfter[failureKey] = Titanfall2Log.ElapsedSeconds + ModelLoader.FailureRetrySeconds;
		return result;
	}

	/// <summary>
	/// Reports how many model paths are being skipped because they failed recently.
	/// Without this the only visible symptom of the retry cache is a shorter log.
	/// </summary>
	void LogModelFailureSummary()
	{
		if ( _modelFailureRetryAfter.Count == 0 ) return;
		Log.Info( $"Titanfall 2 model load retry cache: {_modelFailureRetryAfter.Count} distinct paths failed and are "
			+ $"skipped for {ModelLoader.FailureRetrySeconds:0}s each ({MapPath})." );
	}
}
