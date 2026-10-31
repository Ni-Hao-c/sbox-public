using NumericsVector3 = System.Numerics.Vector3;

namespace Titanfall2;

/// <summary>Retail fog controller fallback and priority-ordered convex fog volumes.</summary>
public sealed class Titanfall2FogEnvironment
{
	readonly Dictionary<string, string> _fallback;
	readonly (Titanfall2LightEnvironment.Volume Shape, Dictionary<string, string> Controller)[] _volumes;

	public Titanfall2FogEnvironment( IReadOnlyList<Dictionary<string, string>> entities )
	{
		var controllers = entities.Where( e => e.GetValueOrDefault( "classname" ) == "env_fog_controller" ).ToArray();
		// CFogControllerManager keeps the first controller unless a later one has
		// spawnflags bit 0. Disabled fog is still selectable: it clears the fog.
		foreach ( var controller in controllers )
			if ( _fallback is null || (int.TryParse( controller.GetValueOrDefault( "spawnflags" ), out var flags ) && (flags & 1) != 0) ) _fallback = controller;
		_volumes = entities.Where( e => e.GetValueOrDefault( "classname" ) == "fog_volume" )
			.OrderByDescending( e => int.TryParse( e.GetValueOrDefault( "priority" ), out var priority ) ? priority : 0 )
			.Select( e => (new Titanfall2LightEnvironment.Volume( e ), controllers.FirstOrDefault( c =>
				string.Equals( c.GetValueOrDefault( "targetname" ), e.GetValueOrDefault( "target" ), StringComparison.OrdinalIgnoreCase )
				&& !string.IsNullOrEmpty( e.GetValueOrDefault( "target" ) ) )) )
			.Where( entry => entry.Item2 is not null ).ToArray();
	}

	public Dictionary<string, string> Select( NumericsVector3 position )
	{
		foreach ( var (shape, controller) in _volumes )
			if ( shape.Contains( position ) ) return controller;
		return _fallback;
	}
}
