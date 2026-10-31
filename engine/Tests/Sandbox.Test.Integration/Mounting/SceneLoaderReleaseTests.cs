using Sandbox.Mounting;
using System;

namespace MountingTests;

/// <summary>
/// Regression tests for <see cref="SceneLoader{T}.ReleaseCachedScene"/>. A mount that drops
/// its built scene (and the generated resources the scene names) must not let the resource
/// library serve the stale <see cref="SceneFile"/> on the next open; otherwise the map
/// reopens with error models. Titanfall 2's <c>MapLoader.ReleaseSceneResource</c> follows
/// the same sequence: drop the cached result, destroy the cached scene, dispose the build.
/// </summary>
[TestClass]
public class SceneLoaderReleaseTests
{
	Configuration Config => new();

	sealed class CountingSceneLoader : SceneLoader<TestGameMount>
	{
		public int Builds { get; private set; }

		protected override void BuildScene()
		{
			Builds++;
			_ = new GameObject( true, $"release_test_root_{Builds}" );
		}

		public bool Release()
		{
			var releasedCache = ReleaseCachedResult();
			var releasedScene = ReleaseCachedScene();
			return releasedCache || releasedScene;
		}
	}

	async Task<(MountHost Host, CountingSceneLoader Loader)> MountCountingScene()
	{
		var host = new MountHost( Config );
		host.RegisterTypes( GetType().Assembly );
		await host.Mount( "testgame" );

		var loader = new CountingSceneLoader();
		loader.InitializeInternal( ResourceType.Scene, $"/maps/release_test_{Guid.NewGuid():N}.scene", host.GetSource( "testgame" ) );

		return (host, loader);
	}

	[TestMethod]
	public async Task ReleasingCachedSceneMakesTheNextOpenRebuild()
	{
		var (host, loader) = await MountCountingScene();
		using ( host )
		{
			var first = await loader.GetOrCreate() as SceneFile;
			Assert.IsNotNull( first, "SceneLoader didn't produce a SceneFile" );
			Assert.AreEqual( 1, loader.Builds );
			Assert.AreSame( first, ResourceLibrary.Get<SceneFile>( loader.Path ), "The built scene is registered under its mount path" );

			Assert.IsTrue( loader.Release(), "Release must report that it dropped the cached scene" );

			// This is the regression: without destroying the cached SceneFile the resource
			// library would keep serving it, and the next open would deserialize a scene
			// whose generated models/lightmaps the release path just disposed.
			Assert.IsNull( ResourceLibrary.Get<SceneFile>( loader.Path ), "A released scene must not stay in the resource library" );

			var rebuilt = await loader.GetOrCreate() as SceneFile;
			Assert.IsNotNull( rebuilt, "Opening the map again must rebuild it" );
			Assert.AreNotSame( first, rebuilt, "Reopening after release must not reuse the dropped SceneFile" );
			Assert.AreEqual( 2, loader.Builds, "Reopening must run BuildScene again" );
			Assert.AreSame( rebuilt, ResourceLibrary.Get<SceneFile>( loader.Path ), "The rebuilt scene is registered again" );
		}
	}

	[TestMethod]
	public async Task ReleasingWithoutACachedSceneReportsNothing()
	{
		var (host, loader) = await MountCountingScene();
		using ( host )
		{
			Assert.IsFalse( loader.Release(), "There is nothing to release before the first load" );

			var scene = await loader.GetOrCreate() as SceneFile;
			Assert.IsNotNull( scene );
			Assert.AreEqual( 1, loader.Builds );
		}
	}
}
