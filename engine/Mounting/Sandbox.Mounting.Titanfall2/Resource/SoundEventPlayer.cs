/// <summary>Plays an authored Miles event, including its own looping, layering and controller program.</summary>
[Library( "titanfall2_sound_event" ), Hide]
public sealed class Titanfall2SoundEventPlayer : Component, Component.DontExecuteOnServer, Sandbox.Internal.IUpdateSubscriber
{
	[Property] public string MountIdent { get; set; } = "titanfall2";
	[Property] public string EventName { get; set; }
	[Property] public string Language { get; set; }
	[Property] public bool PlayOnStart { get; set; } = true;
	[Property] public float Volume { get; set; } = 1f;
	Titanfall2SoundEventSession _session;
	Titanfall2SoundEventHandle _handle;
	protected override void OnStart() { if ( PlayOnStart ) Play(); }
	public bool Play()
	{
		if ( Application.IsHeadless || string.IsNullOrWhiteSpace( EventName ) ) return false;
		if ( Sandbox.Mounting.Directory.Get( MountIdent ) is not Titanfall2Mount mount || !mount.IsMounted ) return false;
		try
		{
			_session ??= mount.CreateSoundEventSession( Language );
		}
		catch ( Exception exception )
		{
			// A missing/blocked native Miles runtime must not take the component's
			// update loop down with it; report once per attempt and stay silent.
			Titanfall2Log.Warning( $"Titanfall 2 sound event runtime unavailable: {exception.Message}" );
			return false;
		}
		_handle?.Stop();
		_session.UpdateListener( Sound.Listener, Volume );
		_handle = _session.Play( EventName, WorldPosition );
		return _handle is not null;
	}
	public void Stop() { _handle?.Stop(); _handle = null; }
	public void SetController( string name, float value ) => _handle?.SetController( name, value );
	public void SetGlobalController( string name, float value ) => _session?.SetController( name, value );
	protected override void OnUpdate()
	{
		if ( _handle is not null ) _handle.Position = WorldPosition;
		_session?.UpdateListener( Sound.Listener, Volume );
	}
	protected override void OnDisabled() => Release();
	protected override void OnDestroy() => Release();
	void Release() { Stop(); _session?.Dispose(); _session = null; }
}
