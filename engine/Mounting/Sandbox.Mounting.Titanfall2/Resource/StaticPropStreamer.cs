using System.Diagnostics;
using Sandbox.Mounting.PerfKit;
using Titanfall2;

/// <summary>
/// Progressively loads every Titanfall 2 BSP static prop. Cells near the active
/// camera are prioritized, then the remaining map cells are populated using the
/// same frame budget. Spawned props are retained until the scene is destroyed.
/// </summary>
[Library, Hide]
public sealed partial class Titanfall2StaticPropStreamer : Component, Sandbox.Internal.IUpdateSubscriber
{
	static readonly Sandbox.Diagnostics.Logger Log = new( "Titanfall2Props" );
	const int DataMagic = 0x32505354; // TSP2
	const int DataVersion = 8;
	const int MaximumModelCount = 65536;
	const int MaximumPropCount = 1_000_000;
	const int VisibilityMagic = 0x32565354; // TSV2
	const int VisibilityVersion = 1;
	// Encoded size of one element, used to bound a decoded count by the bytes that are
	// actually present instead of by a guessed maximum.
	const int PlaneBytes = 16;  // normal xyz + distance
	const int NodeBytes = 8;    // plane index + child
	const int PortalBytes = 8;  // from cell + to cell

	// Edge length of one BSP prop streaming cell, in world units.
	const float CellSize = 512f;
	// Cells within this distance of the camera are populated first.
	const float LoadRadius = 1024f;
	// Prop cells per axis grouped into one batch cluster; batches are keyed per cluster and model.
	// Read once from SBOX_TITANFALL2_BATCH_CLUSTER (1/2/4/8/16, default 4). The default keeps the
	// current batch composition, so the setting is safe to A/B without changing the shipped look.
	static readonly int BatchClusterCells = Titanfall2StreamingSettings.PropBatchClusterCells;
	// Props at least this large are never distance culled, so landmarks keep their silhouette.
	const float LandmarkRadius = 256f;
	// Extra retention band past a prop's render distance once it is visible.
	const float RenderDistanceHysteresis = 512f;
	// Maximum portal hops walked from the camera's PVS cell.
	const int PvsPortalDepth = 16;
	// Runtime visibility pass: cells visited and time spent per frame.
	const int RuntimeCellsPerFrame = 16;
	const float RuntimeCellBudgetMilliseconds = 1.5f;
	// Spawn pass after loading: props created and time spent per frame.
	const int PropsPerFrame = 16;
	const float FrameBudgetMilliseconds = 3f;
	// Loading-screen spawn pass: props created and time spent per yield.
	const int PreloadPerYield = 512;
	const float PreloadBudgetMilliseconds = 35f;
	// Colliders the loading pass may start per yield; the rest are left to the runtime pass.
	const int PreloadCollisionBodiesPerYield = 4;
	// Runtime collision pass: colliders created or destroyed and time spent per frame.
	const int CollisionInstancesPerFrame = 2;
	const float CollisionFrameBudgetMilliseconds = 1f;
	// Prop colliders activate within this distance and are released past the larger one (hysteresis).
	const float CollisionEnableDistance = 1536f;
	const float CollisionDisableDistance = 2560f;
	// Props smaller than this radius never cast shadows (when prop shadows are enabled).
	const float ShadowMinimumRadius = 48f;
	// Shadow distance gate. Decisions are recomputed only after the camera moved this far (or its
	// PVS cell changed), never as part of the 128-unit visibility refresh.
	const float ShadowGateRecomputeDistance = 512f;
	// Shadow flag writes applied per frame at most. One write is a native flag change plus a
	// scene-list notification that pulls whole batches into or out of the shadow pass, so a pass
	// that decides hundreds of changes must not apply them inside one frame.
	const int ShadowGateChangesPerFrame = 32;
	// Gate distances are quantized to this bucket, and the restore distance is half the disable
	// distance: wide hysteresis on top of the bucket means a prop walking along the boundary
	// cannot flip on small camera moves.
	const float ShadowGateBucketSize = 256f;
	// A gate pass that queues more changes than this is reported as a spike (Titanfall2Log.Spike,
	// verbose logging only); passes slower than 2 ms are reported by ReportSpike as well.
	const int ShadowGateReportChanges = 64;
	// Cell refresh trigger. The anchor is quantized to this grid and a refresh only runs when the
	// grid bucket changed and the anchor moved at least RefreshHysteresisDistance since the last
	// refresh: a camera sitting on a bucket boundary cannot refresh twice, while crossing a bucket
	// always refreshes, so a fast camera still follows and a slow walk refreshes every ~256 units.
	// This is the last pass that was not budgeted; the portal walk inside it is now cached per PVS
	// cell and advanced as a budgeted job (see UpdateCellRefresh).
	const float RefreshGridSize = 256f;
	const float RefreshHysteresisDistance = RefreshGridSize * 0.75f;
	// Incremental PVS round budget: portal edges walked and wall time per frame. A retail map's
	// portal graph has ~700 edges, so with the per-camera-cell cache a round normally completes in
	// its first step; these caps only bound the first-visit worst case.
	const int PvsEdgesPerFrame = 8192;
	const float PvsBudgetMilliseconds = 2f;

	[Property, Hide]
	public string MountIdent { get; set; }

	[Property, Hide]
	public string MapPath { get; set; }

	/// <summary>Pure-world maps keep collision resident independently of the render camera.</summary>
	[Property, Hide] public bool KeepAllCollisions { get; set; }
	/// <summary>True only after the resident collision preload finishes without missing props/bodies.</summary>
	public bool ResidentCollisionsReady { get; private set; }
	/// <summary>Number of resident prop colliders expected in the native physics world.</summary>
	public int ResidentCollisionCount => _collidersCreated;

	[Property, Hide]
	public Vector3 FallbackAnchor { get; set; }

	[Property, Hide]
	public string EncodedProps { get; set; }

	[Property, Hide]
	public Color BakedSkyColor { get; set; }
	[Property, Hide] public string EncodedLocalLights { get; set; }
	Titanfall2LocalLightBuffer _localLights;

	/// <summary>
	/// The CellBSP tree and its portals, encoded so they survive the component being
	/// recreated from scene state. <see cref="_visibility"/> is a plain field and is
	/// therefore lost when that happens, while this property is serialized alongside
	/// <see cref="EncodedProps"/>; without it portal culling silently stops working and
	/// every static prop stays in the render list.
	/// </summary>
	[Property, Hide]
	public string EncodedVisibility { get; set; }

	readonly Dictionary<CellCoordinate, PropCell> _cells = new();
	readonly Dictionary<ClusterCoordinate, PropCluster> _clusters = new();
	readonly List<PropCell> _workQueue = new();
	// The runtime pass walks clusters, not cells: a batch belongs to a cluster and can be
	// shared by every cell in it, so visiting cells would process one batch repeatedly.
	readonly List<PropCluster> _runtimeClusterQueue = new();
	// Collision activation has its own list, cursor and budgets. It is the one pass that is
	// allowed to stop early, so it must not share a loop with the state pass.
	readonly List<PropCell> _collisionCellQueue = new();
	readonly Stopwatch _refreshTimer = new();
	readonly Stopwatch _statusTimer = new();
	Vector3 _lastAnchor;
	bool _hasAnchor;
	bool _initialized;
	bool _initializationFailed;
	bool _completionLogged;
	bool _batchesBuilt;
	bool _runtimePvsUpdatePending;
	int _runtimeClusterCursor;
	bool _runtimeQueueRefreshPending;
	int _collisionCellCursor;
	int _totalProps;
	int _activeCells;
	int _liveProps;
	int _failedProps;
	int _failureLogs;
	int _skinnedProps;
	int _plainProps;
	int _instancedProps;
	int _batchCount;
	int _renderingProps;
	int _shadowCastingProps;
	int _collidableProps;
	int _collidersCreated;
	int _colliderFailures;
	int _collisionFilteredProps;
	int _collidersReleased;
	int _pvsCameraCell = -1;
	int _pvsVisibleProps;
	int _pvsCulledProps;
	int _propsWithPvsCells;
	Titanfall2BspReader.VisibilityCellMask _visiblePvsCells;
	Titanfall2BspReader.BspVisibility _visibility;

	// Diagnostics. The engine overlay reports one hitch number for the whole frame,
	// so each expensive unit of work reports itself with its own path and duration.
	readonly Stopwatch _collisionProbe = new();
	// Reusable budget timers. These paths run every frame, and Stopwatch.StartNew()
	// allocates, so the timers are created once and restarted in place.
	readonly Stopwatch _runtimeClusterTimer = new();
	readonly Stopwatch _collisionQueueTimer = new();
	readonly Stopwatch _populateTimer = new();
	readonly Dictionary<string, double> _modelFailureRetryAfter = new( StringComparer.OrdinalIgnoreCase );
	int _collisionInstancesThisFrame;
	int _collisionBodiesThisYield;
	int _collidersDeferred;
	int _collisionResumeCount;
	// The loading pass that must finish before readiness uses the load-time budgets instead of
	// the runtime churn caps: 2 colliders per yield would need collidableProps/2 frames.
	int _collisionInstanceLimitOverride;
	float _collisionBudgetOverrideMilliseconds;
	/// <summary>Budget of the populate pass currently running; SpawnProp needs it to decide whether starting another collider fits.</summary>
	float _populateBudgetMilliseconds;
	bool _pvsDiagnosticsLogged;
	bool _visibilityRestoreFailed;

	// Composition diagnostics for the render submission count, reported once when the batches are
	// built. The render main thread costs about a microsecond per submitted object here, so the
	// number to A/B is the batch count and the cluster grid that produced it, not the C# frame time.
	// _cellModelGroups is per-cell bookkeeping (distinct batched models per cell) that only exists
	// to report how many batches the cluster grouping merged away; it is dropped after that line.
	readonly Dictionary<PropCell, HashSet<Model>> _cellModelGroups = new();
	// Distinct models among the props that stay individual objects, split by the reason.
	readonly HashSet<Model> _individualModels = new();
	int _cellModelGroupCount;
	int _plainStaticProps;
	int _plainFallbackProps;
	bool _batchCompositionLogged;

	public void Configure( string mountIdent, string mapPath, Vector3 fallbackAnchor,
		IReadOnlyList<Titanfall2BspReader.StaticPropInstance> props,
		Titanfall2BspReader.BspVisibility visibility, Color bakedSkyColor = default, IReadOnlyList<Titanfall2WorldLight> localLights = null,
		IReadOnlyList<float> fadeDistances = null )
	{
		// AddComponent can awaken with no data before this first configuration.
		// Keep empty legacy scenes valid, but decode the newly supplied payload.
		if ( string.IsNullOrWhiteSpace( EncodedProps ) )
		{
			_initialized = false;
			_visibilityRestoreFailed = false;
		}
		MountIdent = mountIdent;
		MapPath = mapPath;
		FallbackAnchor = fallbackAnchor;
		_visibility = visibility;
		EncodedProps = EncodeWithFade( props, visibility, fadeDistances );
		BakedSkyColor = bakedSkyColor;
		EncodedLocalLights = Titanfall2LocalLightBuffer.Encode( localLights );
		EncodedVisibility = EncodeVisibility( visibility );
	}

	protected override void OnAwake()
	{
		InitializeCells();
	}

	protected override void OnEnabled() => SetResourcesEnabled( true );
	protected override void OnDisabled() => SetResourcesEnabled( false );

	void SetResourcesEnabled( bool enabled )
	{
		// These native objects are created directly, so disabling the component
		// does not automatically remove them from the scene's render lists.
		foreach ( var cell in _cells.Values )
		{
			foreach ( var instance in cell.Instances )
			{
				if ( instance.RenderObject.IsValid() ) instance.RenderObject.RenderingEnabled = enabled && instance.RenderingEnabled;
				if ( instance.CollisionObject.IsValid() ) instance.CollisionObject.Enabled = enabled;
			}
		}
		foreach ( var cluster in _clusters.Values )
		{
			foreach ( var batch in cluster.Batches.Values )
			{
				if ( batch.RenderBatch.IsValid() ) batch.RenderBatch.RenderingEnabled = enabled && batch.RenderingEnabled;
				foreach ( var fallback in batch.FallbackObjects )
					if ( fallback.IsValid() ) fallback.RenderingEnabled = enabled && batch.RenderingEnabled;
			}
		}
	}

	protected override async Task OnLoad( LoadingContext context )
	{
		ResidentCollisionsReady = false;
		if ( Application.IsDedicatedServer ) KeepAllCollisions = true;
		if ( !_initialized ) InitializeCells();
		if ( !_initialized || _initializationFailed ) return;
		if ( _cells.Count == 0 || _totalProps == 0 )
		{
			ResidentCollisionsReady = KeepAllCollisions;
			return;
		}

		var loadTimer = Stopwatch.StartNew();
		var cacheBefore = Titanfall2ProcessedCache.GetStatistics();
		context.Title = $"Loading Titanfall 2 static props (0/{_totalProps})";
		// The active camera is not guaranteed to exist while the SceneFile is being
		// deserialized. The first map spawn remains a deterministic priority anchor.
		RefreshCells( FallbackAnchor );

		while ( _liveProps + _failedProps < _totalProps )
		{
			if ( !this.IsValid() || !GameObject.IsValid() ) return;
			PopulateCells( PreloadPerYield, PreloadBudgetMilliseconds );
			var processed = _liveProps + _failedProps;
			context.Title = $"Loading Titanfall 2 static props ({processed}/{_totalProps})";
			if ( processed < _totalProps ) await Task.Yield();
		}

		if ( KeepAllCollisions )
		{
			// Readiness must arrive in a bounded number of yields. At the runtime churn cap
			// (2 colliders per frame) a map with thousands of solid props would take
			// thousands of frames after the prop pass, so the load pass uses the preload
			// count/budget instead.
			_collisionInstanceLimitOverride = PreloadCollisionBodiesPerYield;
			_collisionBudgetOverrideMilliseconds = PreloadBudgetMilliseconds;
			try
			{
				foreach ( var cell in _cells.Values )
				{
					cell.CollisionCursor = 0;
					bool complete;
					do
					{
						if ( !this.IsValid() || !GameObject.IsValid() ) return;
						_collisionQueueTimer.Restart();
						_collisionInstancesThisFrame = 0;
						complete = UpdateCellCollisionState( cell, FallbackAnchor, _collisionQueueTimer );
						context.Title = $"Loading Titanfall 2 prop collision ({_collidersCreated}/{_collidableProps})";
						if ( !complete ) await Task.Yield();
					} while ( !complete );
				}
			}
			finally
			{
				_collisionInstanceLimitOverride = 0;
				_collisionBudgetOverrideMilliseconds = 0f;
			}
			ResidentCollisionsReady = _failedProps == 0 && _colliderFailures == 0
				&& _collidersCreated + _collisionFilteredProps == _collidableProps;
		}

		LogCompletion();
		LogBatchComposition();
		SetResourcesEnabled( Active );
		Log.Info( $"Titanfall 2 static props finished during scene loading in "
			+ $"{loadTimer.Elapsed.TotalSeconds:0.00}s ({MapPath})." );
		if ( KeepAllCollisions && !ResidentCollisionsReady )
			Log.Warning( $"Titanfall 2 resident prop collision is incomplete: {_failedProps} missing props, "
				+ $"{_colliderFailures} missing collision models, {_collidersCreated}/{_collidableProps} colliders; "
				+ $"ttf2_map_ready will not be published ({MapPath})." );
		var cacheAfter = Titanfall2ProcessedCache.GetStatistics();
		Log.Info( $"Titanfall 2 processed model cache: {cacheAfter.ModelHits - cacheBefore.ModelHits} hits, "
			+ $"{cacheAfter.ModelMisses - cacheBefore.ModelMisses} misses; textures {cacheAfter.TextureHits - cacheBefore.TextureHits} hits/"
			+ $"{cacheAfter.TextureMisses - cacheBefore.TextureMisses} misses; materials {cacheAfter.MaterialHits - cacheBefore.MaterialHits} hits/"
			+ $"{cacheAfter.MaterialMisses - cacheBefore.MaterialMisses} misses; {cacheAfter.Writes - cacheBefore.Writes} derived writes; "
			+ $"{cacheAfter.Rejected} rejected / {cacheAfter.WriteFailures} write failures total; "
			+ $"mounted prop shadows {(Titanfall2StreamingSettings.PropShadows ? "enabled" : "disabled")}." );
	}

	protected override void OnUpdate()
	{
		if ( !_initialized ) InitializeCells();
		if ( !_initialized || _cells.Count == 0 ) return;

		var anchor = Scene?.Camera.IsValid() == true
			? Scene.Camera.WorldPosition
			: FallbackAnchor;
		var pvsCellChanged = TryFindPvsCell( anchor, out var pvsCell ) && pvsCell != _pvsCameraCell;
		// Quantized trigger plus a cached, budgeted PVS round; replaces the unbudgeted 128-unit
		// full refresh that rebuilt every distance and re-walked all portals on every step.
		UpdateCellRefresh( anchor, pvsCell, pvsCellChanged );

		PopulateCells( PropsPerFrame, FrameBudgetMilliseconds );
		ProcessRuntimeClusterQueue();
		ProcessCollisionQueue();
		// Independent of the sweep above: recompute when the camera moved far enough, then apply a
		// bounded number of queued shadow flag changes. No-op when the gate is off.
		UpdateShadowGate( anchor, pvsCellChanged );
		// A collision model that failed to load during the loading pass is retried by the
		// runtime pass; publish readiness once every expected collider exists.
		if ( KeepAllCollisions && !ResidentCollisionsReady && _failedProps == 0 && _colliderFailures == 0
			&& _collidersCreated + _collisionFilteredProps == _collidableProps )
		{
			ResidentCollisionsReady = true;
			Titanfall2Log.Info( $"Titanfall 2 resident prop collision became ready after the loading pass: "
				+ $"{_collidersCreated}/{_collidableProps} colliders ({MapPath})." );
		}
		LogCompletion();
		LogBatchComposition();
		if ( !_completionLogged && _statusTimer.Elapsed.TotalSeconds >= 5.0 )
		{
			var processed = _liveProps + _failedProps;
			var queued = Math.Max( 0, _totalProps - processed );
			Log.Info( $"Titanfall 2 prop streaming: {_liveProps} retained, {queued} queued, "
				+ $"{_activeCells}/{_cells.Count} cells activated, {_failedProps} skipped ({MapPath})." );
			_statusTimer.Restart();
		}
	}

	protected override void OnDestroy()
	{
		ResidentCollisionsReady = false;
		var releasedProps = _liveProps;
		var releasedBatches = _batchCount;
		// A cluster owns its batches because several cells may share one of them. Walking
		// cells here would delete a shared batch on its first member cell and leave every
		// other member pointing at a deleted object, so the cluster is visited once and is
		// the only thing that ever deletes a batch.
		foreach ( var cluster in _clusters.Values )
			DestroyClusterInstances( cluster );
		_localLights?.Dispose();
		_localLights = null;
		_clusters.Clear();
		_cells.Clear();
		_workQueue.Clear();
		_runtimeClusterQueue.Clear();
		_collisionCellQueue.Clear();
		Log.Info( $"Titanfall 2 static prop scene resources released: {releasedProps} props, "
			+ $"{releasedBatches} instance batches ({MapPath})." );
	}

	void QueueRuntimeCellRefresh()
	{
		if ( _runtimeClusterCursor < _runtimeClusterQueue.Count ) _runtimeQueueRefreshPending = true;
		else RebuildRuntimeClusterQueue();

		// Collision restarts from the nearest cells on every refresh so colliders follow the
		// player. It is capped per frame and can no longer affect the pass above.
		RebuildCollisionQueue();
	}

	/// <summary>
	/// Applies visibility state to every cluster in finite time. This pass
	/// is unconditional: it advances by at least one cluster per frame and is never rewound
	/// while running, so one sweep completes in at most one frame per cluster regardless of
	/// whether the player ever stops moving. Collision activation used to share this loop and
	/// abort it whenever its own per-frame budget ran out, which starved the state updates
	/// (whole frames were spent on one cell) and kept the PVS request pending forever.
	/// </summary>
	void ProcessRuntimeClusterQueue()
	{
		if ( _runtimeClusterCursor >= _runtimeClusterQueue.Count ) return;
		_runtimeClusterTimer.Restart();
		var processedCells = 0;
		while ( processedCells < RuntimeCellsPerFrame
			&& _runtimeClusterCursor < _runtimeClusterQueue.Count )
		{
			var cluster = _runtimeClusterQueue[_runtimeClusterCursor];
			if ( _runtimePvsUpdatePending ) UpdateClusterVisibilityState( cluster );

			_runtimeClusterCursor++;
			// The cap counts cells rather than clusters, so a larger cluster cannot multiply
			// how much of the map a single frame is allowed to touch.
			processedCells += cluster.Cells.Count;
			if ( _runtimeClusterTimer.Elapsed.TotalMilliseconds >= RuntimeCellBudgetMilliseconds ) break;
		}

		if ( _runtimeClusterCursor < _runtimeClusterQueue.Count ) return;
		_runtimeClusterQueue.Clear();
		_runtimeClusterCursor = 0;
		if ( _runtimeQueueRefreshPending )
		{
			// A refresh landed while this sweep was walking, so the clusters visited before
			// it may hold state from before the refresh. Start one more sweep in the new
			// distance order; only a sweep that runs clean clears the request below.
			RebuildRuntimeClusterQueue();
			return;
		}

		_runtimePvsUpdatePending = false;
	}

	/// <summary>
	/// Logs one measured unit of work when it exceeded its budget. Cheap enough to run
	/// unconditionally: a shared stopwatch restart plus a format string means the fast
	/// path allocates nothing (see <see cref="Titanfall2Log"/>).
	/// </summary>
	static void ReportSpike( Stopwatch probe, string description ) => Titanfall2Log.ReportSpike( probe, description );
	static void ReportSpike<T1>( Stopwatch probe, string format, T1 value ) => Titanfall2Log.ReportSpike( probe, format, value );
	static void ReportSpike<T1, T2>( Stopwatch probe, string format, T1 first, T2 second ) => Titanfall2Log.ReportSpike( probe, format, first, second );
	static void ReportSpike<T1, T2, T3>( Stopwatch probe, string format, T1 first, T2 second, T3 third ) => Titanfall2Log.ReportSpike( probe, format, first, second, third );

	void PopulateCells( int maximumProps, float budgetMilliseconds )
	{
		if ( _workQueue.Count == 0 ) return;
		_populateTimer.Restart();
		_collisionBodiesThisYield = 0;
		_populateBudgetMilliseconds = budgetMilliseconds;
		var attempted = 0;
		while ( attempted < maximumProps && _workQueue.Count > 0 )
		{
			var cell = _workQueue[0];
			if ( cell.NextProp >= cell.Props.Count )
			{
				_workQueue.RemoveAt( 0 );
				continue;
			}
			if ( !cell.Active )
			{
				cell.Active = true;
				_activeCells++;
			}

			var prop = cell.Props[cell.NextProp++];
			attempted++;
			if ( SpawnProp( cell, prop ) )
			{
				_liveProps++;
			}
			else
			{
				_failedProps++;
			}

			if ( cell.NextProp >= cell.Props.Count )
				_workQueue.RemoveAt( 0 );
			if ( _populateTimer.Elapsed.TotalMilliseconds >= budgetMilliseconds )
				break;
		}
	}

	bool SpawnProp( PropCell cell, StreamedProp prop )
	{
		if ( string.IsNullOrWhiteSpace( prop.ModelPath ) || string.IsNullOrWhiteSpace( MountIdent ) ) return false;
		if ( Application.IsDedicatedServer )
		{
			var serverProp = new PropInstance( prop, null, 0f, false, false, true );
			cell.Instances.Add( serverProp );
			if ( prop.Collidable ) _collidableProps++;
			return true;
		}
		var resourcePath = $"mount://{MountIdent}/{prop.ModelPath}.vmdl";
		// The static-instance model already contains every LOD, so there is no starting band
		// to pick: every instance of the prop can share it.
		var staticResourcePath = $"mount://{MountIdent}/{ModelLoader.GetStaticInstanceAllLodsPath( prop.ModelPath )}.vmdl";
		var staticModel = LoadModelWithRetryCache( staticResourcePath, prop.Skin );
		var model = ModelLoader.IsUsableModel( staticModel )
			? staticModel
			: LoadModelWithRetryCache( resourcePath, prop.Skin );
		if ( !ModelLoader.IsUsableModel( model ) )
		{
			if ( _failureLogs++ < 16 ) Titanfall2Log.Warning( $"Unable to stream Titanfall 2 static prop model '{resourcePath}'." );
			return false;
		}

		var scale = MathF.Max( prop.Scale, 0.001f );
		var radius = model.Bounds.Size.Length * 0.5f * scale;
		var castShadows = ShouldCastShadows( radius );
		// Individual objects keep the exact per-cell distance/PVS decision; only a shared
		// batch, which has one flag for every member cell, has to fall back to the cluster
		// aggregate. Reading the state here rather than the raw mask means a prop spawned out
		// of the render list is created hidden instead of rendering until the next visibility pass.
		var instanceVisible = IsRenderVisible( prop.PvsCells ) && IsWithinRenderDistance( prop.Position, radius, false, prop.FadeDistance );
		var batchVisible = IsRenderVisible( prop.PvsCells );
		var worldTransform = new Transform( prop.Position, prop.Rotation.ToRotation(), scale );
		SceneObject renderObject = null;
		var isBatched = ModelLoader.IsUsableModel( staticModel ) && ModelLoader.CanInstance( staticModel );

		// Opaque/cutout static variants contain bind-pose vertices without a
		// skeleton, so identical models can share one instanced draw inside their
		// permanent BSP cell. Special transparent/decal/water models stay as
		// individual native objects to preserve material pass ordering.
		// The batch key is the cluster and the model, so one model repeated in neighbouring
		// cells shares a single instanced draw. The model is the prop's one all-LOD model, so
		// every band of every instance in the cluster shares this one batch.
		if ( isBatched )
		{
			var cluster = cell.Cluster;
			if ( !cluster.Batches.TryGetValue( staticModel, out var pendingBatch ) )
			{
				pendingBatch = new PendingBatch( staticModel, batchVisible );
				cluster.Batches.Add( staticModel, pendingBatch );
			}
			pendingBatch.Add( worldTransform, radius, castShadows, prop.ProbeColor, prop.Probe, prop.PvsCells, prop.FadeDistance );
			pendingBatch.RenderingEnabled |= batchVisible;
			_instancedProps++;
			// Count the (cell, model) pair this batch covers. The cluster dictionary already
			// merged every pair that shares a cluster, so the difference between this count and
			// _batchCount is exactly how many submitted objects the cluster grid removed.
			if ( !_cellModelGroups.TryGetValue( cell, out var modelsInCell ) )
			{
				modelsInCell = new HashSet<Model>();
				_cellModelGroups.Add( cell, modelsInCell );
			}
			if ( modelsInCell.Add( staticModel ) ) _cellModelGroupCount++;
		}
		else if ( ModelLoader.IsUsableModel( staticModel ) )
		{
			// Usable all-LOD static model that refused instancing: it carries a translucent,
			// decal, water or godray mesh, and those must keep their own material pass order.
			renderObject = new SceneObject( Scene.SceneWorld, staticModel, worldTransform );
			_plainProps++;
			_plainStaticProps++;
			_individualModels.Add( staticModel );
		}
		else if ( model.BoneCount > 0 || model.AnimationCount > 0 )
		{
			var skinned = new SceneModel( Scene.SceneWorld, model, worldTransform )
			{
				UseAnimGraph = false,
				PlaybackRate = 0f
			};
			skinned.UpdateToBindPose();
			renderObject = skinned;
			_skinnedProps++;
		}
		else
		{
			// No usable static-instance model, so the prop falls back to a rigid full model.
			renderObject = new SceneObject( Scene.SceneWorld, model, worldTransform );
			_plainProps++;
			_plainFallbackProps++;
			_individualModels.Add( model );
		}
		if ( renderObject.IsValid() )
		{
			renderObject.Flags.IsStatic = true;
			renderObject.Flags.CastShadows = castShadows;
			renderObject.ColorTint = new Color( prop.ProbeColor.x, prop.ProbeColor.y, prop.ProbeColor.z, 1f );
			prop.Probe.Apply( renderObject, BakedSkyColor );
			_localLights?.Apply( renderObject.Attributes );
			renderObject.RenderingEnabled = Active && instanceVisible;
		}

		if ( instanceVisible ) _renderingProps++;
		if ( castShadows ) _shadowCastingProps++;
		var instance = new PropInstance( prop, renderObject, radius, instanceVisible, castShadows, isBatched );
		if ( prop.Collidable )
		{
			_collidableProps++;
			// A collider costs 2-22 ms, so the loading pass is not allowed to start an
			// unbounded number of them inside one yield. Anything skipped keeps a null
			// CollisionObject and is created by the runtime cell pass as the player
			// approaches, which is where the distance-based activation already lives.
			var withinYieldCap = _collisionBodiesThisYield < PreloadCollisionBodiesPerYield;
			var withinYieldBudget = _populateTimer.Elapsed.TotalMilliseconds < _populateBudgetMilliseconds;
			if ( withinYieldCap && withinYieldBudget )
			{
				_collisionBodiesThisYield++;
				EnsureCollisionState( instance, anchor: _lastAnchor, fallbackModel: model );
			}
			else
			{
				_collidersDeferred++;
			}
		}
		cell.Instances.Add( instance );
		return true;
	}

	/// <summary>
	/// Reports how the submitted-object count is composed, once, after the batches are built.
	/// The engine pays about a microsecond per submitted object on this mount, so the numbers an
	/// A/B run has to compare are the batch count and the cluster grid that produced it, plus the
	/// individual objects that could not be batched at all. The per-cell bookkeeping is dropped
	/// here because it exists only for this line: a whole map's prop cells are hundreds of small
	/// sets and keeping them resident would be pure waste.
	/// </summary>
	void LogBatchComposition()
	{
		if ( _batchCompositionLogged || !_batchesBuilt ) return;
		_batchCompositionLogged = true;

		var batchesIfPerCell = _cellModelGroupCount;
		var mergedBatches = Math.Max( 0, batchesIfPerCell - _batchCount );
		var instancesPerBatch = _batchCount > 0 ? (float)_instancedProps / _batchCount : 0f;
		var individualModels = _individualModels.Count;
		Log.Info( $"Titanfall 2 prop batch composition: {BatchClusterCells}x{BatchClusterCells} cells per cluster, "
			+ $"{_cells.Count} cells in {_clusters.Count} clusters; {_batchCount} instanced batches for "
			+ $"{_instancedProps} props ({instancesPerBatch:0.0} instances/batch); "
			+ $"{mergedBatches} batches not created because clustering merged them "
			+ $"({batchesIfPerCell} if every cell kept its own batch per model); "
			+ $"{_plainProps} individual objects ({_plainStaticProps} special-material static models, "
			+ $"{_plainFallbackProps} full-model fallbacks) across {individualModels} distinct models; "
			+ $"{_skinnedProps} frozen-skinned ({MapPath})." );

		_cellModelGroups.Clear();
		_individualModels.Clear();
	}
}
