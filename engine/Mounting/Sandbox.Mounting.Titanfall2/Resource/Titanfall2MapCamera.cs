using Sandbox.Diagnostics;

/// <summary>
/// Gives a mounted map scene a camera when it is played on its own. Mounted scenes are read
/// only: pressing Play on one reloads the scene from the mount, so a camera added in the editor
/// is not part of the played scene. This component is part of the mounted scene itself, so it
/// survives that reload and creates a spectator camera over the first spawn point when no main
/// camera exists.
/// </summary>
[Library]
[Title( "Titanfall 2 Map Camera" ), Category( "Titanfall 2" ), Icon( "videocam" )]
public sealed class Titanfall2MapCamera : Component
{
	static readonly Logger Log = new( "Titanfall2MapCamera" );

	/// <summary>Create the fallback camera only when the scene has no active main camera.</summary>
	[Property, Title( "Create If Missing" )]
	public bool CreateIfMissing { get; set; } = true;

	/// <summary>How far above the spawn point the camera starts.</summary>
	[Property, Title( "Eye Height" ), Range( 0f, 256f )]
	public float EyeHeight { get; set; } = 64f;

	protected override void OnStart()
	{
		if ( !Game.IsPlaying || !CreateIfMissing )
			return;

		// A host scene (Titanfall2Map) or a game mode may already have a camera. Never replace it.
		if ( Scene.GetAllComponents<CameraComponent>().Any( static camera =>
			camera.IsMainCamera && camera.Enabled && camera.GameObject.Active ) )
			return;

		var spawn = Scene.GetAllComponents<SpawnPoint>().FirstOrDefault();
		var position = spawn?.WorldPosition ?? WorldPosition;
		var rotation = spawn?.WorldRotation ?? Rotation.Identity;

		var cameraObject = new GameObject( true, "titanfall2_map_camera_view" );
		cameraObject.Flags |= GameObjectFlags.NotSaved;
		cameraObject.SetParent( GameObject, false );
		cameraObject.WorldPosition = position + Vector3.Up * EyeHeight;
		cameraObject.WorldRotation = rotation;

		var camera = cameraObject.AddComponent<CameraComponent>();
		camera.IsMainCamera = true;
		camera.FieldOfView = 90f;
		camera.ZNear = 5f;
		camera.ZFar = 30000f;
		camera.BackgroundColor = Color.Black;

		cameraObject.AddComponent<Titanfall2SpectatorCamera>();
		Log.Info( $"Titanfall 2 map camera created at {cameraObject.WorldPosition} (no main camera in the played scene)." );
	}
}
