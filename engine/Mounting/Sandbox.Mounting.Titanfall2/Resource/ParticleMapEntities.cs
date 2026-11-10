using System.Globalization;
using System.Text;

/// <summary>Reads map FX from both the BSP and external entity partitions.</summary>
static class Titanfall2ParticleMapEntities
{
	internal sealed record Result( IReadOnlyList<Titanfall2ParticleMapEntity> Entities, int SkyboxCount,
		int UnresolvedControlPoints, int UnresolvedParents );

	internal static Result Read( params IReadOnlyList<Dictionary<string, string>>[] sources )
	{
		var rows = Merge( sources );
		var parents = new Dictionary<string, Dictionary<string, string>>( StringComparer.OrdinalIgnoreCase );
		foreach ( var row in rows )
			if ( row.TryGetValue( "model", out var model ) && model.StartsWith( '*' ) ) parents.TryAdd( model, row );
		var namedPoints = new Dictionary<string, Vector3>( StringComparer.OrdinalIgnoreCase );
		foreach ( var row in rows )
			if ( row.TryGetValue( "targetname", out var name ) && !string.IsNullOrWhiteSpace( name )
				&& TryTransform( row, parents, 0, out var transform ) ) namedPoints.TryAdd( name, transform.Position );

		var entities = new List<Titanfall2ParticleMapEntity>();
		var skybox = 0;
		var unresolvedPoints = 0;
		var unresolvedParents = 0;
		foreach ( var row in rows )
		{
			if ( !row.GetValueOrDefault( "classname", string.Empty ).Equals( "info_particle_system", StringComparison.OrdinalIgnoreCase ) ) continue;
			if ( row.TryGetValue( "start_active", out var active ) && IsFalse( active ) ) continue;
			if ( IsTrue( row.GetValueOrDefault( "in_skybox" ) ) ) { skybox++; continue; }
			if ( !row.TryGetValue( "effect_name", out var effect ) || string.IsNullOrWhiteSpace( effect ) ) continue;
			if ( !TryTransform( row, parents, 0, out var transform ) )
			{
				if ( !string.IsNullOrWhiteSpace( row.GetValueOrDefault( "parentBrushModel" ) ) ) unresolvedParents++;
				continue;
			}
			var points = new List<Titanfall2ParticleMapControlPoint>();
			for ( var i = 1; i < 64; ++i )
			{
				if ( !row.TryGetValue( $"cpoint{i}", out var target ) || string.IsNullOrWhiteSpace( target ) ) continue;
				if ( namedPoints.TryGetValue( target.Trim(), out var position ) ) points.Add( new( i, position ) );
				else unresolvedPoints++;
			}
			var warmup = Number( row.GetValueOrDefault( "warmup_time" ), 0f );
			// model/scale describe the editor icon; PCFs already author world-space sizes.
			entities.Add( new( effect.Trim(), row.GetValueOrDefault( "targetname", string.Empty ), transform.Position,
				transform.Rotation.Angles(), 1f, points, Math.Clamp( warmup, 0f, 60f ), IsTrue( row.GetValueOrDefault( "warmup_and_pause" ) ) ) );
		}
		return new( entities, skybox, unresolvedPoints, unresolvedParents );
	}

	// Suppress copies across partitions, retaining authored identical entities inside a partition.
	static List<Dictionary<string, string>> Merge( IReadOnlyList<Dictionary<string, string>>[] sources )
	{
		var seen = new HashSet<string>( StringComparer.Ordinal );
		var rows = new List<Dictionary<string, string>>();
		foreach ( var source in sources )
		{
			if ( source is null ) continue;
			var identities = new HashSet<string>( StringComparer.Ordinal );
			foreach ( var row in source )
			{
				var identity = new StringBuilder();
				foreach ( var pair in row.OrderBy( p => p.Key, StringComparer.OrdinalIgnoreCase ) )
					identity.Append( pair.Key.Length ).Append( ':' ).Append( pair.Key.ToLowerInvariant() )
						.Append( pair.Value.Length ).Append( ':' ).Append( pair.Value );
				var key = identity.ToString();
				if ( !seen.Contains( key ) ) rows.Add( row );
				identities.Add( key );
			}
			seen.UnionWith( identities );
		}
		return rows;
	}

	static bool TryTransform( Dictionary<string, string> row, Dictionary<string, Dictionary<string, string>> parents,
		int depth, out Transform transform )
	{
		transform = Transform.Zero;
		if ( depth > 16 || !TryVector( row.GetValueOrDefault( "origin" ), out var position ) ) return false;
		var angles = Vector3.Zero;
		if ( row.TryGetValue( "angles", out var text ) && !TryVector( text, out angles ) ) return false;
		transform = new Transform( position, new Angles( angles.x, angles.y, angles.z ).ToRotation() );
		if ( !row.TryGetValue( "parentBrushModel", out var parent ) || string.IsNullOrWhiteSpace( parent ) ) return true;
		if ( !parents.TryGetValue( parent.Trim(), out var parentRow ) || !TryTransform( parentRow, parents, depth + 1, out var parentTransform ) ) return false;
		transform = parentTransform.ToWorld( transform );
		return true;
	}

	static bool TryVector( string value, out Vector3 result )
	{
		result = default;
		if ( value is null ) return false;
		var parts = value.Split( (char[])null, StringSplitOptions.RemoveEmptyEntries );
		if ( parts.Length != 3 ) return false;
		var x = Number( parts[0], float.NaN ); var y = Number( parts[1], float.NaN ); var z = Number( parts[2], float.NaN );
		result = new( x, y, z );
		return float.IsFinite( x ) && float.IsFinite( y ) && float.IsFinite( z );
	}

	static float Number( string text, float fallback ) => float.TryParse( text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value )
		&& float.IsFinite( value ) ? value : fallback;
	static bool IsTrue( string value ) => value?.Trim() is { } text && (text == "1" || text.Equals( "true", StringComparison.OrdinalIgnoreCase ));
	static bool IsFalse( string value ) => value?.Trim() is { } text && (text == "0" || text.Equals( "false", StringComparison.OrdinalIgnoreCase ));
}
