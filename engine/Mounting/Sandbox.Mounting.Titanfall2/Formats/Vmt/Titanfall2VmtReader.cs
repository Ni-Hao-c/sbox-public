using System.Globalization;
using System.Text;

static class Titanfall2LegacyPath
{
	internal const string AnimatedTexturePrefix = "__legacy_vtf_frames/";

	internal static bool TryGetMaterialName( string path, out string name ) =>
		TryGetMaterialAssetName( path, ".vmt", out name );

	internal static bool TryGetTextureName( string path, out string name ) =>
		TryGetMaterialAssetName( path, ".vtf", out name );

	internal static string NormalizeTextureName( string path )
	{
		if ( string.IsNullOrWhiteSpace( path ) ) return string.Empty;
		var normalized = path.Replace( '\\', '/' ).Trim().Trim( '"' ).TrimStart( '/' );
		if ( normalized.StartsWith( "materials/", StringComparison.OrdinalIgnoreCase ) ) normalized = normalized[10..];
		if ( normalized.EndsWith( ".vtf", StringComparison.OrdinalIgnoreCase ) ) normalized = normalized[..^4];
		return normalized.Trim( '/' );
	}

	internal static string GetAnimatedTextureName( string textureName ) =>
		AnimatedTexturePrefix + NormalizeTextureName( textureName );

	static bool TryGetMaterialAssetName( string path, string extension, out string name )
	{
		name = string.Empty;
		if ( string.IsNullOrWhiteSpace( path ) ) return false;
		var normalized = path.Replace( '\\', '/' ).Trim().TrimStart( '/' );
		if ( !normalized.StartsWith( "materials/", StringComparison.OrdinalIgnoreCase )
			|| !normalized.EndsWith( extension, StringComparison.OrdinalIgnoreCase ) ) return false;
		name = normalized[10..^extension.Length].Trim( '/' );
		return name.Length > 0;
	}
}

sealed class Titanfall2VmtDefinition
{
	readonly VmtBlock _root;

	internal string ShaderName { get; }
	internal bool IsUnlitTwoTexture => ShaderName.Equals( "UnlitTwoTexture", StringComparison.OrdinalIgnoreCase );

	internal Titanfall2VmtDefinition( string shaderName, VmtBlock root )
	{
		ShaderName = shaderName;
		_root = root;
	}

	internal Titanfall2MaterialMetadata GetMetadata( string materialName )
	{
		var inferred = MaterialLoader.InferMetadata( materialName );
		var mode = GetBool( "$additive" )
			? Titanfall2MaterialMode.Additive
			: GetBool( "$translucent" )
				? Titanfall2MaterialMode.AlphaBlend
				: GetBool( "$alphatest" )
					? Titanfall2MaterialMode.Cutout
					: Titanfall2MaterialMode.Opaque;
		return new Titanfall2MaterialMetadata(
			mode,
			inferred.IsDecal || GetBool( "$decal" ),
			GetBool( "$nocull" ) || inferred.DoubleSided,
			GetBool( "$vertexcolor" ),
			GetBool( "$vertexalpha" ),
			ShaderName.Contains( "Unlit", StringComparison.OrdinalIgnoreCase )
				|| ShaderName.Equals( "Basic", StringComparison.OrdinalIgnoreCase )
				|| ShaderName.Equals( "SpriteCard", StringComparison.OrdinalIgnoreCase ),
			inferred.IsWater,
			GetString( "$surfaceprop" ) );
	}

	internal string GetTexture( params string[] keys )
	{
		foreach ( var key in keys )
		{
			var value = GetString( key );
			if ( string.IsNullOrWhiteSpace( value ) ) continue;
			var normalized = Titanfall2LegacyPath.NormalizeTextureName( value );
			if ( normalized.Equals( "env_cubemap", StringComparison.OrdinalIgnoreCase ) ) continue;
			return normalized;
		}
		return string.Empty;
	}

	internal string GetString( string key, string fallback = null )
	{
		return _root.Values.TryGetValue( key, out var value ) ? ResolveRootVariable( value ) : fallback;
	}

	internal bool GetBool( string key, bool fallback = false )
	{
		var value = GetString( key );
		if ( string.IsNullOrWhiteSpace( value ) ) return fallback;
		if ( bool.TryParse( value, out var boolean ) ) return boolean;
		return TryParseFloat( value, out var number ) ? MathF.Abs( number ) > float.Epsilon : fallback;
	}

	internal float GetFloat( string key, float fallback = 0f )
	{
		return TryParseFloat( GetString( key ), out var value ) ? value : fallback;
	}

	internal Vector4 GetVector( string key, Vector4 fallback )
	{
		return TryParseVector( GetString( key ), out var value ) ? value : fallback;
	}

	internal bool TryGetTextureScroll( bool texture2, out Vector2 velocity )
	{
		velocity = Vector2.Zero;
		foreach ( var proxy in EnumerateProxies( "TextureScroll" ) )
		{
			var target = GetBlockRawString( proxy, "texturescrollvar" ) ?? string.Empty;
			if ( !target.Equals( "$basetexturetransform", StringComparison.OrdinalIgnoreCase )
				&& !target.Equals( "$texture2transform", StringComparison.OrdinalIgnoreCase ) ) continue;
			var targetsSecond = target.Contains( "texture2", StringComparison.OrdinalIgnoreCase );
			if ( targetsSecond != texture2 ) continue;
			var rate = GetBlockFloat( proxy, "texturescrollrate", 1f );
			var angle = GetBlockFloat( proxy, "texturescrollangle", 0f ) * MathF.PI / 180f;
			velocity = new Vector2( MathF.Cos( angle ) * rate, MathF.Sin( angle ) * rate );
			return true;
		}
		return false;
	}

	internal bool TryGetTextureTransform( bool texture2, out VmtTextureTransform transform )
	{
		transform = VmtTextureTransform.Identity;
		foreach ( var proxy in EnumerateProxies( "TextureTransform" ) )
		{
			var result = GetBlockRawString( proxy, "resultvar" ) ?? string.Empty;
			var targetsSecond = result.Contains( "texture2", StringComparison.OrdinalIgnoreCase );
			if ( targetsSecond != texture2 ) continue;
			var translation = ResolveBlockVector( proxy, "translatevar", Vector4.Zero );
			var scale = ResolveBlockFloat( proxy, "scalevar", 1f );
			var rotation = ResolveBlockFloat( proxy, "rotatevar", 0f );
			transform = new VmtTextureTransform( scale, rotation, new Vector2( translation.x, translation.y ) );
			return true;
		}
		return false;
	}

	internal bool TryGetAnimatedTexture( bool texture2, out float frameRate )
	{
		frameRate = 0f;
		foreach ( var proxy in EnumerateProxies( "AnimatedTexture" ) )
		{
			var target = GetBlockRawString( proxy, "animatedtexturevar" ) ?? string.Empty;
			var targetsSecond = target.Equals( "$texture2", StringComparison.OrdinalIgnoreCase );
			if ( targetsSecond != texture2 ) continue;
			frameRate = MathF.Max( 0f, GetBlockFloat( proxy, "animatedtextureframerate", 15f ) );
			return true;
		}
		return false;
	}

	internal VmtNoiseRange GetNoiseRange( string vectorVariable )
	{
		var minimum = new Vector2( 0f, 0f );
		var maximum = new Vector2( 0f, 0f );
		var found = false;
		foreach ( var proxy in EnumerateProxies( "UniformNoise" ) )
		{
			var target = GetBlockRawString( proxy, "resultvar" ) ?? string.Empty;
			if ( !target.StartsWith( vectorVariable, StringComparison.OrdinalIgnoreCase ) ) continue;
			var min = GetBlockFloat( proxy, "minval", 0f );
			var max = GetBlockFloat( proxy, "maxval", 1f );
			if ( target.Contains( "[1]", StringComparison.OrdinalIgnoreCase ) )
			{
				minimum.y = min;
				maximum.y = max;
			}
			else
			{
				minimum.x = min;
				maximum.x = max;
			}
			found = true;
		}
		return new VmtNoiseRange( minimum, maximum, found );
	}

	internal IReadOnlyList<Titanfall2RuntimeExpression> GetRuntimeExpressions()
	{
		var expressions = new List<Titanfall2RuntimeExpression>();
		foreach ( var pair in _root.Values )
		{
			if ( !pair.Key.StartsWith( '$' ) ) continue;
			if ( TryParseFloat( pair.Value, out var value ) )
				expressions.Add( new Titanfall2RuntimeExpression( "Constant", pair.Key, null, null, new Vector4( value, 0f, 0f, 0f ) ) );
			else if ( TryParseVector( pair.Value, out var vector ) )
				expressions.Add( new Titanfall2RuntimeExpression( "VectorConstant", pair.Key, null, null, vector ) );
		}

		foreach ( var proxies in _root.Children.Where( static child => child.Name.Equals( "Proxies", StringComparison.OrdinalIgnoreCase ) ) )
		{
			foreach ( var proxy in proxies.Children )
			{
				var type = proxy.Name;
				var target = GetBlockRawString( proxy, "resultvar" )
					?? GetBlockRawString( proxy, "resultVar" )
					?? GetBlockRawString( proxy, "animatedtextureframenumvar" )
					?? GetBlockRawString( proxy, "texturescrollvar" );
				var sourceA = GetBlockRawString( proxy, "srcvar1" )
					?? GetBlockRawString( proxy, "sourcevar1" )
					?? GetBlockRawString( proxy, "srcvar" );
				var sourceB = GetBlockRawString( proxy, "srcvar2" )
					?? GetBlockRawString( proxy, "sourcevar2" );
				string sourceC = null;
				string sourceD = null;
				if ( type.Equals( "TextureTransform", StringComparison.OrdinalIgnoreCase ) )
				{
					sourceA = GetBlockRawString( proxy, "translatevar" );
					sourceB = GetBlockRawString( proxy, "rotatevar" );
					sourceC = GetBlockRawString( proxy, "scalevar" );
				}
				else if ( type.Equals( "TextureScroll", StringComparison.OrdinalIgnoreCase ) )
				{
					sourceA = GetBlockRawString( proxy, "texturescrollrate" );
					sourceB = GetBlockRawString( proxy, "texturescrollangle" );
					sourceC = GetBlockRawString( proxy, "texturescale" );
				}
				if ( type.Equals( "LessOrEqual", StringComparison.OrdinalIgnoreCase ) )
				{
					sourceC = GetBlockRawString( proxy, "lessEqualVar" );
					sourceD = GetBlockRawString( proxy, "greaterVar" );
				}
				var parameters = type.ToLowerInvariant() switch
				{
					"currenttime" => new Vector4( GetBlockFloat( proxy, "scale", 1f ), 0f, 0f, 0f ),
					"linearramp" => new Vector4( GetBlockFloat( proxy, "initialvalue", 0f ), GetBlockFloat( proxy, "rate", 1f ), 0f, 0f ),
					"sine" => new Vector4( GetBlockFloat( proxy, "sinemin", 0f ), GetBlockFloat( proxy, "sinemax", 1f ),
						GetBlockFloat( proxy, "sineperiod", 1f ), GetBlockFloat( proxy, "timeoffset", 0f ) ),
					"clamp" => new Vector4( GetBlockFloat( proxy, "min", 0f ), GetBlockFloat( proxy, "max", 1f ), 0f, 0f ),
					"remapvalclamped" => new Vector4( GetBlockFloat( proxy, "sourcemin", 0f ), GetBlockFloat( proxy, "sourcemax", 1f ),
						GetBlockFloat( proxy, "resultmin", 0f ), GetBlockFloat( proxy, "resultmax", 1f ) ),
					"wrapminmax" => new Vector4( GetBlockFloat( proxy, "minval", 0f ), GetBlockFloat( proxy, "maxval", 1f ), 0f, 0f ),
					"exponential" => new Vector4( GetBlockFloat( proxy, "scale", 1f ), GetBlockFloat( proxy, "offset", 0f ),
						GetBlockFloat( proxy, "minval", -float.MaxValue ), GetBlockFloat( proxy, "maxval", float.MaxValue ) ),
					"gaussiannoise" => new Vector4( GetBlockFloat( proxy, "mean", 0f ), GetBlockFloat( proxy, "halfwidth", 1f ),
						GetBlockFloat( proxy, "minval", -float.MaxValue ), GetBlockFloat( proxy, "maxval", float.MaxValue ) ),
					"uniformnoise" => new Vector4( GetBlockFloat( proxy, "minval", 0f ), GetBlockFloat( proxy, "maxval", 1f ), 0f, 0f ),
					"entityrandom" => new Vector4( GetBlockFloat( proxy, "scale", 1f ), GetBlockFloat( proxy, "minval", 0f ),
						GetBlockFloat( proxy, "maxval", 1f ), 0f ),
					"texturescroll" => new Vector4( GetBlockFloat( proxy, "texturescrollrate", 1f ),
						GetBlockFloat( proxy, "texturescrollangle", 0f ), GetBlockFloat( proxy, "texturescale", 1f ), 0f ),
					"texturetransform" => new Vector4( ResolveBlockVector( proxy, "translatevar", Vector4.Zero ).x,
						ResolveBlockVector( proxy, "translatevar", Vector4.Zero ).y,
						ResolveBlockFloat( proxy, "rotatevar", 0f ), ResolveBlockFloat( proxy, "scalevar", 1f ) ),
					_ => Vector4.Zero
				};
				// Source proxy names are case-insensitive; compare the same way as the branches above.
				if ( string.IsNullOrWhiteSpace( target )
					&& !type.Equals( "TextureTransform", StringComparison.OrdinalIgnoreCase )
					&& !type.Equals( "AnimatedTexture", StringComparison.OrdinalIgnoreCase ) ) continue;
				expressions.Add( new Titanfall2RuntimeExpression( type, target, sourceA, sourceB, parameters, sourceC, sourceD ) );
			}
		}
		return expressions;
	}

	IEnumerable<VmtBlock> EnumerateProxies( string name )
	{
		foreach ( var proxies in _root.Children.Where( static child => child.Name.Equals( "Proxies", StringComparison.OrdinalIgnoreCase ) ) )
		{
			foreach ( var proxy in proxies.Children )
				if ( proxy.Name.Equals( name, StringComparison.OrdinalIgnoreCase ) ) yield return proxy;
		}
	}

	string GetBlockString( VmtBlock block, string key )
	{
		return block.Values.TryGetValue( key, out var value ) ? ResolveRootVariable( value ) : null;
	}

	static string GetBlockRawString( VmtBlock block, string key )
	{
		return block.Values.TryGetValue( key, out var value ) ? value : null;
	}

	float GetBlockFloat( VmtBlock block, string key, float fallback )
	{
		return TryParseFloat( GetBlockString( block, key ), out var value ) ? value : fallback;
	}

	float ResolveBlockFloat( VmtBlock block, string key, float fallback )
	{
		var variable = GetBlockString( block, key );
		return TryParseFloat( variable, out var value ) ? value : fallback;
	}

	Vector4 ResolveBlockVector( VmtBlock block, string key, Vector4 fallback )
	{
		var variable = GetBlockString( block, key );
		return TryParseVector( variable, out var value ) ? value : fallback;
	}

	string ResolveRootVariable( string value )
	{
		if ( string.IsNullOrWhiteSpace( value ) || value[0] != '$' ) return value;
		return _root.Values.TryGetValue( value, out var resolved ) && !string.Equals( resolved, value, StringComparison.OrdinalIgnoreCase )
			? resolved
			: value;
	}

	static bool TryParseFloat( string text, out float value ) => float.TryParse(
		text,
		NumberStyles.Float,
		CultureInfo.InvariantCulture,
		out value );

	static bool TryParseVector( string text, out Vector4 value )
	{
		value = default;
		if ( string.IsNullOrWhiteSpace( text ) ) return false;
		var components = text.Trim().Trim( '[', ']', '{', '}', '(', ')' )
			.Split( [' ', '\t', ','], StringSplitOptions.RemoveEmptyEntries );
		if ( components.Length == 0 || components.Length > 4 ) return false;
		Span<float> parsed = stackalloc float[4] { 0f, 0f, 0f, 1f };
		for ( var index = 0; index < components.Length; index++ )
			if ( !TryParseFloat( components[index], out parsed[index] ) ) return false;
		value = new Vector4( parsed[0], components.Length > 1 ? parsed[1] : parsed[0],
			components.Length > 2 ? parsed[2] : parsed[0], components.Length > 3 ? parsed[3] : 1f );
		return true;
	}

	internal sealed class VmtBlock
	{
		internal string Name { get; }
		internal Dictionary<string, string> Values { get; } = new( StringComparer.OrdinalIgnoreCase );
		internal List<VmtBlock> Children { get; } = [];

		internal VmtBlock( string name ) => Name = name;
	}
}

readonly record struct VmtTextureTransform( float Scale, float RotationDegrees, Vector2 Translation )
{
	internal static VmtTextureTransform Identity => new( 1f, 0f, Vector2.Zero );
}

readonly record struct VmtNoiseRange( Vector2 Minimum, Vector2 Maximum, bool IsDefined );

static class Titanfall2VmtReader
{
	internal static bool TryRead( byte[] bytes, out Titanfall2VmtDefinition definition, out string error )
	{
		definition = null;
		error = null;
		if ( bytes is null || bytes.Length == 0 )
		{
			error = "VMT is empty.";
			return false;
		}

		try
		{
			var text = DecodeText( bytes );
			var reader = new TokenReader( text );
			if ( !reader.TryRead( out var shaderName ) || shaderName is "{" or "}" )
			{
				error = "VMT shader name is missing.";
				return false;
			}
			if ( !reader.TryRead( out var open ) || open != "{" )
			{
				error = $"VMT shader '{shaderName}' has no parameter block.";
				return false;
			}
			var root = new Titanfall2VmtDefinition.VmtBlock( shaderName );
			if ( !TryReadBlock( reader, root, out error ) ) return false;
			definition = new Titanfall2VmtDefinition( shaderName, root );
			return true;
		}
		catch ( Exception exception )
		{
			error = exception.Message;
			return false;
		}
	}

	static bool TryReadBlock( TokenReader reader, Titanfall2VmtDefinition.VmtBlock block, out string error, int depth = 0 )
	{
		error = null;
		// Nested blocks recurse; a corrupt file of repeated "k {" would otherwise
		// end in an uncatchable StackOverflowException instead of a parse error.
		if ( depth > 64 )
		{
			error = $"VMT block '{block.Name}' is nested too deeply.";
			return false;
		}
		while ( reader.TryRead( out var key ) )
		{
			if ( key == "}" ) return true;
			if ( key == "{" )
			{
				error = $"Unexpected '{{' inside VMT block '{block.Name}'.";
				return false;
			}
			if ( !reader.TryRead( out var value ) )
			{
				error = $"VMT key '{key}' has no value.";
				return false;
			}
			if ( value == "{" )
			{
				var child = new Titanfall2VmtDefinition.VmtBlock( key );
				if ( !TryReadBlock( reader, child, out error, depth + 1 ) ) return false;
				block.Children.Add( child );
			}
			else if ( value == "}" )
			{
				error = $"VMT key '{key}' is missing a value before '}}'.";
				return false;
			}
			else
			{
				block.Values[key] = value;
			}
		}
		error = $"VMT block '{block.Name}' is not closed.";
		return false;
	}

	static string DecodeText( byte[] bytes )
	{
		if ( bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF )
			return Encoding.UTF8.GetString( bytes, 3, bytes.Length - 3 );
		return Encoding.UTF8.GetString( bytes );
	}

	sealed class TokenReader( string text )
	{
		int _position;

		internal bool TryRead( out string token )
		{
			token = null;
			SkipTrivia();
			if ( _position >= text.Length ) return false;
			var current = text[_position];
			if ( current is '{' or '}' )
			{
				token = current.ToString();
				_position++;
				return true;
			}
			if ( current == '"' )
			{
				_position++;
				var start = _position;
				while ( _position < text.Length && text[_position] != '"' ) _position++;
				token = text[start.._position];
				if ( _position < text.Length ) _position++;
				return true;
			}

			var unquotedStart = _position;
			while ( _position < text.Length )
			{
				current = text[_position];
				if ( char.IsWhiteSpace( current ) || current is '{' or '}' ) break;
				if ( current == '/' && _position + 1 < text.Length && text[_position + 1] == '/' ) break;
				_position++;
			}
			token = text[unquotedStart.._position];
			return token.Length > 0;
		}

		void SkipTrivia()
		{
			while ( _position < text.Length )
			{
				if ( char.IsWhiteSpace( text[_position] ) )
				{
					_position++;
					continue;
				}
				if ( text[_position] == '/' && _position + 1 < text.Length && text[_position + 1] == '/' )
				{
					_position += 2;
					while ( _position < text.Length && text[_position] is not ('\r' or '\n') ) _position++;
					continue;
				}
				break;
			}
		}
	}
}
