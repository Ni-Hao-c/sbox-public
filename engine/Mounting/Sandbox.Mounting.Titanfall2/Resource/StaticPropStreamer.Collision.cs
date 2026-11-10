using System.Diagnostics;
using Sandbox.Mounting.PerfKit;
using Titanfall2;

public sealed partial class Titanfall2StaticPropStreamer
{
	/// <summary>
	/// Applies collision state on its own schedule. One collider costs a synchronous model
	/// load plus a native physics body (2-22 ms), so this pass is capped per frame and
	/// resumes the cell it stopped on. It is separate from the state pass precisely because
	/// stopping early is normal for it.
	/// </summary>
	void ProcessCollisionQueue()
	{
		if ( _collisionCellCursor >= _collisionCellQueue.Count ) return;
		_collisionQueueTimer.Restart();
		_collisionInstancesThisFrame = 0;
		while ( _collisionCellCursor < _collisionCellQueue.Count )
		{
			if ( _collisionInstancesThisFrame >= CollisionInstancesPerFrame ) break;
			var cell = _collisionCellQueue[_collisionCellCursor];
			if ( !UpdateCellCollisionState( cell, _lastAnchor, _collisionQueueTimer ) )
			{
				// The budget stopped this cell partway. Leave the cursor on it so the next frame
				// resumes its own instance cursor exactly where it stopped.
				_collisionResumeCount++;
				break;
			}

			_collisionCellCursor++;
			if ( _collisionQueueTimer.Elapsed.TotalMilliseconds >= CollisionFrameBudgetMilliseconds ) break;
		}

		if ( _collisionCellCursor < _collisionCellQueue.Count ) return;
		_collisionCellQueue.Clear();
		_collisionCellCursor = 0;
	}

	/// <summary>
	/// Applies the collision state for one cell. Returns false when the per-frame cap
	/// or the frame budget stopped the pass early, in which case the collision queue
	/// resumes this cell on a later frame. A single collider is cheap in isolation
	/// (0.02-0.69 ms measured), but a dense cell holds many and movement used to churn
	/// them at 90-130 per second, so the pass needs both a count cap and a time cap.
	/// The budget timer is supplied by the caller because the collision pass has its own
	/// per-frame budget; sharing the state pass's timer would abort immediately.
	/// </summary>
	bool UpdateCellCollisionState( PropCell cell, Vector3 anchor, Stopwatch budgetTimer )
	{
		// The loading pass that gates readiness overrides both budgets with the preload
		// settings; the runtime pass keeps the churn caps.
		var frameBudget = _collisionBudgetOverrideMilliseconds > 0f
			? _collisionBudgetOverrideMilliseconds
			: CollisionFrameBudgetMilliseconds;
		var instanceLimit = _collisionInstanceLimitOverride > 0
			? _collisionInstanceLimitOverride
			: CollisionInstancesPerFrame;

		// Resume from this cell's own cursor. Restarting the scan from the first
		// instance every frame would re-spend the whole per-frame cap on the same
		// leading instances and the tail of a dense cell would never be reached.
		while ( cell.CollisionCursor < cell.Instances.Count )
		{
			if ( _collisionInstancesThisFrame >= instanceLimit )
				return false;

			// Checked before every collider rather than after a batch of them, so one slow
			// activation cannot carry the frame past the budget. Returning here leaves the
			// cursor on this instance: it is retried next frame with a fresh budget, so the
			// pass still makes progress even when it stops immediately every frame.
			if ( budgetTimer.Elapsed.TotalMilliseconds >= frameBudget )
				return false;

			var instance = cell.Instances[cell.CollisionCursor];
			// Only a body that was actually created or destroyed counts against the cap. Counting
			// every check spent the default cap of 2 on instances whose state was already right,
			// so the queue advanced ~100 instances/s and colliders lagged the player in dense
			// areas. Pure checks stay bounded by the frame budget above.
			if ( instance.Source.Collidable && !instance.CollisionUnavailable
				&& EnsureCollisionState( instance, anchor, fallbackModel: null ) )
				_collisionInstancesThisFrame++;

			cell.CollisionCursor++;
		}

		return true;
	}

	/// <returns>True when a collision body was created, destroyed or attempted (the costly work).</returns>
	bool EnsureCollisionState( PropInstance instance, Vector3 anchor, Model fallbackModel )
	{
		if ( !instance.Source.Collidable
			|| instance.CollisionUnavailable || instance.CollisionFiltered )
			return false;

		var collisionActive = instance.CollisionObject.IsValid() && !instance.CollisionObject.IsDestroyed;
		var shouldBeActive = KeepAllCollisions || ShouldKeepCollision( instance.Position, instance.Radius, anchor, collisionActive );
		if ( shouldBeActive == collisionActive ) return false;
		// A collider destroyed by something else still holds its reference here. Releasing its
		// count first keeps _collidersCreated from drifting upward when it is recreated below.
		if ( !collisionActive && instance.CollisionObject is not null )
		{
			instance.CollisionObject = null;
			_collidersCreated = Math.Max( 0, _collidersCreated - 1 );
		}
		if ( !shouldBeActive )
		{
			// Destroy rather than recycle. A collision-object pool was tried here and removed:
			// its release path cleared the instance reference without handing the object to the
			// pool, so released colliders were never torn down at all, and its measured reuse
			// rate was ~2%. At the ~15 activations per second the instance cap and the frame
			// budget leave, one GameObject lifecycle is not worth that complexity or that risk.
			instance.CollisionObject.Destroy();
			instance.CollisionObject = null;
			_collidersCreated = Math.Max( 0, _collidersCreated - 1 );
			_collidersReleased++;
			return true;
		}

		var prop = instance.Source;
		_collisionProbe.Restart();
		var resourcePath = $"mount://{MountIdent}/{prop.ModelPath}.vmdl";
		var collision = LoadCollisionModelWithRetryCache( resourcePath, prop.Skin );
		if ( collision.Status == Titanfall2CollisionModelStatus.Filtered )
		{
			instance.CollisionFiltered = true;
			_collisionFilteredProps++;
			return true;
		}
		if ( collision.Status == Titanfall2CollisionModelStatus.Empty )
		{
			instance.CollisionUnavailable = true;
			_colliderFailures++;
			if ( _failureLogs++ < 16 )
				Titanfall2Log.Warning( $"Titanfall 2 static prop imported without usable collision geometry: '{prop.ModelPath}' ({MapPath})." );
			return true;
		}
		// An already loaded full model may recover a transient collision-only load failure,
		// but a model without physics must not turn a retryable failure into a terminal one.
		var resolvedModel = ModelLoader.IsUsableModel( collision.Model ) ? collision.Model
			: ModelLoader.IsUsableModel( fallbackModel ) && fallbackModel.Physics is { Parts.Count: > 0 } ? fallbackModel : null;
		if ( !ModelLoader.IsUsableModel( resolvedModel ) )
		{
			// A read/parse failure is transient, not proof that the prop has no collision.
			// Leaving CollisionUnavailable false lets a later refresh retry it (the model
			// retry cache bounds how often the file is read again) instead of permanently
			// blocking dedicated-server readiness on one hiccup.
			if ( _failureLogs++ < 16 )
				Titanfall2Log.Warning( $"Titanfall 2 static prop collision model is temporarily unavailable: "
					+ $"'{prop.ModelPath}' at {prop.Position} ({MapPath})." );
			ReportSpike( _collisionProbe, "deferred collision body for '{0}'", prop.ModelPath );
			return true;
		}

		var data = resolvedModel.GetData<Titanfall2ModelCollisionData>();
		// Only a known v6 instance and known MDL contents can prove this object has
		// no collision for ANY retail query. Nonzero contents still need gameplay
		// query classification; never discard bullet-only props using a player mask.
		var hasContents = prop.SourceIndex >= 0 && data is not null;
		var contents = hasContents ? Titanfall2CollisionSource.StaticPropContents( data.ModelContents, prop.CollisionFlagsRemove ) : 0u;
		if ( hasContents && contents == 0 )
		{
			instance.CollisionFiltered = true;
			_collisionFilteredProps++;
			return true;
		}
		var collisionObject = new GameObject( GameObject, true, System.IO.Path.GetFileNameWithoutExtension( prop.ModelPath ) );
		collisionObject.Flags |= GameObjectFlags.NotSaved;
		collisionObject.WorldPosition = prop.Position;
		collisionObject.WorldRotation = prop.Rotation;
		collisionObject.WorldScale = Vector3.One * MathF.Max( prop.Scale, 0.001f );
		collisionObject.IsStatic = true;
		if ( Titanfall2StreamingSettings.NavMeshStaticProps )
			collisionObject.Tags.Add( Titanfall2DeferredNavMeshBuilder.NavMeshBodyTag );
		var collider = collisionObject.AddComponent<ModelCollider>();
		collider.Model = resolvedModel;
		collider.Static = true;
		var source = collisionObject.AddComponent<Titanfall2CollisionSource>();
		source.MountIdent = MountIdent;
		source.MapPath = MapPath;
		source.ModelPath = prop.ModelPath;
		source.SourceIndex = prop.SourceIndex;
		source.HasInstanceMetadata = prop.SourceIndex >= 0;
		source.SolidType = prop.SolidType;
		source.ReservedCollisionData = prop.ReservedCollisionData;
		source.CollisionFlagsRemove = prop.CollisionFlagsRemove;
		source.HasEffectiveContents = hasContents;
		source.EffectiveContents = contents;
		if ( hasContents ) Titanfall2CollisionQueries.Classify( collisionObject, collider, contents );
		if ( ModelLoader.IsUsableModel( collider.Model )
			&& collider.Model.Physics is { Parts.Count: > 0 } )
		{
			instance.CollisionObject = collisionObject;
			source.CollisionDestroyed = OnResidentCollisionDestroyed;
			_collidersCreated++;
			ReportSpike( _collisionProbe, "collision body for '{0}'", prop.ModelPath );
			return true;
		}

		_colliderFailures++;
		instance.CollisionUnavailable = true;
		if ( _failureLogs++ < 16 )
			Titanfall2Log.Warning( $"Titanfall 2 static prop has no usable collision bodies: "
				+ $"'{prop.ModelPath}' at {prop.Position} ({MapPath})." );
		collisionObject.Destroy();
		ReportSpike( _collisionProbe, "failed collision body for '{0}'", prop.ModelPath );
		return true;
	}

	static bool ShouldKeepCollision( Vector3 position, float radius, Vector3 anchor, bool currentlyActive )
	{
		if ( Titanfall2StreamingSettings.NavMeshStaticProps ) return true;
		var configuredDistance = currentlyActive ? CollisionDisableDistance : CollisionEnableDistance;
		var distance = configuredDistance + radius;
		return position.DistanceSquared( anchor ) <= distance * distance;
	}
}
