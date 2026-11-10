using Titanfall2;

public sealed partial class Titanfall2StaticPropStreamer
{
	Titanfall2StaticPropGpuScene _gpuScene;
	int _mixedGpuInstanceCount;
	sealed record MixedGpuInstance( PropInstance Instance, Model Model, ulong OriginalMask, ulong ResidualMask );
	int FullGpuInstanceCount => Math.Max( 0, (_gpuScene?.InstanceCount ?? 0) - _mixedGpuInstanceCount );

	void BuildGpuStaticScene()
	{
		if ( Application.IsDedicatedServer || !Graphics.IsAvailable || !Titanfall2StreamingSettings.GpuStaticScene
			|| !Titanfall2GpuSceneCompatibility.Available ) return;
		var eligible = _clusters.Values.SelectMany( cluster => cluster.Batches.Values )
			.Where( batch => ModelLoader.TryGetGpuGeometry( batch.Model, out var geometry ) && !geometry.IsMixed ).ToArray();
		var mixed = new List<MixedGpuInstance>();
		foreach ( var cell in _cells.Values )
		{
			foreach ( var instance in cell.Instances )
			{
				if ( instance.IsBatched || !instance.RenderObject.IsValid() ) continue;
				var model = instance.RenderObject.Model;
				if ( ModelLoader.TryGetGpuGeometry( model, out var geometry ) && geometry.IsMixed )
					mixed.Add( new( instance, model, instance.RenderObject.MeshGroupMask, geometry.ResidualMask ) );
			}
		}
		if ( eligible.Length == 0 && mixed.Count == 0 ) return;
		try
		{
			var instances = new List<Titanfall2StaticPropGpuScene.Instance>();
			foreach ( var batch in eligible )
			{
				for ( var index = 0; index < batch.Sources.Count; ++index )
				{
					var source = batch.Sources[index];
					var distance = batch.Radii[index] >= LandmarkRadius ? float.PositiveInfinity
						: GetRenderDistance( source.FadeDistance ) + RenderDistanceHysteresis + batch.Radii[index];
					instances.Add( new( source.InstanceId, batch.Model, batch.Transforms[index], batch.Radii[index], distance,
						batch.ShadowedInstanceCount > 0, source.Probe, source.PvsCells ) );
				}
			}
			foreach ( var part in mixed )
			{
				var instance = part.Instance;
				var source = instance.Source;
				var distance = instance.Radius >= LandmarkRadius ? float.PositiveInfinity
					: GetRenderDistance( source.FadeDistance ) + RenderDistanceHysteresis + instance.Radius;
				instances.Add( new( source.InstanceId, part.Model, instance.RenderObject.Transform, instance.Radius, distance,
					instance.CastShadows, source.Probe, source.PvsCells ) );
			}
			var layout = Titanfall2StaticPropGpuScene.Compile( instances, _compiledScene.Props.Length );
			_gpuScene = new( Scene.SceneWorld, layout, BakedSkyColor, _localLights, ResolveGpuPvs );
			_gpuScene.RenderingEnabled = false;
			_gpuScene.UpdatePolicyCamera( FallbackAnchor );
			foreach ( var part in mixed ) part.Instance.RenderObject.MeshGroupMask = part.ResidualMask;
			_gpuScene.RenderingEnabled = Active;
			foreach ( var batch in eligible ) batch.GpuRendered = true;
			_mixedGpuInstanceCount = mixed.Count;
			Log.Info( $"Titanfall 2 GPU static scene: {instances.Count} instances, {eligible.Length} CPU batches replaced, "
				+ $"{layout.Materials.Length} material groups, {layout.Draws.Length} draw records; "
				+ $"{mixed.Count} mixed props retain only special mesh groups in their native objects; "
				+ "runtime CPU LOD grouping and transform uploads disabled for GPU meshes." );
		}
		catch ( Exception exception )
		{
			foreach ( var part in mixed )
				if ( part.Instance.RenderObject.IsValid() ) part.Instance.RenderObject.MeshGroupMask = part.OriginalMask;
			_mixedGpuInstanceCount = 0;
			_gpuScene?.Delete(); _gpuScene = null;
			Log.Warning( exception, "Titanfall 2 GPU static scene preparation failed; retaining CPU renderer." );
		}
	}

	Titanfall2BspReader.VisibilityCellMask ResolveGpuPvs( Vector3 position )
	{
		if ( _visibility is not { IsValid: true } || _compiledVisibility is null ) return default;
		var cell = _visibility.FindCell( new System.Numerics.Vector3( position.x, position.y, position.z ) );
		return _compiledVisibility[cell];
	}
}
