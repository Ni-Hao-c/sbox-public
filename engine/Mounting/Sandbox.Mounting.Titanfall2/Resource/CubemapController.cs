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

	internal int ConfigureCustom( IReadOnlyList<Texture> textures, IReadOnlyList<Vector3> positions, BBox mapBounds )
	{
		_ownedTextures.AddRange( textures.Where( texture => texture is not null && texture.IsValid() ) );
		var count = Math.Min( _ownedTextures.Count, positions.Count );
		for ( var index = 0; index < count; index++ )
		{
			var radius = CalculateProbeRadius( positions, index, mapBounds );
			var probeObject = new GameObject( GameObject, false, $"titanfall2_cubemap_{index:D3}" );
			probeObject.WorldPosition = positions[index];
			var probe = probeObject.AddComponent<EnvmapProbe>();
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
		_fallbackPosition = position;
		_fallbackBounds = mapBounds;
		_fallbackPending = true;
		_fallbackDelay.Restart();
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
		foreach ( var probeObject in _probeObjects )
		{
			if ( probeObject.IsValid() && !probeObject.IsDestroyed ) probeObject.Destroy();
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
