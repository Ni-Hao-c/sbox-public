using static Titanfall2StaticPropSceneData;
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
	const int DataVersion = Titanfall2StaticPropSceneData.DataVersion;
	const int PvsPortalDepth = Titanfall2StaticPropSceneData.PvsPortalDepth;
	// Edge length of one BSP prop streaming cell, in world units.
	const float CellSize = Titanfall2CompiledPropLayout.CellSize;
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
	// Portal reachability is compiled at import; this refresh only updates spatial state.
	const float RefreshGridSize = 256f;
	const float RefreshHysteresisDistance = RefreshGridSize * 0.75f;

	[Property, Hide]
	public string MountIdent { get; set; }

	[Property, Hide]
	public string MapPath { get; set; }

	/// <summary>Pure-world maps keep collision resident independently of the render camera.</summary>
	[Property, Hide] public bool KeepAllCollisions { get; set; }
	/// <summary>The load-time transaction finished, including collision attempts in resident mode.</summary>
	public bool PreparationComplete { get; private set; }
	public bool ResourcesPrepared => PreparationComplete && !_initializationFailed && _failedProps == 0;
	/// <summary>True only after the resident collision preload finishes without missing props/bodies.</summary>
	public bool ResidentCollisionsReady => Collisions.ResidentReady;
	/// <summary>Number of resident prop colliders expected in the native physics world.</summary>
	public int ResidentCollisionCount => Collisions.Created;

	[Property, Hide]
	public Vector3 FallbackAnchor { get; set; }

	[Property, Hide]
	public string EncodedProps { get; set; }

	[Property, Hide]
	public Color BakedSkyColor { get; set; }
	[Property, Hide] public string EncodedLocalLights { get; set; }
	readonly Titanfall2StaticPropRenderResources _renderResources = new();
	Titanfall2LocalLightBuffer _localLights => _renderResources.Lights;
	Titanfall2StaticPropGpuScene _gpuScene => _renderResources.GpuScene;

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
	readonly Stopwatch _refreshTimer = new();
	readonly Stopwatch _statusTimer = new();
	Vector3 _lastAnchor;
	bool _hasAnchor;
	bool _initialized;
	bool _initializationFailed;
	bool _releasing;
	CollisionRuntime _collisions;
	CollisionRuntime Collisions => _collisions ??= new( this );
	bool _completionLogged;
	bool _batchesBuilt;
	bool _runtimePvsUpdatePending;
	int _runtimeClusterCursor;
	bool _runtimeQueueRefreshPending;
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
	int _pvsCameraCell = -1;
	int _pvsVisibleProps;
	int _pvsCulledProps;
	int _propsWithPvsCells;
	Titanfall2BspReader.VisibilityCellMask _visiblePvsCells;
	Titanfall2BspReader.BspVisibility _visibility;

	// Reusable budget timers. These paths run every frame, and Stopwatch.StartNew()
	// allocates, so the timers are created once and restarted in place.
	readonly Stopwatch _runtimeClusterTimer = new();
	readonly Stopwatch _populateTimer = new();
	readonly Dictionary<string, double> _modelFailureRetryAfter = new( StringComparer.OrdinalIgnoreCase );
	/// <summary>Budget of the populate pass currently running; SpawnProp needs it to decide whether starting another collider fits.</summary>
	float _populateBudgetMilliseconds;
	bool _pvsDiagnosticsLogged;
	bool _visibilityRestoreFailed;
	CompiledPropScene _compiledScene;

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
		ConfigureCompiled( mountIdent, mapPath, fallbackAnchor,
			Titanfall2StaticPropSceneData.Compile( props, visibility, localLights, fadeDistances ), bakedSkyColor );
	}

	internal void ConfigureCompiled( string mountIdent, string mapPath, Vector3 fallbackAnchor,
		Titanfall2StaticPropSceneData.Payload payload, Color bakedSkyColor )
	{
		// Configuration is data-only; OnLoad owns all resource creation for each scene clone.
		if ( _totalProps > 0 || _renderResources.IsPrepared )
			throw new InvalidOperationException( "Cannot reconfigure a prepared static prop scene." );
		_initialized = false;
		_initializationFailed = false;
		_visibilityRestoreFailed = false;
		MountIdent = mountIdent;
		MapPath = mapPath;
		FallbackAnchor = fallbackAnchor;
		EncodedProps = payload.Props;
		EncodedVisibility = payload.Visibility;
		EncodedLocalLights = payload.LocalLights;
		BakedSkyColor = bakedSkyColor;
		_visibility = null;
		_compiledVisibility = null;
		_compiledScene = null;
	}

	protected override void OnAwake()
	{
		InitializeCells();
	}

	protected override void OnEnabled() => SetResourcesEnabled( true );
	protected override void OnDisabled() => SetResourcesEnabled( false );

	void SetResourcesEnabled( bool enabled )
	{
		if ( _gpuScene.IsValid() ) _gpuScene.RenderingEnabled = enabled;
		// These native objects are created directly, so disabling the component
		// does not automatically remove them from the scene's render lists.
		foreach ( var cell in _cells.Values )
		{
			foreach ( var instance in cell.Instances )
			{
				if ( instance.RenderObject.IsValid() ) instance.RenderObject.RenderingEnabled = enabled && instance.RenderingEnabled;
				Collisions.SetEnabled( instance, enabled );
			}
		}
		foreach ( var cluster in _clusters.Values )
		{
			foreach ( var batch in cluster.Batches.Values )
			{
				if ( batch.RenderBatch.IsValid() ) batch.RenderBatch.RenderingEnabled = enabled && !batch.GpuRendered && batch.RenderingEnabled;
				foreach ( var fallback in batch.FallbackObjects )
					if ( fallback.IsValid() ) fallback.RenderingEnabled = enabled && batch.RenderingEnabled;
			}
		}
	}

	protected override void OnDestroy() => ReleaseResources();

	void ReleaseResources()
	{
		if ( _releasing ) return;
		_runtimePhase = RuntimePhase.Retiring;
		_releasing = true;
		_renderResources.ReleaseGpuScene();
		_mixedGpuInstanceCount = 0;
		PreparationComplete = false;
		Collisions.ResidentReady = false;
		var releasedProps = _liveProps;
		var releasedBatches = _batchCount;
		// Detach each cluster once: cells share its batch references. The render owner
		// then deletes native batches/objects before releasing their light buffers.
		foreach ( var cluster in _clusters.Values )
			DestroyClusterInstances( cluster );
		_renderResources.Dispose();
		_clusters.Clear();
		_cells.Clear();
		_workQueue.Clear();
		_runtimeClusterQueue.Clear();
		Collisions.Clear();
		_compiledScene = null;
		_compiledVisibility = null;
		_renderCensus = null;
		Log.Info( $"Titanfall 2 static prop scene resources released: {releasedProps} props, "
			+ $"{releasedBatches} instance batches ({MapPath})." );
	}

	void QueueRuntimeCellRefresh()
	{
		if ( _runtimeClusterCursor < _runtimeClusterQueue.Count ) _runtimeQueueRefreshPending = true;
		else RebuildRuntimeClusterQueue();

		// Collision restarts from the nearest cells on every refresh so colliders follow the
		// player. It is capped per frame and can no longer affect the pass above.
		Collisions.Refresh();
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
		Collisions.BeginPopulate();
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
			Collisions.Register( serverProp, null, _populateTimer, _populateBudgetMilliseconds );
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
			pendingBatch.Sources.Add( prop );
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
			_renderResources.Track( renderObject );
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
		Collisions.Register( instance, model, _populateTimer, _populateBudgetMilliseconds );
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
		var logicalBatches = _clusters.Values.Sum( cluster => cluster.Batches.Count );
		var mergedBatches = Math.Max( 0, batchesIfPerCell - logicalBatches );
		var gpuInstances = _gpuScene?.InstanceCount ?? 0;
		var cpuInstances = _instancedProps - FullGpuInstanceCount;
		var instancesPerBatch = _batchCount > 0 ? (float)cpuInstances / _batchCount : 0f;
		var individualModels = _individualModels.Count;
		Log.Info( $"Titanfall 2 prop batch composition: {BatchClusterCells}x{BatchClusterCells} cells per cluster, "
			+ $"{_cells.Count} cells in {_clusters.Count} clusters; {_batchCount} instanced batches for "
			+ $"{cpuInstances} CPU props ({instancesPerBatch:0.0} instances/batch); "
			+ $"{gpuInstances} GPU-resident props in {_gpuScene?.MaterialGroups ?? 0} submission groups "
			+ $"({_mixedGpuInstanceCount} mixed props also retain native special meshes); "
			+ $"{mergedBatches} batches not created because clustering merged them "
			+ $"({batchesIfPerCell} if every cell kept its own batch per model); "
			+ $"{_plainProps} individual objects ({_plainStaticProps} special-material static models, "
			+ $"{_plainFallbackProps} full-model fallbacks) across {individualModels} distinct models; "
			+ $"{_skinnedProps} frozen-skinned ({MapPath})." );

		_cellModelGroups.Clear();
		_individualModels.Clear();
	}
}
