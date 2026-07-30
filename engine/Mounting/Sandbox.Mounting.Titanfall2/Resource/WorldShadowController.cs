using System.Diagnostics;

/// <summary>
/// Keeps all world chunks rendered while limiting which chunks enter the
/// directional-light shadow cascades. This avoids rebuilding or hiding geometry
/// as the player moves.
/// </summary>
[Library]
public sealed class Titanfall2WorldShadowController : Component, Component.DontExecuteOnServer
{
	readonly List<ShadowChunk> _chunks = [];
	readonly Stopwatch _refreshTimer = new();

	protected override void OnStart()
	{
		_chunks.Clear();
		foreach ( var child in GameObject.Children )
		{
			if ( !child.Name.StartsWith( "world_", StringComparison.OrdinalIgnoreCase ) ) continue;
			var renderer = child.Components.Get<ModelRenderer>();
			if ( !renderer.IsValid() ) continue;
			_chunks.Add( new ShadowChunk( renderer, renderer.Bounds.Center, renderer.Bounds.Size.Length * 0.5f, true ) );
		}
		_refreshTimer.Start();
		Refresh();
	}

	protected override void OnUpdate()
	{
		if ( _refreshTimer.Elapsed.TotalSeconds < 0.25 ) return;
		_refreshTimer.Restart();
		Refresh();
	}

	protected override void OnDestroy() => _chunks.Clear();

	void Refresh()
	{
		if ( Scene?.Camera.IsValid() != true ) return;
		var anchor = Scene.Camera.WorldPosition;
		var enableDistance = Titanfall2StreamingSettings.WorldShadowEnableDistance;
		var disableDistance = MathF.Max( enableDistance, Titanfall2StreamingSettings.WorldShadowDisableDistance );
		foreach ( var chunk in _chunks )
		{
			if ( !chunk.Renderer.IsValid() ) continue;
			var threshold = chunk.Casting ? disableDistance : enableDistance;
			var distance = threshold + chunk.Radius;
			var casting = chunk.Center.DistanceSquared( anchor ) <= distance * distance;
			if ( casting == chunk.Casting ) continue;
			chunk.Casting = casting;
			chunk.Renderer.RenderType = casting
				? ModelRenderer.ShadowRenderType.On
				: ModelRenderer.ShadowRenderType.Off;
		}
	}

	sealed class ShadowChunk( ModelRenderer renderer, Vector3 center, float radius, bool casting )
	{
		public ModelRenderer Renderer { get; } = renderer;
		public Vector3 Center { get; } = center;
		public float Radius { get; } = radius;
		public bool Casting { get; set; } = casting;
	}
}
