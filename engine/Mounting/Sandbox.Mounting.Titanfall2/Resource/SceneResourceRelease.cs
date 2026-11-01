/// <summary>
/// A loader whose cached result belongs to the maps that used it, not to the mount. The
/// base <see cref="Sandbox.Mounting.ResourceLoader"/> keeps every result for the mount's
/// lifetime, so each visited map's textures, materials and meshes stayed in video memory:
/// sp_training, then sp_timeshift_spoke02, then sp_tday ran a 4 GB GPU out of memory.
/// </summary>
interface ITitanfall2SceneResource
{
	/// <summary>Drops cached references only. Never destroys: live users keep the object.</summary>
	bool ReleaseSceneResource();

	/// <summary>
	/// True when this loader cannot be released right now - a load is in flight - and the
	/// release has to be attempted again.
	/// </summary>
	bool IsReleaseDeferred => false;
}

public partial class Titanfall2Mount
{
	// Settle time between the last map scene going away and the release. The lease count,
	// not this delay, decides whether a release happens; the delay only lets the destroyed
	// scene finish tearing down (component OnDestroy, queued native disposes) first.
	const int SceneReleaseSettleMilliseconds = 100;
	// Retries while a loader is mid-load, at the settle interval above (about 5 s total).
	const int SceneReleaseRetryLimit = 50;
	// The engine's NativeResourceCache holds each recently fetched resource strongly for
	// one to two 5 s swaps. Collect again after each swap so those are freed too.
	static readonly int[] FollowUpCollectionDelaysMilliseconds = [5500, 11000];
	// One lease per live map scene, taken by Titanfall2SceneResourceOwner. Reaching zero is
	// what triggers a release, instead of polling Scene.All on a blind timer.
	int _mapSceneLeases;
	bool _sceneReleasePending;
	WeakReference<Scene> _mapBuildScene;

	/// <summary>
	/// Marks the temporary scene a map is being built in. Its destruction right after the
	/// build is part of loading the map, not leaving it.
	/// </summary>
	internal void BeginMapBuild( Scene buildScene ) => _mapBuildScene = buildScene is null ? null : new WeakReference<Scene>( buildScene );

	/// <summary>Taken while a scene shows a mounted map; see <see cref="_mapSceneLeases"/>.</summary>
	internal void AddMapSceneLease() => _mapSceneLeases++;

	internal void OnSceneOwnerDestroyed( Scene scene )
	{
		if ( _mapSceneLeases > 0 ) _mapSceneLeases--;
		if ( scene is not null && _mapBuildScene is not null && _mapBuildScene.TryGetTarget( out var building ) && ReferenceEquals( scene, building ) ) return;
		if ( _mapSceneLeases > 0 ) return;
		if ( !CanReleaseSceneResources() || _sceneReleasePending ) return;
		_sceneReleasePending = true;
		_ = ReleaseWhenUnusedAsync();
	}

	// The editor keeps its scenes outside Scene.All and reloads them for undo and previews,
	// so a release there would fight the editor rather than save memory. A dedicated server
	// has no scene resource owners at all (the component does not run on servers), so its
	// releases happen in ReleaseSceneResourcesBeforeBuild during map rotation.
	static bool CanReleaseSceneResources() => !Application.IsEditor;

	static bool AnyLiveMapScene() => Scene.All.Any( static scene => scene.GetAllComponents<Titanfall2SceneResourceOwner>().Any() );

	async Task ReleaseWhenUnusedAsync()
	{
		try
		{
			// Bounded so a load that never completes cannot keep this retrying forever; the
			// next map build releases whatever is left anyway.
			for ( var attempt = 0; attempt < SceneReleaseRetryLimit; attempt++ )
			{
				await Task.Delay( SceneReleaseSettleMilliseconds );
				await MainThread.Wait();
				if ( !IsMounted || _mapSceneLeases > 0 || AnyLiveMapScene() ) return;
				// A loader still finishing its load cannot be torn down; try again once it
				// has settled rather than leaving its map cached until the next build.
				if ( !ReleaseSceneResources( "map scene destroyed" ) ) return;
			}
		}
		catch ( Exception exception )
		{
			Log.Warning( exception, $"Titanfall 2 scene resource release failed: {exception.Message}" );
		}
		finally
		{
			_sceneReleasePending = false;
		}
	}

	/// <summary>
	/// Called before a map build. Covers a map change whose previous scene was destroyed
	/// without a frame in between, and is the only release path on a dedicated server.
	/// </summary>
	internal void ReleaseSceneResourcesBeforeBuild( MapLoader building )
	{
		if ( !CanReleaseSceneResources() || _mapSceneLeases > 0 || AnyLiveMapScene() ) return;
		ReleaseSceneResources( "next map build", building );
	}

	/// <summary>
	/// Forgets every map-scoped cached resource. Anything still in use stays valid; the next
	/// map loads what it needs again, so assets it shares with the previous map are decoded
	/// a second time and a revisited map is rebuilt.
	/// </summary>
	/// <returns>True when a loader refused because it is still loading, so the caller retries.</returns>
	bool ReleaseSceneResources( string reason, ResourceLoader except = null )
	{
		var textures = 0;
		var materials = 0;
		var models = 0;
		var maps = 0;
		var deferred = 0;
		foreach ( var loader in Resources )
		{
			if ( ReferenceEquals( loader, except ) || loader is not ITitanfall2SceneResource resource ) continue;
			if ( !resource.ReleaseSceneResource() )
			{
				if ( resource.IsReleaseDeferred ) deferred++;
				continue;
			}
			switch ( loader )
			{
				case MapLoader: maps++; break;
				case Titanfall2TextureLoader: textures++; break;
				case MaterialLoader or LegacyMaterialLoader: materials++; break;
				default: models++; break;
			}
		}
		if ( textures + materials + models + maps == 0 ) return deferred > 0;
		var brushSources = 0;
		foreach ( var source in _brushModelSources )
			if ( source.Release() ) brushSources++;
		ReleaseMountCaches();
		var before = GC.GetTotalMemory( false );
		Collect();
		Log.Info( $"Titanfall 2 scene resources released ({reason}): {textures} textures, {materials} materials, {models} models, "
			+ $"{maps} map builds, {brushSources} brush model sources; managed heap {before / (1024.0 * 1024.0):0} -> {GC.GetTotalMemory( false ) / (1024.0 * 1024.0):0} MB." );
		_ = Task.Run( static async () =>
		{
			var elapsed = 0;
			foreach ( var delay in FollowUpCollectionDelaysMilliseconds )
			{
				await Task.Delay( delay - elapsed );
				elapsed = delay;
				Collect();
			}
		} );
		return deferred > 0;
	}

	// Finalizers of the released wrappers queue their native handle releases on the main
	// thread, which drains them at the end of the current frame.
	static void Collect()
	{
		GC.Collect();
		GC.WaitForPendingFinalizers();
		GC.Collect();
	}
}
