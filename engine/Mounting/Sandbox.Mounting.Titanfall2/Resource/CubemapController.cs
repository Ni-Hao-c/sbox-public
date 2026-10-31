using System.Diagnostics;

/// <summary>
/// Owns runtime cubemap textures created from an rBSP's embedded VTF and creates
/// static scene probes. If a map has no embedded probes, it creates one cheap
/// render-once fallback after the loading frame has completed.
/// </summary>
[Library]
public sealed class Titanfall2CubemapController : Component, Component.DontExecuteOnServer, Sandbox.Internal.IUpdateSubscriber
{
	static readonly Sandbox.Diagnostics.Logger Log = new( "Titanfall2Cubemaps" );
	readonly List<Texture> _ownedTextures = [];
	readonly List<GameObject> _probeObjects = [];
	readonly Stopwatch _fallbackDelay = new();
	Vector3 _fallbackPosition;
	BBox _fallbackBounds;
	bool _fallbackPending;

	// Runtime textures have no serializable resource path. Keep the authored VTF
	// source so playing/cloning a mounted scene can reconstruct its own textures.
	[Property, Hide] public string EncodedCubemapVtf { get; set; }
	[Property, Hide] public Vector3[] ProbePositions { get; set; } = [];
	[Property, Hide] public BBox SourceBounds { get; set; }
	[Property, Hide] public bool UseFallback { get; set; }
	[Property, Hide] public Vector3 FallbackPosition { get; set; }

	internal int ConfigureCustom( IReadOnlyList<Texture> textures, IReadOnlyList<Vector3> positions, BBox mapBounds, byte[] sourceVtf = null )
	{
		if ( sourceVtf is not null ) EncodedCubemapVtf = Convert.ToBase64String( sourceVtf );
		ProbePositions = positions.ToArray();
		SourceBounds = mapBounds;
		UseFallback = false;
		_ownedTextures.AddRange( textures.Where( texture => texture is not null && texture.IsValid() ) );
		var count = Math.Min( _ownedTextures.Count, positions.Count );
		for ( var index = 0; index < count; index++ )
		{
			var radius = CalculateProbeRadius( positions, index, mapBounds );
			var name = $"titanfall2_cubemap_{index:D3}";
			var probe = Scene.GetComponentsInChildren<EnvmapProbe>()
				.FirstOrDefault( p => p.GameObject.Parent == GameObject && p.GameObject.Name == name );
			var probeObject = probe?.GameObject ?? new GameObject( GameObject, false, name );
			probeObject.WorldPosition = positions[index];
			probe ??= probeObject.AddComponent<EnvmapProbe>();
			probe.Mode = EnvmapProbe.EnvmapProbeMode.CustomTexture;
			probe.Texture = _ownedTextures[index];
			probe.Projection = SceneCubemap.ProjectionMode.Sphere;
			probe.Bounds = BBox.FromPositionAndSize( Vector3.Zero, Vector3.One * radius * 2f );
			probe.Feathering = MathF.Max( 64f, radius * 0.25f );
			probe.Priority = 1;
			probeObject.Enabled = true;
			_probeObjects.Add( probeObject );
		}
		return count;
	}

	internal void ConfigureDelayedFallback( Vector3 position, BBox mapBounds )
	{
		UseFallback = true;
		FallbackPosition = position;
		SourceBounds = mapBounds;
		_fallbackPosition = position;
		_fallbackBounds = mapBounds;
		_fallbackPending = true;
		_fallbackDelay.Restart();
	}

	protected override Task OnLoad( LoadingContext context )
	{
		if ( _ownedTextures.Count > 0 ) return Task.CompletedTask;
		if ( !string.IsNullOrEmpty( EncodedCubemapVtf ) )
		{
			try
			{
				var bytes = Convert.FromBase64String( EncodedCubemapVtf );
				if ( VtfTextureDecoder.TryCreateCubeFrames( bytes, out var textures, out var error ) )
				{
					var count = ConfigureCustom( textures, ProbePositions, SourceBounds );
					Log.Info( $"Titanfall 2 runtime cubemaps restored after scene copy: {count} probes." );
				}
				else Log.Warning( $"Unable to restore Titanfall 2 runtime cubemaps: {error}" );
			}
			catch ( FormatException exception )
			{
				Log.Warning( exception, "Invalid serialized Titanfall 2 cubemap data." );
			}
		}
		else if ( UseFallback ) ConfigureDelayedFallback( FallbackPosition, SourceBounds );
		return Task.CompletedTask;
	}

	protected override void OnUpdate()
	{
		if ( !_fallbackPending || _fallbackDelay.Elapsed.TotalSeconds < 2.0 ) return;
		_fallbackPending = false;
		var size = _fallbackBounds.Size;
		var radius = Math.Clamp( size.Length * 0.5f, 1024f, 8192f );
		var probeObject = new GameObject( GameObject, false, "titanfall2_cubemap_fallback" );
		probeObject.WorldPosition = _fallbackPosition;
		var probe = probeObject.AddComponent<EnvmapProbe>();
		probe.Mode = EnvmapProbe.EnvmapProbeMode.Realtime;
		probe.UpdateStrategy = EnvmapProbe.CubemapDynamicUpdate.OnEnabled;
		probe.Resolution = EnvmapProbe.CubemapResolution.Small;
		probe.MultiBounce = false;
		probe.Projection = SceneCubemap.ProjectionMode.Sphere;
		probe.Bounds = BBox.FromPositionAndSize( Vector3.Zero, Vector3.One * radius * 2f );
		probe.Feathering = MathF.Max( 128f, radius * 0.25f );
		probe.ZNear = 16f;
		probe.ZFar = Math.Clamp( radius * 2f, 2048f, 16384f );
		probeObject.Enabled = true;
		_probeObjects.Add( probeObject );
		Log.Info( $"Titanfall 2 delayed 128px fallback environment probe created at {_fallbackPosition}." );
	}

	protected override void OnDestroy()
	{
		_fallbackPending = false;
		// Destroy immediately: GameObject.Destroy only queues deletion, so the probes
		// would stay in the SceneWorld for the rest of the frame and keep sampling the
		// textures disposed below them.
		foreach ( var probeObject in _probeObjects )
		{
			if ( probeObject.IsValid() && !probeObject.IsDestroyed ) probeObject.DestroyImmediate();
		}
		_probeObjects.Clear();
		foreach ( var texture in _ownedTextures ) texture?.Dispose();
		_ownedTextures.Clear();
	}

	static float CalculateProbeRadius( IReadOnlyList<Vector3> positions, int index, BBox mapBounds )
	{
		var nearestSquared = float.MaxValue;
		for ( var other = 0; other < positions.Count; other++ )
		{
			if ( other == index ) continue;
			nearestSquared = MathF.Min( nearestSquared, positions[index].DistanceSquared( positions[other] ) );
		}
		var radius = nearestSquared < float.MaxValue
			? MathF.Sqrt( nearestSquared ) * 1.25f
			: mapBounds.Size.Length * 0.5f;
		return Math.Clamp( radius, 512f, 8192f );
	}
}
