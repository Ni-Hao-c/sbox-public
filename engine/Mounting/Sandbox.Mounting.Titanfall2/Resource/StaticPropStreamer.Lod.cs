using System.Diagnostics;
using Sandbox.Mounting.PerfKit;
using Titanfall2;

public sealed partial class Titanfall2StaticPropStreamer
{
	/// <summary>
	/// Compares each batch in the cluster against its LOD bands and queues the ones that want a
	/// swap, unless the pending queue is at its limit. Saturation SKIPS the evaluation instead of
	/// dropping an entry: dropping cleared the batch's queued flag, so the next sweep queued it
	/// again and the queue churned (measured ~664 queue operations per second) while the dropped
	/// batch's LOD never converged. Skipping costs one comparison per cluster visit, keeps the
	/// queue bounded, and every queued batch still reaches its swap at one per frame; a slot
	/// frees on every frame that something is pending, so evaluation resumes the next frame.
	///
	/// With PropLods off (the runtime A/B) nothing new is produced and the pending queue is left
	/// to drain at the levels it already had. Forcing every batch back to LOD0 on the toggle
	/// would fire thousands of model loads and make the on/off measurement meaningless.
	///
	/// Only runs for the legacy LOD mode. The native batches have no band state at all - their
	/// level is chosen from the batch bounds inside the draw - so nothing here would apply to
	/// them, and queueing a swap for one would load a whole extra model per batch for nothing.
	/// </summary>
	void UpdateClusterLodState( PropCluster cluster, Vector3 anchor )
	{
		if ( _appliedLodMode == Titanfall2PropLodMode.Native ) return;
		// A mode transition owns every batch rebuild while it runs. Queueing a legacy swap here
		// would race it: both paths would load a model and replace the same batch's draw object.
		if ( _lodModeRebuildCursor < _lodModeRebuildQueue.Count ) return;
		if ( !Titanfall2StreamingSettings.PropLods ) return;

		var limit = Titanfall2StreamingSettings.PropLodSwapQueueLimit;
		if ( limit > 0 && _lodSwapQueue.Count >= limit ) return;

		foreach ( var batch in cluster.Batches.Values )
		{
			if ( !batch.RenderBatch.IsValid() || batch.FallbackObjects.Count > 0 ) continue;
			var desiredLod = SelectLod( batch.DistanceToClosestInstance( anchor ), batch.CurrentLod );
			// A batch that a mode transition could not rebuild still draws the model of the mode
			// it was built for. Queueing it here is what brings it onto the legacy models, which
			// is the only convergence path left for it.
			var staleMode = batch.LodMode != Titanfall2PropLodMode.Legacy;
			if ( !staleMode && desiredLod == batch.CurrentLod && !batch.LodSwapQueued ) continue;
			batch.DesiredLod = desiredLod;
			if ( batch.LodSwapQueued ) continue;
			batch.LodSwapQueued = true;
			_lodSwapQueue.Enqueue( batch );
		}
	}

	static int SelectLod( float distance, int currentLod )
	{
		if ( !Titanfall2StreamingSettings.PropLods ) return 0;
		var hysteresis = Titanfall2StreamingSettings.PropLodHysteresis;
		var lod = distance > Titanfall2StreamingSettings.PropLod3Distance + (currentLod >= 3 ? -hysteresis : hysteresis) ? 3
			: distance > Titanfall2StreamingSettings.PropLod2Distance + (currentLod >= 2 ? -hysteresis : hysteresis) ? 2
			: distance > Titanfall2StreamingSettings.PropLod1Distance + (currentLod >= 1 ? -hysteresis : hysteresis) ? 1
			: 0;
		return Math.Clamp( lod, 0, ModelLoader.StaticInstanceLodCount - 1 );
	}

	void ProcessLodSwaps()
	{
		if ( _lodSwapQueue.Count == 0 ) return;
		_lodSwapTimer.Restart();
		var processed = 0;
		while ( processed < Titanfall2StreamingSettings.PropLodSwapsPerFrame
			&& _lodSwapQueue.Count > 0 )
		{
			var batch = _lodSwapQueue.Dequeue();
			batch.LodSwapQueued = false;
			if ( batch.DesiredLod != batch.CurrentLod ) TrySwapBatchLod( batch, batch.DesiredLod );
			processed++;
			if ( _lodSwapTimer.Elapsed.TotalMilliseconds >= Titanfall2StreamingSettings.PropLodSwapBudgetMilliseconds ) break;
		}
	}

	/// <summary>
	/// Loads a mounted model and remembers failures. The resource loader does not cache
	/// a failed load, so without this every retry re-reads and re-parses the model: a
	/// model referenced by many instances paid that cost once per instance, and the LOD
	/// swap queue paid it again on every cell refresh. Failures back off for a while
	/// instead of being written off permanently, so a transient problem can recover.
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
			_modelFailureRetryAfter[failureKey] = Titanfall2Log.ElapsedSeconds
				+ Titanfall2StreamingSettings.ModelFailureRetrySeconds;
			return null;
		}

		return model;
	}

	void TrySwapBatchLod( PendingBatch batch, int lod )
	{
		var path = $"mount://{MountIdent}/{ModelLoader.GetStaticInstancePath( batch.ModelPath, lod )}.vmdl";
		_lodSwapProbe.Restart();
		var replacement = LoadModelWithRetryCache( path, batch.Skin );
		if ( replacement is null || !ModelLoader.CanInstance( replacement ) )
		{
			ReportSpike( _lodSwapProbe, "LOD{0}->LOD{1} swap for '{2}' produced no usable model", batch.CurrentLod, lod, batch.ModelPath );
			return;
		}
		try
		{
			var replacementBatch = CreateRenderBatch( batch, replacement, batch.RenderBatch.CastShadows, Titanfall2PropLodMode.Legacy );
			replacementBatch.RenderingEnabled = batch.RenderingEnabled;
			batch.RenderBatch.Delete();
			batch.RenderBatch = replacementBatch;
			// A freshly built batch starts with every transform; restore the distance-culled subset
			// so an LOD swap cannot flash the whole group for one refresh.
			batch.Culler.ApplyTo( replacementBatch );
			batch.Model = replacement;
			batch.CurrentLod = lod;
			// A swap is the legacy path by definition. Recording it on the batch is what makes a
			// batch that a mode transition left behind converge: it was queued precisely because
			// its recorded mode was not legacy, and this is the pass that resolves that.
			batch.LodMode = Titanfall2PropLodMode.Legacy;
			// Probe-able: the number of swaps actually applied. Together with an allocation
			// measurement this is what tells the LOD path apart from the rest of the frame.
			_lodSwapsApplied++;
		}
		catch ( Exception exception )
		{
			Log.Warning( exception, $"Unable to switch Titanfall 2 prop batch '{batch.ModelPath}' to LOD{lod}." );
		}

		ReportSpike( _lodSwapProbe, "LOD{0} swap for '{1}' ({2} instances)", batch.CurrentLod, batch.ModelPath, batch.Transforms.Count );
	}

	/// <summary>
	/// Brings the batches that already exist onto the LOD mode the setting now selects. The mode
	/// is a runtime A/B switch, and a batch cannot change mode by itself: a legacy batch holds a
	/// model that contains one band's geometry, while a native batch holds the one model with
	/// every level, so the model and the native draw object both have to be replaced.
	///
	/// It runs on the legacy swap budget because it is the same unit of work - one model load
	/// plus one native batch - and a toggle must not turn into a frame-long hitch on a map whose
	/// prop models were never loaded in the mode being switched to. The batches are snapshotted
	/// when the switch starts, so this frame's spawns (which already use the new mode) are not
	/// part of it. A second switch while a transition is running is picked up when that
	/// transition finishes, and starts a fresh snapshot then.
	/// </summary>
	void ProcessLodModeTransition()
	{
		if ( _lodModeRebuildCursor >= _lodModeRebuildQueue.Count )
		{
			var requested = Titanfall2StreamingSettings.PropLodMode;
			if ( requested == _appliedLodMode ) return;
			BeginLodModeTransition( requested );
			if ( _lodModeRebuildQueue.Count == 0 ) return;
		}

		_lodModeRebuildTimer.Restart();
		while ( _lodModeRebuildCursor < _lodModeRebuildQueue.Count )
		{
			var (cluster, batch) = _lodModeRebuildQueue[_lodModeRebuildCursor];
			_lodModeRebuildCursor++;
			RebuildBatchForLodMode( cluster, batch );
			if ( _lodModeRebuildTimer.Elapsed.TotalMilliseconds >= Titanfall2StreamingSettings.PropLodSwapBudgetMilliseconds ) break;
		}

		if ( _lodModeRebuildCursor < _lodModeRebuildQueue.Count ) return;
		Log.Info( $"Titanfall 2 prop LOD mode transition finished: now {DescribeLodMode( _appliedLodMode )}, "
			+ $"{_lodModeRebuildsApplied}/{_lodModeRebuildQueue.Count} batches rebuilt, "
			+ $"{_lodModeRebuildsSkipped} left on their previous model ({MapPath})." );
	}

	void BeginLodModeTransition( Titanfall2PropLodMode mode )
	{
		// The new mode is recorded before the queue is processed: every rebuild below has to load
		// the model of the mode being switched to, and no batch may be spawned into the old one.
		_appliedLodMode = mode;
		_lodModeRebuildQueue.Clear();
		_lodModeRebuildCursor = 0;
		_lodModeRebuildsApplied = 0;
		_lodModeRebuildsSkipped = 0;

		foreach ( var cluster in _clusters.Values )
		{
			foreach ( var batch in cluster.Batches.Values )
			{
				// A batch without a native draw object is a set of individual fallback objects: it
				// never had a LOD mechanism to switch and keeps drawing the model it already has.
				if ( !batch.RenderBatch.IsValid() || batch.FallbackObjects.Count > 0 ) continue;
				_lodModeRebuildQueue.Add( (cluster, batch) );
			}
		}

		// A queued legacy swap rebuilds the very batch this transition is about to rebuild, and
		// the queue only drains by swapping. Drop it instead of racing it: every batch it pointed
		// at is in the rebuild queue above, so nothing is lost, and the queued flag is cleared so
		// the legacy pass can queue a batch again if the mode comes back to legacy.
		while ( _lodSwapQueue.TryDequeue( out var pending ) ) pending.LodSwapQueued = false;

		Log.Info( $"Titanfall 2 prop LOD mode switched to {DescribeLodMode( mode )}: "
			+ $"{_lodModeRebuildQueue.Count} existing batches will be rebuilt across frames ({MapPath})." );
	}

	/// <summary>
	/// Rebuilds one existing batch for the current mode. The instances, their transforms, their
	/// visibility and their colliders are untouched: only the model the batch draws and the way
	/// it is drawn change, which is why the switch does not re-spawn a single prop.
	/// </summary>
	void RebuildBatchForLodMode( PropCluster cluster, PendingBatch batch )
	{
		var useNativeLods = _appliedLodMode == Titanfall2PropLodMode.Native;
		// Legacy starts the rebuilt batch in the band its own distance selects and lets the swap
		// queue converge it from there; native always starts at level 0 because the model it
		// loads carries every level and the draw picks the level itself.
		var targetLod = useNativeLods
			? 0
			: SelectLod( batch.DistanceToClosestInstance( _lastAnchor ), 0 );
		var path = $"mount://{MountIdent}/"
			+ (useNativeLods
				? ModelLoader.GetStaticInstanceAllLodsPath( batch.ModelPath )
				: ModelLoader.GetStaticInstancePath( batch.ModelPath, targetLod ))
			+ ".vmdl";

		_lodModeProbe.Restart();
		var replacement = LoadModelWithRetryCache( path, batch.Skin );
		if ( replacement is null || !ModelLoader.CanInstance( replacement ) )
		{
			// Never delete a batch that is drawing for a model that failed to load: it keeps the
			// geometry it had, and the next mode switch tries again.
			_lodModeRebuildsSkipped++;
			ReportSpike( _lodModeProbe, "LOD mode rebuild for '{0}' produced no usable model", batch.ModelPath );
			return;
		}

		MountedInstanceBatch replacementBatch;
		try
		{
			replacementBatch = CreateRenderBatch( batch, replacement, batch.RenderBatch.CastShadows, _appliedLodMode );
			replacementBatch.RenderingEnabled = batch.RenderingEnabled;
		}
		catch ( Exception exception )
		{
			_lodModeRebuildsSkipped++;
			Log.Warning( exception, $"Unable to rebuild Titanfall 2 prop batch '{batch.ModelPath}' for the "
				+ $"{DescribeLodMode( _appliedLodMode )} LOD mode." );
			return;
		}

		// The cluster dictionary is keyed by the model a batch draws, and a mode switch changes
		// that model. If another batch in this cluster already reaches the same replacement, this
		// one keeps the model it has: folding two already-built instance sets into a single batch
		// would mean rewriting the survivor's transform array, which is not something a mode
		// switch may do to a batch that is already drawing.
		// That batch then stays on its previous model for good while the switch is to native:
		// UpdateClusterLodState returns immediately for the native mode, so the swap
		// path that could have rebuilt it never runs, and the batch's recorded LodMode stays
		// inconsistent with the global mode until the mode is switched back to legacy. This is
		// knowingly left unrecovered rather than retried: the case needs two legacy batches of
		// one model path inside one cluster, which the default cluster size of one cell cannot
		// produce, and both models draw real geometry, so the batch is never blank.
		// The entry is removed by the key it was stored under. A legacy swap changes batch.Model
		// without re-keying, so removing batch.Model either missed (leaving this batch under
		// two keys: deleted twice on teardown, queued twice by the next switch) or removed a
		// different batch keyed by that LOD model (orphaning it: never deleted or rebuilt).
		var previousKey = batch.Key;
		if ( !ReferenceEquals( previousKey, replacement ) )
		{
			if ( cluster.Batches.TryGetValue( replacement, out var existing ) && !ReferenceEquals( existing, batch ) )
			{
				replacementBatch.Delete();
				_lodModeRebuildsSkipped++;
				return;
			}

			cluster.Batches.Remove( previousKey );
			cluster.Batches[replacement] = batch;
			batch.Key = replacement;
		}

		batch.RenderBatch.Delete();
		batch.RenderBatch = replacementBatch;
		batch.Culler.ApplyTo( replacementBatch );
		batch.Model = replacement;
		batch.CurrentLod = targetLod;
		batch.DesiredLod = targetLod;
		batch.LodSwapQueued = false;
		batch.LodMode = _appliedLodMode;
		_lodModeRebuildsApplied++;
		ReportSpike( _lodModeProbe, "LOD mode rebuild for '{0}' ({1} instances)", batch.ModelPath, batch.Transforms.Count );
	}

	static string DescribeLodMode( Titanfall2PropLodMode mode )
		=> mode == Titanfall2PropLodMode.Native
			? "native (one Model per prop with every LOD, explicit lodLevel per draw)"
			: "legacy (one Model per LOD, distance band swaps)";
}
