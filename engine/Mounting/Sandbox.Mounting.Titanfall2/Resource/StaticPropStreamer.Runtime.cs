public sealed partial class Titanfall2StaticPropStreamer
{
	enum RuntimePhase { Uninitialized, Preparing, Ready, Retiring }
	RuntimePhase _runtimePhase;
	int _cpuRenderClusters = -1;

	// Rebuilt only when the render backend changes ownership. GPU batches do not enter
	// the CPU visibility queue; native residual meshes of mixed props still do.
	void RefreshCpuRenderOwnership()
	{
		_cpuRenderClusters = 0;
		foreach ( var cluster in _clusters.Values )
		{
			cluster.HasCpuRenderWork = cluster.Batches.Values.Any( batch => !batch.GpuRendered )
				|| cluster.Cells.Any( cell => cell.Instances.Any( instance => !instance.IsBatched && instance.RenderObject.IsValid() ) );
			if ( cluster.HasCpuRenderWork ) _cpuRenderClusters++;
		}
		_runtimePvsUpdatePending = true;
		_shadowGateHasRun = false;
		_pendingBatchShadowChanges.Clear();
		_pendingInstanceShadowChanges.Clear();
		RebuildRuntimeClusterQueue();
	}

	protected override void OnUpdate()
	{
		if ( _runtimePhase is RuntimePhase.Preparing or RuntimePhase.Retiring ) return;
		if ( !_initialized ) InitializeCells();
		if ( !_initialized || _cells.Count == 0 ) return;

		var anchor = Scene?.Camera.IsValid() == true
			? Scene.Camera.WorldPosition
			: FallbackAnchor;
		_gpuScene?.UpdatePolicyCamera( anchor );
		var needsSpatialWork = _cpuRenderClusters != 0 || !PreparationComplete
			|| Collisions.NeedsSpatialWork;
		var pvsCellChanged = false;
		if ( needsSpatialWork )
		{
			TryFindPvsCell( anchor, out var pvsCell );
			pvsCellChanged = pvsCell != _pvsCameraCell;
			UpdateCellRefresh( anchor, pvsCell, pvsCellChanged );
		}

		if ( !PreparationComplete && _workQueue.Count > 0 ) PopulateCells( PropsPerFrame, FrameBudgetMilliseconds );
		if ( _runtimeClusterQueue.Count > 0 ) ProcessRuntimeClusterQueue();
		Collisions.Tick();
		// Independent of the sweep above: recompute when the camera moved far enough, then apply a
		// bounded number of queued shadow flag changes. No-op when the gate is off.
		if ( _cpuRenderClusters != 0 ) UpdateShadowGate( anchor, pvsCellChanged );
		if ( !_completionLogged )
		{
			LogCompletion();
			LogBatchComposition();
		}
		if ( !_completionLogged && _statusTimer.Elapsed.TotalSeconds >= 5.0 )
		{
			var processed = _liveProps + _failedProps;
			var queued = Math.Max( 0, _totalProps - processed );
			Log.Info( $"Titanfall 2 prop streaming: {_liveProps} retained, {queued} queued, "
				+ $"{_activeCells}/{_cells.Count} cells activated, {_failedProps} skipped ({MapPath})." );
			_statusTimer.Restart();
		}
	}

}
