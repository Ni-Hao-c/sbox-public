using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Titanfall2;

/// <summary>
/// Reads Titanfall 2's Source 1 PCF files. They use DMX binary encoding 5 and
/// the pcf revision 2 schema. The generic DMX tree is deliberately kept local
/// to this reader; mounted maps retain only the compact particle definitions.
/// </summary>
static class Titanfall2PcfReader
{
	const int MaximumStrings = 1_000_000;
	const int MaximumElements = 1_000_000;
	const int MaximumAttributes = 1_000_000;
	const int MaximumArrayItems = 4_000_000;
	const int MaximumBinaryBytes = 256 * 1024 * 1024;
	const int ArrayTypeOffset = 14;

	internal static bool TryReadDefinitions( byte[] bytes, string sourcePath,
		out IReadOnlyList<Titanfall2ParticleDefinition> definitions, out string error )
	{
		definitions = Array.Empty<Titanfall2ParticleDefinition>();
		error = null;
		try
		{
			var reader = new DmxReader( bytes );
			var root = reader.Read();
			if ( !root.TryGetElements( "particleSystemDefinitions", out var sourceDefinitions ) )
			{
				error = "PCF root has no particleSystemDefinitions array.";
				return false;
			}

			var parsed = new List<Titanfall2ParticleDefinition>( sourceDefinitions.Count );
			foreach ( var element in sourceDefinitions )
			{
				if ( element is null || string.IsNullOrWhiteSpace( element.Name ) ) continue;
				parsed.Add( ConvertDefinition( element, sourcePath ) );
			}
			definitions = parsed;
			return true;
		}
		catch ( Exception exception )
		{
			error = exception.Message;
			return false;
		}
	}

	static Titanfall2ParticleDefinition ConvertDefinition( DmxElement element, string sourcePath )
	{
		var definition = new Titanfall2ParticleDefinition
		{
			Name = element.Name,
			SourcePath = sourcePath,
			MaterialName = NormalizeMaterialName( element.GetString( "material" ) ),
			MaxParticles = Math.Clamp( element.GetInt( "max_particles", 100 ), 1, 1_000_000 )
		};

		if ( element.TryGetElements( "emitters", out var emitters ) )
		{
			foreach ( var emitter in emitters )
			{
				if ( emitter.GetBool( "mute", false ) ) continue;
				var function = GetFunctionName( emitter );
				if ( function.Equals( "emit_continuously", StringComparison.OrdinalIgnoreCase ) )
				{
					definition.EmissionRate += MathF.Max( 0f, emitter.GetFloat( "emission_rate", 10f ) );
					definition.Looping = true;
				}
				else if ( function.Equals( "emit_instantaneously", StringComparison.OrdinalIgnoreCase ) )
				{
					definition.Burst += MathF.Max( 0f, emitter.GetFloat( "num_to_emit", 1f ) );
					definition.Looping = true;
				}
				else if ( function.Equals( "emit_noise", StringComparison.OrdinalIgnoreCase ) )
				{
					definition.EmissionRate += MathF.Max( 0f, emitter.GetFloat( "emission minimum", 0f ) );
					definition.Looping = true;
				}
			}
		}

		if ( element.TryGetElements( "initializers", out var initializers ) )
		{
			foreach ( var initializer in initializers ) ApplyInitializer( definition, initializer );
		}
		if ( element.TryGetElements( "operators", out var operators ) )
		{
			foreach ( var particleOperator in operators ) ApplyOperator( definition, particleOperator );
		}
		if ( element.TryGetElements( "forces", out var forces ) )
		{
			foreach ( var force in forces ) ApplyForce( definition, force );
		}
		if ( element.TryGetElements( "renderers", out var renderers ) )
		{
			foreach ( var renderer in renderers ) ApplyRenderer( definition, renderer );
		}
		if ( element.TryGetElements( "children", out var children ) )
		{
			foreach ( var child in children )
			{
				var childName = child.GetString( "child", child.Name );
				if ( !string.IsNullOrWhiteSpace( childName ) ) definition.Children.Add( childName );
			}
		}

		if ( definition.Burst <= 0f && definition.EmissionRate <= 0f ) definition.Burst = 1f;
		if ( definition.LifetimeMaximum <= 0f ) definition.LifetimeMaximum = MathF.Max( definition.LifetimeMinimum, 1f );
		if ( definition.LifetimeMinimum <= 0f ) definition.LifetimeMinimum = definition.LifetimeMaximum;
		if ( definition.RadiusMaximum <= 0f ) definition.RadiusMaximum = MathF.Max( definition.RadiusMinimum, 1f );
		if ( definition.RadiusMinimum <= 0f ) definition.RadiusMinimum = definition.RadiusMaximum;
		definition.Duration = MathF.Max( 0.05f, definition.LifetimeMaximum );
		return definition;
	}

	static void ApplyInitializer( Titanfall2ParticleDefinition definition, DmxElement initializer )
	{
		if ( initializer.GetBool( "mute", false ) ) return;
		var function = GetFunctionName( initializer );
		if ( function.Equals( "Lifetime Random", StringComparison.OrdinalIgnoreCase ) )
		{
			definition.LifetimeMinimum = MathF.Max( 0.01f, initializer.GetFloat( "lifetime_min", 1f ) );
			definition.LifetimeMaximum = MathF.Max( definition.LifetimeMinimum, initializer.GetFloat( "lifetime_max", definition.LifetimeMinimum ) );
		}
		else if ( function.Equals( "Radius Random", StringComparison.OrdinalIgnoreCase ) )
		{
			definition.RadiusMinimum = MathF.Max( 0f, initializer.GetFloat( "radius_min", 1f ) );
			definition.RadiusMaximum = MathF.Max( definition.RadiusMinimum, initializer.GetFloat( "radius_max", definition.RadiusMinimum ) );
		}
		else if ( function.Equals( "Alpha Random", StringComparison.OrdinalIgnoreCase ) )
		{
			definition.AlphaMinimum = NormalizeAlpha( initializer.GetFloat( "alpha_min", 255f ) );
			definition.AlphaMaximum = NormalizeAlpha( initializer.GetFloat( "alpha_max", 255f ) );
		}
		else if ( function.Equals( "Color Random", StringComparison.OrdinalIgnoreCase ) )
		{
			definition.ColorMinimum = initializer.GetColor( "color1", RgbaColor.White );
			definition.ColorMaximum = initializer.GetColor( "color2", definition.ColorMinimum );
		}
		else if ( function.Equals( "Position Modify Offset Random", StringComparison.OrdinalIgnoreCase ) )
		{
			definition.PositionMinimum += initializer.GetVector3( "offset min", Vector3.Zero );
			definition.PositionMaximum += initializer.GetVector3( "offset max", Vector3.Zero );
			definition.LocalPosition = initializer.GetBool( "offset in local space 0/1", definition.LocalPosition );
		}
		else if ( function.Equals( "Position Within Sphere Random", StringComparison.OrdinalIgnoreCase ) )
		{
			definition.Shape = Titanfall2ParticleShape.Sphere;
			definition.ShapeMinimum = MathF.Max( 0f, initializer.GetFloat( "distance_min", 0f ) );
			definition.ShapeMaximum = MathF.Max( definition.ShapeMinimum, initializer.GetFloat( "distance_max", 0f ) );
			definition.VelocityMinimum += initializer.GetVector3( "speed_in_local_coordinate_system_min", Vector3.Zero );
			definition.VelocityMaximum += initializer.GetVector3( "speed_in_local_coordinate_system_max", Vector3.Zero );
			definition.LocalVelocity |= definition.VelocityMinimum != Vector3.Zero || definition.VelocityMaximum != Vector3.Zero;
		}
		else if ( function.Equals( "Position Within Box Random", StringComparison.OrdinalIgnoreCase ) )
		{
			definition.Shape = Titanfall2ParticleShape.Box;
			definition.PositionMinimum += initializer.GetVector3( "min", Vector3.Zero );
			definition.PositionMaximum += initializer.GetVector3( "max", Vector3.Zero );
		}
		else if ( function.Equals( "Position Along Ring", StringComparison.OrdinalIgnoreCase ) )
		{
			definition.Shape = Titanfall2ParticleShape.Ring;
			definition.ShapeMaximum = MathF.Max( 0f, initializer.GetFloat( "initial radius", 50f ) );
			definition.ShapeThickness = MathF.Max( 0f, initializer.GetFloat( "thickness", 0f ) );
		}
		else if ( function.Contains( "Velocity", StringComparison.OrdinalIgnoreCase ) )
		{
			var minimum = initializer.GetVector3( "output minimum",
				initializer.GetVector3( "velocity_min",
					initializer.GetVector3( "speed_in_local_coordinate_system_min", Vector3.Zero ) ) );
			var maximum = initializer.GetVector3( "output maximum",
				initializer.GetVector3( "velocity_max",
					initializer.GetVector3( "speed_in_local_coordinate_system_max", minimum ) ) );
			definition.VelocityMinimum += minimum;
			definition.VelocityMaximum += maximum;
			definition.LocalVelocity |= initializer.GetBool( "Apply Velocity in Local Space (0/1)", false )
				|| initializer.GetBool( "apply velocity in local space (0/1)", false )
				|| initializer.GetVector3( "speed_in_local_coordinate_system_min", Vector3.Zero ) != Vector3.Zero
				|| initializer.GetVector3( "speed_in_local_coordinate_system_max", Vector3.Zero ) != Vector3.Zero;
		}
		else if ( function.Equals( "Trail Length Random", StringComparison.OrdinalIgnoreCase ) )
		{
			definition.TrailMinimum = MathF.Max( 0f, initializer.GetFloat( "length_min", definition.TrailMinimum ) );
			definition.TrailMaximum = MathF.Max( definition.TrailMinimum, initializer.GetFloat( "length_max", definition.TrailMinimum ) );
		}
		else if ( function.Equals( "Rotation Random", StringComparison.OrdinalIgnoreCase ) )
		{
			definition.RotationMinimum = initializer.GetFloat( "rotation_initial",
				definition.RotationMinimum );
			definition.RotationMaximum = initializer.GetFloat( "rotation_offset_max",
				definition.RotationMinimum );
		}
		else if ( function.Equals( "Rotation Yaw Random", StringComparison.OrdinalIgnoreCase ) )
		{
			definition.YawMinimum = initializer.GetFloat( "yaw_initial", definition.YawMinimum );
			definition.YawMaximum = initializer.GetFloat( "yaw_offset_max", definition.YawMinimum );
		}
		else if ( function.Equals( "Rotation Yaw Flip Random", StringComparison.OrdinalIgnoreCase ) )
		{
			definition.RandomYawFlip = true;
		}
		else if ( function.Equals( "Rotation Speed Random", StringComparison.OrdinalIgnoreCase ) )
		{
			definition.RotationRateMinimum = initializer.GetFloat( "rotation_speed_random_min", 0f );
			definition.RotationRateMaximum = initializer.GetFloat( "rotation_speed_random_max", definition.RotationRateMinimum );
		}
		else if ( function.Equals( "Scalar Random", StringComparison.OrdinalIgnoreCase ) )
		{
			var minimum = initializer.GetFloat( "min", 0f );
			var maximum = initializer.GetFloat( "max", minimum );
			var outputField = initializer.GetInt( "output field", -1 );
			// Alpha2 is a Titanfall extension multiplied into normal alpha. s&box
			// has one alpha channel, so preserve the combined result.
			if ( outputField is 7 or 16 )
			{
				definition.AlphaMultiplierMinimum *= minimum;
				definition.AlphaMultiplierMaximum *= maximum;
			}
			else if ( outputField == 3 )
			{
				definition.RadiusMultiplierMinimum *= minimum;
				definition.RadiusMultiplierMaximum *= maximum;
			}
			else if ( outputField == 12 )
			{
				definition.YawMinimum = minimum;
				definition.YawMaximum = maximum;
			}
		}
		else if ( function.Equals( "Sequence Random", StringComparison.OrdinalIgnoreCase ) )
		{
			definition.SequenceMinimum = initializer.GetInt( "sequence_min", 0 );
			definition.SequenceMaximum = Math.Max( definition.SequenceMinimum, initializer.GetInt( "sequence_max", definition.SequenceMinimum ) );
		}
	}

	static void ApplyOperator( Titanfall2ParticleDefinition definition, DmxElement particleOperator )
	{
		if ( particleOperator.GetBool( "mute", false ) ) return;
		var function = GetFunctionName( particleOperator );
		if ( function.Equals( "Movement Basic", StringComparison.OrdinalIgnoreCase ) )
		{
			definition.Damping = MathF.Max( definition.Damping, particleOperator.GetFloat( "drag", 0f ) );
			definition.Gravity += particleOperator.GetVector3( "gravity", Vector3.Zero );
		}
		else if ( function.Equals( "Alpha Fade and Decay", StringComparison.OrdinalIgnoreCase ) )
		{
			definition.AlphaFade = new ParticleFadeDefinition(
				particleOperator.GetFloat( "start_fade_in_time", 0f ),
				particleOperator.GetFloat( "end_fade_in_time", 0.15f ),
				particleOperator.GetFloat( "start_fade_out_time", 0.75f ),
				particleOperator.GetFloat( "end_fade_out_time", 1f ) );
		}
		else if ( function.Equals( "Alpha Fade In Simple", StringComparison.OrdinalIgnoreCase ) )
		{
			var fade = definition.AlphaFade ?? new ParticleFadeDefinition( 0f, 0f, 1f, 1f );
			var duration = Math.Clamp( particleOperator.GetFloat( "proportional fade in time", 0.25f ), 0f, 1f );
			definition.AlphaFade = fade with { FadeInStart = 0f, FadeInEnd = duration };
		}
		else if ( function.Equals( "Alpha Fade Out Simple", StringComparison.OrdinalIgnoreCase ) )
		{
			var fade = definition.AlphaFade ?? new ParticleFadeDefinition( 0f, 0f, 1f, 1f );
			var duration = Math.Clamp( particleOperator.GetFloat( "proportional fade out time", 0.25f ), 0f, 1f );
			definition.AlphaFade = fade with { FadeOutStart = 1f - duration, FadeOutEnd = 1f };
		}
		else if ( function.Equals( "Alpha Fade In Random", StringComparison.OrdinalIgnoreCase ) )
		{
			var fade = definition.AlphaFade ?? new ParticleFadeDefinition( 0f, 0f, 1f, 1f );
			var duration = Math.Clamp( particleOperator.GetFloat( "fade in time max", 0.25f ), 0f, 1f );
			definition.AlphaFade = fade with { FadeInStart = 0f, FadeInEnd = duration };
		}
		else if ( function.Equals( "Alpha Fade Out Random", StringComparison.OrdinalIgnoreCase ) )
		{
			var fade = definition.AlphaFade ?? new ParticleFadeDefinition( 0f, 0f, 1f, 1f );
			var duration = Math.Clamp( particleOperator.GetFloat( "fade out time max", 0.25f ), 0f, 1f );
			definition.AlphaFade = fade with { FadeOutStart = 1f - duration, FadeOutEnd = 1f };
		}
		else if ( function.Equals( "Radius Scale", StringComparison.OrdinalIgnoreCase ) )
		{
			definition.RadiusScale = new ParticleScaleDefinition(
				particleOperator.GetFloat( "start_time", 0f ),
				particleOperator.GetFloat( "end_time", 1f ),
				particleOperator.GetFloat( "start_scale", 1f ),
				particleOperator.GetFloat( "end_scale", 1f ) );
		}
		else if ( function.Equals( "Rotation Spin Roll", StringComparison.OrdinalIgnoreCase ) )
		{
			definition.RotationRate = particleOperator.GetFloat( "spin_rate_degrees", 0f );
		}
		else if ( function.Equals( "Color Fade", StringComparison.OrdinalIgnoreCase ) )
		{
			definition.ColorFade = particleOperator.GetColor( "color_fade", definition.ColorMaximum );
			definition.ColorFadeStart = particleOperator.GetFloat( "fade_start_time", 0f );
			definition.ColorFadeEnd = particleOperator.GetFloat( "fade_end_time", 1f );
		}
		else if ( function.Equals( "Graph Scalar", StringComparison.OrdinalIgnoreCase ) )
		{
			var points = particleOperator.GetVector2Array( "graph" );
			if ( points.Count > 0 )
			{
				definition.ScalarGraphs.Add( new ParticleScalarGraphDefinition(
					particleOperator.GetInt( "output field", 7 ),
					particleOperator.GetInt( "output op", 0 ),
					particleOperator.GetFloat( "output minimum", 0f ),
					particleOperator.GetFloat( "output maximum", 1f ),
					particleOperator.GetBool( "graph time is in lifespans", true ),
					particleOperator.GetBool( "graph loop", false ),
					MathF.Max( 0.001f, particleOperator.GetFloat( "graph time", 1f ) ),
					points ) );
			}
		}
	}

	static void ApplyForce( Titanfall2ParticleDefinition definition, DmxElement force )
	{
		if ( force.GetBool( "mute", false ) ) return;
		var function = GetFunctionName( force );
		if ( function.Contains( "Gravity", StringComparison.OrdinalIgnoreCase ) )
			definition.Gravity += force.GetVector3( "gravity", new Vector3( 0f, 0f, -100f ) );
		else if ( function.Contains( "Drag", StringComparison.OrdinalIgnoreCase ) )
			definition.Damping = MathF.Max( definition.Damping, force.GetFloat( "drag", 0f ) );
	}

	static void ApplyRenderer( Titanfall2ParticleDefinition definition, DmxElement renderer )
	{
		if ( renderer.GetBool( "mute", false ) ) return;
		var function = GetFunctionName( renderer );
		if ( function.Contains( "sprite_trail", StringComparison.OrdinalIgnoreCase ) )
		{
			definition.Renderer |= Titanfall2ParticleRenderer.Trail;
			definition.AnimationRate = MathF.Max( 0f, renderer.GetFloat( "animation rate", definition.AnimationRate ) );
			definition.TrailMinimum = MathF.Max( definition.TrailMinimum, renderer.GetFloat( "min length", definition.TrailMinimum ) );
			definition.TrailMaximum = MathF.Max( definition.TrailMinimum, renderer.GetFloat( "max length", definition.TrailMaximum ) );
		}
		else if ( function.Contains( "rope", StringComparison.OrdinalIgnoreCase ) )
		{
			definition.Renderer |= Titanfall2ParticleRenderer.Rope;
		}
		else if ( function.Contains( "sprite", StringComparison.OrdinalIgnoreCase ) )
		{
			definition.Renderer |= Titanfall2ParticleRenderer.Sprite;
			definition.AnimationRate = MathF.Max( 0f, renderer.GetFloat( "animation rate", 1f ) );
			definition.OrientationType = renderer.GetInt( "orientation_type", 0 );
			definition.DepthFeather = MathF.Max( 0f, renderer.GetFloat( "soft particles", 0f ) );
		}
		else if ( function.Contains( "model", StringComparison.OrdinalIgnoreCase ) )
		{
			definition.Renderer |= Titanfall2ParticleRenderer.Model;
			definition.ModelName = NormalizeModelName( renderer.GetString( "sequence 0 model" ) );
		}
		else if ( function.Contains( "light source", StringComparison.OrdinalIgnoreCase ) && !renderer.GetBool( "mute", false ) )
		{
			definition.Renderer |= Titanfall2ParticleRenderer.Light;
			definition.LightRadiusScale = MathF.Max( 0.01f, renderer.GetFloat( "radius scale", 1f ) );
			definition.LightColorScale = MathF.Max( 0f, renderer.GetFloat( "color scale", 1f ) );
			definition.LightColorByAlpha = renderer.GetBool( "color scale by alpha", false );
		}
	}

	static string GetFunctionName( DmxElement element ) => element?.GetString( "functionName", element.Name ) ?? string.Empty;

	static float NormalizeAlpha( float value ) => value > 1f ? Math.Clamp( value / 255f, 0f, 1f ) : Math.Clamp( value, 0f, 1f );

	static string NormalizeMaterialName( string value )
	{
		if ( string.IsNullOrWhiteSpace( value ) ) return string.Empty;
		var normalized = value.Replace( '\\', '/' ).Trim().Trim( '"' ).TrimStart( '/' );
		if ( normalized.StartsWith( "materials/", StringComparison.OrdinalIgnoreCase ) ) normalized = normalized[10..];
		if ( normalized.EndsWith( ".vmt", StringComparison.OrdinalIgnoreCase ) ) normalized = normalized[..^4];
		return normalized;
	}

	static string NormalizeModelName( string value )
	{
		if ( string.IsNullOrWhiteSpace( value ) ) return string.Empty;
		var normalized = value.Replace( '\\', '/' ).Trim().Trim( '"' ).TrimStart( '/' );
		if ( !normalized.StartsWith( "models/", StringComparison.OrdinalIgnoreCase ) ) normalized = "models/" + normalized;
		return normalized;
	}

	sealed class DmxReader
	{
		readonly byte[] _bytes;
		int _position;
		string[] _strings;
		DmxElement[] _elements;

		internal DmxReader( byte[] bytes ) => _bytes = bytes ?? throw new ArgumentNullException( nameof(bytes) );

		internal DmxElement Read()
		{
			ReadHeader();
			var stringCount = ReadCount( MaximumStrings, "DMX string" );
			_strings = new string[stringCount];
			for ( var index = 0; index < _strings.Length; index++ ) _strings[index] = ReadCString();

			var elementCount = ReadCount( MaximumElements, "DMX element" );
			_elements = new DmxElement[elementCount];
			for ( var index = 0; index < _elements.Length; index++ )
			{
				var type = ReadStringReference();
				var name = ReadStringReference();
				Ensure( 16 );
				_position += 16; // DmObjectId_t / GUID, unused by PCF conversion.
				_elements[index] = new DmxElement( name, type );
			}

			foreach ( var element in _elements )
			{
				var attributeCount = ReadCount( MaximumAttributes, "DMX attribute" );
				for ( var index = 0; index < attributeCount; index++ )
				{
					var name = ReadStringReference();
					var encodedType = ReadByte();
					var isArray = encodedType >= ArrayTypeOffset;
					var type = isArray ? encodedType - ArrayTypeOffset : encodedType;
					if ( type < 1 || type > 14 ) throw new InvalidDataException( $"Unsupported DMX attribute type {encodedType}." );
					var count = isArray ? ReadCount( MaximumArrayItems, "DMX array" ) : 1;
					var value = ReadAttributeValue( type, isArray, count );
					element.Attributes[name] = new DmxAttribute( type, isArray, value );
				}
			}
			if ( _elements.Length == 0 ) throw new InvalidDataException( "DMX contains no elements." );
			return _elements[0];
		}

		void ReadHeader()
		{
			var maximum = Math.Min( _bytes.Length, 512 );
			var headerEnd = -1;
			for ( var index = 0; index + 4 < maximum; index++ )
			{
				if ( _bytes[index] == (byte)'-' && _bytes[index + 1] == (byte)'-' && _bytes[index + 2] == (byte)'>'
					&& _bytes[index + 3] == (byte)'\n' && _bytes[index + 4] == 0 )
				{
					headerEnd = index + 5;
					break;
				}
			}
			if ( headerEnd < 0 ) throw new InvalidDataException( "PCF has no terminated DMX header." );
			var header = Encoding.ASCII.GetString( _bytes, 0, headerEnd - 2 );
			if ( !header.Contains( "dmx encoding binary 5 format pcf 2", StringComparison.OrdinalIgnoreCase ) )
				throw new InvalidDataException( $"Unsupported PCF DMX header '{header.Trim()}'." );
			_position = headerEnd;
		}

		object ReadAttributeValue( int type, bool isArray, int count )
		{
			if ( type == 1 )
			{
				var values = new DmxElement[count];
				for ( var index = 0; index < values.Length; index++ )
				{
					var elementIndex = ReadInt32();
					if ( elementIndex == -1 ) continue;
					if ( elementIndex == -2 )
					{
						_ = ReadCString();
						continue;
					}
					if ( elementIndex < 0 || elementIndex >= _elements.Length )
						throw new InvalidDataException( $"DMX element reference {elementIndex} is invalid." );
					values[index] = _elements[elementIndex];
				}
				return isArray ? values : values[0];
			}
			if ( type == 5 )
			{
				var values = new string[count];
				for ( var index = 0; index < values.Length; index++ )
					values[index] = isArray ? ReadCString() : ReadStringReference();
				return isArray ? values : values[0];
			}
			if ( type == 6 )
			{
				var values = new byte[count][];
				for ( var index = 0; index < values.Length; index++ )
				{
					var length = ReadCount( MaximumBinaryBytes, "DMX binary" );
					Ensure( length );
					values[index] = _bytes.AsSpan( _position, length ).ToArray();
					_position += length;
				}
				return isArray ? values : values[0];
			}

			var result = new object[count];
			for ( var index = 0; index < result.Length; index++ ) result[index] = ReadFixedValue( type );
			return isArray ? result : result[0];
		}

		object ReadFixedValue( int type ) => type switch
		{
			2 => ReadInt32(),
			3 => ReadSingle(),
			4 => ReadByte() != 0,
			7 => ReadInt32() / 10000f,
			8 => new RgbaColor( ReadByte(), ReadByte(), ReadByte(), ReadByte() ),
			9 => new Vector2( ReadSingle(), ReadSingle() ),
			10 => new Vector3( ReadSingle(), ReadSingle(), ReadSingle() ),
			11 => new Vector4( ReadSingle(), ReadSingle(), ReadSingle(), ReadSingle() ),
			12 => new Vector3( ReadSingle(), ReadSingle(), ReadSingle() ),
			13 => new Vector4( ReadSingle(), ReadSingle(), ReadSingle(), ReadSingle() ),
			14 => ReadMatrix(),
			_ => throw new InvalidDataException( $"Unsupported fixed DMX type {type}." )
		};

		float[] ReadMatrix()
		{
			var matrix = new float[16];
			for ( var index = 0; index < matrix.Length; index++ ) matrix[index] = ReadSingle();
			return matrix;
		}

		string ReadStringReference()
		{
			var index = ReadInt32();
			if ( index < 0 || index >= _strings.Length ) throw new InvalidDataException( $"DMX string reference {index} is invalid." );
			return _strings[index];
		}

		string ReadCString()
		{
			var start = _position;
			while ( _position < _bytes.Length && _bytes[_position] != 0 ) _position++;
			if ( _position >= _bytes.Length ) throw new EndOfStreamException( "DMX string is not terminated." );
			var value = Encoding.UTF8.GetString( _bytes, start, _position - start );
			_position++;
			return value;
		}

		int ReadCount( int maximum, string label )
		{
			var count = ReadInt32();
			if ( count < 0 || count > maximum ) throw new InvalidDataException( $"Invalid {label} count {count}." );
			return count;
		}

		byte ReadByte()
		{
			Ensure( 1 );
			return _bytes[_position++];
		}

		int ReadInt32()
		{
			Ensure( 4 );
			var value = BinaryPrimitives.ReadInt32LittleEndian( _bytes.AsSpan( _position, 4 ) );
			_position += 4;
			return value;
		}

		float ReadSingle() => BitConverter.Int32BitsToSingle( ReadInt32() );

		void Ensure( int length )
		{
			if ( length < 0 || _position < 0 || _position > _bytes.Length - length )
				throw new EndOfStreamException( $"PCF ended at byte {_position} while reading {length} bytes." );
		}
	}

	sealed class DmxElement( string name, string type )
	{
		internal string Name { get; } = name;
		internal string Type { get; } = type;
		internal Dictionary<string, DmxAttribute> Attributes { get; } = new( StringComparer.OrdinalIgnoreCase );

		internal bool TryGetElements( string name, out IReadOnlyList<DmxElement> values )
		{
			values = Array.Empty<DmxElement>();
			if ( !Attributes.TryGetValue( name, out var attribute ) || attribute.Type != 1 ) return false;
			if ( attribute.Value is DmxElement[] array )
			{
				values = array.Where( static value => value is not null ).ToArray();
				return true;
			}
			if ( attribute.Value is DmxElement value )
			{
				values = [value];
				return true;
			}
			return false;
		}

		internal string GetString( string name, string fallback = "" ) =>
			Attributes.TryGetValue( name, out var attribute ) && attribute.Value is string value ? value : fallback;

		internal int GetInt( string name, int fallback = 0 )
		{
			if ( !Attributes.TryGetValue( name, out var attribute ) ) return fallback;
			return attribute.Value switch
			{
				int value => value,
				float value => (int)value,
				bool value => value ? 1 : 0,
				_ => fallback
			};
		}

		internal float GetFloat( string name, float fallback = 0f )
		{
			if ( !Attributes.TryGetValue( name, out var attribute ) ) return fallback;
			return attribute.Value switch
			{
				float value => value,
				int value => value,
				bool value => value ? 1f : 0f,
				_ => fallback
			};
		}

		internal bool GetBool( string name, bool fallback = false )
		{
			if ( !Attributes.TryGetValue( name, out var attribute ) ) return fallback;
			return attribute.Value switch
			{
				bool value => value,
				int value => value != 0,
				float value => MathF.Abs( value ) > float.Epsilon,
				_ => fallback
			};
		}

		internal Vector3 GetVector3( string name, Vector3 fallback ) =>
			Attributes.TryGetValue( name, out var attribute ) && attribute.Value is Vector3 value ? value : fallback;

		internal IReadOnlyList<Vector2> GetVector2Array( string name )
		{
			if ( !Attributes.TryGetValue( name, out var attribute ) ) return Array.Empty<Vector2>();
			if ( attribute.Value is object[] values )
				return values.OfType<Vector2>().OrderBy( static value => value.x ).ToArray();
			if ( attribute.Value is Vector2 value ) return [value];
			return Array.Empty<Vector2>();
		}

		internal RgbaColor GetColor( string name, RgbaColor fallback ) =>
			Attributes.TryGetValue( name, out var attribute ) && attribute.Value is RgbaColor value ? value : fallback;
	}

	readonly record struct DmxAttribute( int Type, bool IsArray, object Value );
}

internal enum Titanfall2ParticleShape
{
	Point,
	Box,
	Sphere,
	Ring
}

[Flags]
internal enum Titanfall2ParticleRenderer
{
	Unsupported = 0,
	Sprite = 1 << 0,
	Trail = 1 << 1,
	Model = 1 << 2,
	Light = 1 << 3,
	Rope = 1 << 4
}

internal readonly record struct RgbaColor( byte R, byte G, byte B, byte A )
{
	internal static RgbaColor White => new( 255, 255, 255, 255 );
}

internal readonly record struct ParticleFadeDefinition( float FadeInStart, float FadeInEnd, float FadeOutStart, float FadeOutEnd );
internal readonly record struct ParticleScaleDefinition( float StartTime, float EndTime, float StartScale, float EndScale );
internal readonly record struct ParticleScalarGraphDefinition(
	int OutputField,
	int OutputOperation,
	float OutputMinimum,
	float OutputMaximum,
	bool TimeInLifespans,
	bool Loop,
	float Duration,
	IReadOnlyList<Vector2> Points );

internal sealed class Titanfall2ParticleDefinition
{
	internal string Name { get; init; }
	internal string SourcePath { get; init; }
	internal string MaterialName { get; init; }
	internal int MaxParticles { get; init; } = 100;
	internal float LifetimeMinimum { get; set; } = 1f;
	internal float LifetimeMaximum { get; set; } = 1f;
	internal float Duration { get; set; } = 1f;
	internal float Burst { get; set; }
	internal float EmissionRate { get; set; }
	internal bool Looping { get; set; } = true;
	internal float RadiusMinimum { get; set; } = 1f;
	internal float RadiusMaximum { get; set; } = 1f;
	internal float RadiusMultiplierMinimum { get; set; } = 1f;
	internal float RadiusMultiplierMaximum { get; set; } = 1f;
	internal float AlphaMinimum { get; set; } = 1f;
	internal float AlphaMaximum { get; set; } = 1f;
	internal float AlphaMultiplierMinimum { get; set; } = 1f;
	internal float AlphaMultiplierMaximum { get; set; } = 1f;
	internal RgbaColor ColorMinimum { get; set; } = RgbaColor.White;
	internal RgbaColor ColorMaximum { get; set; } = RgbaColor.White;
	internal RgbaColor? ColorFade { get; set; }
	internal float ColorFadeStart { get; set; }
	internal float ColorFadeEnd { get; set; } = 1f;
	internal Vector3 PositionMinimum { get; set; }
	internal Vector3 PositionMaximum { get; set; }
	internal bool LocalPosition { get; set; }
	internal Vector3 VelocityMinimum { get; set; }
	internal Vector3 VelocityMaximum { get; set; }
	internal bool LocalVelocity { get; set; }
	internal Vector3 Gravity { get; set; }
	internal float Damping { get; set; }
	internal float RotationMinimum { get; set; }
	internal float RotationMaximum { get; set; }
	internal float RotationRate { get; set; }
	internal float RotationRateMinimum { get; set; }
	internal float RotationRateMaximum { get; set; }
	internal float YawMinimum { get; set; }
	internal float YawMaximum { get; set; }
	internal bool RandomYawFlip { get; set; }
	internal int SequenceMinimum { get; set; }
	internal int SequenceMaximum { get; set; }
	internal float AnimationRate { get; set; } = 1f;
	internal float TrailMinimum { get; set; }
	internal float TrailMaximum { get; set; }
	internal string ModelName { get; set; }
	internal float LightRadiusScale { get; set; } = 1f;
	internal float LightColorScale { get; set; } = 1f;
	internal bool LightColorByAlpha { get; set; }
	internal Titanfall2ParticleShape Shape { get; set; }
	internal float ShapeMinimum { get; set; }
	internal float ShapeMaximum { get; set; }
	internal float ShapeThickness { get; set; }
	internal Titanfall2ParticleRenderer Renderer { get; set; }
	internal int OrientationType { get; set; }
	internal float DepthFeather { get; set; }
	internal ParticleFadeDefinition? AlphaFade { get; set; }
	internal ParticleScaleDefinition? RadiusScale { get; set; }
	internal List<ParticleScalarGraphDefinition> ScalarGraphs { get; } = [];
	internal List<string> Children { get; } = [];
}
