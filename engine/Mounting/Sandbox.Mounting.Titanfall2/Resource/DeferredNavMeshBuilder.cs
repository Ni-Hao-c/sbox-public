using System.Diagnostics;

/// <summary>
/// Queues incremental NavMesh generation after a mounted map has finished loading.
/// The scene NavMesh is initialized with DeferGeneration during the loading phase.
/// </summary>
[Library]
public sealed class Titanfall2DeferredNavMeshBuilder : Component, Sandbox.Internal.IUpdateSubscriber
{
	static readonly Sandbox.Diagnostics.Logger Log = new( "Titanfall2NavMesh" );
	public const string NavMeshBodyTag = "titanfall2_navmesh";

	[Property, Hide]
	public float DelaySeconds { get; set; } = 3f;

	[Property, Hide]
	public bool GenerateNavMesh { get; set; }

	Stopwatch _delayTimer;
	bool _queued;

	public void Configure( bool generateNavMesh, float delaySeconds )
	{
		GenerateNavMesh = generateNavMesh;
		DelaySeconds = Math.Max( 0f, delaySeconds );
		ApplyState();
	}

	protected override void OnAwake()
	{
		ApplyState();
	}

	void ApplyState()
	{
		var navMesh = Scene?.NavMesh;
		if ( navMesh is null ) return;
		navMesh.DeferGeneration = true;
		navMesh.IsEnabled = GenerateNavMesh;
		if ( GenerateNavMesh )
		{
			navMesh.IncludedBodies = new TagSet( [NavMeshBodyTag] );
			Log.Info( $"Titanfall 2 deferred NavMesh generation armed for bodies tagged '{NavMeshBodyTag}'." );
		}
		else
		{
			Log.Info( "Titanfall 2 NavMesh generation disabled for this mounted map." );
		}
	}

	protected override void OnStart()
	{
		if ( !GenerateNavMesh ) return;
		_delayTimer = Stopwatch.StartNew();
	}

	protected override void OnUpdate()
	{
		if ( !GenerateNavMesh ) return;
		if ( _queued ) return;
		_delayTimer ??= Stopwatch.StartNew();
		if ( _delayTimer.Elapsed.TotalSeconds < DelaySeconds ) return;
		if ( !Application.IsDedicatedServer
			&& Components.Get<Titanfall2WorldMaterialStreamer>() is { IsCompleted: false } ) return;

		var navMesh = Scene?.NavMesh;
		if ( navMesh is null || navMesh.IsGenerating ) return;

		navMesh.IsEnabled = true;
		navMesh.IncludedBodies = new TagSet( [NavMeshBodyTag] );
		navMesh.RequestTilesGeneration( navMesh.Bounds );
		_queued = true;
		Log.Info( $"Titanfall 2 incremental NavMesh generation queued after {DelaySeconds:0.##}s for bounds {navMesh.Bounds}." );
	}
}
