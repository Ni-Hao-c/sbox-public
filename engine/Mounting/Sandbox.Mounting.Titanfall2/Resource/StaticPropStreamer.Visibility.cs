using System.Diagnostics;
using Sandbox.Mounting.PerfKit;
using Titanfall2;

public sealed partial class Titanfall2StaticPropStreamer
{
	Titanfall2BspReader.VisibilityCellMask ResolveVisiblePvsCells( Vector3 anchor )
	{
		_pvsCameraCell = -1;
		if ( !TryFindPvsCell( anchor, out _pvsCameraCell ) ) return default;
		var reachable = _visibility.GetReachableCells( _pvsCameraCell, Titanfall2StreamingSettings.PropPvsPortalDepth )
			.Union( _visibility.AlwaysVisibleCells );
		LogPvsDiagnostics( anchor, applied: true );
		return reachable;
	}

	bool TryFindPvsCell( Vector3 anchor, out int cell )
	{
		cell = -1;
		if ( !Titanfall2StreamingSettings.PropPvsCulling )
		{
			LogPvsDiagnostics( anchor, applied: false, reason: "SBOX_TITANFALL2_PROP_PVS disabled culling" );
			return false;
		}
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
	/// the only way to tell the three failure modes apart at runtime: culling turned
	/// off, data that failed its validity gate, and an anchor that no cell contains.
	/// The engine's own summary cannot distinguish them, and the empty visible/culled
	/// counters in the completion line look identical in all three cases.
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

	/// <summary>True when any distance rule (global or authored per prop) can hide an instance.</summary>
	static bool IsDistanceCullActive => !Titanfall2StreamingSettings.PropRenderDistanceDryRun
		&& (Titanfall2StreamingSettings.PropRenderDistance > 0 || Titanfall2StreamingSettings.PropAuthoredFade);

	/// <summary>
	/// The distance at which one prop stops drawing, before its radius and hysteresis are added:
	/// the nearer of the global PropRenderDistance and the prop's authored fade distance.
	/// Infinity means distance never hides it.
	/// </summary>
	static float GetRenderDistance( float fadeDistance )
	{
		var global = Titanfall2StreamingSettings.PropRenderDistance > 0 ? Titanfall2StreamingSettings.PropRenderDistance : float.PositiveInfinity;
		if ( !Titanfall2StreamingSettings.PropAuthoredFade || !(fadeDistance > 0) ) return global;
		return MathF.Min( global, fadeDistance * Titanfall2StreamingSettings.PropAuthoredFadeScale );
	}

	bool IsWithinRenderDistance( Vector3 position, float radius, bool wasVisible, float fadeDistance )
	{
		var landmark = Titanfall2StreamingSettings.PropLandmarkRadius;
		if ( !IsDistanceCullActive || (landmark > 0 && radius >= landmark) ) return true;
		var distance = GetRenderDistance( fadeDistance );
		if ( float.IsPositiveInfinity( distance ) ) return true;
		distance += radius + (wasVisible ? Titanfall2StreamingSettings.PropRenderDistanceHysteresis : 0);
		return position.DistanceSquared( _lastAnchor ) <= distance * distance;
	}

	/// <summary>Per-instance keep distances (render distance plus hysteresis) for a batch's culler.</summary>
	static float[] GetKeepDistances( PendingBatch batch )
	{
		var key = (Titanfall2StreamingSettings.PropRenderDistance, Titanfall2StreamingSettings.PropRenderDistanceHysteresis,
			Titanfall2StreamingSettings.PropAuthoredFade, Titanfall2StreamingSettings.PropAuthoredFadeScale);
		if ( batch.KeepDistances is { } cached && cached.Length == batch.FadeDistances.Count && batch.KeepDistanceKey == key ) return cached;
		var result = new float[batch.FadeDistances.Count];
		for ( var index = 0; index < result.Length; index++ )
			result[index] = GetRenderDistance( batch.FadeDistances[index] ) + key.PropRenderDistanceHysteresis;
		batch.KeepDistances = result;
		batch.KeepDistanceKey = key;
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
		var instanceCull = IsDistanceCullActive;
		var anchor = _lastAnchor;

		foreach ( var batch in cluster.Batches.Values )
		{
			var visible = IsRenderVisible( batch.PvsCells );
			var wasRendered = batch.RenderingEnabled;

			if ( batch.RenderBatch.IsValid() )
			{
				if ( instanceCull )
				{
					if ( visible )
					{
						// Re-evaluate a batch restored after PVS hid it at an older anchor.
						if ( !wasRendered ) batch.Culler.Invalidate();
						var all = batch.GetOrBuildTransformArray();
						var changed = batch.Culler.Update( all, anchor, float.PositiveInfinity, batch.Radii,
							Titanfall2StreamingSettings.PropLandmarkRadius, GetKeepDistances( batch ) );
						if ( batch.Culler.KeptCount == 0 ) visible = false;
						else if ( changed ) batch.Culler.ApplyTo( batch.RenderBatch );
					}
				}
				else if ( batch.Culler.IsInitialized )
				{
					// Switched back to "no cull" at runtime: restore the full set once instead of
					// leaving the batch drawing the last subset forever.
					batch.Culler.Reset();
					batch.RenderBatch.SetDrawTransforms( batch.GetOrBuildTransformArray() );
				}
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
		var visible = _aggregatedProps; // Baked geometry now follows native world visibility.
		foreach ( var cell in _cells.Values )
			foreach ( var instance in cell.Instances )
				if ( !instance.IsBatched && instance.RenderingEnabled ) visible++;

		foreach ( var cluster in _clusters.Values )
			foreach ( var batch in cluster.Batches.Values )
				if ( batch.RenderingEnabled ) visible += batch.Transforms.Count;

		_pvsVisibleProps = visible;
		_pvsCulledProps = Math.Max( 0, _liveProps - _pvsVisibleProps );
	}

	void UpdateClusterShadowState( PropCluster cluster, Vector3 anchor )
	{
		if ( !Titanfall2StreamingSettings.PropShadows && _shadowCastingProps == 0 ) return;
		foreach ( var cell in cluster.Cells )
		{
			foreach ( var instance in cell.Instances )
			{
				if ( instance.IsBatched ) continue;
				var castShadows = ShouldCastShadows( instance.Position, instance.Radius, anchor );
				if ( castShadows != instance.CastShadows )
				{
					instance.CastShadows = castShadows;
					if ( instance.RenderObject.IsValid() )
						instance.RenderObject.Flags.CastShadows = castShadows;
					_shadowCastingProps += castShadows ? 1 : -1;
				}
			}
		}

		foreach ( var pendingBatch in cluster.Batches.Values )
		{
			// A valid render batch is all-or-none, so one near instance is enough to decide; only
			// the fallback path needs the exact count.
			var hasRenderBatch = pendingBatch.RenderBatch.IsValid();
			var nearCount = 0;
			for ( var index = 0; index < pendingBatch.Transforms.Count; ++index )
			{
				if ( ShouldCastShadows(
					pendingBatch.Transforms[index].Position,
					pendingBatch.Radii[index],
					anchor ) )
				{
					nearCount++;
					if ( hasRenderBatch ) break;
				}
			}

			// A batch has one shadow-pass flag. If any instance in this cluster/model
			// group is close enough, the permanent batch casts as a whole.
			var desiredCount = pendingBatch.RenderBatch.IsValid()
				? (nearCount > 0 ? pendingBatch.Transforms.Count : 0)
				: nearCount;
			if ( pendingBatch.RenderBatch.IsValid() )
				pendingBatch.RenderBatch.CastShadows = desiredCount > 0;
			else if ( pendingBatch.FallbackObjects.Count == pendingBatch.Transforms.Count )
			{
				for ( var index = 0; index < pendingBatch.FallbackObjects.Count; ++index )
					pendingBatch.FallbackObjects[index].Flags.CastShadows = ShouldCastShadows(
						pendingBatch.Transforms[index].Position,
						pendingBatch.Radii[index],
						anchor );
			}

			_shadowCastingProps += desiredCount - pendingBatch.ShadowedInstanceCount;
			pendingBatch.ShadowedInstanceCount = desiredCount;
		}
	}

	/// <summary>
	/// Reports what the optional distance cull would hide, measured against the live cell
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

		var dryRun = Titanfall2StreamingSettings.PropRenderDistanceDryRun;
		Log.Info( $"Titanfall 2 prop render distance: {(dryRun ? "dry run, nothing was hidden" : "active")} at "
			+ $"{renderDistance:0} (+{Titanfall2StreamingSettings.PropRenderDistanceHysteresis:0} hysteresis); "
			+ $"{batches} of {_batchCount} batches and {props} of {_liveProps} individual props sit beyond it "
			+ $"relative to the anchor at the last cell refresh ({MapPath})." );
	}
}
