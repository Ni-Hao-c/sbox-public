using System.Runtime.InteropServices;
using Sandbox.Rendering;
using Titanfall2;

/// <summary>One map's immutable geometry/instance tables and GPU-written per-view output.</summary>
sealed partial class Titanfall2StaticPropGpuScene : SceneCustomObject
{
	[StructLayout( LayoutKind.Sequential )]
	internal struct Vertex
	{
		[VertexLayout.Position] public Vector3 Position;
		[VertexLayout.Normal] public Vector3 Normal;
		[VertexLayout.TexCoord] public Vector2 TexCoord;
		[VertexLayout.Color] public Color32 Color;
		[VertexLayout.TexCoord( 8 )] public uint VisibleBase;
		[VertexLayout.TexCoord( 9 )] public uint MaterialId;
	}

	[StructLayout( LayoutKind.Sequential )]
	internal struct Candidate
	{
		public Vector4 Sphere;
		public Vector4 OriginAndDistance;
		public uint SourceId, FirstBucket, CastShadows, Reserved;
		public uint Pvs0, Pvs1, Pvs2, Pvs3;
	}

	[StructLayout( LayoutKind.Sequential )]
	internal struct Bucket { public uint FirstVisible, Capacity; }
	[StructLayout( LayoutKind.Sequential )]
	internal struct Draw { public uint IndexCount, FirstIndex, Bucket; public int BaseVertex; }
	internal sealed record Instance( int SourceId, Model Model, Transform Transform, float Radius, float KeepDistance,
		bool CastShadows, Titanfall2ProbeData Probe, Titanfall2BspReader.VisibilityCellMask Pvs );
	sealed record MaterialGroup( Material Material, uint FirstDraw, uint DrawCount, bool Table, bool SimpleSurface );
	internal sealed record Layout( Vertex[] Vertices, uint[] Indices, Candidate[] Candidates, Bucket[] Buckets,
		Draw[] Draws, GpuInstanceTransform[] Transforms, Titanfall2ProbeData[] Probes, int VisibleCapacity,
		Material[] Materials, uint[] FirstDraws, uint[] DrawCounts, BBox Bounds,
		bool[] MaterialTables = null, Material[] SourceMaterials = null, Titanfall2GpuMaterialData[] MaterialData = null,
		bool[] SimpleSurfaces = null );

	internal static Layout Compile( IReadOnlyList<Instance> instances, int sourceCount )
	{
		if ( instances.Count == 0 ) throw new ArgumentException( "A GPU scene needs instances." );
		var vertices = new List<Vertex>();
		var indices = new List<uint>();
		var candidates = new List<Candidate>();
		var buckets = new List<Bucket>();
		var draws = new Dictionary<Material, List<Draw>>();
		var materialIds = new Dictionary<Material, uint>();
		var transforms = new GpuInstanceTransform[sourceCount];
		var probes = new Titanfall2ProbeData[sourceCount];
		var usedIds = new HashSet<int>();
		var visibleCapacity = 0;
		var bounds = instances[0].Model.Bounds.Transform( instances[0].Transform );
		foreach ( var modelGroup in instances.GroupBy( instance => instance.Model ) )
		{
			if ( !ModelLoader.TryGetGpuGeometry( modelGroup.Key, out var geometry ) )
				throw new InvalidDataException( "Unsupported geometry entered GPU scene compilation." );
			var members = modelGroup.ToArray();
			var firstBucket = checked((uint)buckets.Count);
			for ( var lod = 0; lod < 4; ++lod )
			{
				var bucket = new Bucket { FirstVisible = checked((uint)visibleCapacity), Capacity = checked((uint)members.Length) };
				visibleCapacity = checked(visibleCapacity + members.Length);
				buckets.Add( bucket );
				foreach ( var mesh in geometry.Lods[lod] )
				{
					if ( !materialIds.TryGetValue( mesh.Material, out var materialId ) )
					{
						materialId = (uint)materialIds.Count; materialIds.Add( mesh.Material, materialId );
					}
					var vertexBase = vertices.Count;
					var firstIndex = checked((uint)indices.Count);
					foreach ( var vertex in mesh.Vertices ) vertices.Add( new Vertex
					{
						Position = vertex.Position,
						Normal = vertex.Normal,
						TexCoord = vertex.TexCoord,
						Color = vertex.Color,
						VisibleBase = bucket.FirstVisible,
						MaterialId = materialId
					} );
					foreach ( var index in mesh.Indices )
					{
						if ( (uint)index >= mesh.Vertices.Length ) throw new InvalidDataException( "Invalid GPU geometry index." );
						indices.Add( checked((uint)index) );
					}
					if ( !draws.TryGetValue( mesh.Material, out var group ) ) draws.Add( mesh.Material, group = new() );
					group.Add( new Draw
					{
						IndexCount = checked((uint)mesh.Indices.Length),
						FirstIndex = firstIndex,
						BaseVertex = vertexBase,
						Bucket = firstBucket + (uint)lod
					} );
				}
			}
			foreach ( var member in members )
			{
				if ( (uint)member.SourceId >= sourceCount || !usedIds.Add( member.SourceId ) ) throw new InvalidDataException( "Invalid or duplicate GPU source ID." );
				var box = member.Model.Bounds.Transform( member.Transform );
				bounds = bounds.AddBBox( box );
				var center = box.Center;
				candidates.Add( new Candidate
				{
					Sphere = new Vector4( center, box.Size.Length * 0.5f ),
					OriginAndDistance = new Vector4( member.Transform.Position, member.KeepDistance ),
					SourceId = (uint)member.SourceId,
					FirstBucket = firstBucket,
					CastShadows = member.CastShadows ? 1u : 0u,
					Pvs0 = (uint)member.Pvs.Low,
					Pvs1 = (uint)(member.Pvs.Low >> 32),
					Pvs2 = (uint)member.Pvs.High,
					Pvs3 = (uint)(member.Pvs.High >> 32)
				} );
				transforms[member.SourceId] = GpuInstanceTransform.From( member.Transform );
				probes[member.SourceId] = member.Probe;
			}
		}
		var allDraws = new List<Draw>();
		var sourceMaterials = materialIds.OrderBy( pair => pair.Value ).Select( pair => pair.Key ).ToArray();
		// Only immutable 2D material inputs are tabled. Animated proxies and native cube
		// bindings keep their original material, including all future parameter changes.
		static bool CanTable( Material material ) => material.GetVector4( "g_flT2HasEnvironment" ).x < 0.5f
			&& material.GetFeature( "F_ADDITIVE_BLEND" ) == 0 && !Titanfall2MaterialAnimationRegistry.IsAnimated( material );
		var groups = draws.GroupBy( pair =>
		{
			var table = CanTable( pair.Key );
			// Only tabled immutable materials may select the specialized variant. Animated
			// parameters stay on the general path even if their initial values are zero.
			var simple = table && IsSimpleSurface( pair.Key.GetVector4( "g_vT2UvDistortion" ), pair.Key.GetVector4( "g_flT2DetailBlend" ).x );
			return (Shader: pair.Key.ShaderName, Filter: pair.Key.GetFeature( "F_TEXTURE_FILTERING" ), Table: table, Simple: simple, Identity: table ? null : pair.Key);
		} ).OrderByDescending( group => group.Key.Table ).ToArray();
		var materials = groups.Select( group => group.First().Key ).ToArray();
		var materialTables = groups.Select( group => group.Key.Table ).ToArray();
		var simpleSurfaces = groups.Select( group => group.Key.Simple ).ToArray();
		var materialData = sourceMaterials.Select( Titanfall2GpuMaterialData.Capture ).ToArray();
		var starts = new uint[materials.Length];
		var counts = new uint[materials.Length];
		for ( var i = 0; i < materials.Length; ++i )
		{
			starts[i] = (uint)allDraws.Count;
			foreach ( var member in groups[i] ) allDraws.AddRange( member.Value );
			counts[i] = (uint)allDraws.Count - starts[i];
		}
		// Stock CommandList stores these three arguments numerically in float fields.
		// Bound the mount layout to the exact-integer range instead of patching the engine.
		if ( allDraws.Count >= 16_777_216 ) throw new InvalidDataException( "GPU scene exceeds stock command argument precision." );
		var layout = new Layout( vertices.ToArray(), indices.ToArray(), candidates.ToArray(), buckets.ToArray(), allDraws.ToArray(),
			transforms, probes, visibleCapacity, materials, starts, counts, bounds, materialTables, sourceMaterials, materialData, simpleSurfaces );
		ValidateBufferBudget( layout );
		return layout;
	}

	internal static bool IsSimpleSurface( Vector4 distortion, float detailBlend ) =>
		distortion.x == 0 && distortion.y == 0 && float.IsFinite( detailBlend ) && detailBlend <= 0;

	static BufferBudget ValidateBufferBudget( Layout layout )
	{
		var budget = GetBufferBudget( layout, Titanfall2StreamingSettings.GpuStaticSceneViewSlots );
		if ( budget.TotalBytes > (long)Titanfall2StreamingSettings.GpuStaticSceneBudgetMb * 1024 * 1024 )
			throw new InvalidDataException( $"GPU static scene requires {budget.TotalBytes / (1024 * 1024)} MiB including {budget.ViewSlots} view slots and conservative outputs, exceeding its configured budget." );
		return budget;
	}
}
