using System.Diagnostics;
using Titanfall2;

public sealed partial class Titanfall2StaticPropStreamer
{
	// Nested to consume the scene's private instance records without exposing an engine API.
	// This worker owns physics scheduling and readiness; render ownership is independent.
	sealed class CollisionRuntime( Titanfall2StaticPropStreamer owner )
	{
		public readonly List<PropCell> Queue = new();
		public bool Dirty;
		public int Cursor;
		public int Expected;
		public int Created;
		public int Failures;
		public int Filtered;
		public int Released;
		public readonly Stopwatch Probe = new();
		public readonly Stopwatch Timer = new();
		public int Changes;
		public int PreloadAttempts;
		public int Deferred;
		public int Resumes;
		public int InstanceLimitOverride;
		public float BudgetOverrideMilliseconds;
		public bool ResidentReady;
		public bool HasPending => Created + Filtered + Failures < Expected;
		public bool NeedsSpatialWork => !owner.KeepAllCollisions || HasPending || Dirty;

		public void BeginPopulate() => PreloadAttempts = 0;

		public void Clear()
		{
			Queue.Clear();
			Cursor = 0;
			Expected = Created = Filtered = Failures = 0;
			ResidentReady = Dirty = false;
		}

		public void Register( PropInstance instance, Model model, Stopwatch timer, float budgetMilliseconds )
		{
			if ( !instance.Source.Collidable ) return;
			Expected++;
			if ( Application.IsDedicatedServer ) return;
			if ( PreloadAttempts < PreloadCollisionBodiesPerYield && timer.Elapsed.TotalMilliseconds < budgetMilliseconds )
			{
				PreloadAttempts++;
				EnsureCollisionState( instance, owner._lastAnchor, model );
			}
			else Deferred++;
		}

		public void Refresh()
		{
			if ( !owner.KeepAllCollisions || HasPending ) RebuildCollisionQueue();
		}

		public void Tick()
		{
			if ( Dirty )
			{
				Dirty = false;
				RebuildCollisionQueue();
			}
			if ( Queue.Count > 0 ) ProcessCollisionQueue();
			if ( owner.KeepAllCollisions && !ResidentReady && owner._failedProps == 0 && Failures == 0
				&& Created + Filtered == Expected )
			{
				ResidentReady = true;
				Titanfall2Log.Info( $"Titanfall 2 resident prop collision became ready after the loading pass: "
					+ $"{Created}/{Expected} colliders ({owner.MapPath})." );
			}
		}
		/// <summary>
		/// Applies collision state on its own schedule. One collider costs a synchronous model
		/// load plus a native physics body (2-22 ms), so this pass is capped per frame and
		/// resumes the cell it stopped on. It is separate from the state pass precisely because
		/// stopping early is normal for it.
		/// </summary>
		public void ProcessCollisionQueue()
		{
			if ( Cursor >= Queue.Count ) return;
			Timer.Restart();
			Changes = 0;
			while ( Cursor < Queue.Count )
			{
				if ( Changes >= CollisionInstancesPerFrame ) break;
				var cell = Queue[Cursor];
				if ( !UpdateCellCollisionState( cell, owner._lastAnchor, Timer ) )
				{
					// The budget stopped this cell partway. Leave the cursor on it so the next frame
					// resumes its own instance cursor exactly where it stopped.
					Resumes++;
					break;
				}

				Cursor++;
				if ( Timer.Elapsed.TotalMilliseconds >= CollisionFrameBudgetMilliseconds ) break;
			}

			if ( Cursor < Queue.Count ) return;
			Queue.Clear();
			Cursor = 0;
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
		public bool UpdateCellCollisionState( PropCell cell, Vector3 anchor, Stopwatch budgetTimer )
		{
			// The loading pass that gates readiness overrides both budgets with the preload
			// settings; the runtime pass keeps the churn caps.
			var frameBudget = BudgetOverrideMilliseconds > 0f
				? BudgetOverrideMilliseconds
				: CollisionFrameBudgetMilliseconds;
			var instanceLimit = InstanceLimitOverride > 0
				? InstanceLimitOverride
				: CollisionInstancesPerFrame;

			// Resume from this cell's own cursor. Restarting the scan from the first
			// instance every frame would re-spend the whole per-frame cap on the same
			// leading instances and the tail of a dense cell would never be reached.
			while ( cell.CollisionCursor < cell.Instances.Count )
			{
				if ( Changes >= instanceLimit )
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
					Changes++;

				cell.CollisionCursor++;
			}

			return true;
		}

		/// <returns>True when a collision body was created, destroyed or attempted (the costly work).</returns>
		public bool EnsureCollisionState( PropInstance instance, Vector3 anchor, Model fallbackModel )
		{
			if ( !instance.Source.Collidable
				|| instance.CollisionUnavailable || instance.CollisionFiltered )
				return false;

			var collisionActive = instance.CollisionObject.IsValid() && !instance.CollisionObject.IsDestroyed;
			var shouldBeActive = owner.KeepAllCollisions || ShouldKeepCollision( instance.Position, instance.Radius, anchor, collisionActive );
			if ( shouldBeActive == collisionActive ) return false;
			// A collider destroyed by something else still holds its reference here. Releasing its
			// count first keeps Created from drifting upward when it is recreated below.
			if ( !collisionActive && instance.CollisionObject is not null )
			{
				instance.CollisionObject = null;
				Created = Math.Max( 0, Created - 1 );
			}
			if ( !shouldBeActive )
			{
				// Destroy rather than recycle. A collision-object pool was tried here and removed:
				// its release path cleared the instance reference without handing the object to the
				// pool, so released colliders were never torn down at all, and its measured reuse
				// rate was ~2%. At the ~15 activations per second the instance cap and the frame
				// budget leave, one owner.GameObject lifecycle is not worth that complexity or that risk.
				instance.CollisionObject.Destroy();
				instance.CollisionObject = null;
				Created = Math.Max( 0, Created - 1 );
				Released++;
				return true;
			}

			var prop = instance.Source;
			Probe.Restart();
			var resourcePath = $"mount://{owner.MountIdent}/{prop.ModelPath}.vmdl";
			var collision = owner.LoadCollisionModelWithRetryCache( resourcePath, prop.Skin );
			if ( collision.Status == Titanfall2CollisionModelStatus.Filtered )
			{
				instance.CollisionFiltered = true;
				Filtered++;
				return true;
			}
			if ( collision.Status == Titanfall2CollisionModelStatus.Empty )
			{
				instance.CollisionUnavailable = true;
				Failures++;
				if ( owner._failureLogs++ < 16 )
					Titanfall2Log.Warning( $"Titanfall 2 static prop imported without usable collision geometry: '{prop.ModelPath}' ({owner.MapPath})." );
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
				if ( owner._failureLogs++ < 16 )
					Titanfall2Log.Warning( $"Titanfall 2 static prop collision model is temporarily unavailable: "
						+ $"'{prop.ModelPath}' at {prop.Position} ({owner.MapPath})." );
				ReportSpike( Probe, "deferred collision body for '{0}'", prop.ModelPath );
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
				Filtered++;
				return true;
			}
			var collisionObject = new GameObject( owner.GameObject, true, System.IO.Path.GetFileNameWithoutExtension( prop.ModelPath ) );
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
			source.MountIdent = owner.MountIdent;
			source.MapPath = owner.MapPath;
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
				Created++;
				ReportSpike( Probe, "collision body for '{0}'", prop.ModelPath );
				return true;
			}

			Failures++;
			instance.CollisionUnavailable = true;
			if ( owner._failureLogs++ < 16 )
				Titanfall2Log.Warning( $"Titanfall 2 static prop has no usable collision bodies: "
					+ $"'{prop.ModelPath}' at {prop.Position} ({owner.MapPath})." );
			collisionObject.Destroy();
			ReportSpike( Probe, "failed collision body for '{0}'", prop.ModelPath );
			return true;
		}

		static bool ShouldKeepCollision( Vector3 position, float radius, Vector3 anchor, bool currentlyActive )
		{
			if ( Titanfall2StreamingSettings.NavMeshStaticProps ) return true;
			var configuredDistance = currentlyActive ? CollisionDisableDistance : CollisionEnableDistance;
			var distance = configuredDistance + radius;
			return position.DistanceSquared( anchor ) <= distance * distance;
		}
		public void RebuildCollisionQueue()
		{
			Queue.Clear();
			Queue.AddRange( owner._cells.Values );
			Queue.Sort( static ( left, right ) => left.DistanceSquared.CompareTo( right.DistanceSquared ) );
			Cursor = 0;
			// A refresh re-evaluates every cell from the start, so any collision pass that
			// was interrupted by the per-frame cap begins again rather than resuming.
			foreach ( var cell in Queue ) cell.CollisionCursor = 0;
		}

		void OnResidentCollisionDestroyed()
		{
			if ( owner._releasing || !owner.KeepAllCollisions ) return;
			ResidentReady = false;
			Dirty = true;
		}


		public async Task Preload( LoadingContext context )
		{
			// Readiness must arrive in a bounded number of yields. At the runtime churn cap
			// (2 colliders per frame) a map with thousands of solid props would take
			// thousands of frames after the prop pass, so the load pass uses the preload
			// count/budget instead.
			InstanceLimitOverride = PreloadCollisionBodiesPerYield;
			BudgetOverrideMilliseconds = PreloadBudgetMilliseconds;
			try
			{
				foreach ( var cell in owner._cells.Values )
				{
					cell.CollisionCursor = 0;
					bool complete;
					do
					{
						if ( !owner.IsValid() || !owner.GameObject.IsValid() ) return;
						Timer.Restart();
						Changes = 0;
						complete = UpdateCellCollisionState( cell, owner.FallbackAnchor, Timer );
						context.Title = $"Loading Titanfall 2 prop collision ({Created}/{Expected})";
						if ( !complete ) await owner.Task.Yield();
					} while ( !complete );
				}
			}
			finally
			{
				InstanceLimitOverride = 0;
				BudgetOverrideMilliseconds = 0f;
			}
			ResidentReady = owner._failedProps == 0 && Failures == 0
				&& Created + Filtered == Expected;
			if ( !HasPending )
			{
				Queue.Clear();
				Cursor = 0;
			}
		}

		public void SetEnabled( PropInstance instance, bool enabled )
		{
			if ( instance.CollisionObject.IsValid() ) instance.CollisionObject.Enabled = enabled;
		}

		public void Release( PropInstance instance )
		{
			if ( instance.CollisionObject.IsValid() && !instance.CollisionObject.IsDestroyed ) instance.CollisionObject.Destroy();
			instance.CollisionObject = null;
		}
	}
}
