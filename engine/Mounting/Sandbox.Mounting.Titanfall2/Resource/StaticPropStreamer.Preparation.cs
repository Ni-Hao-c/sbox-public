using System.Diagnostics;

public sealed partial class Titanfall2StaticPropStreamer
{
	protected override async Task OnLoad( LoadingContext context )
	{
		_runtimePhase = RuntimePhase.Preparing;
		try { await PrepareResources( context ); }
		finally
		{
			if ( !_releasing ) _runtimePhase = PreparationComplete ? RuntimePhase.Ready : RuntimePhase.Uninitialized;
		}
	}

	bool HasPendingResidentCollisions => _collidersCreated + _collisionFilteredProps + _colliderFailures < _collidableProps;

	async Task PrepareResources( LoadingContext context )
	{
		PreparationComplete = false;
		ResidentCollisionsReady = false;
		if ( Application.IsDedicatedServer ) KeepAllCollisions = true;
		if ( !_initialized ) InitializeCells();
		if ( !_initialized || _initializationFailed ) return;
		if ( _cells.Count == 0 || _totalProps == 0 )
		{
			ResidentCollisionsReady = KeepAllCollisions;
			PreparationComplete = true;
			return;
		}

		var loadTimer = Stopwatch.StartNew();
		var cacheBefore = Titanfall2ProcessedCache.GetStatistics();
		Log.Info( $"Titanfall 2 effective configuration: {System.Text.Json.JsonSerializer.Serialize( Titanfall2EffectiveConfiguration.Capture() )}" );
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
			if ( !HasPendingResidentCollisions )
			{
				_collisionCellQueue.Clear();
				_collisionCellCursor = 0;
			}
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
		PreparationComplete = true;
	}

}
