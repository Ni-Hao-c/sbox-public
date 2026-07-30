/// <summary>
/// Replays Titanfall's script_rotator/rotate_forever behavior for linked
/// prop_dynamic entities. This is visual transform animation only; it does not
/// create physics movers or submit changing geometry to NavMesh.
/// </summary>
[Library]
public sealed class Titanfall2RotatingMover : Component, Component.DontExecuteOnServer
{
	[Property, Hide]
	public Vector3 Pivot { get; set; }

	[Property, Hide]
	public Vector3 Axis { get; set; } = Vector3.Up;

	[Property, Hide]
	public float DegreesPerSecond { get; set; }

	[Property, Hide]
	public float StartDelay { get; set; }

	Transform _initialTransform;
	float _elapsed;
	float _angle;
	bool _captured;

	internal Titanfall2RotatingMover Configure(
		Vector3 pivot,
		Vector3 axis,
		float degreesPerSecond,
		float startDelay )
	{
		Pivot = pivot;
		Axis = axis.LengthSquared > float.Epsilon ? axis.Normal : Vector3.Up;
		DegreesPerSecond = degreesPerSecond;
		StartDelay = MathF.Max( 0f, startDelay );
		return this;
	}

	protected override void OnStart()
	{
		_initialTransform = GameObject.WorldTransform;
		_captured = true;
	}

	protected override void OnUpdate()
	{
		if ( !_captured )
		{
			_initialTransform = GameObject.WorldTransform;
			_captured = true;
		}

		_elapsed += Time.Delta;
		if ( _elapsed < StartDelay || MathF.Abs( DegreesPerSecond ) <= 0.001f ) return;

		_angle = MathF.IEEERemainder( _angle + DegreesPerSecond * Time.Delta, 360f );
		GameObject.WorldTransform = _initialTransform.RotateAround(
			Pivot,
			Rotation.FromAxis( Axis, _angle ) );
	}
}
