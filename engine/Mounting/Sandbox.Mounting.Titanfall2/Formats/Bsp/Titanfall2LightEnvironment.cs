using System.Globalization;
using System.Numerics;
using NumericsVector3 = System.Numerics.Vector3;
using NumericsVector4 = System.Numerics.Vector4;

namespace Titanfall2;

/// <summary>Authored environment indices and convex environment-volume selection.</summary>
public static class Titanfall2LightEnvironment
{
	public static Dictionary<string, string> Default( IReadOnlyList<Dictionary<string, string>> entities )
	{
		var lights = entities.Where( e => IsClass( e, "light_environment" ) ).ToArray();
		var local = entities.Where( e => IsClass( e, "light_environment_volume" ) )
			.Select( Index ).Where( i => i >= 0 ).ToHashSet();
		// A volume-bound light is local to that region (often the 3D sky),
		// regardless of how close its editor icon is to the combined BSP bounds.
		return lights.FirstOrDefault( e => !local.Contains( Index( e ) ) ) ?? lights.FirstOrDefault();
	}

	public static Dictionary<string, string> Select( IReadOnlyList<Dictionary<string, string>> entities, NumericsVector3 position )
	{
		foreach ( var volume in entities )
		{
			if ( !IsClass( volume, "light_environment_volume" ) || !Contains( volume, position ) ) continue;
			var index = Index( volume );
			var light = entities.FirstOrDefault( e => IsClass( e, "light_environment" ) && index >= 0 && Index( e ) == index );
			if ( light is not null ) return light;
		}
		return Default( entities );
	}

	public static int Index( IReadOnlyDictionary<string, string> entity ) =>
		entity.TryGetValue( "lightEnvironmentIndex", out var text ) && int.TryParse( text.TrimStart( '*' ), out var index ) ? index : -1;

	public static bool Contains( IReadOnlyDictionary<string, string> volume, NumericsVector3 position )
		=> new Volume( volume ).Contains( position );

	/// <summary>Preparsed volume; testing a moving camera allocates nothing.</summary>
	public sealed class Volume
	{
		readonly NumericsVector3 _origin, _mins, _maxs;
		readonly Quaternion _inverse = Quaternion.Identity;
		readonly NumericsVector4[][] _brushes = [];
		readonly bool _valid;
		public bool IsValid => _valid;
		public int EnvironmentIndex { get; }

		public Volume( IReadOnlyDictionary<string, string> volume )
		{
			EnvironmentIndex = Index( volume );
			if ( !Vector( volume, "origin", out _origin ) || !Vector( volume, "*trigger_bounds_mins", out _mins )
				|| !Vector( volume, "*trigger_bounds_maxs", out _maxs ) ) return;
			if ( Vector( volume, "angles", out var angles ) )
			{
				var rotation = Quaternion.CreateFromAxisAngle( NumericsVector3.UnitZ, angles.Y * MathF.PI / 180 )
					* Quaternion.CreateFromAxisAngle( NumericsVector3.UnitY, angles.X * MathF.PI / 180 )
					* Quaternion.CreateFromAxisAngle( NumericsVector3.UnitX, angles.Z * MathF.PI / 180 );
				_inverse = Quaternion.Conjugate( rotation );
			}
			var brushes = new Dictionary<string, List<NumericsVector4>>();
			foreach ( var (key, text) in volume )
			{
				if ( !key.StartsWith( "*trigger_brush_", StringComparison.Ordinal ) ) continue;
				var parts = key.Split( '_' );
				if ( parts.Length != 5 || parts[3] != "plane" ) continue;
				var numbers = Numbers( text );
				if ( numbers.Length != 4 ) return;
				if ( !brushes.TryGetValue( parts[2], out var planes ) ) brushes[parts[2]] = planes = [];
				planes.Add( new NumericsVector4( numbers[0], numbers[1], numbers[2], numbers[3] ) );
			}
			_brushes = brushes.Values.Select( planes => planes.ToArray() ).ToArray();
			_valid = true;
		}

		public bool Contains( NumericsVector3 position )
		{
			if ( !_valid ) return false;
			var local = NumericsVector3.Transform( position - _origin, _inverse );
			if ( !float.IsFinite( local.X ) || !float.IsFinite( local.Y ) || !float.IsFinite( local.Z )
				|| local.X < _mins.X || local.X > _maxs.X || local.Y < _mins.Y || local.Y > _maxs.Y || local.Z < _mins.Z || local.Z > _maxs.Z ) return false;
			if ( _brushes.Length == 0 ) return true;
			foreach ( var planes in _brushes )
			{
				var inside = true;
				foreach ( var p in planes )
					if ( local.X * p.X + local.Y * p.Y + local.Z * p.Z > p.W + 0.001f ) { inside = false; break; }
				if ( inside ) return true;
			}
			return false;
		}
	}

	static bool IsClass( IReadOnlyDictionary<string, string> entity, string name ) =>
		entity.TryGetValue( "classname", out var value ) && value.Equals( name, StringComparison.OrdinalIgnoreCase );

	static bool Vector( IReadOnlyDictionary<string, string> entity, string key, out NumericsVector3 value )
	{
		var numbers = entity.TryGetValue( key, out var text ) ? Numbers( text ) : [];
		value = numbers.Length == 3 ? new NumericsVector3( numbers[0], numbers[1], numbers[2] ) : default;
		return numbers.Length == 3;
	}

	static float[] Numbers( string text )
	{
		var parts = text.Split( (char[])null, StringSplitOptions.RemoveEmptyEntries );
		var result = new float[parts.Length];
		for ( var i = 0; i < parts.Length; ++i )
			if ( !float.TryParse( parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out result[i] ) || !float.IsFinite( result[i] ) ) return [];
		return result;
	}
}

