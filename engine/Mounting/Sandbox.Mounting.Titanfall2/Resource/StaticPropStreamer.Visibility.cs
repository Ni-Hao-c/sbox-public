using System.Diagnostics;
using Sandbox.Mounting.PerfKit;
using Titanfall2;

public sealed partial class Titanfall2StaticPropStreamer
{
	Titanfall2BspReader.VisibilityCellMask ResolveVisiblePvsCells( Vector3 anchor )
	{
		_pvsCameraCell = -1;
		if ( !TryFindPvsCell( anchor, out _pvsCameraCell ) ) return default;
		var reachable = _visibility.GetReachableCells( _pvsCameraCell, PvsPortalDepth )
			.Union( _visibility.AlwaysVisibleCells );
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
