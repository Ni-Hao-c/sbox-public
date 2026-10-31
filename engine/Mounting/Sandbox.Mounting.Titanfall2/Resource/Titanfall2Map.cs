using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Sandbox.Diagnostics;

/// <summary>
/// Puts a mounted Titanfall 2 map into a scene of your own, the way <see cref="MapInstance"/> does for
/// Hammer maps: the map's objects become unsaved children of this GameObject, so they show in the editor
/// without being written into the scene file and load again when the game starts.
///
/// Mounted scenes are read only, and pressing play on one plays the map as mounted, without anything
/// added to it in the editor. This component is how an ordinary scene gets the map next to its own
/// player, camera and game logic.
/// </summary>
[Library]
[Title( "Titanfall 2 Map" ), Category( "Titanfall 2" ), Icon( "map" )]
public sealed class Titanfall2Map : Component, Component.ExecuteInEditor, Sandbox.Internal.IUpdateSubscriber
{
	static readonly Logger Log = new( "Titanfall2Map" );

	/// <summary>
	/// The map's name, as in the game's VPKs: mp_angel_city, mp_homestead, mp_wargames...
	/// A trailing .bsp or .bsp.scene is accepted and stripped.
	/// </summary>
	[Property, Title( "Map" )]
	public string MapName { get; set; } = "mp_angel_city";

	/// <summary>
	/// Load the map in the editor's scene view too. Off by default: a mounted map preloads tens of
	/// thousands of static props, so loading it while merely editing a scene is slow.
	/// </summary>
	[Property, Title( "Load In Editor" )]
	public bool LoadInEditor { get; set; }

	/// <summary>Load world assets and resident collision without mount-owned gameplay objects or cameras.</summary>
	[Property, Title( "Pure World" )]
	public bool PureWorld { get; set; }

	/// <summary>
	/// Spawn a spectator camera over the first spawn point when the map loads, unless the scene
	/// already has a main camera of its own.
	/// </summary>
	[Property, Title( "Spawn Spectator Camera" )]
	public bool SpawnSpectatorCamera { get; set; } = true;

	/// <summary>
	/// How far above the spawn point the spectator camera starts.
	/// </summary>
	[Property, Title( "Camera Eye Height" ), Range( 0f, 256f )]
	public float CameraEyeHeight { get; set; } = 64f;

	/// <summary>The mounted scene this component loads.</summary>
	public string ScenePath
	{
		get
		{
			// OnUpdate polls this every frame. Rebuild the normalized path only when an input changes.
			if ( _scenePath is null || !ReferenceEquals( _scenePathMapName, MapName ) || _scenePathPureWorld != PureWorld )
			{
				_scenePathMapName = MapName;
				_scenePathPureWorld = PureWorld;
				_scenePath = $"mount://titanfall2/maps/{NormalizeMapName( MapName )}.bsp{(PureWorld ? ".world" : "")}.scene";
			}
			return _scenePath;
		}
	}

	string _loadedMap;
	string _missingMap;
	string _scenePath;
	string _scenePathMapName;
	bool _scenePathPureWorld;
	readonly List<GameObject> _mapObjects = new();

	/// <summary>
	/// Every map name the mounted Titanfall 2 content exposes, sorted for a picker.
	/// Empty when the mount isn't installed or mounted.
	/// </summary>
	public static IReadOnlyList<string> GetAvailableMaps()
	{
		var mount = Sandbox.Mounting.Directory.Get( "titanfall2" );
		if ( mount is null ) return Array.Empty<string>();

		return mount.GetAll( ResourceType.Scene )
			.Select( static loader => loader.RelativePath.Replace( '\\', '/' ) )
			.Where( static path => path.StartsWith( "maps/", StringComparison.OrdinalIgnoreCase )
				&& path.EndsWith( ".bsp.scene", StringComparison.OrdinalIgnoreCase ) )
			.Select( static path => path["maps/".Length..^".bsp.scene".Length] )
			.Where( static name => !string.IsNullOrWhiteSpace( name ) )
			.OrderBy( static name => name, StringComparer.OrdinalIgnoreCase )
			.ToArray();
	}

	protected override Task OnLoad()
	{
		if ( ShouldRun && Active )
			LoadMap();

		return Task.CompletedTask;
	}

	protected override void OnUpdate()
	{
		if ( !ShouldRun )
		{
			if ( _loadedMap is not null || _missingMap is not null ) UnloadMap();
			return;
		}

		// Reload when the inspector changes the map to another name. A name that isn't a map (one
		// being typed a letter at a time) keeps the map that is loaded instead of thrashing.
		if ( ScenePath != _loadedMap && ScenePath != _missingMap )
			LoadMap();
	}

	protected override void OnDisabled() => UnloadMap();

	protected override void OnDestroy() => UnloadMap();

	// The editor and play copy can coexist. Preview policy belongs to this scene,
	// not the application's play state.
	bool ShouldRun => Scene.IsEditor ? LoadInEditor : Game.IsPlaying;

	/// <summary>
	/// Loads the selected map as unsaved children of this GameObject.
	/// </summary>
	public void LoadMap()
	{
		if ( !ShouldRun ) return;

		var mapName = NormalizeMapName( MapName );
		if ( string.IsNullOrWhiteSpace( mapName ) )
		{
			UnloadMap();
			_missingMap = ScenePath;
			return;
		}

		SceneFile sceneFile;
		try
		{
			sceneFile = SceneFile.Load( ScenePath );
		}
		catch ( Exception exception )
		{
			_missingMap = ScenePath;
			Log.Warning( exception, $"{GameObject.Name}: couldn't load the Titanfall 2 map '{mapName}' ({ScenePath})." );
			return;
		}

		if ( sceneFile?.GameObjects is null )
		{
			_missingMap = ScenePath;
			Log.Warning( $"{GameObject.Name}: couldn't load the Titanfall 2 map '{mapName}' ({ScenePath}). "
				+ "Is the Titanfall 2 mount installed and mounted?" );
			return;
		}

		UnloadMap();
		_loadedMap = ScenePath;
		_missingMap = null;

		// The map's colliders would join that body and the whole map would move with it.
		if ( Components.GetInAncestorsOrSelf<Rigidbody>() is { } rigidbody )
			Log.Warning( $"{GameObject.Name}: Titanfall 2 Map is under '{rigidbody.GameObject.Name}', which has a Rigidbody, "
				+ "so the map will move with it. Put it on an empty GameObject." );

		var timer = System.Diagnostics.Stopwatch.StartNew();
		var failed = 0;
		// SceneFile is shared by every instance. Copy and remap the entire map at once
		// so cross-root references follow the new ids without changing the source resource.
		var roots = new JsonArray( sceneFile.GameObjects.Select( static json => json?.DeepClone() ).ToArray() );
		SceneUtility.MakeIdGuidsUnique( new JsonObject { ["GameObjects"] = roots } );
		using ( Scene.BatchGroup() )
		{
			foreach ( var json in roots )
			{
				GameObject gameObject = null;
				try
				{
					gameObject = new GameObject( false );
					gameObject.Flags |= GameObjectFlags.NotSaved;
					gameObject.SetParent( GameObject, false );
					_mapObjects.Add( gameObject );
					gameObject.Deserialize( json as JsonObject );
				}
				catch ( Exception exception )
				{
					_mapObjects.Remove( gameObject );
					if ( gameObject.IsValid() ) gameObject.DestroyImmediate();
					// One unregistered component shouldn't take the whole map, or the user's scene,
					// down with it; the rest of the map is still useful.
					failed++;
					Log.Warning( exception, $"{GameObject.Name}: couldn't deserialize a Titanfall 2 map object from '{ScenePath}'." );
				}
			}
		}

		Log.Info( $"{GameObject.Name}: Titanfall 2 map '{mapName}' loaded in {timer.Elapsed.TotalSeconds:0.00}s ({sceneFile.GameObjects.Length - failed}/{sceneFile.GameObjects.Length} top-level objects; {ScenePath})." );

		if ( !PureWorld && SpawnSpectatorCamera )
			CreateSpectatorCamera();
	}

	/// <summary>
	/// Immediately removes the map objects this component created.
	/// </summary>
	public void UnloadMap()
	{
		_loadedMap = null;
		_missingMap = null;

		// NotSaved is shared by other runtime systems; it is not an ownership marker.
		// Clear before callbacks run so a reentrant unload cannot destroy roots twice.
		var owned = _mapObjects.ToArray();
		_mapObjects.Clear();
		foreach ( var child in owned )
			if ( child.IsValid() ) child.DestroyImmediate();
	}

	void CreateSpectatorCamera()
	{
		if ( Scene.GetAllComponents<CameraComponent>().Any( static camera => camera.IsMainCamera ) )
			return;

		var spawn = Scene.GetAllComponents<SpawnPoint>().FirstOrDefault();
		var position = spawn?.WorldPosition ?? WorldPosition;
		var rotation = spawn?.WorldRotation ?? Rotation.Identity;

		var gameObject = new GameObject( true, "titanfall2_spectator_camera" );
		gameObject.Flags |= GameObjectFlags.NotSaved;
		gameObject.SetParent( GameObject, false );
		_mapObjects.Add( gameObject );
		gameObject.WorldPosition = position + Vector3.Up * CameraEyeHeight;
		gameObject.WorldRotation = rotation;

		var camera = gameObject.AddComponent<CameraComponent>();
		camera.IsMainCamera = true;
		camera.FieldOfView = 90f;
		camera.ZNear = 5f;
		camera.ZFar = 30000f;
		camera.BackgroundColor = Color.Black;

		gameObject.AddComponent<Titanfall2SpectatorCamera>();
	}

	/// <summary>mp_angel_city, mp_angel_city.bsp and maps/mp_angel_city.bsp.scene all name the same map.</summary>
	internal static string NormalizeMapName( string mapName )
	{
		if ( string.IsNullOrWhiteSpace( mapName ) ) return string.Empty;

		var name = mapName.Replace( '\\', '/' ).Trim();
		if ( name.StartsWith( "mount://titanfall2/maps/", StringComparison.OrdinalIgnoreCase ) )
			name = name["mount://titanfall2/maps/".Length..];
		if ( name.StartsWith( "maps/", StringComparison.OrdinalIgnoreCase ) )
			name = name["maps/".Length..];

		foreach ( var suffix in new[] { ".bsp.world.scene", ".bsp.scene", ".scene", ".bsp" } )
		{
			if ( name.EndsWith( suffix, StringComparison.OrdinalIgnoreCase ) )
				name = name[..^suffix.Length];
		}

		return name.Trim( '/' );
	}
}
