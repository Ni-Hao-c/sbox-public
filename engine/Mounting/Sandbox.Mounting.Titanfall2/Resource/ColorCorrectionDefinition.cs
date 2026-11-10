using System.Globalization;
using Titanfall2;

/// <summary>Preparsed authored correction. Spatial evaluation allocates nothing per view.</summary>
sealed class Titanfall2ColorCorrectionDefinition
{
	internal readonly string Name, TexturePath;
	internal readonly float MaximumWeight, FadeIn, FadeOut;
	internal readonly bool IsBrush;
	readonly Vector3 _origin;
	readonly float _minimum, _maximum;
	readonly bool _validOrigin;
	readonly Titanfall2LightEnvironment.Volume _shape;

	internal Titanfall2ColorCorrectionDefinition( string mountIdent, Dictionary<string, string> entity )
	{
		Name = entity.GetValueOrDefault( "targetname", "color_correction" );
		var filename = entity.GetValueOrDefault( "filename", string.Empty ).Replace( '\\', '/' ).TrimStart( '/' );
		TexturePath = string.IsNullOrWhiteSpace( filename ) ? null : $"mount://{mountIdent}/{filename}.vtex";
		MaximumWeight = Math.Max( Number( entity, "maxweight", 1 ), 0 );
		_minimum = Number( entity, "minfalloff", 0 );
		_maximum = Number( entity, "maxfalloff", 1000 );
		_origin = Position( entity.GetValueOrDefault( "origin", "0 0 0" ), out _validOrigin );
		IsBrush = string.Equals( entity.GetValueOrDefault( "classname" ), "color_correction_volume", StringComparison.OrdinalIgnoreCase );
		if ( IsBrush )
		{
			var shape = new Dictionary<string, string>( entity, StringComparer.OrdinalIgnoreCase );
			shape.TryAdd( "origin", "0 0 0" );
			_shape = new( shape );
		}
		FadeIn = Math.Max( 0, Number( entity, "fadeInDuration", Number( entity, "fadeDuration", 0 ) ) );
		FadeOut = Math.Max( 0, Number( entity, "fadeOutDuration", Number( entity, "fadeDuration", 0 ) ) );
	}

	internal bool IsGlobal => !IsBrush && (_minimum == -1 || _maximum == -1 || _minimum == _maximum);
	internal bool HasUsableShape => IsBrush ? _shape.IsValid : IsGlobal || _validOrigin;

	internal float SpatialWeight( Vector3 position )
	{
		if ( IsBrush ) return _shape.Contains( new( position.x, position.y, position.z ) ) ? MaximumWeight : 0;
		if ( IsGlobal ) return MaximumWeight;
		var distance = (position - _origin).Length;
		var value = float.IsFinite( distance ) ? MaximumWeight * (1 - Math.Clamp( (distance - _minimum) / (_maximum - _minimum), 0, 1 )) : 0;
		return float.IsFinite( value ) ? value : 0;
	}

	static float Number( Dictionary<string, string> entity, string key, float fallback ) =>
		entity.TryGetValue( key, out var text ) && float.TryParse( text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value )
			&& float.IsFinite( value ) ? value : fallback;

	static Vector3 Position( string text, out bool valid )
	{
		valid = false;
		var parts = text.Split( (char[])null, StringSplitOptions.RemoveEmptyEntries );
		if ( parts.Length != 3 ) return Vector3.Zero;
		var values = new float[3];
		for ( var i = 0; i < 3; i++ )
			if ( !float.TryParse( parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out values[i] ) || !float.IsFinite( values[i] ) ) return Vector3.Zero;
		valid = true;
		return new( values[0], values[1], values[2] );
	}

	/// <summary>Continuous entry/exit fade per rendered camera, including direction changes.</summary>
	internal struct FadeState
	{
		float _from, _target, _started, _duration;
		bool _initialized;

		internal float Evaluate( float target, float time, float fadeIn, float fadeOut )
		{
			var current = _initialized ? Value( time ) : 0;
			if ( !_initialized || target != _target || time < _started )
			{
				_duration = target > current ? fadeIn : fadeOut;
				_from = current;
				_target = target;
				_started = time;
				_initialized = true;
			}
			return Value( time );
		}

		readonly float Value( float time ) => _duration <= 0 ? _target
			: _from + (_target - _from) * Math.Clamp( (time - _started) / _duration, 0, 1 );
	}
}
