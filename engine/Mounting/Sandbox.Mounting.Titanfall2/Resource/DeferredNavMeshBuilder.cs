using System.Diagnostics;
using System.Runtime.CompilerServices;

/// <summary>
/// Controls NavMesh generation for mounted maps. Navigation state is shared by the
/// mounted maps in a scene and restored when the last participating map leaves.
/// <para>
/// A host scene with navigation enabled would otherwise rasterize every tile of the
/// whole map, including every prop collider, before the loading screen closes
/// (minutes on large maps). In on-demand mode the mount defers that load-time pass,
/// restricts the input to tagged world collision, and requests tiles only around
/// players, NavMesh agents and agent targets as they move. Disabled maps do not
/// change host settings.
/// </para>
/// </summary>
[Library, Hide]
public sealed class Titanfall2DeferredNavMeshBuilder : Component, Sandbox.Internal.IUpdateSubscriber
{
	static readonly Sandbox.Diagnostics.Logger Log = new( "Titanfall2NavMesh" );
	public const string NavMeshBodyTag = "titanfall2_navmesh";
	// Engine tiles are 256 voxels of 4 units, on a grid whose origin is a multiple of
	// this size. Requests use boxes inset inside one such cell, so a different engine
	// tile size still generates the tiles covering the requested area.
	const float TileSize = 1024f;
	const float ScanIntervalSeconds = 0.25f;
	const int MaxTileRequestsPerScan = 64;

	[Property, Hide]
	public float DelaySeconds { get; set; } = 3f;

	/// <summary>Enable navigation even when the host scene did not.</summary>
	[Property, Hide]
	public bool GenerateNavMesh { get; set; }

	/// <summary>Generate tiles near players and agents instead of the whole map.</summary>
	[Property, Hide]
	public bool OnDemand { get; set; }

	[Property, Hide]
	public float OnDemandRadius { get; set; } = 2048f;

	/// <summary>Collision bounds of this map; seeds NavMesh bounds when the host has none.</summary>
	[Property, Hide]
	public BBox WorldBounds { get; set; }

	Stopwatch _delayTimer;
	static readonly ConditionalWeakTable<Scene, NavigationState> NavigationStates = new();
	NavigationState _navigation;
	Scene _navigationScene;

	sealed class NavigationState
	{
		public int Users;
		public bool Queued;
		public bool WasEnabled;
		public bool WasDeferred;
		public bool OnDemand;
		public TagSet IncludedBodies;
		public TagSet MountedBodies;
		public readonly HashSet<(int X, int Y)> RequestedTiles = new();
		public readonly Stopwatch ScanTimer = Stopwatch.StartNew();
		public int LoggedTileCount;
	}

	public void Configure( bool generateNavMesh, float delaySeconds )
		=> Configure( generateNavMesh, false, OnDemandRadius, delaySeconds, WorldBounds );

	public void Configure( bool generateNavMesh, bool onDemand, float onDemandRadius, float delaySeconds, BBox worldBounds )
	{
		GenerateNavMesh = generateNavMesh;
		OnDemand = onDemand;
		OnDemandRadius = Math.Max( TileSize * 0.5f, onDemandRadius );
		DelaySeconds = Math.Max( 0f, delaySeconds );
		WorldBounds = worldBounds;
		ReleaseState();
		ApplyState();
	}

	protected override void OnEnabled() => ApplyState();
	protected override void OnDisabled() => ReleaseState();
	protected override void OnDestroy() => ReleaseState();

	void ApplyState()
	{
		if ( !Active || (!GenerateNavMesh && !OnDemand) )
		{
			ReleaseState();
			return;
		}
		if ( _navigation is not null ) return;
		var navMesh = Scene?.NavMesh;
		if ( navMesh is null ) return;
		// On-demand alone only takes over navigation the host already asked for.
		if ( !GenerateNavMesh && !navMesh.IsEnabled && !NavigationStates.TryGetValue( Scene, out _ ) ) return;
		_navigationScene = Scene;
		_navigation = NavigationStates.GetValue( Scene, _ => CreateState( navMesh ) );
		if ( _navigation.Users++ == 0 )
		{
			navMesh.DeferGeneration = true;
			navMesh.IsEnabled = true;
			navMesh.IncludedBodies = _navigation.MountedBodies;
			if ( _navigation.OnDemand )
				Log.Info( $"Titanfall 2 NavMesh on demand: whole-map generation skipped; tiles within {OnDemandRadius:0} units of players and agents are generated as needed (bodies tagged '{NavMeshBodyTag}')." );
		}
		_delayTimer = Stopwatch.StartNew();
		// A newly added map needs generation even if another map already queued it.
		_navigation.Queued = false;
	}

	NavigationState CreateState( Sandbox.Navigation.NavMesh navMesh )
	{
		var hostTags = navMesh.IncludedBodies?.TryGetAll() ?? Array.Empty<string>();
		var hostIncludesAll = navMesh.IncludedBodies is null || navMesh.IncludedBodies.IsEmpty;
		return new NavigationState
		{
			WasEnabled = navMesh.IsEnabled,
			WasDeferred = navMesh.DeferGeneration,
			IncludedBodies = navMesh.IncludedBodies,
			OnDemand = OnDemand,
			// Whole-map mode keeps an enabled host's empty filter (every body). On-demand
			// mode limits input to mounted world collision so streamed prop colliders,
			// which appear and disappear with distance, are not baked into tiles.
			MountedBodies = !OnDemand && navMesh.IsEnabled && hostIncludesAll
				? navMesh.IncludedBodies
				: new TagSet( hostTags.Append( NavMeshBodyTag ) )
		};
	}

	void ReleaseState()
	{
		if ( _navigation is null ) return;
		if ( --_navigation.Users == 0 )
		{
			var navMesh = _navigationScene?.NavMesh;
			// Respect subsequent host changes instead of overwriting its new filter.
			if ( navMesh is not null && ReferenceEquals( navMesh.IncludedBodies, _navigation.MountedBodies ) )
			{
				navMesh.IncludedBodies = _navigation.IncludedBodies;
				navMesh.DeferGeneration = _navigation.WasDeferred;
				navMesh.IsEnabled = _navigation.WasEnabled;
			}
			NavigationStates.Remove( _navigationScene );
		}
		_navigation = null;
		_navigationScene = null;
		_delayTimer = null;
	}

	protected override void OnUpdate()
	{
		ApplyState();
		if ( _navigation is null ) return;
		_delayTimer ??= Stopwatch.StartNew();
		if ( _delayTimer.Elapsed.TotalSeconds < DelaySeconds ) return;
		// This used to wait for Titanfall2WorldMaterialStreamer to finish. That component
		// is never added to a scene, so the condition could not become true and the wait
		// was dead code; world materials are created synchronously while the map is built.
		if ( !Application.IsDedicatedServer && HasPendingWorldMaterials() ) return;

		var navMesh = Scene?.NavMesh;
		if ( navMesh is null || navMesh.IsGenerating ) return;
		if ( !ReferenceEquals( navMesh.IncludedBodies, _navigation.MountedBodies ) ) return;
		EnsureBounds( navMesh );

		if ( _navigation.OnDemand )
		{
			RequestNearbyTiles( navMesh );
			return;
		}
		if ( _navigation.Queued ) return;
		navMesh.RequestTilesGeneration( navMesh.Bounds );
		_navigation.Queued = true;
		Log.Info( $"Titanfall 2 incremental NavMesh generation queued after {DelaySeconds:0.##}s for bounds {navMesh.Bounds}." );
	}

	/// <summary>
	/// Tile requests are clamped to NavMesh bounds. Those are computed only by the
	/// engine's load pass, so a map added after that pass would request nothing.
	/// </summary>
	void EnsureBounds( Sandbox.Navigation.NavMesh navMesh )
	{
		if ( navMesh.CustomBounds || WorldBounds.Size.Length <= 0f ) return;
		var current = navMesh.Bounds;
		if ( current.Size.Length <= 0f ) navMesh.Bounds = WorldBounds;
		else if ( !current.Contains( WorldBounds ) ) navMesh.Bounds = current.AddBBox( WorldBounds );
	}

	void RequestNearbyTiles( Sandbox.Navigation.NavMesh navMesh )
	{
		var state = _navigation;
		// Every mounted map in the scene shares one tile set; scan once per interval.
		if ( state.ScanTimer.Elapsed.TotalSeconds < ScanIntervalSeconds ) return;
		state.ScanTimer.Restart();
		var budget = MaxTileRequestsPerScan;
		foreach ( var player in Scene.GetAllComponents<PlayerController>() )
			if ( player.Active ) RequestAround( navMesh, player.WorldPosition, OnDemandRadius, ref budget );
		foreach ( var agent in Scene.GetAllComponents<NavMeshAgent>() )
		{
			if ( !agent.Active ) continue;
			RequestAround( navMesh, agent.WorldPosition, OnDemandRadius, ref budget );
			if ( agent.TargetPosition is Vector3 target ) RequestAround( navMesh, target, TileSize, ref budget );
		}
		if ( state.RequestedTiles.Count >= state.LoggedTileCount + 64 )
		{
			state.LoggedTileCount = state.RequestedTiles.Count;
			Log.Info( $"Titanfall 2 NavMesh on demand: {state.RequestedTiles.Count} tiles requested so far." );
		}
	}

	/// <summary>Requests unrequested tiles within <paramref name="radius"/>, nearest ring first.</summary>
	void RequestAround( Sandbox.Navigation.NavMesh navMesh, Vector3 position, float radius, ref int budget )
	{
		if ( budget <= 0 ) return;
		var bounds = navMesh.Bounds;
		if ( position.x < bounds.Mins.x - radius || position.x > bounds.Maxs.x + radius
			|| position.y < bounds.Mins.y - radius || position.y > bounds.Maxs.y + radius ) return;
		var centerX = (int)MathF.Floor( position.x / TileSize );
		var centerY = (int)MathF.Floor( position.y / TileSize );
		var rings = (int)MathF.Ceiling( radius / TileSize );
		for ( var ring = 0; ring <= rings && budget > 0; ++ring )
		{
			for ( var dx = -ring; dx <= ring && budget > 0; ++dx )
			{
				for ( var dy = -ring; dy <= ring && budget > 0; ++dy )
				{
					if ( Math.Max( Math.Abs( dx ), Math.Abs( dy ) ) != ring ) continue;
					var tile = (X: centerX + dx, Y: centerY + dy);
					var min = new Vector2( tile.X * TileSize, tile.Y * TileSize );
					// Distance from the position to the nearest point of the tile.
					var nearest = new Vector2( Math.Clamp( position.x, min.x, min.x + TileSize ), Math.Clamp( position.y, min.y, min.y + TileSize ) );
					if ( (nearest - new Vector2( position.x, position.y )).Length > radius ) continue;
					if ( !_navigation.RequestedTiles.Add( tile ) ) continue;
					navMesh.RequestTilesGeneration( new BBox(
						new Vector3( min.x + 1f, min.y + 1f, bounds.Mins.z ),
						new Vector3( min.x + TileSize - 1f, min.y + TileSize - 1f, bounds.Maxs.z ) ) );
					--budget;
				}
			}
		}
	}

	/// <summary>
	/// True while a world material streamer that owns this map still has materials to
	/// apply. No code path installs that component today, so this reports false; it is
	/// kept as the single place that would gate generation if materials become streamed.
	/// </summary>
	bool HasPendingWorldMaterials()
	{
		return Components.Get<Titanfall2WorldMaterialStreamer>() is { IsCompleted: false };
	}
}
