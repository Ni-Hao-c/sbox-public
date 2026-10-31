using System;

namespace SceneTests;

[TestClass]
[DoNotParallelize]
public class Titanfall2MapLifecycleTests : SceneTest
{
	sealed class EditorTestScene() : Scene( true );
	Scene _scene;
	SceneFile _file;

	[TestCleanup]
	public void Cleanup()
	{
		_scene?.Destroy();
		if ( _file is not null ) Game.Resources.Unregister( _file );
	}

	Titanfall2Map CreateMap( bool editor = false, bool malformedObject = false )
	{
		_scene = editor ? new EditorTestScene() : new Scene();
		using var scope = _scene.Push();
		Game.TypeLibrary.AddAssembly( typeof( Titanfall2Map ).Assembly, false );
		var template = _scene.CreateObject();
		template.Name = "owned_map_root";
		var json = template.Serialize();
		template.DestroyImmediate();
		_file = new SceneFile { GameObjects = malformedObject ? [null, json] : [json] };
		var name = $"lifecycle_test_{Guid.NewGuid():N}";
		_file.Register( $"mount://titanfall2/maps/{name}.bsp.scene" );
		var map = _scene.CreateObject().Components.Create<Titanfall2Map>();
		map.MapName = name;
		map.SpawnSpectatorCamera = false;
		return map;
	}

	[DataTestMethod]
	[DataRow( false )]
	[DataRow( true )]
	public void UnloadingOnlyRemovesObjectsCreatedByThisMap( bool disable )
	{
		var map = CreateMap();
		using var scope = _scene.Push();
		map.LoadMap();
		var owned = map.GameObject.Children.Single();
		var unrelated = new GameObject( map.GameObject, true, "host_runtime_object" );
		unrelated.Flags |= GameObjectFlags.NotSaved;
		var saved = new GameObject( map.GameObject, true, "host_saved_object" );
		if ( disable ) map.Enabled = false;
		else map.UnloadMap();
		Assert.IsFalse( owned.IsValid );
		Assert.IsTrue( unrelated.IsValid, "NotSaved does not mean this map owns the object." );
		Assert.IsTrue( saved.IsValid );
		map.UnloadMap();
		Assert.AreEqual( 2, map.GameObject.Children.Count, "Repeated unload must leave host objects intact." );
	}

	[TestMethod]
	public void ReloadKeepsHostObjectsAndReplacesMapRootsAndCamera()
	{
		var map = CreateMap();
		using var scope = _scene.Push();
		map.SpawnSpectatorCamera = true;
		map.LoadMap();
		var first = map.GameObject.Children.ToArray();
		Assert.AreEqual( 2, first.Length );
		Assert.IsTrue( first.Any( child => child.Components.Get<CameraComponent>() is not null ) );
		var host = new GameObject( map.GameObject, true, "host_runtime_object" );
		host.Flags |= GameObjectFlags.NotSaved;
		map.LoadMap();
		Assert.IsTrue( first.All( child => !child.IsValid ) );
		Assert.IsTrue( host.IsValid );
		Assert.AreEqual( 3, map.GameObject.Children.Count );
		Assert.AreEqual( 1, _scene.GetAllComponents<CameraComponent>().Count() );
	}

	[TestMethod]
	public void MalformedRootDoesNotLeavePartialGameObject()
	{
		var map = CreateMap( malformedObject: true );
		using var scope = _scene.Push();
		map.LoadMap();
		Assert.AreEqual( 1, map.GameObject.Children.Count, "The failed deserialization must destroy its partial root." );
		Assert.AreEqual( "owned_map_root", map.GameObject.Children.Single().Name );
	}

	[TestMethod]
	public void EditorPreviewLoadsAndUnloadsThroughActualEditorTicks()
	{
		var map = CreateMap( editor: true );
		using var scope = _scene.Push();
		_scene.EditorTick( 0.1f, 0.1f );
		Assert.AreEqual( 0, map.GameObject.Children.Count );
		map.LoadInEditor = true;
		_scene.EditorTick( 0.2f, 0.1f );
		Assert.AreEqual( 1, map.GameObject.Children.Count, "Editor preview must subscribe to the editor's update dispatch." );
		var owned = map.GameObject.Children.Single();
		var host = new GameObject( map.GameObject, true, "host_runtime_object" );
		host.Flags |= GameObjectFlags.NotSaved;
		map.LoadInEditor = false;
		_scene.EditorTick( 0.3f, 0.1f );
		Assert.IsFalse( owned.IsValid );
		Assert.IsTrue( host.IsValid );
		map.LoadInEditor = true;
		_scene.EditorTick( 0.4f, 0.1f );
		Assert.AreEqual( 2, map.GameObject.Children.Count );
	}

	[TestMethod]
	public void EditorPreviewCanRetryMissingMapAfterBeingTurnedOff()
	{
		var map = CreateMap( editor: true );
		using var scope = _scene.Push();
		Game.Resources.Unregister( _file );
		map.LoadInEditor = true;
		_scene.EditorTick( 0.1f, 0.1f );
		Assert.AreEqual( 0, map.GameObject.Children.Count );
		map.LoadInEditor = false;
		_scene.EditorTick( 0.2f, 0.1f );
		_file.Register( map.ScenePath );
		map.LoadInEditor = true;
		_scene.EditorTick( 0.3f, 0.1f );
		Assert.AreEqual( 1, map.GameObject.Children.Count, "A previous miss must not suppress a new preview session." );
	}

	[TestMethod]
	public void RemovingMapComponentPreservesHostAndItsRuntimeChildren()
	{
		var map = CreateMap();
		using var scope = _scene.Push();
		map.LoadMap();
		var root = map.GameObject;
		var owned = root.Children.Single();
		var host = new GameObject( root, true, "host_runtime_object" );
		host.Flags |= GameObjectFlags.NotSaved;
		map.Destroy();
		Assert.IsFalse( owned.IsValid );
		Assert.IsTrue( root.IsValid );
		Assert.IsTrue( host.IsValid );
	}

	[TestMethod]
	public void CloningMapHostDoesNotShareOwnershipOfGeneratedRoots()
	{
		var map = CreateMap();
		using var scope = _scene.Push();
		map.LoadMap();
		var owned = map.GameObject.Children.Single();
		var clone = map.GameObject.Clone().Components.Get<Titanfall2Map>();
		Assert.AreEqual( 1, clone.GameObject.Children.Count );
		var clonedRoot = clone.GameObject.Children.Single();
		Assert.AreNotSame( owned, clonedRoot );
		Assert.AreNotEqual( owned.Id, clonedRoot.Id );
		Assert.AreSame( owned, _scene.Directory.FindByGuid( owned.Id ) );
		clone.UnloadMap();
		Assert.IsTrue( owned.IsValid );
		Assert.IsFalse( clonedRoot.IsValid );
		map.UnloadMap();
		Assert.IsFalse( owned.IsValid );
	}

	[TestMethod]
	public void UnloadAlsoReleasesOwnedRootsMovedOutOfTheHost()
	{
		var map = CreateMap();
		using var scope = _scene.Push();
		map.LoadMap();
		var owned = map.GameObject.Children.Single();
		owned.SetParent( _scene, true );
		map.UnloadMap();
		Assert.IsFalse( owned.IsValid, "Ownership must not be lost when the hierarchy changes." );
	}

	[DataTestMethod]
	[DataRow( false )]
	[DataRow( true )]
	public void MultipleInstancesKeepCrossRootReferencesWithinTheirOwnMap( bool targetFirst )
	{
		var map = CreateMap();
		using var scope = _scene.Push();
		var source = _scene.CreateObject( false );
		source.Name = "source";
		var target = _scene.CreateObject( false );
		target.Name = "target";
		source.Components.Create<CameraComponent>().PostProcessAnchor = target;
		source.Components.Create<Titanfall2WorldReady>().WorldCollision = target.Components.Create<ModelCollider>();
		var sourceJson = source.Serialize();
		var targetJson = target.Serialize();
		source.DestroyImmediate();
		target.DestroyImmediate();
		_file.GameObjects = targetFirst ? [targetJson, sourceJson] : [sourceJson, targetJson];
		var originalJson = sourceJson.ToJsonString();
		map.LoadMap();
		var secondMap = _scene.CreateObject().Components.Create<Titanfall2Map>();
		secondMap.MapName = map.MapName;
		secondMap.SpawnSpectatorCamera = false;
		secondMap.LoadMap();
		foreach ( var instance in new[] { map, secondMap } )
		{
			var loadedSource = instance.GameObject.Children.Single( child => child.Name == "source" );
			var loadedTarget = instance.GameObject.Children.Single( child => child.Name == "target" );
			Assert.AreSame( loadedTarget, loadedSource.Components.Get<CameraComponent>( true ).PostProcessAnchor,
				"Object references must resolve inside this instance, including forward references." );
			Assert.AreSame( loadedTarget.Components.Get<ModelCollider>( true ), loadedSource.Components.Get<Titanfall2WorldReady>( true ).WorldCollision,
				"Component references must resolve inside this instance." );
		}
		Assert.AreEqual( originalJson, sourceJson.ToJsonString(), "Instancing must not mutate the shared SceneFile JSON." );
	}
}
