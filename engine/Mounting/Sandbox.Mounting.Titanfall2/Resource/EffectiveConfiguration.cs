/// <summary>Effective runtime configuration snapshots; no work in the frame loop.</summary>
static class Titanfall2EffectiveConfiguration
{
	internal static object Capture() => new
	{
		ModuleVersionId = typeof( Titanfall2Mount ).Assembly.ManifestModule.ModuleVersionId,
		ModuleLocation = typeof( Titanfall2Mount ).Assembly.Location,
		EngineVersionId = typeof( Scene ).Assembly.ManifestModule.ModuleVersionId,
		Quality = new
		{
			Titanfall2StreamingSettings.TextureMaxDimension,
			Titanfall2StreamingSettings.UseBspLightmaps,
			Titanfall2StreamingSettings.Lightmap16Bit,
			Titanfall2StreamingSettings.LightmapMips,
			Titanfall2StreamingSettings.PropRenderDistance,
			Titanfall2StreamingSettings.PropAuthoredFadeScale,
			Titanfall2StreamingSettings.PropLodDistanceScale,
			Titanfall2StreamingSettings.MapShadows,
			Titanfall2StreamingSettings.ShadowGeometry,
			Titanfall2StreamingSettings.ShadowCascadeCount,
			Titanfall2StreamingSettings.PropShadows,
			Titanfall2StreamingSettings.PropShadowDistance,
			Titanfall2StreamingSettings.NativeContactShadows,
			Titanfall2StreamingSettings.Godrays,
			Titanfall2StreamingSettings.BakedExposureCompensation
		},
		Backend = new
		{
			Titanfall2StreamingSettings.WorldMaterialTable,
			Titanfall2StreamingSettings.GpuStaticScene,
			Titanfall2StreamingSettings.GpuMixedProps,
			Titanfall2StreamingSettings.GpuStaticSceneBudgetMb,
			Titanfall2StreamingSettings.GpuStaticSceneViewSlots,
			Titanfall2StreamingSettings.WorldRenderCellSize,
			Titanfall2StreamingSettings.PropBatchClusterCells,
			Titanfall2StreamingSettings.ResidentWorld,
			Titanfall2StreamingSettings.ProcessedCache,
			Titanfall2StreamingSettings.FpsPreset
		}
	};
}
