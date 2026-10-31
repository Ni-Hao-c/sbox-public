using System.Globalization;

/// <summary>
/// One registry evaluates all active VMT proxy expressions. A mounted map adds a
/// single updater component, avoiding one ticking component per material.
/// </summary>
static class Titanfall2MaterialAnimationRegistry
{
	sealed class Entry
	{
		internal readonly WeakReference<Material> Material;
		internal readonly string Name;
		internal readonly Titanfall2RuntimeExpression[] Expressions;
		internal readonly Dictionary<string, float> Values = new( StringComparer.OrdinalIgnoreCase );
		internal readonly Dictionary<string, Vector4> Vectors = new( StringComparer.OrdinalIgnoreCase );
		internal readonly bool LegacyLayers;
		// Per-expression data derived from strings once, so the per-frame evaluation
		// does not allocate normalized target names or "[0]"/"[1]" lookup keys.
		internal readonly int[] TargetKinds;
		internal readonly string[] SourceAComponentX;
		internal readonly string[] SourceAComponentY;
		// Last value written to each material parameter. Material.Set is skipped when
		// an expression produces the same value as the previous frame.
		internal readonly float[] AppliedValues = [float.NaN, float.NaN, float.NaN, float.NaN];
		internal readonly Vector4[] AppliedLayers = [new Vector4( float.NaN ), new Vector4( float.NaN )];
		// Vector names and their component lookup keys, built once instead of formatting
		// "name[0]".."name[3]" on every vector write frame.
		internal readonly Dictionary<string, string[]> ComponentKeys = new( StringComparer.OrdinalIgnoreCase );
		internal readonly Dictionary<string, string> LegacyVectorTargets = new( StringComparer.OrdinalIgnoreCase );

		internal Entry( Material material, string name, IReadOnlyList<Titanfall2RuntimeExpression> expressions )
		{
			Material = new WeakReference<Material>( material );
			Name = name ?? string.Empty;
			Expressions = expressions.Select( static expression => expression with { Type = expression.Type?.ToLowerInvariant() ?? string.Empty } ).ToArray();
			LegacyLayers = material.ShaderName.Contains( "titanfall2_vmt_unlit_two_texture", StringComparison.Ordinal );
			TargetKinds = new int[Expressions.Length];
			SourceAComponentX = new string[Expressions.Length];
			SourceAComponentY = new string[Expressions.Length];
			for ( var index = 0; index < Expressions.Length; index++ )
			{
				var expression = Expressions[index];
				TargetKinds[index] = ClassifyTarget( expression.Target );
				if ( string.IsNullOrWhiteSpace( expression.SourceA ) ) continue;
				SourceAComponentX[index] = expression.SourceA + "[0]";
				SourceAComponentY[index] = expression.SourceA + "[1]";
			}
		}
	}

	const int TargetNone = -1;
	const int TargetOpacity = 0;
	const int TargetEmissive = 1;
	const int TargetFresnel = 2;
	const int TargetDetailBlend = 3;

	static readonly object Sync = new();
	static readonly List<Entry> Entries = [];
	static float _lastUpdateTime = float.NegativeInfinity;
	static int _animatorCount;

	internal static void Register( Material material, string name, IReadOnlyList<Titanfall2RuntimeExpression> expressions )
	{
		if ( material is null || expressions is null || expressions.Count == 0 ) return;
		// The factory has already applied static parameters. Do not tick a
		// material whose VMT contains constants but no actual proxy program.
		if ( expressions.All( static expression => expression.Type is "Constant" or "VectorConstant" ) ) return;
		lock ( Sync )
		{
			for ( var index = Entries.Count - 1; index >= 0; index-- )
			{
				if ( !Entries[index].Material.TryGetTarget( out var existing ) ) Entries.RemoveAt( index );
				else if ( ReferenceEquals( existing, material ) ) return;
			}
			Entries.Add( new Entry( material, name, expressions ) );
		}
	}

	internal static void Update( float time )
	{
		if ( MathF.Abs( time - _lastUpdateTime ) <= float.Epsilon ) return;
		_lastUpdateTime = time;
		lock ( Sync )
		{
			for ( var index = Entries.Count - 1; index >= 0; index-- )
			{
				var entry = Entries[index];
				if ( !entry.Material.TryGetTarget( out var material ) || !material.IsValid )
				{
					Entries.RemoveAt( index );
					continue;
				}
				Evaluate( entry, material, time );
			}
		}
	}

	internal static void Activate()
	{
		lock ( Sync ) _animatorCount++;
	}

	internal static void Deactivate()
	{
		lock ( Sync )
		{
			_animatorCount = Math.Max( 0, _animatorCount - 1 );
			// Materials survive scene/play transitions in the resource cache. Their
			// loaders will not register again on the next map load; retain live weak
			// entries so stopping the last scene does not permanently stop its proxies.
			if ( _animatorCount == 0 ) Entries.RemoveAll( static entry => !entry.Material.TryGetTarget( out _ ) );
		}
	}

	static void Evaluate( Entry entry, Material material, float time )
	{
		for ( var index = 0; index < entry.Expressions.Length; index++ )
		{
			var expression = entry.Expressions[index];
			if ( string.IsNullOrWhiteSpace( expression.Target ) ) continue;
			var type = expression.Type;
			var p = expression.Parameters;
			if ( type == "vectorconstant" )
			{
				entry.Vectors[expression.Target] = p;
				continue;
			}
			if ( type == "texturescroll" )
			{
				var rate = Resolve( entry, expression.SourceA, p.x );
				var angle = Resolve( entry, expression.SourceB, p.y ) * MathF.PI / 180f;
				var translation = new Vector4( MathF.Cos( angle ) * rate * time, MathF.Sin( angle ) * rate * time, 0f, 0f );
				// Bound periodic translations before float precision degrades in a long session.
				translation.x -= MathF.Floor( translation.x ); translation.y -= MathF.Floor( translation.y );
				entry.Vectors[expression.Target] = translation;
				var scale = Resolve( entry, expression.SourceC, p.z );
				// Source scroll matrices scale about UV (0,0); our shared transform
				// shader scales about (0.5,0.5). Compensate without changing vector outputs.
				var originOffset = (scale - 1f) * 0.5f;
				ApplyLayerTransform( entry, material, expression.Target,
					new Vector4( scale, 0f, translation.x + originOffset, translation.y + originOffset ) );
				continue;
			}
			if ( type == "texturetransform" )
			{
				var translation = ResolveVector( entry, index, expression.SourceA, p );
				var rotation = Resolve( entry, expression.SourceB, p.z );
				var scale = Resolve( entry, expression.SourceC, p.w );
				var transform = new Vector4( scale, rotation * MathF.PI / 180f, translation.x, translation.y );
				entry.Vectors[expression.Target] = transform;
				ApplyLayerTransform( entry, material, expression.Target, transform );
				continue;
			}
			var a = Resolve( entry, expression.SourceA );
			var b = Resolve( entry, expression.SourceB );
			if ( type is "equals" or "lessorequal" or "selectfirstifnonzero" or "add" or "subtract" or "multiply" or "divide" or "frac" or "int" )
			{
				var selected = type == "lessorequal" ? (a <= b ? expression.SourceC : expression.SourceD)
					: type == "selectfirstifnonzero" ? (IsNonZero( entry, expression.SourceA ) ? expression.SourceA : expression.SourceB) : expression.SourceA;
				if ( !expression.Target.EndsWith( ']' ) && (entry.Vectors.ContainsKey( expression.Target ) || (selected is not null && entry.Vectors.ContainsKey( selected ))) )
				{
					var left = ReadVector( entry, selected ); var right = ReadVector( entry, expression.SourceB );
					float Component( float x, float y ) => type switch
					{
						"add" => x + y,
						"subtract" => x - y,
						"multiply" => x * y,
						"divide" => y == 0f ? x : x / y,
						"frac" => x - MathF.Truncate( x ),
						"int" => MathF.Truncate( x ),
						_ => x
					};
					WriteVector( entry, material, expression.Target, new Vector4( Component( left.x, right.x ), Component( left.y, right.y ), Component( left.z, right.z ), Component( left.w, right.w ) ) );
					continue;
				}
			}
			var value = type switch
			{
				"constant" => p.x,
				"currenttime" => time * p.x,
				"linearramp" => p.x + time * p.y,
				"sine" => EvaluateSine( p, time ),
				"add" => a + b,
				"subtract" => a - b,
				"multiply" => a * b,
				"divide" => b == 0f ? a : a / b,
				"frac" => a - MathF.Truncate( a ),
				"int" => MathF.Truncate( a ),
				"abs" => MathF.Abs( a ),
				"lessorequal" => Resolve( entry, a <= b ? expression.SourceC : expression.SourceD ),
				"selectfirstifnonzero" => a != 0f ? a : b,
				"wrapminmax" => p.y <= p.x ? p.x : p.x + (a - p.x) - MathF.Floor( (a - p.x) / (p.y - p.x) ) * (p.y - p.x),
				"exponential" => Math.Clamp( p.x * MathF.Exp( a + p.y ), MathF.Min( p.z, p.w ), MathF.Max( p.z, p.w ) ),
				"gaussiannoise" => Gaussian( entry, expression, time ),
				"clamp" => Math.Clamp( a, MathF.Min( p.x, p.y ), MathF.Max( p.x, p.y ) ),
				"remapvalclamped" => RemapClamped( a, p.x, p.y, p.z, p.w ),
				"equals" => a,
				"uniformnoise" => Lerp( p.x, p.y, Hash01( entry.Name, expression.Target, (int)MathF.Floor( time * 30f ) ) ),
				"entityrandom" => Lerp( p.y, p.z, Hash01( entry.Name, expression.Target, 0 ) ) * p.x,
				_ => float.NaN
			};
			if ( string.IsNullOrWhiteSpace( expression.Target ) || !float.IsFinite( value ) ) continue;
			entry.Values[expression.Target] = value;
			if ( TryComponent( expression.Target, out var vectorName, out var component ) )
			{
				var vector = entry.Vectors.GetValueOrDefault( vectorName );
				vector = component switch { 0 => vector with { x = value }, 1 => vector with { y = value }, 2 => vector with { z = value }, _ => vector with { w = value } };
				WriteVector( entry, material, vectorName, vector );
			}
			ApplyTarget( entry, material, entry.TargetKinds[index], value );
		}
	}

	static float EvaluateSine( Vector4 parameters, float time )
	{
		// Source treats timeOffset as a delay and only replaces an exactly zero period.
		var period = parameters.z == 0f ? 1f : parameters.z;
		return parameters.x + (parameters.y - parameters.x)
			* (0.5f + 0.5f * MathF.Sin( MathF.Tau * (time - parameters.w) / period ));
	}

	static bool TryComponent( string name, out string vector, out int component )
	{
		vector = null; component = -1;
		if ( name is null || name.Length < 4 || name[^3] != '[' || name[^1] != ']' || name[^2] is < '0' or > '3' ) return false;
		vector = name[..^3]; component = name[^2] - '0'; return true;
	}

	static Vector4 ReadVector( Entry entry, string name ) => name is not null && entry.Vectors.TryGetValue( name, out var vector ) ? vector : new Vector4( Resolve( entry, name ) );
	static bool IsNonZero( Entry entry, string name ) => ReadVector( entry, name ) is var vector && (vector.x != 0f || vector.y != 0f || vector.z != 0f);
	static void WriteVector( Entry entry, Material material, string name, Vector4 vector )
	{
		if ( !float.IsFinite( vector.x ) || !float.IsFinite( vector.y ) || !float.IsFinite( vector.z ) || !float.IsFinite( vector.w ) ) return;
		entry.Vectors[name] = vector;
		entry.Values.Remove( name );
		// A later vector assignment supersedes earlier component assignments.
		if ( !entry.ComponentKeys.TryGetValue( name, out var componentKeys ) )
		{
			componentKeys = [$"{name}[0]", $"{name}[1]", $"{name}[2]", $"{name}[3]"];
			entry.ComponentKeys[name] = componentKeys;
		}
		foreach ( var key in componentKeys ) entry.Values.Remove( key );
		if ( !entry.LegacyLayers ) return;
		if ( !entry.LegacyVectorTargets.TryGetValue( name, out var target ) )
		{
			target = name.ToLowerInvariant() switch { "$color" or "$layercolor1" => "g_vVmtColor1", "$color2" or "$layercolor2" => "g_vVmtColor2", _ => string.Empty };
			entry.LegacyVectorTargets[name] = target;
		}
		if ( target.Length > 0 ) material.Set( target, vector );
	}
	static float Gaussian( Entry entry, Titanfall2RuntimeExpression expression, float time )
	{
		var frame = (int)MathF.Floor( time * 30f );
		var u = MathF.Max( 0.000001f, Hash01( entry.Name, expression.Target, unchecked(frame * 2) ) );
		var v = Hash01( entry.Name, expression.Target, unchecked(frame * 2 + 1) );
		var p = expression.Parameters;
		return Math.Clamp( p.x + p.y * MathF.Sqrt( -2f * MathF.Log( u ) ) * MathF.Cos( MathF.Tau * v ), MathF.Min( p.z, p.w ), MathF.Max( p.z, p.w ) );
	}

	static void ApplyLayerTransform( Entry entry, Material material, string target, Vector4 transform )
	{
		if ( !entry.LegacyLayers || !float.IsFinite( transform.x ) || !float.IsFinite( transform.y )
			|| !float.IsFinite( transform.z ) || !float.IsFinite( transform.w ) ) return;
		var layer = target.Equals( "$basetexturetransform", StringComparison.OrdinalIgnoreCase ) ? 0
			: target.Equals( "$texture2transform", StringComparison.OrdinalIgnoreCase ) ? 1 : -1;
		if ( layer < 0 || entry.AppliedLayers[layer] == transform ) return;
		entry.AppliedLayers[layer] = transform;
		material.Set( layer == 0 ? "g_vVmtTexture1Transform" : "g_vVmtTexture2Transform", transform );
	}

	static Vector4 ResolveVector( Entry entry, int index, string source, Vector4 fallback )
	{
		if ( string.IsNullOrWhiteSpace( source ) ) return fallback;
		var vector = entry.Vectors.GetValueOrDefault( source, fallback );
		if ( entry.Values.TryGetValue( entry.SourceAComponentX[index], out var x ) ) vector.x = x;
		if ( entry.Values.TryGetValue( entry.SourceAComponentY[index], out var y ) ) vector.y = y;
		return vector;
	}

	static float Resolve( Entry entry, string source, float fallback = 0f )
	{
		if ( string.IsNullOrWhiteSpace( source ) ) return fallback;
		if ( entry.Values.TryGetValue( source, out var value ) ) return value;
		if ( TryComponent( source, out var name, out var component ) && entry.Vectors.TryGetValue( name, out var vector ) )
			return component switch { 0 => vector.x, 1 => vector.y, 2 => vector.z, _ => vector.w };
		if ( entry.Vectors.TryGetValue( source, out vector ) ) return vector.x;
		return float.TryParse( source, NumberStyles.Float, CultureInfo.InvariantCulture, out value ) ? value : fallback;
	}

	static int ClassifyTarget( string target )
	{
		if ( string.IsNullOrWhiteSpace( target ) ) return TargetNone;
		var normalized = target.Replace( "_", string.Empty ).ToLowerInvariant();
		if ( normalized is "$alpha" or "alpha" || normalized.Contains( "opacity" ) ) return TargetOpacity;
		if ( normalized.Contains( "emissive" ) || normalized.Contains( "selfillum" ) ) return TargetEmissive;
		if ( normalized.Contains( "fresnel" ) ) return TargetFresnel;
		if ( normalized.Contains( "detailblend" ) ) return TargetDetailBlend;
		return TargetNone;
	}

	static void ApplyTarget( Entry entry, Material material, int kind, float value )
	{
		if ( kind == TargetNone ) return;
		value = kind is TargetOpacity or TargetDetailBlend ? Math.Clamp( value, 0f, 1f ) : MathF.Max( 0f, value );
		if ( entry.AppliedValues[kind] == value ) return;
		entry.AppliedValues[kind] = value;
		if ( kind == TargetOpacity )
			material.Set( entry.LegacyLayers ? "g_flVmtMaterialAlpha" : "g_flT2MaterialOpacity", value );
		else if ( kind == TargetEmissive )
			material.Set( "g_flT2EmissiveStrength", value );
		else if ( kind == TargetFresnel )
			material.Set( "g_flT2FresnelStrength", value );
		else
			material.Set( "g_flT2DetailBlend", value );
	}

	static float RemapClamped( float value, float sourceMinimum, float sourceMaximum, float resultMinimum, float resultMaximum )
	{
		var range = sourceMaximum - sourceMinimum;
		if ( MathF.Abs( range ) <= 0.000001f ) return resultMinimum;
		return Lerp( resultMinimum, resultMaximum, Math.Clamp( (value - sourceMinimum) / range, 0f, 1f ) );
	}

	static float Hash01( string materialName, string target, int frame )
	{
		unchecked
		{
			uint hash = 2166136261;
			foreach ( var character in materialName ?? string.Empty ) hash = (hash ^ character) * 16777619;
			foreach ( var character in target ?? string.Empty ) hash = (hash ^ character) * 16777619;
			hash = (hash ^ (uint)frame) * 16777619;
			return (hash & 0x00FFFFFF) / 16777215f;
		}
	}

	static float Lerp( float minimum, float maximum, float amount ) => minimum + (maximum - minimum) * amount;
}

[Library]
public sealed class Titanfall2MaterialAnimator : Component, Component.ExecuteInEditor, Component.DontExecuteOnServer, Sandbox.Internal.IUpdateSubscriber
{
	protected override void OnEnabled() => Titanfall2MaterialAnimationRegistry.Activate();

	protected override void OnUpdate() => Titanfall2MaterialAnimationRegistry.Update( Time.Now );

	protected override void OnDisabled() => Titanfall2MaterialAnimationRegistry.Deactivate();
}
