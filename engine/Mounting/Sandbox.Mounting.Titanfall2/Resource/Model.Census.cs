using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

partial class ModelLoader
{
	internal sealed record StaticLodStatistics( int ResolvedBand, int SubMeshes, long Vertices, long Indices );
	internal sealed record StaticGeometryStatistics( long VertexBytes, long IndexBytes, StaticLodStatistics[] Lods );
	// Count geometry without retaining the CPU vertex arrays or extending the Model's lifetime.
	static readonly ConditionalWeakTable<Model, StaticGeometryStatistics> GeometryStatistics = new();

	internal static bool TryGetGeometryStatistics( Model model, out StaticGeometryStatistics statistics )
		=> GeometryStatistics.TryGetValue( model, out statistics );

	static StaticGeometryStatistics CompileGeometryStatistics( StaticLodGeometry[] levels )
	{
		var resolved = 0;
		var lods = new StaticLodStatistics[StaticInstanceBandCount];
		long vertexBytes = 0, indexBytes = 0;
		for ( var level = 0; level < lods.Length; ++level )
		{
			if ( levels[level].Meshes.Length > 0 ) resolved = level;
			var ownVertices = levels[level].Meshes.Sum( mesh => (long)mesh.Vertices.Length );
			var ownIndices = levels[level].Meshes.Sum( mesh => (long)mesh.Indices.Length );
			vertexBytes += ownVertices * Marshal.SizeOf<TitanfallVertex>();
			indexBytes += ownIndices * sizeof( int );
			lods[level] = new( resolved, levels[resolved].Meshes.Length,
				levels[resolved].Meshes.Sum( mesh => (long)mesh.Vertices.Length ),
				levels[resolved].Meshes.Sum( mesh => (long)mesh.Indices.Length ) );
		}
		return new( vertexBytes, indexBytes, lods );
	}
}
