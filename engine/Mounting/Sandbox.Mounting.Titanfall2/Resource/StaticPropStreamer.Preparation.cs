using System.Diagnostics;

public sealed partial class Titanfall2StaticPropStreamer
{
	protected override async Task OnLoad( LoadingContext context )
	{
		_runtimePhase = RuntimePhase.Preparing;
		try { await PrepareResources( context ); }
		catch
		{
			// A failed/cancelled load must not leave native resources attached to a
			// partially loaded component while the caller handles the exception.
			ReleaseResources();
			throw;
		}
		finally
		{
			if ( !_releasing ) _runtimePhase = PreparationComplete ? RuntimePhase.Ready : RuntimePhase.Uninitialized;
		}
	}

	async Task PrepareResources( LoadingContext context )
	{
		PreparationComplete = false;
		Collisions.ResidentReady = false;
		if ( Application.IsDedicatedServer ) KeepAllCollisions = true;
		if ( !_initialized ) InitializeCells();
		if ( !_initialized || _initializationFailed ) return;
		if ( _cells.Count == 0 || _totalProps == 0 )
		{
			Collisions.ResidentReady = KeepAllCollisions;
			PreparationComplete = true;
			return;
		}

		_renderResources.Prepare( EncodedLocalLights );
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

		if ( KeepAllCollisions ) await Collisions.Preload( context );
		if ( _releasing || !this.IsValid() ) return;

		LogCompletion();
		LogBatchComposition();
		SetResourcesEnabled( Active );
		Log.Info( $"Titanfall 2 static props finished during scene loading in "
			+ $"{loadTimer.Elapsed.TotalSeconds:0.00}s ({MapPath})." );
		if ( KeepAllCollisions && !ResidentCollisionsReady )
			Log.Warning( $"Titanfall 2 resident prop collision is incomplete: {_failedProps} missing props, "
				+ $"{Collisions.Failures} missing collision models, {Collisions.Created}/{Collisions.Expected} colliders; "
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
