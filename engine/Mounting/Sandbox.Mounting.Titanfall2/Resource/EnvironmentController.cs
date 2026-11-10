using System.Text.Json;
using Titanfall2;

/// <summary>Keeps the camera's authored sun and sky environment out of unrelated regions.</summary>
[Library, Hide]
public sealed class Titanfall2EnvironmentController : Component, Component.ExecuteInEditor, Component.DontExecuteOnServer, Sandbox.Internal.IUpdateSubscriber
{
	[Property, Hide] public string EncodedEntities { get; set; }
	[Property, Hide] public string EncodedStates { get; set; }
	public int CurrentEnvironmentIndex { get; private set; } = -1;
	readonly Dictionary<int, float[]> _states = [];
	Titanfall2LightEnvironment.Volume[] _volumes = [];
	int _defaultIndex;
	DirectionalLight _sun;

	internal void Configure( IReadOnlyList<Dictionary<string, string>> entities, float[][] states )
	{
		_states.Clear();
		_volumes = [];
		_sun = null;
		CurrentEnvironmentIndex = -1;
		_defaultIndex = -1;
		Scene.RenderAttributes.Set( "T2EnvironmentEnabled", 0 );
		EncodedEntities = JsonSerializer.Serialize( entities );
		EncodedStates = JsonSerializer.Serialize( states );
		Restore();
	}

	protected override Task OnLoad( LoadingContext context ) { Restore(); return Task.CompletedTask; }
	protected override void OnEnabled()
	{
		Restore();
		Apply( CurrentEnvironmentIndex >= 0 ? CurrentEnvironmentIndex : _defaultIndex );
	}

	void Restore()
	{
		if ( _states.Count > 0 || string.IsNullOrEmpty( EncodedEntities ) || string.IsNullOrEmpty( EncodedStates ) ) return;
		var entities = JsonSerializer.Deserialize<Dictionary<string, string>[]>( EncodedEntities ) ?? [];
		var states = JsonSerializer.Deserialize<float[][]>( EncodedStates ) ?? [];
		// -1 is the serialized import baseline, independent of authored environment IDs.
		foreach ( var state in states )
			if ( state is { Length: 10 } && state.All( float.IsFinite ) && state[0] >= -1 ) _states[(int)state[0]] = state;
		var fallback = Titanfall2LightEnvironment.Default( entities );
		_defaultIndex = fallback is null ? -1 : Titanfall2LightEnvironment.Index( fallback );
		if ( !_states.ContainsKey( _defaultIndex ) ) _defaultIndex = -1;
		_volumes = entities.Where( e => e.GetValueOrDefault( "classname" ) == "light_environment_volume" )
			.Select( e => new Titanfall2LightEnvironment.Volume( e ) ).ToArray();
		_sun = Components.Get<DirectionalLight>();
		Apply( _defaultIndex );
	}

	protected override void OnUpdate()
	{
		if ( _states.Count == 0 || !_sun.IsValid() ) return;
		var index = _defaultIndex;
		if ( Scene.Camera.IsValid() )
		{
			var p = Scene.Camera.WorldPosition;
			var position = new System.Numerics.Vector3( p.x, p.y, p.z );
			foreach ( var volume in _volumes )
			{
				if ( volume.EnvironmentIndex < 0 || !_states.ContainsKey( volume.EnvironmentIndex ) || !volume.Contains( position ) ) continue;
				index = volume.EnvironmentIndex;
				break;
			}
		}
		if ( index != CurrentEnvironmentIndex ) Apply( index );
	}

	void Apply( int index )
	{
		if ( !_sun.IsValid() ) return;
		float[] state;
		if ( index == _defaultIndex && _states.TryGetValue( -1, out var baseline ) ) state = baseline;
		else if ( !_states.TryGetValue( index, out state ) ) return;
		CurrentEnvironmentIndex = index;
		_sun.LightColor = new Color( state[1], state[2], state[3] );
		var direction = new Vector3( state[4], state[5], state[6] );
		if ( direction.LengthSquared > 0.000001f ) _sun.WorldRotation = Rotation.LookAt( direction );
		_sun.SkyColor = new Color( state[7], state[8], state[9] );
		Scene.RenderAttributes.Set( "T2EnvironmentSkyColor", new Vector3( state[7], state[8], state[9] ) );
		Scene.RenderAttributes.Set( "T2EnvironmentEnabled", 1 );
	}

	protected override void OnDisabled()
	{
		Apply( _defaultIndex );
		Scene.RenderAttributes.Set( "T2EnvironmentEnabled", 0 );
	}
}
