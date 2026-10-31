using System.Diagnostics;
using Sandbox.Mounting.PerfKit;
using Titanfall2;

public sealed partial class Titanfall2StaticPropStreamer
{
	/// <summary>
	/// Creates the instanced draw object for one batch. Which flags to submit with and whether
	/// the draw passes an explicit LOD are mount settings; the batch itself (bounds, submission
	/// flags, explicit-LOD draw, instance-set replacement) is PerfKit's reusable implementation.
	/// </summary>
	MountedInstanceBatch CreateRenderBatch( PendingBatch batch, Model model, bool castShadows, Titanfall2PropLodMode lodMode )
	{
		var probeColor = batch.AverageProbeColor;
		var options = new MountedInstanceBatchOptions
		{
			CastShadows = castShadows,
			Batchable = Titanfall2StreamingSettings.PropBatchable,
			IncludeInCubemap = Titanfall2StreamingSettings.PropIncludeInCubemap,
			NeedsEnvironmentMap = Titanfall2StreamingSettings.PropNeedsEnvironmentMap,
			Tint = new Color( probeColor.x, probeColor.y, probeColor.z, 1f ),
			SelectLod = lodMode == Titanfall2PropLodMode.Native ? ModelLoader.SelectLodByDistance : null,
		};
		MountedInstanceBatch result = batch.Probes.Any( static probe => probe.Metadata.x > 0 )
			? new Titanfall2ProbeInstanceBatch( Scene.SceneWorld, model, batch.GetOrBuildTransformArray(), batch.Probes.ToArray(), BakedSkyColor, options, _localLights )
			: new Titanfall2PlainInstanceBatch( Scene.SceneWorld, model, batch.GetOrBuildTransformArray(), options );
		result.FrustumCullingEnabled = Titanfall2StreamingSettings.PropInstanceFrustumCulling;
		// Production opaque/cutout variants return depth normals before reading probes.
		// Leave arbitrary forward-only diagnostic/custom batch shaders on the full path.
		if ( result is Titanfall2ProbeInstanceBatch probes ) probes.SkipDepthProbeBindings = true;
		return result;
	}

	void BuildPendingBatches()
	{
		if ( _batchesBuilt ) return;
		_batchesBuilt = true;

		foreach ( var cluster in _clusters.Values )
		{
			foreach ( var pendingBatch in cluster.Batches.Values )
			{
				if ( pendingBatch.Transforms.Count == 0 ) continue;
				var castShadows = pendingBatch.ShadowedInstanceCount > 0;
				try
				{
					pendingBatch.RenderBatch = CreateRenderBatch(
						pendingBatch, pendingBatch.Model, castShadows, pendingBatch.LodMode );
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
						fallback.Flags.CastShadows = ShouldCastShadows(
							pendingBatch.Transforms[index].Position,
							pendingBatch.Radii[index],
							_lastAnchor );
						fallback.RenderingEnabled = Active && pendingBatch.RenderingEnabled;
						pendingBatch.FallbackObjects.Add( fallback );
					}
				}
			}
		}
	}

	static bool ShouldCastShadows( Vector3 position, float radius, Vector3 anchor )
	{
		if ( !Titanfall2StreamingSettings.PropShadows ) return false;
		if ( radius < Titanfall2StreamingSettings.PropShadowMinimumRadius ) return false;
		if ( Titanfall2StreamingSettings.PropShadowDistance <= 0f ) return true;
		var distance = Titanfall2StreamingSettings.PropShadowDistance + radius;
		return position.DistanceSquared( anchor ) <= distance * distance;
	}

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
