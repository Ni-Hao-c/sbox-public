using System.Diagnostics;

/// <summary>
/// Keeps all world chunks rendered while limiting which chunks enter the
/// directional-light shadow cascades. This avoids rebuilding or hiding geometry
/// as the player moves. World geometry can be one <see cref="ModelRenderer"/> per
/// cell or an <see cref="AggregateRenderer"/> per material, so both are tracked.
/// </summary>
[Library]
public sealed class Titanfall2WorldShadowController : Component, Component.DontExecuteOnServer, Sandbox.Internal.IUpdateSubscriber
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
			if ( renderer.IsValid() )
			{
				_chunks.Add( ShadowChunk.ForRenderer( renderer ) );
				continue;
			}

			// The default world path attaches one aggregate per material, not a
			// ModelRenderer per cell. Without this branch the distance gate below
			// silently had no targets and every aggregate kept its fixed flag.
			var aggregate = child.Components.Get<AggregateRenderer>();
			if ( aggregate.IsValid() ) _chunks.Add( ShadowChunk.ForAggregate( aggregate ) );
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
			if ( !chunk.IsValid ) continue;
			var threshold = chunk.Casting ? disableDistance : enableDistance;
			var distance = threshold + chunk.Radius;
			var casting = Titanfall2StreamingSettings.MapShadows
				&& (enableDistance <= 0f || chunk.Center.DistanceSquared( anchor ) <= distance * distance);
			if ( casting == chunk.Casting ) continue;
			chunk.Casting = casting;
			chunk.Apply( casting );
		}
	}

	sealed class ShadowChunk
	{
		readonly ModelRenderer _renderer;
		readonly AggregateRenderer _aggregate;

		ShadowChunk( ModelRenderer renderer, AggregateRenderer aggregate, Vector3 center, float radius, bool casting )
		{
			_renderer = renderer;
			_aggregate = aggregate;
			Center = center;
			Radius = radius;
			Casting = casting;
		}

		public static ShadowChunk ForRenderer( ModelRenderer renderer ) => new(
			renderer, null, renderer.Bounds.Center, renderer.Bounds.Size.Length * 0.5f,
			renderer.RenderType == ModelRenderer.ShadowRenderType.On );

		public static ShadowChunk ForAggregate( AggregateRenderer aggregate )
		{
			var bounds = aggregate.Model is { IsValid: true } model ? model.Bounds : default;
			return new ShadowChunk( null, aggregate, bounds.Center, bounds.Size.Length * 0.5f, aggregate.CastShadows );
		}

		public Vector3 Center { get; }
		public float Radius { get; }
		public bool Casting { get; set; }

		public bool IsValid => _renderer.IsValid() || _aggregate.IsValid();

		public void Apply( bool casting )
		{
			if ( _renderer.IsValid() )
				_renderer.RenderType = casting ? ModelRenderer.ShadowRenderType.On : ModelRenderer.ShadowRenderType.Off;
			if ( _aggregate.IsValid() )
				_aggregate.CastShadows = casting;
		}
	}
}
