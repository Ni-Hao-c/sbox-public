/// <summary>
/// A free-flying spectator camera for mounted Titanfall 2 maps. WASD moves, Space/Ctrl go up and
/// down, holding Run moves faster, and the mouse looks around. It exists so a map can be inspected
/// before the mount has a player controller of its own.
/// </summary>
[Library]
[Title( "Titanfall 2 Spectator Camera" ), Category( "Titanfall 2" ), Icon( "videocam" )]
public sealed class Titanfall2SpectatorCamera : Component, Sandbox.Internal.IUpdateSubscriber
{
	[Property, Title( "Move Speed" ), Range( 1f, 10000f )]
	public float MoveSpeed { get; set; } = 600f;

	[Property, Title( "Sprint Speed" ), Range( 1f, 100000f )]
	public float SprintSpeed { get; set; } = 2400f;

	[Property, Title( "Look Sensitivity" ), Range( 0.05f, 5f )]
	public float LookSensitivity { get; set; } = 1f;

	Angles _angles;

	protected override void OnStart()
	{
		_angles = WorldRotation.Angles();
	}

	protected override void OnUpdate()
	{
		if ( !Game.IsPlaying )
			return;

		_angles += Input.AnalogLook * LookSensitivity;
		_angles.pitch = Math.Clamp( _angles.pitch, -89f, 89f );
		_angles.roll = 0f;
		WorldRotation = _angles.ToRotation();

		var wish = WorldRotation * Input.AnalogMove;
		if ( Input.Down( "Jump" ) ) wish += Vector3.Up;
		if ( Input.Down( "Duck" ) ) wish += Vector3.Down;
		if ( wish.LengthSquared > 1f ) wish = wish.Normal;

		var speed = Input.Down( "Run" ) ? SprintSpeed : MoveSpeed;
		WorldPosition += wish * speed * Time.Delta;
	}
}
