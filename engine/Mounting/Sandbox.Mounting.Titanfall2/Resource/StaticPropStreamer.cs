using System.Diagnostics;
using Titanfall2;

/// <summary>
/// Progressively loads every Titanfall 2 BSP static prop. Cells near the active
/// camera are prioritized, then the remaining map cells are populated using the
/// same frame budget. Spawned props are retained until the scene is destroyed.
/// </summary>
[Library]
public sealed class Titanfall2StaticPropStreamer : Component, Component.DontExecuteOnServer, Sandbox.Internal.IUpdateSubscriber
{
	static readonly Sandbox.Diagnostics.Logger Log = new( "Titanfall2Props" );
	const int DataMagic = 0x32505354; // TSP2
	const int DataVersion = 3;
	const int MaximumModelCount = 65536;
	const int MaximumPropCount = 1_000_000;

	[Property, Hide]
	public string MountIdent { get; set; }

	[Property, Hide]
	public string MapPath { get; set; }

	[Property, Hide]
	public Vector3 FallbackAnchor { get; set; }

	[Property, Hide]
	public string EncodedProps { get; set; }

	readonly Dictionary<CellCoordinate, PropCell> _cells = new();
	readonly List<PropCell> _workQueue = new();
	readonly List<PropCell> _runtimeCellQueue = new();
	readonly Queue<PendingBatch> _lodSwapQueue = new();
	readonly Stopwatch _refreshTimer = new();
	readonly Stopwatch _statusTimer = new();
	Vector3 _lastAnchor;
	bool _hasAnchor;
	bool _initialized;
	bool _completionLogged;
	bool _batchesBuilt;
	bool _runtimePvsUpdatePending;
	int _runtimeCellCursor;
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
	int _collidersReleased;
	int _pvsCameraCell = -1;
	int _pvsVisibleProps;
	int _pvsCulledProps;
	Titanfall2BspReader.VisibilityCellMask _visiblePvsCells;
	Titanfall2BspReader.BspVisibility _visibility;

	public void Configure( string mountIdent, string mapPath, Vector3 fallbackAnchor,
		IReadOnlyList<Titanfall2BspReader.StaticPropInstance> props,
		Titanfall2BspReader.BspVisibility visibility )
	{
		MountIdent = mountIdent;
		MapPath = mapPath;
		FallbackAnchor = fallbackAnchor;
		_visibility = visibility;
		EncodedProps = Encode( props, visibility );
	}

	protected override void OnAwake()
	{
		InitializeCells();
	}

	protected override async Task OnLoad( LoadingContext context )
	{
		if ( !_initialized ) InitializeCells();
		if ( !_initialized || _cells.Count == 0 || _totalProps == 0 ) return;

		var loadTimer = Stopwatch.StartNew();
		context.Title = $"Loading Titanfall 2 static props (0/{_totalProps})";
		// The active camera is not guaranteed to exist while the SceneFile is being
		// deserialized. The first map spawn remains a deterministic priority anchor.
		RefreshCells( FallbackAnchor );

		while ( _liveProps + _failedProps < _totalProps )
		{
			PopulateCells(
				Titanfall2StreamingSettings.PropPreloadPerYield,
				Titanfall2StreamingSettings.PropPreloadBudgetMilliseconds );
			var processed = _liveProps + _failedProps;
			context.Title = $"Loading Titanfall 2 static props ({processed}/{_totalProps})";
			if ( processed < _totalProps ) await Task.Yield();
		}

		LogCompletion();
		Log.Info( $"Titanfall 2 static props finished during scene loading in "
			+ $"{loadTimer.Elapsed.TotalSeconds:0.00}s; gameplay can now start ({MapPath})." );
	}

	protected override void OnUpdate()
	{
		if ( !_initialized ) InitializeCells();
		if ( !_initialized || _cells.Count == 0 ) return;

		var anchor = Scene?.Camera.IsValid() == true
			? Scene.Camera.WorldPosition
			: FallbackAnchor;
		var refreshDistance = Titanfall2StreamingSettings.PropCellSize * 0.25f;
		var pvsCellChanged = TryFindPvsCell( anchor, out var pvsCell ) && pvsCell != _pvsCameraCell;
		if ( !_hasAnchor
			|| pvsCellChanged
			|| anchor.DistanceSquared( _lastAnchor ) >= refreshDistance * refreshDistance )
		{
			RefreshCells( anchor );
		}

		PopulateCells(
			Titanfall2StreamingSettings.PropsPerFrame,
			Titanfall2StreamingSettings.PropFrameBudgetMilliseconds );
		ProcessRuntimeCellQueue();
		ProcessLodSwaps();
		LogCompletion();
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
		var releasedProps = _liveProps;
		var releasedBatches = _batchCount;
		foreach ( var cell in _cells.Values )
			DestroyCellInstances( cell );
		_cells.Clear();
		_workQueue.Clear();
		_runtimeCellQueue.Clear();
		_lodSwapQueue.Clear();
		Log.Info( $"Titanfall 2 static prop scene resources released: {releasedProps} props, "
			+ $"{releasedBatches} instance batches ({MapPath})." );
	}

	void InitializeCells()
	{
		if ( _initialized ) return;
		_initialized = true;
		if ( string.IsNullOrWhiteSpace( EncodedProps ) ) return;

		try
		{
			foreach ( var prop in Decode( EncodedProps ) )
			{
				var coordinate = CellCoordinate.FromPosition( prop.Position, Titanfall2StreamingSettings.PropCellSize );
				if ( !_cells.TryGetValue( coordinate, out var cell ) )
				{
					cell = new PropCell( coordinate );
					_cells.Add( coordinate, cell );
				}
				cell.Props.Add( prop );
			}
			_totalProps = _cells.Values.Sum( static cell => cell.Props.Count );

			_refreshTimer.Start();
			_statusTimer.Start();
			Log.Info( $"Titanfall 2 prop streamer ready: {_totalProps} props in "
				+ $"{_cells.Count} cells; prioritize within {Titanfall2StreamingSettings.PropLoadRadius:0}, "
				+ "then load the full map and retain all loaded props until scene teardown, "
				+ $"{Titanfall2StreamingSettings.PropsPerFrame} props/frame, "
				+ $"{Titanfall2StreamingSettings.PropFrameBudgetMilliseconds:0.0} ms budget ({MapPath})." );
		}
		catch ( Exception exception )
		{
			_cells.Clear();
			Log.Warning( $"Failed to initialize Titanfall 2 prop streaming for '{MapPath}': {exception.Message}" );
		}
	}

	void RefreshCells( Vector3 anchor )
	{
		_lastAnchor = anchor;
		_hasAnchor = true;
		_refreshTimer.Restart();
		var loadDistanceSquared = Titanfall2StreamingSettings.PropLoadRadius * Titanfall2StreamingSettings.PropLoadRadius;
		var previousPvsCell = _pvsCameraCell;
		var previousPvsCells = _visiblePvsCells;
		_visiblePvsCells = ResolveVisiblePvsCells( anchor );
		var pvsChanged = previousPvsCell != _pvsCameraCell || previousPvsCells != _visiblePvsCells;

		foreach ( var cell in _cells.Values )
		{
			cell.DistanceSquared = cell.Coordinate.DistanceSquaredToCell( anchor, Titanfall2StreamingSettings.PropCellSize );
			cell.Wanted = cell.DistanceSquared <= loadDistanceSquared;
		}

		_runtimePvsUpdatePending |= pvsChanged;
		QueueRuntimeCellRefresh();

		if ( _completionLogged ) return;
		_workQueue.Clear();
		_workQueue.AddRange( _cells.Values.Where( static cell => cell.NextProp < cell.Props.Count ) );
		_workQueue.Sort( static ( left, right ) =>
		{
			var priority = right.Wanted.CompareTo( left.Wanted );
			return priority != 0 ? priority : left.DistanceSquared.CompareTo( right.DistanceSquared );
		} );
	}

	void QueueRuntimeCellRefresh()
	{
		_runtimeCellQueue.Clear();
		_runtimeCellQueue.AddRange( _cells.Values );
		_runtimeCellQueue.Sort( static ( left, right ) => left.DistanceSquared.CompareTo( right.DistanceSquared ) );
		_runtimeCellCursor = 0;
	}

	void ProcessRuntimeCellQueue()
	{
		if ( _runtimeCellCursor >= _runtimeCellQueue.Count ) return;
		var timer = Stopwatch.StartNew();
		var processed = 0;
		while ( processed < Titanfall2StreamingSettings.PropRuntimeCellsPerFrame
			&& _runtimeCellCursor < _runtimeCellQueue.Count )
		{
			var cell = _runtimeCellQueue[_runtimeCellCursor++];
			if ( _runtimePvsUpdatePending ) UpdateCellVisibilityState( cell );
			UpdateCellLodState( cell, _lastAnchor );
			UpdateCellShadowState( cell, _lastAnchor );
			UpdateCellCollisionState( cell, _lastAnchor );
			processed++;
			if ( timer.Elapsed.TotalMilliseconds >= Titanfall2StreamingSettings.PropRuntimeCellBudgetMilliseconds ) break;
		}

		if ( _runtimeCellCursor < _runtimeCellQueue.Count ) return;
		_runtimePvsUpdatePending = false;
		_runtimeCellQueue.Clear();
		_runtimeCellCursor = 0;
	}

	Titanfall2BspReader.VisibilityCellMask ResolveVisiblePvsCells( Vector3 anchor )
	{
		_pvsCameraCell = -1;
		if ( !TryFindPvsCell( anchor, out _pvsCameraCell ) ) return default;
		return _visibility.GetReachableCells( _pvsCameraCell, Titanfall2StreamingSettings.PropPvsPortalDepth )
			.Union( _visibility.AlwaysVisibleCells );
	}

	bool TryFindPvsCell( Vector3 anchor, out int cell )
	{
		cell = -1;
		if ( !Titanfall2StreamingSettings.PropPvsCulling || _visibility is not { IsValid: true } ) return false;
		cell = _visibility.FindCell( new System.Numerics.Vector3( anchor.x, anchor.y, anchor.z ) );
		return cell >= 0;
	}

	bool IsPvsVisible( Titanfall2BspReader.VisibilityCellMask propCells )
	{
		// A missing/invalid PVS mapping must never hide a prop. This also covers
		// static props which lie outside the original cell AABB reference table.
		return _visiblePvsCells.IsEmpty || propCells.IsEmpty || propCells.Intersects( _visiblePvsCells );
	}

	void UpdateCellVisibilityState( PropCell cell )
	{
		foreach ( var instance in cell.Instances )
		{
			if ( instance.IsBatched ) continue;
			var visible = IsPvsVisible( instance.Source.PvsCells );
			if ( visible == instance.RenderingEnabled ) continue;
			instance.RenderingEnabled = visible;
			if ( instance.RenderObject.IsValid() ) instance.RenderObject.RenderingEnabled = visible;
		}

		foreach ( var batch in cell.Batches.Values )
		{
			var visible = IsPvsVisible( batch.PvsCells );
			if ( batch.RenderingEnabled == visible ) continue;
			batch.RenderingEnabled = visible;
			if ( batch.RenderBatch.IsValid() ) batch.RenderBatch.RenderingEnabled = visible;
			foreach ( var fallback in batch.FallbackObjects )
				if ( fallback.IsValid() ) fallback.RenderingEnabled = visible;
		}

	}

	void UpdatePvsStatistics()
	{
		_pvsVisibleProps = _cells.Values.Sum( static current =>
			current.Instances.Count( instance => !instance.IsBatched && instance.RenderingEnabled )
			+ current.Batches.Values.Sum( batch => batch.RenderingEnabled ? batch.Transforms.Count : 0 ) );
		_pvsCulledProps = Math.Max( 0, _liveProps - _pvsVisibleProps );
	}

	void UpdateCellLodState( PropCell cell, Vector3 anchor )
	{
		if ( !Titanfall2StreamingSettings.PropLods ) return;
		foreach ( var batch in cell.Batches.Values )
		{
			if ( !batch.RenderBatch.IsValid() || batch.FallbackObjects.Count > 0 ) continue;
			var desiredLod = SelectLod( batch.DistanceToClosestInstance( anchor ), batch.CurrentLod );
			if ( desiredLod == batch.CurrentLod && !batch.LodSwapQueued ) continue;
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
		var lod = distance > Titanfall2StreamingSettings.PropLod3Distance + (currentLod >= 3 ? -hysteresis : hysteresis ) ? 3
			: distance > Titanfall2StreamingSettings.PropLod2Distance + (currentLod >= 2 ? -hysteresis : hysteresis ) ? 2
			: distance > Titanfall2StreamingSettings.PropLod1Distance + (currentLod >= 1 ? -hysteresis : hysteresis ) ? 1
			: 0;
		return Math.Clamp( lod, 0, ModelLoader.StaticInstanceLodCount - 1 );
	}

	void ProcessLodSwaps()
	{
		if ( _lodSwapQueue.Count == 0 ) return;
		var timer = Stopwatch.StartNew();
		var processed = 0;
		while ( processed < Titanfall2StreamingSettings.PropLodSwapsPerFrame
			&& _lodSwapQueue.Count > 0 )
		{
			var batch = _lodSwapQueue.Dequeue();
			batch.LodSwapQueued = false;
			if ( batch.DesiredLod != batch.CurrentLod ) TrySwapBatchLod( batch, batch.DesiredLod );
			processed++;
			if ( timer.Elapsed.TotalMilliseconds >= Titanfall2StreamingSettings.PropLodSwapBudgetMilliseconds ) break;
		}
	}

	void TrySwapBatchLod( PendingBatch batch, int lod )
	{
		var path = $"mount://{MountIdent}/{ModelLoader.GetStaticInstancePath( batch.ModelPath, lod )}.vmdl";
		var replacement = Model.Load( path );
		if ( replacement is null || replacement == Model.Error || !ModelLoader.CanInstance( replacement ) ) return;
		try
		{
			var replacementBatch = new Titanfall2StaticModelBatch(
				Scene.SceneWorld, replacement, batch.Transforms, batch.RenderBatch.CastShadows, batch.AverageProbeColor )
			{
				RenderingEnabled = batch.RenderingEnabled
			};
			batch.RenderBatch.Delete();
			batch.RenderBatch = replacementBatch;
			batch.Model = replacement;
			batch.CurrentLod = lod;
		}
		catch ( Exception exception )
		{
			Log.Warning( exception, $"Unable to switch Titanfall 2 prop batch '{batch.ModelPath}' to LOD{lod}." );
		}
	}

	void PopulateCells( int maximumProps, float budgetMilliseconds )
	{
		if ( _workQueue.Count == 0 ) return;
		var timer = Stopwatch.StartNew();
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
			if ( timer.Elapsed.TotalMilliseconds >= budgetMilliseconds )
				break;
		}
	}

	void LogCompletion()
	{
		if ( _completionLogged || _totalProps <= 0 || _liveProps + _failedProps < _totalProps ) return;
		BuildPendingBatches();
		_completionLogged = true;
		Log.Info( $"Titanfall 2 prop streaming complete: {_liveProps}/{_totalProps} retained in "
			+ $"{_activeCells}/{_cells.Count} cells, {_instancedProps} GPU-instanced in {_batchCount} batches, "
			+ $"{_skinnedProps} frozen-skinned fallbacks, {_plainProps} individual rigid, "
			+ $"{_renderingProps} rendering, {_shadowCastingProps} casting shadows, "
			+ $"{_collidersCreated}/{_collidableProps} colliders active "
			+ $"({_collidersReleased} released, {_colliderFailures} unavailable), "
			+ $"PVS cell {_pvsCameraCell} ({_pvsVisibleProps} visible/{_pvsCulledProps} culled), "
			+ $"{_failedProps} skipped ({MapPath})." );
	}

	bool SpawnProp( PropCell cell, StreamedProp prop )
	{
		if ( string.IsNullOrWhiteSpace( prop.ModelPath ) || string.IsNullOrWhiteSpace( MountIdent ) ) return false;
		var resourcePath = $"mount://{MountIdent}/{prop.ModelPath}.vmdl";
		var initialLod = SelectLod( MathF.Sqrt( cell.DistanceSquared ), 0 );
		var staticResourcePath = $"mount://{MountIdent}/{ModelLoader.GetStaticInstancePath( prop.ModelPath, initialLod )}.vmdl";
		var staticModel = Model.Load( staticResourcePath );
		var model = staticModel is not null && staticModel != Model.Error
			? staticModel
			: Model.Load( resourcePath );
		if ( model is null || model == Model.Error )
		{
			if ( _failureLogs++ < 16 ) Titanfall2Log.Warning( $"Unable to stream Titanfall 2 static prop model '{resourcePath}'." );
			return false;
		}

		var scale = MathF.Max( prop.Scale, 0.001f );
		var radius = model.Bounds.Size.Length * 0.5f * scale;
		var castShadows = ShouldCastShadows( prop.Position, radius, _lastAnchor );
		var renderingEnabled = IsPvsVisible( prop.PvsCells );
		var worldTransform = new Transform( prop.Position, prop.Rotation.ToRotation(), scale );
		SceneObject renderObject = null;
		var isBatched = staticModel.IsValid() && staticModel != Model.Error && ModelLoader.CanInstance( staticModel );

		// Opaque/cutout static variants contain bind-pose vertices without a
		// skeleton, so identical models can share one instanced draw inside their
		// permanent BSP cell. Special transparent/decal/water models stay as
		// individual native objects to preserve material pass ordering.
		if ( isBatched )
		{
			if ( !cell.Batches.TryGetValue( staticModel, out var pendingBatch ) )
			{
				pendingBatch = new PendingBatch( prop.ModelPath, staticModel, initialLod, renderingEnabled );
				cell.Batches.Add( staticModel, pendingBatch );
			}
			pendingBatch.Add( worldTransform, radius, castShadows, prop.ProbeColor, prop.PvsCells );
			pendingBatch.RenderingEnabled |= renderingEnabled;
			_instancedProps++;
		}
		else if ( staticModel.IsValid() && staticModel != Model.Error )
		{
			renderObject = new SceneObject( Scene.SceneWorld, staticModel, worldTransform );
			_plainProps++;
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
			renderObject = new SceneObject( Scene.SceneWorld, model, worldTransform );
			_plainProps++;
		}
		if ( renderObject.IsValid() )
		{
			renderObject.Flags.IsStatic = true;
			renderObject.Flags.CastShadows = castShadows;
			renderObject.ColorTint = new Color( prop.ProbeColor.x, prop.ProbeColor.y, prop.ProbeColor.z, 1f );
			renderObject.RenderingEnabled = renderingEnabled;
		}

		if ( renderingEnabled ) _renderingProps++;
		if ( castShadows ) _shadowCastingProps++;
		var instance = new PropInstance( prop, renderObject, radius, renderingEnabled, castShadows, isBatched );
		if ( prop.Collidable && (Titanfall2StreamingSettings.PropCollisions || Titanfall2StreamingSettings.NavMeshStaticProps) )
		{
			_collidableProps++;
			EnsureCollisionState( instance, anchor: _lastAnchor, fallbackModel: model );
		}
		cell.Instances.Add( instance );
		return true;
	}

	void UpdateCellCollisionState( PropCell cell, Vector3 anchor )
	{
		foreach ( var instance in cell.Instances )
			EnsureCollisionState( instance, anchor, fallbackModel: null );
	}

	void EnsureCollisionState( PropInstance instance, Vector3 anchor, Model fallbackModel )
	{
		if ( !instance.Source.Collidable
			|| (!Titanfall2StreamingSettings.PropCollisions && !Titanfall2StreamingSettings.NavMeshStaticProps)
			|| instance.CollisionUnavailable )
			return;

		var collisionActive = instance.CollisionObject.IsValid() && !instance.CollisionObject.IsDestroyed;
		var shouldBeActive = ShouldKeepCollision( instance.Position, instance.Radius, anchor, collisionActive );
		if ( shouldBeActive == collisionActive ) return;
		if ( !shouldBeActive )
		{
			instance.CollisionObject.Destroy();
			instance.CollisionObject = null;
			_collidersCreated = Math.Max( 0, _collidersCreated - 1 );
			_collidersReleased++;
			return;
		}

		var prop = instance.Source;
		var collisionObject = new GameObject( GameObject, true, System.IO.Path.GetFileNameWithoutExtension( prop.ModelPath ) );
		collisionObject.WorldPosition = prop.Position;
		collisionObject.WorldRotation = prop.Rotation;
		collisionObject.WorldScale = Vector3.One * MathF.Max( prop.Scale, 0.001f );
		collisionObject.IsStatic = true;
		if ( Titanfall2StreamingSettings.NavMeshStaticProps )
			collisionObject.Tags.Add( Titanfall2DeferredNavMeshBuilder.NavMeshBodyTag );
		var collider = collisionObject.AddComponent<ModelCollider>();
		var resourcePath = $"mount://{MountIdent}/{prop.ModelPath}.vmdl";
		var collisionModel = Model.Load( resourcePath );
		collider.Model = collisionModel.IsValid() && collisionModel != Model.Error ? collisionModel : fallbackModel;
		collider.Static = true;
		if ( collider.Model.IsValid()
			&& collider.Model != Model.Error
			&& collider.Model.Physics is { Parts.Count: > 0 } )
		{
			instance.CollisionObject = collisionObject;
			_collidersCreated++;
			return;
		}

		_colliderFailures++;
		instance.CollisionUnavailable = true;
		if ( _failureLogs++ < 16 )
			Titanfall2Log.Warning( $"Titanfall 2 static prop has no usable collision bodies: "
				+ $"'{prop.ModelPath}' at {prop.Position} ({MapPath})." );
		collisionObject.Destroy();
	}

	static bool ShouldKeepCollision( Vector3 position, float radius, Vector3 anchor, bool currentlyActive )
	{
		if ( Titanfall2StreamingSettings.NavMeshStaticProps || !Titanfall2StreamingSettings.StreamPropCollisions ) return true;
		var configuredDistance = currentlyActive
			? Math.Max( Titanfall2StreamingSettings.PropCollisionEnableDistance, Titanfall2StreamingSettings.PropCollisionDisableDistance )
			: Titanfall2StreamingSettings.PropCollisionEnableDistance;
		var distance = configuredDistance + radius;
		return position.DistanceSquared( anchor ) <= distance * distance;
	}

	void UpdateCellShadowState( PropCell cell, Vector3 anchor )
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

		foreach ( var pendingBatch in cell.Batches.Values )
		{
			var nearCount = 0;
			for ( var index = 0; index < pendingBatch.Transforms.Count; ++index )
			{
				if ( ShouldCastShadows(
					pendingBatch.Transforms[index].Position,
					pendingBatch.Radii[index],
					anchor ) )
				{
					nearCount++;
				}
			}

			// A batch has one shadow-pass flag. If any instance in this cell/model
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

	void BuildPendingBatches()
	{
		if ( _batchesBuilt ) return;
		_batchesBuilt = true;

		foreach ( var cell in _cells.Values )
		{
			foreach ( var pendingBatch in cell.Batches.Values )
			{
				if ( pendingBatch.Transforms.Count == 0 ) continue;
				var castShadows = pendingBatch.ShadowedInstanceCount > 0;
				try
				{
					pendingBatch.RenderBatch = new Titanfall2StaticModelBatch(
						Scene.SceneWorld,
						pendingBatch.Model,
						pendingBatch.Transforms,
						castShadows,
						pendingBatch.AverageProbeColor )
					{
						RenderingEnabled = pendingBatch.RenderingEnabled
					};
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
						fallback.Flags.CastShadows = ShouldCastShadows(
							pendingBatch.Transforms[index].Position,
							pendingBatch.Radii[index],
							_lastAnchor );
						fallback.RenderingEnabled = pendingBatch.RenderingEnabled;
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
		var distance = Titanfall2StreamingSettings.PropShadowDistance + radius;
		return position.DistanceSquared( anchor ) <= distance * distance;
	}

	void DestroyCellInstances( PropCell cell )
	{
		if ( !cell.Active && cell.Instances.Count == 0 && cell.Batches.Count == 0 ) return;
		foreach ( var pendingBatch in cell.Batches.Values )
		{
			_shadowCastingProps = Math.Max( 0, _shadowCastingProps - pendingBatch.ShadowedInstanceCount );
			pendingBatch.RenderBatch?.Delete();
			foreach ( var fallback in pendingBatch.FallbackObjects )
				fallback?.Delete();
		}
		cell.Batches.Clear();

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

	static string Encode( IReadOnlyList<Titanfall2BspReader.StaticPropInstance> source, Titanfall2BspReader.BspVisibility visibility )
	{
		var valid = source.Where( static prop => !string.IsNullOrWhiteSpace( prop.ModelPath ) ).ToArray();
		var models = valid.Select( static prop => NormalizeModelPath( prop.ModelPath ) )
			.Distinct( StringComparer.OrdinalIgnoreCase ).ToArray();
		var indices = models.Select( static ( path, index ) => (path, index) )
			.ToDictionary( static pair => pair.path, static pair => pair.index, StringComparer.OrdinalIgnoreCase );

		using var stream = new MemoryStream();
		using ( var writer = new BinaryWriter( stream, System.Text.Encoding.UTF8, true ) )
		{
			writer.Write( DataMagic );
			writer.Write( DataVersion );
			writer.Write( models.Length );
			foreach ( var model in models ) writer.Write( model );
			writer.Write( valid.Length );
			foreach ( var prop in valid )
			{
				writer.Write( indices[NormalizeModelPath( prop.ModelPath )] );
				writer.Write( prop.Origin.X );
				writer.Write( prop.Origin.Y );
				writer.Write( prop.Origin.Z );
				writer.Write( prop.Angles.X );
				writer.Write( prop.Angles.Y );
				writer.Write( prop.Angles.Z );
				writer.Write( prop.Scale );
				writer.Write( prop.IsCollidable );
				// LIGHTPROBE_INDICES is not a confirmed one-index-per-prop table
				// in R2. Do not turn unverified records into per-instance RGB tint.
				writer.Write( 1f );
				writer.Write( 1f );
				writer.Write( 1f );
				var objectIndex = visibility is { IsValid: true }
					? visibility.WorldMeshCount + prop.SourceIndex
					: -1;
				var pvsCells = visibility?.GetObjectCellMask( objectIndex ) ?? default;
				writer.Write( pvsCells.Low );
				writer.Write( pvsCells.High );
			}
		}
		return Convert.ToBase64String( stream.GetBuffer(), 0, checked((int)stream.Length) );
	}

	static IEnumerable<StreamedProp> Decode( string encoded )
	{
		using var stream = new MemoryStream( Convert.FromBase64String( encoded ), false );
		using var reader = new BinaryReader( stream, System.Text.Encoding.UTF8, false );
		if ( reader.ReadInt32() != DataMagic ) throw new InvalidDataException( "Static prop data has an invalid signature." );
		if ( reader.ReadInt32() != DataVersion ) throw new InvalidDataException( "Static prop data has an unsupported version." );
		var modelCount = reader.ReadInt32();
		if ( modelCount < 0 || modelCount > MaximumModelCount ) throw new InvalidDataException( $"Invalid model count {modelCount}." );
		var models = new string[modelCount];
		for ( var index = 0; index < models.Length; index++ ) models[index] = reader.ReadString();

		var propCount = reader.ReadInt32();
		if ( propCount < 0 || propCount > MaximumPropCount ) throw new InvalidDataException( $"Invalid prop count {propCount}." );
		for ( var index = 0; index < propCount; index++ )
		{
			var modelIndex = reader.ReadInt32();
			if ( modelIndex < 0 || modelIndex >= models.Length ) throw new InvalidDataException( $"Invalid model index {modelIndex}." );
			var position = new Vector3( reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle() );
			var rotation = new Angles( reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle() );
			var scale = reader.ReadSingle();
			var collidable = reader.ReadBoolean();
			var probeColor = new Vector3( reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle() );
			var pvsCells = new Titanfall2BspReader.VisibilityCellMask( reader.ReadUInt64(), reader.ReadUInt64() );
			yield return new StreamedProp( models[modelIndex], position, rotation, scale, collidable, probeColor, pvsCells );
		}
	}

	static string NormalizeModelPath( string path )
	{
		var normalized = path.Replace( '\\', '/' ).TrimStart( '/' );
		return normalized.EndsWith( ".vmdl", StringComparison.OrdinalIgnoreCase ) ? normalized[..^5] : normalized;
	}

	readonly record struct StreamedProp(
		string ModelPath,
		Vector3 Position,
		Angles Rotation,
		float Scale,
		bool Collidable,
		Vector3 ProbeColor,
		Titanfall2BspReader.VisibilityCellMask PvsCells );

	sealed class PropInstance(
		StreamedProp source,
		SceneObject renderObject,
		float radius,
		bool renderingEnabled,
		bool castShadows,
		bool isBatched )
	{
		public StreamedProp Source { get; } = source;
		public GameObject CollisionObject { get; set; }
		public SceneObject RenderObject { get; } = renderObject;
		public Vector3 Position => Source.Position;
		public float Radius { get; } = radius;
		public bool RenderingEnabled { get; set; } = renderingEnabled;
		public bool CastShadows { get; set; } = castShadows;
		public bool IsBatched { get; } = isBatched;
		public bool CollisionUnavailable { get; set; }
	}

	sealed class PendingBatch( string modelPath, Model model, int currentLod, bool renderingEnabled )
	{
		public string ModelPath { get; } = modelPath;
		public Model Model { get; set; } = model;
		public int CurrentLod { get; set; } = currentLod;
		public int DesiredLod { get; set; } = currentLod;
		public bool LodSwapQueued { get; set; }
		public bool RenderingEnabled { get; set; } = renderingEnabled;
		public Titanfall2BspReader.VisibilityCellMask PvsCells { get; private set; }
		public List<Transform> Transforms { get; } = new();
		public List<float> Radii { get; } = new();
		public List<Vector3> ProbeColors { get; } = new();
		public List<SceneObject> FallbackObjects { get; } = new();
		public Titanfall2StaticModelBatch RenderBatch { get; set; }
		public int ShadowedInstanceCount { get; set; }

		public Vector3 AverageProbeColor => ProbeColors.Count == 0
			? Vector3.One
			: ProbeColors.Aggregate( Vector3.Zero, static ( sum, color ) => sum + color ) / ProbeColors.Count;

		public void Add( Transform transform, float radius, bool castShadows, Vector3 probeColor,
			Titanfall2BspReader.VisibilityCellMask pvsCells )
		{
			Transforms.Add( transform );
			Radii.Add( radius );
			ProbeColors.Add( probeColor );
			PvsCells = PvsCells.Union( pvsCells );
			if ( castShadows ) ShadowedInstanceCount++;
		}

		public float DistanceToClosestInstance( Vector3 anchor )
		{
			var result = float.MaxValue;
			foreach ( var transform in Transforms ) result = MathF.Min( result, MathF.Sqrt( transform.Position.DistanceSquared( anchor ) ) );
			return result;
		}
	}

	readonly record struct CellCoordinate( int X, int Y )
	{
		public static CellCoordinate FromPosition( Vector3 position, float size ) => new(
			(int)MathF.Floor( position.x / size ),
			(int)MathF.Floor( position.y / size ) );

		public float DistanceSquaredToCell( Vector3 point, float size )
		{
			var minimumX = X * size;
			var minimumY = Y * size;
			var maximumX = minimumX + size;
			var maximumY = minimumY + size;
			var deltaX = point.x < minimumX ? minimumX - point.x : point.x > maximumX ? point.x - maximumX : 0f;
			var deltaY = point.y < minimumY ? minimumY - point.y : point.y > maximumY ? point.y - maximumY : 0f;
			return deltaX * deltaX + deltaY * deltaY;
		}
	}

	sealed class PropCell( CellCoordinate coordinate )
	{
		public CellCoordinate Coordinate { get; } = coordinate;
		public List<StreamedProp> Props { get; } = new();
		public List<PropInstance> Instances { get; } = new();
		public Dictionary<Model, PendingBatch> Batches { get; } = new();
		public int NextProp { get; set; }
		public float DistanceSquared { get; set; }
		public bool Active { get; set; }
		public bool Wanted { get; set; }
	}
}
