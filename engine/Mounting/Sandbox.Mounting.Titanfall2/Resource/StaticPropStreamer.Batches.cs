using System.Diagnostics;
using Sandbox.Mounting.PerfKit;
using Titanfall2;

public sealed partial class Titanfall2StaticPropStreamer
{
	/// <summary>
	/// Creates the instanced draw object for one batch. The batch itself (bounds, submission
	/// flags, explicit-LOD draw, instance-set replacement) is PerfKit's reusable implementation.
	/// </summary>
	MountedInstanceBatch CreateRenderBatch( PendingBatch batch, bool castShadows )
	{
		var model = batch.Model;
		var probeColor = batch.AverageProbeColor;
		var options = new MountedInstanceBatchOptions
		{
			CastShadows = castShadows,
			// Batchable lets the engine merge submissions; the two pass flags keep prop batches
			// out of the extra cubemap and environment-map passes.
			Batchable = true,
			IncludeInCubemap = false,
			NeedsEnvironmentMap = false,
			Tint = new Color( probeColor.x, probeColor.y, probeColor.z, 1f ),
			SelectLod = ModelLoader.SelectLodByDistance,
		};
		MountedInstanceBatch result = batch.Probes.Any( static probe => probe.Metadata.x > 0 )
			? new Titanfall2ProbeInstanceBatch( Scene.SceneWorld, model, batch.GetOrBuildTransformArray(), batch.Probes.ToArray(), BakedSkyColor, options, _localLights )
			: new Titanfall2PlainInstanceBatch( Scene.SceneWorld, model, batch.GetOrBuildTransformArray(), options );
		// Production opaque/cutout variants return depth normals before reading probes.
		// Leave arbitrary forward-only diagnostic/custom batch shaders on the full path.
		if ( result is Titanfall2ProbeInstanceBatch probes ) probes.SkipDepthProbeBindings = true;
		return result;
	}

	void BuildPendingBatches()
	{
		if ( _batchesBuilt ) return;
		_batchesBuilt = true;
		BuildGpuStaticScene();

		foreach ( var cluster in _clusters.Values )
		{
			foreach ( var pendingBatch in cluster.Batches.Values )
			{
				if ( pendingBatch.Transforms.Count == 0 ) continue;
				if ( pendingBatch.GpuRendered ) continue;
				var castShadows = pendingBatch.ShadowedInstanceCount > 0;
				try
				{
					pendingBatch.RenderBatch = CreateRenderBatch( pendingBatch, castShadows );
					pendingBatch.RenderBatch.RenderingEnabled = Active && pendingBatch.RenderingEnabled;
					_batchCount++;

					var batchedShadowCount = castShadows ? pendingBatch.Transforms.Count : 0;
					_shadowCastingProps += batchedShadowCount - pendingBatch.ShadowedInstanceCount;
					pendingBatch.ShadowedInstanceCount = batchedShadowCount;
				}
				catch ( Exception exception )
				{
					Log.Warning( exception, $"Unable to instance Titanfall 2 static model '{pendingBatch.Model.Name}'; "
						+ "retaining permanent individual objects." );
					for ( var index = 0; index < pendingBatch.Transforms.Count; ++index )
					{
						var fallback = new SceneObject(
							Scene.SceneWorld,
							pendingBatch.Model,
							pendingBatch.Transforms[index] );
						fallback.Flags.IsStatic = true;
						var probeColor = pendingBatch.ProbeColors[index];
						fallback.ColorTint = new Color( probeColor.x, probeColor.y, probeColor.z, 1f );
						pendingBatch.Probes[index].Apply( fallback, BakedSkyColor );
						fallback.Flags.CastShadows = ShouldCastShadows( pendingBatch.Radii[index] );
						fallback.RenderingEnabled = Active && pendingBatch.RenderingEnabled;
						pendingBatch.FallbackObjects.Add( fallback );
					}
				}
			}
		}

		// The gate needs one visibility sweep from the current anchor before the first submitted
		// frame. Forcing it only when gating is on keeps the disabled configuration (prop shadows
		// off, or distance 0) exactly on its previous timing: the sweep is otherwise driven by the
		// camera refresh, and its own per-frame budget still bounds the work.
		if ( _shadowGateActive )
		{
			_runtimePvsUpdatePending = true;
			RebuildRuntimeClusterQueue();
		}

		RefreshCpuRenderOwnership();
		LogBatchShadowConfiguration();
		CompileRenderCensus();
	}

	/// <summary>
	/// Reports the effective shadow and LOD distances once, when the batches are built. Both
	/// switches change what the frame submits, so an A/B run has to read the effective values from
	/// the log rather than assume the environment variable took effect (a value that fails to
	/// parse falls back to the default silently). The sweep-time gated counts are logged by
	/// <see cref="LogShadowGateSweep"/> when a sweep actually crosses the distance.
	/// </summary>
	void LogBatchShadowConfiguration()
	{
		var shadowGate = _shadowGateActive
			? $"distance gate {MathF.Sqrt( _shadowGateDistanceSquared ):0} units (restore inside {MathF.Sqrt( _shadowGateRestoreDistanceSquared ):0})"
			: "distance gate disabled (SBOX_TITANFALL2_PROP_SHADOWS off, or SBOX_TITANFALL2_PROP_SHADOW_DISTANCE 0)";
		Log.Info( $"Titanfall 2 prop shadows: {shadowGate} on {_batchCount} batches; LOD switch distances "
			+ $"{ModelLoader.GetLodSwitchDistance( 1 ):0}/{ModelLoader.GetLodSwitchDistance( 2 ):0}/{ModelLoader.GetLodSwitchDistance( 3 ):0} "
			+ $"(SBOX_TITANFALL2_PROP_LOD_SCALE {Titanfall2StreamingSettings.PropLodDistanceScale:0.00}) ({MapPath})." );
	}

	static bool ShouldCastShadows( float radius )
		=> Titanfall2StreamingSettings.PropShadows && radius >= ShadowMinimumRadius;

	/// <summary>
	/// Releases the native batches owned by one cluster. This is the only place a batch is
	/// deleted, so a batch shared by several cells can never be deleted twice, nor while a
	/// member cell still points at it.
	/// </summary>
	void DestroyClusterInstances( PropCluster cluster )
	{
		foreach ( var pendingBatch in cluster.Batches.Values )
		{
			_shadowCastingProps = Math.Max( 0, _shadowCastingProps - pendingBatch.ShadowedInstanceCount );
			pendingBatch.RenderBatch?.Delete();
			foreach ( var fallback in pendingBatch.FallbackObjects )
				fallback?.Delete();
		}
		cluster.Batches.Clear();

		foreach ( var cell in cluster.Cells )
			DestroyCellInstances( cell );
	}

	/// <summary>
	/// Releases the instances and colliders of one cell. Batches are deliberately not touched
	/// here: a batch is shared with every other cell in the cluster, so deleting it from a
	/// single cell would leave the rest of the cluster pointing at a deleted native object.
	/// </summary>
	void DestroyCellInstances( PropCell cell )
	{
		if ( !cell.Active && cell.Instances.Count == 0 ) return;
		foreach ( var instance in cell.Instances )
		{
			if ( instance.RenderingEnabled ) _renderingProps = Math.Max( 0, _renderingProps - 1 );
			if ( !instance.IsBatched && instance.CastShadows )
				_shadowCastingProps = Math.Max( 0, _shadowCastingProps - 1 );
			instance.RenderObject?.Delete();
			if ( instance.CollisionObject.IsValid() && !instance.CollisionObject.IsDestroyed ) instance.CollisionObject.Destroy();
		}
		_liveProps = Math.Max( 0, _liveProps - cell.Instances.Count );
		cell.Instances.Clear();
		cell.NextProp = 0;
		cell.Active = false;
		cell.Wanted = false;
		_activeCells = Math.Max( 0, _activeCells - 1 );
	}
}
