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
}

public partial class Titanfall2Mount
{
	// A game restart destroys the old scene, runs frames for about 8 s (package install,
	// compile), then builds the next map. Release inside that gap, after the destroyed
	// scene's last frame.
	const int SceneReleaseDelayMilliseconds = 1000;
	// The engine's NativeResourceCache holds each recently fetched resource strongly for
	// one to two 5 s swaps. Collect again after each swap so those are freed too.
	static readonly int[] FollowUpCollectionDelaysMilliseconds = [5500, 11000];
	bool _sceneReleasePending;
	WeakReference<Scene> _mapBuildScene;

	/// <summary>
	/// Marks the temporary scene a map is being built in. Its destruction right after the
	/// build is part of loading the map, not leaving it.
	/// </summary>
	internal void BeginMapBuild( Scene buildScene ) => _mapBuildScene = buildScene is null ? null : new WeakReference<Scene>( buildScene );

	internal void OnSceneOwnerDestroyed( Scene scene )
	{
		if ( scene is not null && _mapBuildScene is not null && _mapBuildScene.TryGetTarget( out var building ) && ReferenceEquals( scene, building ) ) return;
		if ( !CanReleaseSceneResources() || _sceneReleasePending ) return;
		_sceneReleasePending = true;
		_ = ReleaseWhenUnusedAsync();
	}

	// The editor keeps its scenes outside Scene.All, so it cannot tell when a map is unused.
	static bool CanReleaseSceneResources() => !Application.IsEditor && !Application.IsDedicatedServer;

	static bool AnyLiveMapScene() => Scene.All.Any( static scene => scene.GetAllComponents<Titanfall2SceneResourceOwner>().Any() );

	async Task ReleaseWhenUnusedAsync()
	{
		try
		{
			await Task.Delay( SceneReleaseDelayMilliseconds );
			await MainThread.Wait();
			if ( IsMounted && !AnyLiveMapScene() ) ReleaseSceneResources( "map scene destroyed" );
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
	/// without a frame in between, so the delayed release never ran.
	/// </summary>
	internal void ReleaseSceneResourcesBeforeBuild( MapLoader building )
	{
		if ( !CanReleaseSceneResources() || AnyLiveMapScene() ) return;
		ReleaseSceneResources( "next map build", building );
	}

	/// <summary>
	/// Forgets every map-scoped cached resource. Anything still in use stays valid; the next
	/// map loads what it needs again, so assets it shares with the previous map are decoded
	/// a second time and a revisited map is rebuilt.
	/// </summary>
	void ReleaseSceneResources( string reason, ResourceLoader except = null )
	{
		var textures = 0;
		var materials = 0;
		var models = 0;
		var maps = 0;
		foreach ( var loader in Resources )
		{
			if ( ReferenceEquals( loader, except ) || loader is not ITitanfall2SceneResource resource || !resource.ReleaseSceneResource() ) continue;
			switch ( loader )
			{
				case MapLoader: maps++; break;
				case Titanfall2TextureLoader: textures++; break;
				case MaterialLoader or LegacyMaterialLoader: materials++; break;
				default: models++; break;
			}
		}
		if ( textures + materials + models + maps == 0 ) return;
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
