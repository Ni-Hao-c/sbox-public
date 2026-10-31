using Titanfall2;
using Titanfall2.Formats;

partial class MapLoader
{
	readonly Dictionary<string, float> _aggregatedPropRadii = new( StringComparer.OrdinalIgnoreCase );

	void AddStaticPropsToWorld( Titanfall2BspReader.ParsedBsp parsed,
		Dictionary<WorldRenderCell, Dictionary<WorldMaterialKey, MapMeshGroup>> groups,
		HashSet<string> materials )
	{
		_aggregatedPropRadii.Clear();
		if ( !Titanfall2StreamingSettings.AggregateStaticProps || Titanfall2StreamingSettings.UseBspLightmaps ) return;
		var timer = System.Diagnostics.Stopwatch.StartNew();
		var vertices = 0;
		var instances = 0;
		foreach ( var modelGroup in parsed.StaticProps.GroupBy( prop => prop.ModelPath, StringComparer.OrdinalIgnoreCase ) )
		{
			if ( string.IsNullOrEmpty( modelGroup.Key ) || IsVistaModelPath( modelGroup.Key ) ) continue;
			var props = modelGroup.ToArray();
			if ( props.Length > Titanfall2StreamingSettings.AggregatePropMaxCopies
				|| props.Any( prop => prop.Skin != 0 || prop.LightProbeColor != System.Numerics.Vector3.One ) ) continue;
			if ( Host.GetByPath( $"mount://{Host.Ident}/{modelGroup.Key}.vmdl" ) is not ModelLoader loader
				|| !loader.TryReadAggregateGeometry( out var geometry ) ) continue;
			var meshes = geometry.Meshes.Where( mesh => mesh.Vertices.Length > 0 && mesh.Indices.Length >= 3
				&& !ShouldSkipWorldMaterial( NormalizeMaterialName( mesh.MaterialName ) ) ).ToArray();
			if ( meshes.Length == 0 || meshes.Any( mesh =>
			{
				var metadata = GetMaterialMetadata( NormalizeMaterialName( mesh.MaterialName ) );
				return metadata.IsTranslucent || metadata.IsWater || metadata.IsDecal;
			} ) ) continue;
			var required = meshes.Sum( mesh => mesh.Indices.Length / 3 * 3 ) * props.Length;
			if ( vertices + required > Titanfall2StreamingSettings.AggregatePropVertexBudget ) continue;
			// Validate the whole model before changing any world group. A rejected
			// model must remain entirely on the ordinary renderer/collision path.
			if ( meshes.Any( mesh => mesh.Indices.Any( index => (uint)index >= mesh.Vertices.Length ) ) ) continue;
			var bounds = new BBox { Mins = float.MaxValue, Maxs = float.MinValue };
			foreach ( var mesh in meshes )
				foreach ( var vertex in mesh.Vertices ) bounds = bounds.AddPoint( ToSandbox( vertex.Position ) );
			foreach ( var prop in props )
			{
				var rotation = Rotation.From( new Angles( prop.Angles.X, prop.Angles.Y, prop.Angles.Z ) );
				var transform = new Transform( ToSandbox( prop.Origin ), rotation, MathF.Max( prop.Scale, 0.001f ) );
				foreach ( var mesh in meshes ) AppendStaticPropMesh( groups, materials, mesh, transform );
			}
			_aggregatedPropRadii.Add( modelGroup.Key, bounds.Size.Length * 0.5f );
			vertices += required;
			instances += props.Length;
		}
		Log.Info( $"Titanfall 2 static props baked into world: {instances} props from {_aggregatedPropRadii.Count} models, "
			+ $"{vertices}/{Titanfall2StreamingSettings.AggregatePropVertexBudget} vertices, {timer.Elapsed.TotalSeconds:0.00}s; "
			+ "shared world materials/cell fragments; repeated, transparent and LOD-bearing props retain their original path." );
	}

	static void AppendStaticPropMesh( Dictionary<WorldRenderCell, Dictionary<WorldMaterialKey, MapMeshGroup>> groups,
		HashSet<string> materials, Titanfall2MeshExtractor.MeshGeometry mesh, Transform transform )
	{
		var name = NormalizeMaterialName( mesh.MaterialName );
		materials.Add( name );
		var transformed = new TitanfallMapVertex[mesh.Vertices.Length];
		for ( var i = 0; i < transformed.Length; i++ )
		{
			var source = mesh.Vertices[i];
			transformed[i] = new TitanfallMapVertex
			{
				Position = transform.PointToWorld( ToSandbox( source.Position ) ),
				Normal = (transform.Rotation * ToSandbox( source.Normal )).Normal,
				TexCoord = ToSandbox( source.TexCoord ),
				Color = source.HasColor ? new Color32( source.Color.R, source.Color.G, source.Color.B, source.Color.A ) : new Color32( 255, 255, 255, 255 )
			};
		}
		// The same spatial/material key as BSP triangles: eligible props really
		// join the terrain's vertex/index buffer, rather than just sharing a parent.
		for ( var i = 0; i + 2 < mesh.Indices.Length; i += 3 )
		{
			var a = transformed[mesh.Indices[i]];
			var b = transformed[mesh.Indices[i + 1]];
			var c = transformed[mesh.Indices[i + 2]];
			var cell = WorldRenderCell.FromTriangle( a.Position, b.Position, c.Position, Titanfall2StreamingSettings.WorldRenderCellSize );
			var group = GetRenderGroup( groups, cell, new WorldMaterialKey( name, -1 ) );
			var offset = group.Vertices.Count;
			group.Vertices.Add( a ); group.Vertices.Add( b ); group.Vertices.Add( c );
			group.Indices.Add( offset ); group.Indices.Add( offset + 2 ); group.Indices.Add( offset + 1 );
			group.Bounds = group.Bounds.AddPoint( a.Position ).AddPoint( b.Position ).AddPoint( c.Position );
		}
	}
}
