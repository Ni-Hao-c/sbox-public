using System.Runtime.CompilerServices;

partial class ModelLoader
{
	internal readonly record struct GpuGeometryVertex( Vector3 Position, Vector3 Normal, Vector2 TexCoord, Color32 Color );
	internal sealed record GpuGeometryMesh( Material Material, GpuGeometryVertex[] Vertices, int[] Indices );
	internal sealed record GpuGeometry( GpuGeometryMesh[][] Lods, ulong ResidualMask = 0 )
	{
		public bool IsMixed => ResidualMask != 0;
	}
	internal const string GpuMeshGroup = "titanfall2_gpu";
	internal const string ResidualMeshGroup = "titanfall2_residual";
	static readonly ConditionalWeakTable<Model, GpuGeometry> SharedGpuGeometry = new();

	internal static bool TryGetGpuGeometry( Model model, out GpuGeometry geometry ) => SharedGpuGeometry.TryGetValue( model, out geometry );

	internal static void RegisterGpuGeometry( Model model, StaticLodGeometry[] levels )
	{
		if ( !Titanfall2StreamingSettings.GpuStaticScene ) return;
		var meshes = levels.SelectMany( level => level.Meshes ).ToArray();
		if ( !meshes.Any( mesh => mesh.GpuCompatible ) ) return;
		var mixed = meshes.Any( mesh => !mesh.GpuCompatible );
		var residualMask = mixed ? model.Parts.Get( ResidualMeshGroup )?.Mask ?? 0 : 0;
		// A model already handled by the old CPU instancer must stay there if some
		// opaque custom shader cannot enter this backend. Partial ownership below is
		// only for the individual-object path, which can retain a residual mask.
		if ( mixed && (CanInstance( model ) || !Titanfall2StreamingSettings.GpuMixedProps || residualMask == 0) ) return;
		var result = new GpuGeometryMesh[StaticInstanceBandCount][];
		var resolved = 0;
		for ( var lod = 0; lod < result.Length; ++lod )
		{
			if ( levels[lod].Meshes.Length == 0 ) { result[lod] = result[resolved]; continue; }
			resolved = lod;
			// Resolve missing original LODs before partitioning. An existing LOD with no
			// compatible meshes is deliberately empty, not an alias of a finer partition.
			result[lod] = levels[lod].Meshes.Where( source => source.GpuCompatible ).Select( source => new GpuGeometryMesh( source.RuntimeMaterial,
				source.Vertices.Select( vertex => new GpuGeometryVertex( vertex.Position, vertex.Normal, vertex.TexCoord, vertex.Color ) ).ToArray(),
				source.Indices ) ).ToArray();
		}
		if ( result.SelectMany( lod => lod ).Any( mesh => !IsGpuSceneMaterial( mesh.Material ) ) ) return;
		SharedGpuGeometry.Add( model, new( result, residualMask ) );
	}

	internal static void AddStaticRenderMesh( ModelBuilder builder, Mesh mesh, int lod, bool gpuCompatible, bool mixed )
	{
		// Both choices are enabled by default, preserving the complete CPU model. The
		// GPU path selects only the residual group on the same native object. Keeping
		// all original LOD registrations also preserves genuinely empty residual LODs,
		// bounds and shared buffers without constructing another model or dummy mesh.
		if ( mixed ) builder.AddMesh( mesh, lod, gpuCompatible ? GpuMeshGroup : ResidualMeshGroup, 0 );
		else builder.AddMesh( mesh, lod );
	}

	static bool IsGpuSceneMaterial( Material material )
	{
		if ( material is null ) return false;
		var name = material.ShaderName.Replace( '\\', '/' );
		return name.EndsWith( "/titanfall2_default.shader", StringComparison.OrdinalIgnoreCase )
			|| name.EndsWith( "/titanfall2_cutout.shader", StringComparison.OrdinalIgnoreCase );
	}
}
