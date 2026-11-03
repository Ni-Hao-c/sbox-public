using System.Diagnostics;
using Sandbox.Mounting.PerfKit;
using Titanfall2;

public sealed partial class Titanfall2StaticPropStreamer
{
	// Shadow distance gate. A batch carries one shadow flag for every member instance, so a single
	// large member would otherwise keep the whole cluster in the cascade pass from any distance.
	// The gate is its own pass, not part of the visibility sweep: decisions are recomputed only
	// after the camera moved ShadowGateRecomputeDistance, they are quantized into coarse distance
	// buckets with wide hysteresis, and the flag writes are queued and applied at most
	// ShadowGateChangesPerFrame per frame. Toggling a batch flag is a native flag change plus a
	// scene-list notification that pulls whole instances into or out of the shadow pass; hundreds
	// of those inside one sweep read as hitching while walking.
	readonly bool _shadowGateActive = Titanfall2StreamingSettings.PropShadows
		&& Titanfall2StreamingSettings.PropShadowDistance > 0f;
	readonly float _shadowGateDistanceSquared = ShadowGateDistanceSquared( 1f );
	// Restore at half the disable distance: wide hysteresis, so a prop walking along the boundary
	// cannot flip on small camera moves. Both thresholds are compared as bucket indices below.
	readonly float _shadowGateRestoreDistanceSquared = ShadowGateDistanceSquared( 0.5f );
	// First bucket at or past the disable distance, and the last bucket inside half of it. Bucket
	// arithmetic means a prop that moves inside one bucket cannot change state at all.
	readonly int _shadowGateDisableBucket = ShadowGateBucket( Titanfall2StreamingSettings.PropShadowDistance );
	readonly int _shadowGateRestoreBucket = Math.Max( 0,
		ShadowGateBucket( Titanfall2StreamingSettings.PropShadowDistance * 0.5f ) - 1 );
	// A pass re-evaluates the gate only after the camera moved this far (its PVS cell also forces
	// one). The visibility sweep's 128-unit refresh is unrelated and keeps its own budget.
	readonly float _shadowGateRecomputeDistanceSquared = ShadowGateRecomputeDistance * ShadowGateRecomputeDistance;
	readonly Stopwatch _shadowGateProbe = new();
	bool _shadowGateHasRun;
	Vector3 _shadowGateAnchor;
	// Decisions from the last pass that are not applied yet. Applying is capped per frame, so one
	// pass can never turn into hundreds of native flag changes inside a single frame.
	readonly List<(PendingBatch Batch, bool Cast)> _pendingBatchShadowChanges = new();
	readonly List<(PropInstance Instance, bool Cast)> _pendingInstanceShadowChanges = new();
	int _shadowGatedBatches;
	int _shadowGatedInstances;
	int _shadowGateQueued;
	int _shadowGateApplied;
	int _shadowGateApplyFrames;
	int _shadowGateMaxAppliedPerFrame;

	static float ShadowGateDistanceSquared( float scale )
	{
		var distance = Titanfall2StreamingSettings.PropShadowDistance * scale;
		return distance > 0f ? distance * distance : 0f;
	}

	static int ShadowGateBucket( float distance )
		=> distance > 0f ? (int)(distance * (1f / ShadowGateBucketSize)) : 0;

	static int ShadowGateBucketOf( float distanceSquared )
	{
		if ( float.IsPositiveInfinity( distanceSquared ) ) return int.MaxValue;
		return ShadowGateBucket( MathF.Sqrt( distanceSquared ) );
	}

	bool ShadowGateShouldCast( int bucket, bool casting )
		=> casting ? bucket < _shadowGateDisableBucket : bucket <= _shadowGateRestoreBucket;

	// Cell refresh state. The cheap half (one distance per cell, cluster minima, two sorted queues)
	// is bounded by construction; the portal walk used to be the unbudgeted part.
	readonly Stopwatch _refreshProbe = new();
	bool _refreshGridValid;
	// Per-camera-cell reachability cache. The reachable set depends only on the camera's PVS cell
	// and the immutable portal graph, not on the exact anchor, so a map with 74 cells needs at most
	// 74 BFS rounds for the whole session. That is what makes the portal walk cheap enough to turn
	// into a budgeted job instead of a per-refresh hit.
	int[][] _pvsAdjacency;
	Titanfall2BspReader.BspVisibility _pvsAdjacencySource;
	Titanfall2BspReader.VisibilityCellMask[] _pvsCachedReachable;
	bool[] _pvsCachedKnown;
	// One BFS round in flight: queue, visited mask and the anchor it was started for. The previous
	// mask stays active until the round completes, so a frame never renders with a half-built set.
	int[] _pvsRoundCells;
	int[] _pvsRoundDepths;
	int _pvsRoundHead;
	int _pvsRoundTail;
	Titanfall2BspReader.VisibilityCellMask _pvsRoundMask;
	int _pvsRoundCell = -1;
	Vector3 _pvsRoundAnchor;
	bool _pvsRoundActive;
	readonly Stopwatch _pvsRoundProbe = new();
	double _pvsRoundMilliseconds;
	double _pvsLastRoundMilliseconds;

	/// <summary>
	/// Drives the runtime cell refresh. A refresh runs when the anchor crossed a
	/// <see cref="RefreshGridSize"/> grid bucket and moved at least
	/// <see cref="RefreshHysteresisDistance"/> since the last one, or when the camera entered a new
	/// PVS cell (which must apply immediately). Standing still does no work at all. The portal walk
	/// is cached per PVS cell and, on a first visit, advanced by <see cref="AdvanceReachableRound"/>
	/// under a per-frame budget while the previous mask stays active.
	/// </summary>
	void UpdateCellRefresh( Vector3 anchor, int pvsCell, bool pvsCellChanged )
	{
		// A round already running for the new cell is not a reason to refresh distances again:
		// while it drains the previous mask is still active and pvsCellChanged stays true.
		var forced = !_refreshGridValid || (pvsCellChanged && pvsCell != _pvsRoundCell);
		var refreshed = false;
		if ( forced || ShouldRefreshCells( anchor ) )
		{
			RefreshCellDistances( anchor );
			refreshed = true;
		}

		// A round for a cell the camera already left must not apply its mask for the new room: drop it
		// when the current cell is already cached, otherwise restart it below for the current cell.
		if ( pvsCell >= 0 && _pvsRoundActive && _pvsRoundCell != pvsCell && IsReachableCellsCached( pvsCell ) )
			_pvsRoundActive = false;

		if ( pvsCell >= 0 && !IsReachableCellsCached( pvsCell )
			&& (!_pvsRoundActive || _pvsRoundCell != pvsCell) )
			BeginReachableRound( pvsCell, anchor );

		// Fast path: on a cell change the round starts and is advanced in this same frame, so a
		// retail map's whole portal graph (~700 edges) fits one step and the new room's visibility
		// applies immediately; a larger graph continues on following frames instead of stalling.
		var applied = AdvanceReachableRound( PvsEdgesPerFrame, PvsBudgetMilliseconds );
		// A mask applied on a frame that did no distance refresh still has to reach the visibility
		// sweep, otherwise batches would keep the previous cell's render state until the next refresh.
		if ( applied && !refreshed )
		{
			_runtimePvsUpdatePending = true;
			QueueRuntimeCellRefresh();
		}
	}

	/// <summary>
	/// Quantized refresh trigger: a 256-unit grid bucket change with a 192-unit distance hysteresis
	/// (a camera sitting on a bucket boundary cannot refresh twice), plus a hard 512-unit fallback so
	/// a refresh can never lag further than two buckets. Crossing a bucket always refreshes, which is
	/// what keeps a fast camera following instead of merely lowering the threshold.
	/// </summary>
	bool ShouldRefreshCells( Vector3 anchor )
	{
		if ( !_hasAnchor ) return true;
		var moved = anchor.DistanceSquared( _lastAnchor );
		if ( moved >= (RefreshGridSize * 2f) * (RefreshGridSize * 2f) ) return true;
		if ( moved < RefreshHysteresisDistance * RefreshHysteresisDistance ) return false;
		return RefreshGridIndex( anchor.x ) != RefreshGridIndex( _lastAnchor.x )
			|| RefreshGridIndex( anchor.y ) != RefreshGridIndex( _lastAnchor.y );
	}

	static int RefreshGridIndex( float coordinate ) => (int)MathF.Floor( coordinate / RefreshGridSize );

	/// <summary>
	/// The cheap half of a refresh: one distance per prop cell, the per-cluster minimum and the two
	/// distance-sorted queues. O(cells) = 477 distance tests plus a 52- and a 477-entry sort over
	/// reused list buffers, so it is bounded by construction and allocates nothing. The portal walk
	/// that used to run here moved to the cached, budgeted reachability round. The spawn work queue
	/// is not rebuilt at runtime: <see cref="RefreshCells"/> already skips it once loading finished.
	/// </summary>
	void RefreshCellDistances( Vector3 anchor )
	{
		_refreshProbe.Restart();
		_lastAnchor = anchor;
		_hasAnchor = true;
		_refreshGridValid = true;
		_refreshTimer.Restart();

		var loadDistanceSquared = LoadRadius * LoadRadius;
		foreach ( var cluster in _clusters.Values )
			cluster.DistanceSquared = float.MaxValue;
		foreach ( var cell in _cells.Values )
		{
			cell.DistanceSquared = cell.Coordinate.DistanceSquaredToCell( anchor, CellSize );
			cell.Wanted = cell.DistanceSquared <= loadDistanceSquared;
			var cluster = cell.Cluster;
			if ( cell.DistanceSquared < cluster.DistanceSquared ) cluster.DistanceSquared = cell.DistanceSquared;
		}

		// The sweep restarts from the new distance order; both queues keep their list capacity and
		// are rebuilt only when no sweep is mid-walk (QueueRuntimeCellRefresh sets the pending flag
		// instead of rebuilding in that case).
		_runtimePvsUpdatePending = true;
		QueueRuntimeCellRefresh();
		var elapsed = _refreshProbe.Elapsed.TotalMilliseconds;
		ReportSpike( _refreshProbe, "cell refresh ({0} cells)", _cells.Count );
		if ( !Titanfall2Log.VerboseLogging ) return;
		Titanfall2Log.Info( $"Titanfall 2 prop cell refresh: cells={_cells.Count} sort={elapsed:0.00}ms "
			+ $"portals={_pvsLastRoundMilliseconds:0.00}ms (grid {RefreshGridSize:0}u, pvs cell {_pvsCameraCell}, "
			+ $"sweep pending {(_runtimePvsUpdatePending ? 1 : 0)}) ({MapPath})." );
		_pvsLastRoundMilliseconds = 0;
	}

	/// <summary>
	/// Builds the portal graph once per visibility payload: adjacency[from] lists the ToCell of every
	/// portal leaving <c>from</c>. The old walk scanned all portals for every dequeued cell
	/// (74 x 712 comparisons per refresh); with this graph a round is O(cells + portals) = ~800 edge
	/// visits, which is why it normally fits a single budgeted step.
	/// </summary>
	void EnsurePvsAdjacency()
	{
		var visibility = _visibility;
		if ( _pvsAdjacency is not null && ReferenceEquals( _pvsAdjacencySource, visibility ) ) return;
		_pvsAdjacency = null;
		_pvsCachedReachable = null;
		_pvsCachedKnown = null;
		_pvsRoundCells = null;
		_pvsRoundDepths = null;
		_pvsAdjacencySource = visibility;
		if ( visibility is not { IsValid: true } ) return;

		var cellCount = visibility.CellCount;
		var adjacency = new int[cellCount][];
		var sizes = new int[cellCount];
		foreach ( var portal in visibility.Portals )
		{
			if ( portal.FromCell < 0 || portal.FromCell >= cellCount || portal.ToCell < 0 || portal.ToCell >= cellCount ) continue;
			sizes[portal.FromCell]++;
		}
		for ( var cell = 0; cell < cellCount; cell++ )
			adjacency[cell] = sizes[cell] == 0 ? Array.Empty<int>() : new int[sizes[cell]];
		var writes = new int[cellCount];
		foreach ( var portal in visibility.Portals )
		{
			if ( portal.FromCell < 0 || portal.FromCell >= cellCount || portal.ToCell < 0 || portal.ToCell >= cellCount ) continue;
			adjacency[portal.FromCell][writes[portal.FromCell]++] = portal.ToCell;
		}

		_pvsAdjacency = adjacency;
		_pvsCachedReachable = new Titanfall2BspReader.VisibilityCellMask[cellCount];
		_pvsCachedKnown = new bool[cellCount];
		_pvsRoundCells = new int[cellCount];
		_pvsRoundDepths = new int[cellCount];
	}

	bool IsReachableCellsCached( int cell )
		=> _pvsCachedKnown is not null && cell >= 0 && cell < _pvsCachedKnown.Length && _pvsCachedKnown[cell];

	void BeginReachableRound( int cell, Vector3 anchor )
	{
		EnsurePvsAdjacency();
		if ( _pvsAdjacency is null || cell < 0 || cell >= _pvsAdjacency.Length ) return;
		_pvsRoundCell = cell;
		_pvsRoundAnchor = anchor;
		_pvsRoundMask = new Titanfall2BspReader.VisibilityCellMask().Add( cell );
		_pvsRoundCells[0] = cell;
		_pvsRoundDepths[0] = 0;
		_pvsRoundHead = 0;
		_pvsRoundTail = 1;
		_pvsRoundActive = true;
		_pvsRoundMilliseconds = 0;
		_pvsRoundProbe.Restart();
	}

	/// <summary>
	/// Advances the reachability round by at most <paramref name="edgeBudget"/> portal edges and
	/// <paramref name="timeBudgetMilliseconds"/> (a non-positive time budget means no time limit,
	/// used by the synchronous first-visit path). Returns true when the round completed and the new
	/// mask was applied. The queue strictly drains — every cell enters it at most once — so a round
	/// finishes in at most CellCount steps regardless of budget, and the previous mask stays active
	/// until then: no frame ever renders a half-built visibility set.
	/// </summary>
	bool AdvanceReachableRound( int edgeBudget, float timeBudgetMilliseconds )
	{
		if ( !_pvsRoundActive ) return false;
		// The visibility payload can be replaced between frames (Configure with a new parse); the
		// adjacency is rebuilt lazily, so a round that outlived it is simply abandoned.
		if ( _pvsAdjacency is null )
		{
			_pvsRoundActive = false;
			return false;
		}
		var edges = 0;
		while ( _pvsRoundHead < _pvsRoundTail )
		{
			var cell = _pvsRoundCells[_pvsRoundHead];
			var depth = _pvsRoundDepths[_pvsRoundHead];
			_pvsRoundHead++;
			if ( depth < PvsPortalDepth )
			{
				var neighbours = _pvsAdjacency[cell];
				edges += neighbours.Length;
				for ( var index = 0; index < neighbours.Length; index++ )
				{
					var next = neighbours[index];
					if ( _pvsRoundMask.Contains( next ) ) continue;
					_pvsRoundMask = _pvsRoundMask.Add( next );
					_pvsRoundCells[_pvsRoundTail] = next;
					_pvsRoundDepths[_pvsRoundTail] = depth + 1;
					_pvsRoundTail++;
				}
			}

			var overTime = timeBudgetMilliseconds > 0f
				&& _pvsRoundProbe.Elapsed.TotalMilliseconds >= timeBudgetMilliseconds;
			if ( edges >= edgeBudget || overTime ) break;
		}

		_pvsRoundMilliseconds += _pvsRoundProbe.Elapsed.TotalMilliseconds;
		_pvsRoundProbe.Restart();
		if ( _pvsRoundHead < _pvsRoundTail ) return false;

		// Round complete: cache it per camera cell and only now replace the active mask.
		_pvsRoundActive = false;
		var reachable = _pvsRoundMask.Union( _visibility.AlwaysVisibleCells );
		if ( _pvsCachedKnown is not null && _pvsRoundCell >= 0 && _pvsRoundCell < _pvsCachedKnown.Length )
		{
			_pvsCachedReachable[_pvsRoundCell] = reachable;
			_pvsCachedKnown[_pvsRoundCell] = true;
		}
		_visiblePvsCells = reachable;
		_pvsCameraCell = _pvsRoundCell;
		_pvsLastRoundMilliseconds = _pvsRoundMilliseconds;
		if ( _pvsRoundMilliseconds > PvsBudgetMilliseconds )
			Titanfall2Log.Spike( $"PVS reachable round for cell {_pvsRoundCell} took {_pvsRoundMilliseconds:0.00} ms "
				+ $"(budget {PvsBudgetMilliseconds:0.0} ms per frame, edges {PvsEdgesPerFrame})" );
		LogPvsDiagnostics( _pvsRoundAnchor, applied: true );
		return true;
	}

	/// <summary>Synchronous reachability for the loading path: computes and caches one cell's set.</summary>
	Titanfall2BspReader.VisibilityCellMask ComputeReachableCells( int cell )
	{
		EnsurePvsAdjacency();
		if ( _pvsAdjacency is null || cell < 0 || cell >= _pvsAdjacency.Length ) return default;
		if ( _pvsCachedKnown[cell] ) return _pvsCachedReachable[cell];
		BeginReachableRound( cell, _lastAnchor );
		// A non-positive time budget means "no time limit", so this drains the whole round at once.
		while ( _pvsRoundActive && !AdvanceReachableRound( int.MaxValue, 0f ) ) { }
		return _pvsCachedKnown[cell] ? _pvsCachedReachable[cell] : default;
	}

	Titanfall2BspReader.VisibilityCellMask ResolveVisiblePvsCells( Vector3 anchor )
	{
		_pvsCameraCell = -1;
		if ( !TryFindPvsCell( anchor, out _pvsCameraCell ) ) return default;
		var reachable = ComputeReachableCells( _pvsCameraCell );
		LogPvsDiagnostics( anchor, applied: true );
		return reachable;
	}

	bool TryFindPvsCell( Vector3 anchor, out int cell )
	{
		cell = -1;
		if ( _visibility is not { IsValid: true } )
		{
			LogPvsDiagnostics( anchor, applied: false, reason: _visibilityRestoreFailed
				? "the encoded visibility payload is empty or could not be decoded"
				: "no visibility data was parsed" );
			return false;
		}
		cell = _visibility.FindCell( new System.Numerics.Vector3( anchor.x, anchor.y, anchor.z ) );
		if ( cell < 0 )
			LogPvsDiagnostics( anchor, applied: false, reason: "FindCell returned -1 for the active anchor" );
		return cell >= 0;
	}

	/// <summary>
	/// Reports exactly why cell-based PVS culling is or is not running. A log line is
	/// the only way to tell the failure modes apart at runtime: data that failed its
	/// validity gate and an anchor that no cell contains. The engine's own summary cannot
	/// distinguish them, and the empty visible/culled counters in the completion line
	/// look identical in both cases.
	/// </summary>
	void LogPvsDiagnostics( Vector3 anchor, bool applied, string reason = null )
	{
		if ( _pvsDiagnosticsLogged ) return;
		_pvsDiagnosticsLogged = true;

		var visibility = _visibility;
		var anchorCell = visibility is { IsValid: true }
			? visibility.FindCell( new System.Numerics.Vector3( anchor.x, anchor.y, anchor.z ) )
			: -1;
		Log.Info( $"Titanfall 2 PVS diagnosis: {(applied ? "culling is active" : $"culling is inactive ({reason})")}; "
			+ $"data cells={visibility?.CellCount ?? 0} planes={visibility?.Planes.Count ?? 0} "
			+ $"nodes={visibility?.Nodes.Count ?? 0} valid={visibility?.IsValid == true}; "
			+ $"anchor {anchor} resolves to cell {anchorCell}; "
			+ $"{_propsWithPvsCells}/{_totalProps} props carry a non-empty cell mask ({MapPath})." );

		// The raw root fields decide why the walk fails. A plane index that is really a
		// bitfield shows up as a huge unsigned value, and a child index that is not a
		// plain node offset shows up as a value near or beyond the node count. Without
		// these numbers a run can only say that FindCell returned -1, which is what left
		// the root cause open.
		if ( visibility is not { IsValid: true } || visibility.Nodes.Count == 0 ) return;

		var nodes = visibility.Nodes;
		var leaves = 0;
		var maximumPlane = int.MinValue;
		var maximumChild = int.MinValue;
		var outOfRangePlanes = 0;
		for ( var index = 0; index < nodes.Count; index++ )
		{
			var node = nodes[index];
			if ( node.Plane < 0 ) leaves++;
			else if ( node.Plane >= visibility.Planes.Count ) outOfRangePlanes++;
			if ( node.Plane > maximumPlane ) maximumPlane = node.Plane;
			if ( node.Child > maximumChild ) maximumChild = node.Child;
		}

		var root = nodes[0];
		Log.Info( $"Titanfall 2 PVS cell tree: root plane={root.Plane} (0x{root.Plane:X8}) child={root.Child} "
			+ $"(0x{root.Child:X8}); leaves={leaves}/{nodes.Count}; max plane={maximumPlane} vs planes={visibility.Planes.Count}; "
			+ $"planes out of range={outOfRangePlanes}; max child={maximumChild} vs nodes={nodes.Count} ({MapPath})." );
	}

	bool IsPvsVisible( Titanfall2BspReader.VisibilityCellMask propCells )
	{
		// A missing/invalid PVS mapping must never hide a prop. This also covers
		// static props which lie outside the original cell AABB reference table.
		return _visiblePvsCells.IsEmpty || propCells.IsEmpty || propCells.Intersects( _visiblePvsCells );
	}

	/// <summary>
	/// PVS is independent of the coarse cell distance, which must not hide a large
	/// landmark. Distance is applied to each instance below, and a shared batch's mask is
	/// the union of its member cells (see PendingBatch.Add), so both callers use the same rule.
	/// </summary>
	bool IsRenderVisible( Titanfall2BspReader.VisibilityCellMask propCells )
	{
		return IsPvsVisible( propCells );
	}

	/// <summary>
	/// The distance at which one prop stops drawing, before its radius and hysteresis are added:
	/// the nearer of the global PropRenderDistance and the prop's authored fade distance.
	/// Infinity means distance never hides it.
	/// </summary>
	static float GetRenderDistance( float fadeDistance )
	{
		var global = Titanfall2StreamingSettings.PropRenderDistance > 0 ? Titanfall2StreamingSettings.PropRenderDistance : float.PositiveInfinity;
		if ( !(fadeDistance > 0) ) return global;
		return MathF.Min( global, fadeDistance * Titanfall2StreamingSettings.PropAuthoredFadeScale );
	}

	bool IsWithinRenderDistance( Vector3 position, float radius, bool wasVisible, float fadeDistance )
	{
		if ( radius >= LandmarkRadius ) return true;
		var distance = GetRenderDistance( fadeDistance );
		if ( float.IsPositiveInfinity( distance ) ) return true;
		distance += radius + (wasVisible ? RenderDistanceHysteresis : 0);
		return position.DistanceSquared( _lastAnchor ) <= distance * distance;
	}

	/// <summary>Per-instance keep distances (render distance plus hysteresis) for a batch's culler.</summary>
	static float[] GetKeepDistances( PendingBatch batch )
	{
		if ( batch.KeepDistances is { } cached && cached.Length == batch.FadeDistances.Count ) return cached;
		var result = new float[batch.FadeDistances.Count];
		for ( var index = 0; index < result.Length; index++ )
			result[index] = GetRenderDistance( batch.FadeDistances[index] ) + RenderDistanceHysteresis;
		batch.KeepDistances = result;
		return result;
	}

	void UpdateClusterVisibilityState( PropCluster cluster )
	{
		foreach ( var cell in cluster.Cells )
		{
			foreach ( var instance in cell.Instances )
			{
				if ( instance.IsBatched ) continue;
				var visible = IsRenderVisible( instance.Source.PvsCells )
					&& IsWithinRenderDistance( instance.Position, instance.Radius, instance.RenderingEnabled, instance.Source.FadeDistance );
				if ( visible == instance.RenderingEnabled ) continue;
				instance.RenderingEnabled = visible;
				if ( instance.RenderObject.IsValid() ) instance.RenderObject.RenderingEnabled = visible;
			}
		}

		// Distance is evaluated against each instance's radius. Large landmarks are
		// retained, and a changed membership rebuilds the subset even at the same count.
		var anchor = _lastAnchor;

		foreach ( var batch in cluster.Batches.Values )
		{
			var visible = IsRenderVisible( batch.PvsCells );
			var wasRendered = batch.RenderingEnabled;

			if ( batch.RenderBatch.IsValid() && visible )
			{
				// Re-evaluate a batch restored after PVS hid it at an older anchor.
				if ( !wasRendered ) batch.Culler.Invalidate();
				var all = batch.GetOrBuildTransformArray();
				var changed = batch.Culler.Update( all, anchor, float.PositiveInfinity, batch.Radii,
					LandmarkRadius, GetKeepDistances( batch ) );
				if ( batch.Culler.KeptCount == 0 ) visible = false;
				else if ( changed ) batch.Culler.ApplyTo( batch.RenderBatch );
			}

			if ( batch.RenderingEnabled == visible ) continue;
			batch.RenderingEnabled = visible;
			if ( batch.RenderBatch.IsValid() ) batch.RenderBatch.RenderingEnabled = visible;
			foreach ( var fallback in batch.FallbackObjects )
				if ( fallback.IsValid() ) fallback.RenderingEnabled = visible;
		}
	}

	/// <summary>
	/// Drives the shadow distance gate. A pass re-evaluates every gated object when the camera has
	/// moved <see cref="ShadowGateRecomputeDistance"/> (or its PVS cell changed, which covers a
	/// teleport inside the movement radius), and the resulting flag changes are applied over the
	/// following frames under the per-frame cap. It does nothing when the gate is off, so the
	/// default configuration pays one boolean test per frame and performs no flag changes at all.
	/// </summary>
	void UpdateShadowGate( Vector3 anchor, bool pvsCellChanged )
	{
		if ( !_shadowGateActive || !_batchesBuilt ) return;

		ApplyPendingShadowChanges();

		if ( _shadowGateHasRun && !pvsCellChanged
			&& anchor.DistanceSquared( _shadowGateAnchor ) < _shadowGateRecomputeDistanceSquared ) return;

		RecomputeShadowGate( anchor );
	}

	/// <summary>
	/// Rebuilds the queue of shadow flag changes. Every batch is judged by the distance to the
	/// closest instance it draws, every individual object by its own distance; both are quantized
	/// into <see cref="ShadowGateBucketSize"/> buckets, with the restore threshold at half the
	/// disable distance, so one camera move cannot flip an object that sits on a boundary. Flag
	/// writes are only queued here, never performed, so a pass costs one pass over the props and
	/// no native work; the writes happen in <see cref="ApplyPendingShadowChanges"/>.
	/// </summary>
	void RecomputeShadowGate( Vector3 anchor )
	{
		var moved = _shadowGateHasRun ? MathF.Sqrt( anchor.DistanceSquared( _shadowGateAnchor ) ) : 0f;
		_shadowGateHasRun = true;
		_shadowGateAnchor = anchor;
		_pendingBatchShadowChanges.Clear();
		_pendingInstanceShadowChanges.Clear();
		_shadowGateQueued = 0;
		_shadowGateApplied = 0;
		_shadowGateApplyFrames = 0;
		_shadowGateMaxAppliedPerFrame = 0;
		_shadowGateProbe.Restart();

		foreach ( var cluster in _clusters.Values )
		{
			foreach ( var batch in cluster.Batches.Values )
			{
				if ( batch.ShadowedInstanceCount <= 0 || !batch.RenderBatch.IsValid() ) continue;
				var casting = batch.RenderBatch.CastShadows;
				var bucket = ShadowGateBucketOf( NearestDistanceSquared( batch.Transforms, anchor ) );
				if ( ShadowGateShouldCast( bucket, casting ) == casting ) continue;
				_pendingBatchShadowChanges.Add( (batch, !casting) );
				_shadowGateQueued++;
			}

			foreach ( var cell in cluster.Cells )
			{
				foreach ( var instance in cell.Instances )
				{
					// Batched props are gated through their batch, and a prop that was built
					// without shadows (radius below the shadow minimum, or prop shadows off) has
					// nothing to gate.
					if ( instance.IsBatched || !instance.CastShadows || !instance.RenderObject.IsValid() ) continue;
					var casting = instance.RenderObject.Flags.CastShadows;
					var bucket = ShadowGateBucketOf( instance.Position.DistanceSquared( anchor ) );
					if ( ShadowGateShouldCast( bucket, casting ) == casting ) continue;
					_pendingInstanceShadowChanges.Add( (instance, !casting) );
					_shadowGateQueued++;
				}
			}
		}

		// A pass that decides hundreds of changes is exactly the case this design has to keep away
		// from the frame: it is queued, not applied, but a large queue still means a long drain.
		// ReportSpike already logs passes slower than its own 2 ms threshold (verbose logging only).
		if ( _shadowGateQueued > ShadowGateReportChanges )
			Titanfall2Log.Spike( $"prop shadow gate pass queued {_shadowGateQueued} changes "
				+ $"(applied at most {ShadowGateChangesPerFrame} per frame)" );
		ReportSpike( _shadowGateProbe, "prop shadow gate pass ({0} changes queued)", _shadowGateQueued );
		if ( _shadowGateQueued == 0 ) return;
		Log.Info( $"Titanfall 2 prop shadow gate: pass after {moved:0} units queued {_shadowGateQueued} flag changes; "
			+ $"{_shadowGatedBatches} of {_batchCount} batches and {_shadowGatedInstances} individual props keep their shadows off "
			+ $"beyond {MathF.Sqrt( _shadowGateDistanceSquared ):0} units ({MapPath})." );
	}

	/// <summary>
	/// Applies queued shadow flag changes, at most <see cref="ShadowGateChangesPerFrame"/> per call.
	/// One entry is one native flag change, one scene-list notification and one whole batch
	/// entering or leaving the shadow pass, so the cap — not the pass cadence — is what bounds the
	/// per-frame cost. The drain is reported once its queue empties, including the largest number of
	/// changes any single frame had to apply.
	/// </summary>
	void ApplyPendingShadowChanges()
	{
		if ( _pendingBatchShadowChanges.Count == 0 && _pendingInstanceShadowChanges.Count == 0 ) return;

		var applied = 0;
		while ( applied < ShadowGateChangesPerFrame && _pendingBatchShadowChanges.Count > 0 )
		{
			var index = _pendingBatchShadowChanges.Count - 1;
			var (batch, cast) = _pendingBatchShadowChanges[index];
			_pendingBatchShadowChanges.RemoveAt( index );
			if ( !batch.RenderBatch.IsValid() || batch.RenderBatch.CastShadows == cast ) continue;
			batch.RenderBatch.CastShadows = cast;
			_shadowGatedBatches = Math.Max( 0, _shadowGatedBatches + (cast ? -1 : 1) );
			applied++;
		}

		while ( applied < ShadowGateChangesPerFrame && _pendingInstanceShadowChanges.Count > 0 )
		{
			var index = _pendingInstanceShadowChanges.Count - 1;
			var (instance, cast) = _pendingInstanceShadowChanges[index];
			_pendingInstanceShadowChanges.RemoveAt( index );
			if ( !instance.RenderObject.IsValid() || instance.RenderObject.Flags.CastShadows == cast ) continue;
			instance.RenderObject.Flags.CastShadows = cast;
			_shadowGatedInstances = Math.Max( 0, _shadowGatedInstances + (cast ? -1 : 1) );
			applied++;
		}

		if ( applied > 0 )
		{
			_shadowGateApplied += applied;
			_shadowGateApplyFrames++;
			if ( applied > _shadowGateMaxAppliedPerFrame ) _shadowGateMaxAppliedPerFrame = applied;
		}
		if ( _pendingBatchShadowChanges.Count != 0 || _pendingInstanceShadowChanges.Count != 0 ) return;
		// Report the cycle once the queue is empty, even when every entry turned out to be a no-op
		// (the live flag already matched): the counters then say how many were skipped by omission.
		if ( _shadowGateQueued == 0 ) return;

		LogShadowGateSweep();
	}

	/// <summary>
	/// Reports one completed gate cycle — the recompute pass queued N changes, the drain applied
	/// them over M frames with at most K per frame — and resets the cycle counters. Named "sweep"
	/// because <see cref="Titanfall2StaticPropStreamer"/>'s batch build path refers to it as the
	/// sweep report; a cycle is exactly one pass plus the frames it took to apply its changes.
	/// </summary>
	void LogShadowGateSweep()
	{
		Log.Info( $"Titanfall 2 prop shadow gate: applied {_shadowGateApplied} of {_shadowGateQueued} queued changes "
			+ $"over {_shadowGateApplyFrames} frames (max {_shadowGateMaxAppliedPerFrame} per frame, "
			+ $"cap {ShadowGateChangesPerFrame}); {_shadowGatedBatches} of {_batchCount} batches and "
			+ $"{_shadowGatedInstances} individual props keep their shadows off beyond "
			+ $"{MathF.Sqrt( _shadowGateDistanceSquared ):0} units ({MapPath})." );
		_shadowGateQueued = 0;
		_shadowGateApplied = 0;
		_shadowGateApplyFrames = 0;
		_shadowGateMaxAppliedPerFrame = 0;
	}

	/// <summary>
	/// Distance to the closest instance in one batch. The gate keeps a batch casting while anything
	/// it draws is close, which is the conservative reading of the batch-wide shadow flag; gate
	/// distances are far below the render distance, so the closest drawn instance and the closest
	/// instance are the same thing wherever the answer matters.
	/// </summary>
	static float NearestDistanceSquared( List<Transform> transforms, Vector3 anchor )
	{
		var nearest = float.PositiveInfinity;
		for ( var index = 0; index < transforms.Count; index++ )
		{
			var distance = anchor.DistanceSquared( transforms[index].Position );
			if ( distance < nearest ) nearest = distance;
		}
		return nearest;
	}

	void UpdatePvsStatistics()
	{
		var visible = 0;
		foreach ( var cell in _cells.Values )
			foreach ( var instance in cell.Instances )
				if ( !instance.IsBatched && instance.RenderingEnabled ) visible++;

		foreach ( var cluster in _clusters.Values )
			foreach ( var batch in cluster.Batches.Values )
				if ( batch.RenderingEnabled ) visible += batch.Transforms.Count;

		_pvsVisibleProps = visible;
		_pvsCulledProps = Math.Max( 0, _liveProps - _pvsVisibleProps );
	}

	/// <summary>
	/// Reports what the global distance cull hides, measured against the live cell
	/// state at completion. The counters are not accumulated during RefreshCells: while a
	/// scene is loading there is no OnUpdate, so RefreshCells has run exactly once, before
	/// any instance existed, and an accumulation from that pass reports zero regardless of
	/// how many props actually sit beyond the radius.
	/// </summary>
	void LogRenderDistanceSummary()
	{
		var renderDistance = Titanfall2StreamingSettings.PropRenderDistance;
		if ( renderDistance <= 0f ) return;

		var batches = 0;
		var props = 0;
		// Batches belong to the cluster, so a cluster counts only when even its closest cell
		// is beyond the radius; that matches UpdateClusterVisibilityState, where one cell
		// inside the radius keeps the whole shared batch visible.
		foreach ( var cluster in _clusters.Values )
		{
			if ( MathF.Sqrt( cluster.DistanceSquared ) <= renderDistance ) continue;
			batches += cluster.Batches.Count;
		}

		foreach ( var cell in _cells.Values )
		{
			var cellDistance = MathF.Sqrt( cell.DistanceSquared );
			if ( cellDistance <= renderDistance ) continue;
			props += cell.Instances.Count;
		}

		Log.Info( $"Titanfall 2 prop render distance: active at "
			+ $"{renderDistance:0} (+{RenderDistanceHysteresis:0} hysteresis); "
			+ $"{batches} of {_batchCount} batches and {props} of {_liveProps} individual props sit beyond it "
			+ $"relative to the anchor at the last cell refresh ({MapPath})." );
	}
}
