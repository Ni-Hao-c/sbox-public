using System.Threading;

namespace Editor;

[DropObject( "sound", "sound", "sound_c", "vsnd" )]
partial class SoundDropObject : BaseDropObject
{
	SoundEvent sound;

	protected override async Task Initialize( string dragData, CancellationToken token )
	{
		Asset asset = await InstallAsset( dragData, token );

		if ( asset is null )
			return;

		if ( token.IsCancellationRequested )
			return;

		PackageStatus = "Loading Sound";
		// Mounted games expose decoded audio as SoundFile resources behind .vsnd.
		// A SoundPoint serializes a SoundEvent, so wrap the mounted SoundFile in an
		// embedded event instead of trying to load it as a local .sound asset.
		if ( Sandbox.Mounting.MountUtility.IsMountPath( asset.Path ) && asset.Path.EndsWith( ".vsnd", StringComparison.OrdinalIgnoreCase ) )
		{
			var soundFile = SoundFile.Load( asset.Path );
			if ( soundFile is not null )
			{
				sound = new SoundEvent
				{
					Sounds = [soundFile],
					DistanceAttenuation = true,
					Distance = 1024.0f,
					OcclusionEnabled = false,
					ReverbEnabled = false,
					EmbeddedResource = new Sandbox.Resources.EmbeddedResource { ResourceCompiler = "embed" }
				};
			}
		}
		else
		{
			sound = asset.LoadResource<SoundEvent>();
		}
		PackageStatus = null;
	}

	public override void OnUpdate()
	{
		using var scope = Gizmo.Scope( "DropObject", traceTransform );

		Gizmo.Draw.Color = Color.White;
		Gizmo.Draw.Sprite( Vector3.Zero, 28f * Gizmo.Settings.GizmoScale, "materials/gizmo/sound.png" );

		if ( !string.IsNullOrWhiteSpace( PackageStatus ) )
		{
			Gizmo.Draw.Text( PackageStatus, new Transform( Vector3.Up * 16f ), "Inter", 14 * Application.DpiScale );
		}
	}

	public override async Task OnDrop()
	{
		await WaitForLoad();

		if ( sound is null )
			return;

		using var scene = SceneEditorSession.Scope();

		using ( SceneEditorSession.Active.UndoScope( "Drop Sound Point" ).WithGameObjectCreations().Push() )
		{
			GameObject = new GameObject();
			GameObject.Name = sound.ResourceName;
			GameObject.WorldTransform = traceTransform;

			var component = GameObject.Components.GetOrCreate<SoundPointComponent>();
			component.SoundEvent = sound;

			EditorScene.Selection.Clear();
			EditorScene.Selection.Add( GameObject );
		}
	}
}
