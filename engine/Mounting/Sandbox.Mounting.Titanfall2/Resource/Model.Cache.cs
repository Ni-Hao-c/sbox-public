using static Titanfall2ProcessedCache;

partial class ModelLoader
{
	sealed record StaticMeshGeometry( int Index, string MaterialName, TitanfallVertex[] Vertices, int[] Indices, BBox Bounds );
	sealed record StaticLodGeometry( StaticMeshGeometry[] Meshes, float Radius )
	{
		public int VertexCount => Meshes.Sum( mesh => mesh.Vertices.Length );
		public int TriangleCount => Meshes.Sum( mesh => mesh.Indices.Length / 3 );
	}

	bool TryReadStaticCache( string identity, out StaticLodGeometry[] levels )
	{
		StaticLodGeometry[] result = null;
		var hit = Titanfall2ProcessedCache.TryRead( "model", identity, reader =>
		{
			var count = reader.Count( StaticInstanceBandCount );
			if ( count != (_allLods ? StaticInstanceBandCount : 1) ) throw new InvalidDataException( "Cached LOD count mismatch." );
			result = new StaticLodGeometry[count];
			for ( var level = 0; level < count; level++ )
			{
				var radius = reader.ReadSingle();
				if ( !float.IsFinite( radius ) || radius < 0 ) throw new InvalidDataException( "Invalid cached model radius." );
				var meshes = new StaticMeshGeometry[reader.Count( 4096 )];
				for ( var i = 0; i < meshes.Length; i++ )
				{
					var index = reader.ReadInt32();
					var name = reader.Text();
					var bounds = reader.Array<BBox>( 1 );
					if ( bounds.Length != 1 ) throw new InvalidDataException( "Missing cached model bounds." );
					var vertices = reader.Array<TitanfallVertex>();
					var indices = reader.Array<int>();
					if ( indices.Length % 3 != 0 || indices.Any( vertex => (uint)vertex >= vertices.Length ) )
						throw new InvalidDataException( "Invalid cached model indices." );
					meshes[i] = new StaticMeshGeometry( index, name, vertices, indices, bounds[0] );
				}
				result[level] = new StaticLodGeometry( meshes, radius );
			}
		} );
		levels = hit ? result : null;
		return hit;
	}

	static void WriteStaticCache( string identity, StaticLodGeometry[] levels ) =>
		Titanfall2ProcessedCache.Write( "model", identity, writer =>
		{
			writer.Write( levels.Length );
			foreach ( var level in levels )
			{
				writer.Write( level.Radius ); writer.Write( level.Meshes.Length );
				foreach ( var mesh in level.Meshes )
				{
					writer.Write( mesh.Index ); WriteString( writer, mesh.MaterialName ); WriteArray( writer, new[] { mesh.Bounds } );
					WriteArray( writer, mesh.Vertices ); WriteArray( writer, mesh.Indices );
				}
			}
		} );
}
