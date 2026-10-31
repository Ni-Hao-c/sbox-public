using Titanfall2;

/// <summary>Processed-cache identity of a map's BSP source, used to key per-map cache entries.</summary>
static class Titanfall2MapCache
{
	internal static string Identity( Titanfall2Mount host, ITitanfall2AssetSource source ) =>
		$"{host.ContentFingerprint}|{source.CacheIdentity}|bsp-default-cm-index21-fog-v3";
}
