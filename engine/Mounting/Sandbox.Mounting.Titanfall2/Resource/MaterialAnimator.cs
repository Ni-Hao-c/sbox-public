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

		internal Entry( Material material, string name, IReadOnlyList<Titanfall2RuntimeExpression> expressions )
		{
			Material = new WeakReference<Material>( material );
			Name = name ?? string.Empty;
			Expressions = expressions.ToArray();
		}
	}

	static readonly object Sync = new();
	static readonly List<Entry> Entries = [];
	static float _lastUpdateTime = float.NegativeInfinity;
	static int _animatorCount;

	internal static void Register( Material material, string name, IReadOnlyList<Titanfall2RuntimeExpression> expressions )
	{
		if ( material is null || expressions is null || expressions.Count == 0 ) return;
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
			if ( _animatorCount == 0 ) Entries.Clear();
		}
	}

	static void Evaluate( Entry entry, Material material, float time )
	{
		foreach ( var expression in entry.Expressions )
		{
			var type = expression.Type?.ToLowerInvariant() ?? string.Empty;
			var a = Resolve( entry, expression.SourceA );
			var b = Resolve( entry, expression.SourceB );
			var p = expression.Parameters;
			var value = type switch
			{
				"constant" => p.x,
				"currenttime" => time * p.x,
				"linearramp" => p.x + time * p.y,
				"sine" => p.x + (p.y - p.x) * (0.5f + 0.5f * MathF.Sin( MathF.Tau * (time + p.w) / MathF.Max( 0.0001f, p.z ) )),
				"add" => a + b,
				"subtract" => a - b,
				"multiply" => a * b,
				"clamp" => Math.Clamp( a, MathF.Min( p.x, p.y ), MathF.Max( p.x, p.y ) ),
				"remapvalclamped" => RemapClamped( a, p.x, p.y, p.z, p.w ),
				"equals" => a,
				"uniformnoise" => Lerp( p.x, p.y, Hash01( entry.Name, expression.Target, (int)MathF.Floor( time * 30f ) ) ),
				"entityrandom" => Lerp( p.y, p.z, Hash01( entry.Name, expression.Target, 0 ) ) * p.x,
				_ => float.NaN
			};
			if ( string.IsNullOrWhiteSpace( expression.Target ) || !float.IsFinite( value ) ) continue;
			entry.Values[expression.Target] = value;
			ApplyTarget( material, expression.Target, value );
		}
	}

	static float Resolve( Entry entry, string source )
	{
		if ( string.IsNullOrWhiteSpace( source ) ) return 0f;
		if ( entry.Values.TryGetValue( source, out var value ) ) return value;
		return float.TryParse( source, NumberStyles.Float, CultureInfo.InvariantCulture, out value ) ? value : 0f;
	}

	static void ApplyTarget( Material material, string target, float value )
	{
		var normalized = target.Replace( "_", string.Empty ).ToLowerInvariant();
		if ( normalized is "$alpha" or "alpha" || normalized.Contains( "opacity" ) )
			material.Set( "g_flT2MaterialOpacity", Math.Clamp( value, 0f, 1f ) );
		else if ( normalized.Contains( "emissive" ) || normalized.Contains( "selfillum" ) )
			material.Set( "g_flT2EmissiveStrength", MathF.Max( 0f, value ) );
		else if ( normalized.Contains( "fresnel" ) )
			material.Set( "g_flT2FresnelStrength", MathF.Max( 0f, value ) );
		else if ( normalized.Contains( "detailblend" ) )
			material.Set( "g_flT2DetailBlend", Math.Clamp( value, 0f, 1f ) );
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
public sealed class Titanfall2MaterialAnimator : Component, Component.DontExecuteOnServer
{
	protected override void OnStart() => Titanfall2MaterialAnimationRegistry.Activate();

	protected override void OnUpdate() => Titanfall2MaterialAnimationRegistry.Update( Time.Now );

	protected override void OnDestroy() => Titanfall2MaterialAnimationRegistry.Deactivate();
}
