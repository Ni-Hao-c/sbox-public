using System.Buffers.Binary;

namespace Sandbox.Mounting.Helldivers2;

/// <summary>
/// Builds a <see cref="Mesh"/> from a parsed unit (or geometry group) plus the matching GPU
/// payload. Vertices are decoded from the layout's item list, and each group becomes one draw
/// call: the first group claims the draw call a fresh mesh already owns while later groups are
/// added as sub meshes with their own base vertex, which is exactly how the engine's own scene
/// compiler divides a mesh into chunks.
///
/// Every overload takes an <see cref="Hd2MeshBuildOptions"/>, whose defaults are the mount's:
/// no up-axis rotation (the game's unit meshes measure as Z-up, like the engine) and a metres to
/// inches scale. It also carries an optional per-group material lookup, so each draw call can get
/// its own material; see <see cref="Hd2MeshBuildOptions.ResolveMaterial"/>.
/// </summary>
internal static class Hd2MeshBuilder
{
	/// <summary>Largest vertex array this mount will allocate, so a corrupt count cannot exhaust memory.</summary>
	const long MaxVertices = 1L << 24;

	/// <summary>Largest index array this mount will allocate.</summary>
	const long MaxIndices = 1L << 26;

	/// <summary>Builds the mesh at <paramref name="meshIndex"/> of a unit's own geometry, with the mount's default options.</summary>
	public static bool TryBuild( Hd2Unit unit, int meshIndex, int lodIndex, ReadOnlySpan<byte> gpuData, out Mesh mesh, out string error ) =>
		TryBuild( unit, meshIndex, lodIndex, gpuData, null, Hd2MeshBuildOptions.Default, out mesh, out error );

	/// <summary>Builds the mesh at <paramref name="meshIndex"/> of a unit's own geometry, with the mount's default options.</summary>
	public static bool TryBuild( Hd2Unit unit, int meshIndex, int lodIndex, ReadOnlySpan<byte> gpuData, Material material, out Mesh mesh, out string error ) =>
		TryBuild( unit, meshIndex, lodIndex, gpuData, material, Hd2MeshBuildOptions.Default, out mesh, out error );

	/// <summary>Builds the mesh at <paramref name="meshIndex"/> of a unit's own geometry.</summary>
	public static bool TryBuild( Hd2Unit unit, int meshIndex, int lodIndex, ReadOnlySpan<byte> gpuData, Hd2MeshBuildOptions options, out Mesh mesh, out string error ) =>
		TryBuild( unit, meshIndex, lodIndex, gpuData, null, options, out mesh, out error );

	/// <summary>Builds the mesh at <paramref name="meshIndex"/> of a unit's own geometry.</summary>
	public static bool TryBuild( Hd2Unit unit, int meshIndex, int lodIndex, ReadOnlySpan<byte> gpuData, Material material, Hd2MeshBuildOptions options, out Mesh mesh, out string error )
	{
		mesh = null;
		error = null;

		if ( unit is null )
		{
			error = "no unit was supplied.";
			return false;
		}

		if ( !Hd2UnitReader.TryResolveMesh( unit, meshIndex, lodIndex, out var source, out error ) ) return false;

		return TryBuild( source, unit.MeshLayouts, gpuData, material, $"hd2_unit_mesh{meshIndex}", options, out mesh, out error );
	}

	/// <summary>Builds one of a unit's meshes when its geometry lives in a geometry group, with the mount's default options.</summary>
	public static bool TryBuild( Hd2GeometryGroup group, Hd2Hash unitName, int meshIndex, ReadOnlySpan<byte> gpuData, out Mesh mesh, out string error ) =>
		TryBuild( group, unitName, meshIndex, gpuData, null, Hd2MeshBuildOptions.Default, out mesh, out error );

	/// <summary>Builds one of a unit's meshes when its geometry lives in a geometry group, with the mount's default options.</summary>
	public static bool TryBuild( Hd2GeometryGroup group, Hd2Hash unitName, int meshIndex, ReadOnlySpan<byte> gpuData, Material material, out Mesh mesh, out string error ) =>
		TryBuild( group, unitName, meshIndex, gpuData, material, Hd2MeshBuildOptions.Default, out mesh, out error );

	/// <summary>Builds one of a unit's meshes when its geometry lives in a geometry group.</summary>
	public static bool TryBuild( Hd2GeometryGroup group, Hd2Hash unitName, int meshIndex, ReadOnlySpan<byte> gpuData, Hd2MeshBuildOptions options, out Mesh mesh, out string error ) =>
		TryBuild( group, unitName, meshIndex, gpuData, null, options, out mesh, out error );

	/// <summary>Builds one of a unit's meshes when its geometry lives in a geometry group.</summary>
	public static bool TryBuild( Hd2GeometryGroup group, Hd2Hash unitName, int meshIndex, ReadOnlySpan<byte> gpuData, Material material, Hd2MeshBuildOptions options, out Mesh mesh, out string error )
	{
		mesh = null;
		error = null;

		if ( group is null )
		{
			error = "no geometry group was supplied.";
			return false;
		}

		if ( !group.Units.TryGetValue( unitName, out var entry ) )
		{
			error = $"the geometry group does not contain unit {unitName}.";
			return false;
		}

		if ( (uint)meshIndex >= (uint)entry.Meshes.Length )
		{
			error = $"mesh index {meshIndex} is out of range of unit {unitName}, which holds {entry.Meshes.Length} meshes.";
			return false;
		}

		return TryBuild( entry.Meshes[meshIndex], group.MeshLayouts, gpuData, material, $"hd2_{unitName}_mesh{meshIndex}", options, out mesh, out error );
	}

	/// <summary>
	/// Builds one mesh against a layout list and a GPU payload, with the mount's default options.
	/// <paramref name="gpuData"/> must be the GPU stream of the resource the layouts came from: the
	/// unit's own stream for <see cref="Hd2Unit.MeshLayouts"/>, or the geometry group's stream for
	/// <see cref="Hd2GeometryGroup.MeshLayouts"/>.
	/// </summary>
	public static bool TryBuild( Hd2UnitMesh source, Hd2MeshLayout[] layouts, ReadOnlySpan<byte> gpuData, Material material, string name, out Mesh mesh, out string error ) =>
		TryBuild( source, layouts, gpuData, material, name, Hd2MeshBuildOptions.Default, out mesh, out error );

	/// <summary>
	/// Builds one mesh against a layout list and a GPU payload with the mount's default options.
	/// </summary>
	public static bool TryBuild( Hd2UnitMesh source, Hd2MeshLayout[] layouts, ReadOnlySpan<byte> gpuData, Hd2MeshBuildOptions options, string name, out Mesh mesh, out string error ) =>
		TryBuild( source, layouts, gpuData, null, name, options, out mesh, out error );

	/// <summary>
	/// Builds one mesh. <paramref name="gpuData"/> must be the GPU stream of the resource the
	/// layouts came from: the unit's own stream for <see cref="Hd2Unit.MeshLayouts"/>, or the
	/// geometry group's stream for <see cref="Hd2GeometryGroup.MeshLayouts"/>.
	///
	/// <paramref name="options"/> carries the up-axis rotation, the metres to inches scale and the
	/// optional per-group material lookup. Positions and bounds go through the rotation and scale,
	/// normals through the rotation and a re-normalise, UVs through neither, and no index is
	/// reversed because the rotation preserves handedness and the scale is uniform.
	/// </summary>
	public static bool TryBuild( Hd2UnitMesh source, Hd2MeshLayout[] layouts, ReadOnlySpan<byte> gpuData, Material material, string name, Hd2MeshBuildOptions options, out Mesh mesh, out string error )
	{
		mesh = null;
		error = null;

		try
		{
			if ( source is null )
			{
				error = "no mesh was supplied.";
				return false;
			}

			if ( layouts is null )
			{
				error = "no mesh layouts were supplied.";
				return false;
			}

			var layoutIndex = source.Header.LayoutIndex;
			if ( layoutIndex < 0 )
			{
				error = $"the mesh has no vertex layout (layout index {layoutIndex}).";
				return false;
			}

			if ( layoutIndex >= layouts.Length )
			{
				error = $"the mesh's layout index {layoutIndex} is out of range of the {layouts.Length} layouts it was given.";
				return false;
			}

			var layout = layouts[layoutIndex];
			if ( layout.VertexStride == 0 )
			{
				error = $"layout {layoutIndex} declares a zero vertex stride.";
				return false;
			}

			if ( !layout.TryFindItem( Hd2MeshLayoutItemType.Position, out var positionItem ) )
			{
				error = $"layout {layoutIndex} has no position item, so no mesh can be built from it.";
				return false;
			}

			var hasNormal = layout.TryFindItem( Hd2MeshLayoutItemType.Normal, out var normalItem );
			var hasUv = layout.TryFindItem( Hd2MeshLayoutItemType.UVCoords, out var uvItem );

			if ( source.Groups.Length == 0 )
			{
				error = "the mesh has no groups.";
				return false;
			}

			var totalVertices = 0L;
			var totalIndices = 0L;
			foreach ( var group in source.Groups )
			{
				totalVertices += group.NumVertices;
				totalIndices += group.NumIndices;
			}

			if ( totalVertices <= 0 )
			{
				error = "the mesh's groups hold no vertices.";
				return false;
			}

			if ( totalVertices > MaxVertices )
			{
				error = $"the mesh's groups hold {totalVertices} vertices, beyond this mount's {MaxVertices} vertex limit.";
				return false;
			}

			if ( totalIndices < 3 || totalIndices > MaxIndices )
			{
				error = $"the mesh's groups hold {totalIndices} indices, which is outside the supported 3 to {MaxIndices} range.";
				return false;
			}

			var indexStride = layout.NumIndices > 0 ? (int)( layout.IndicesSize / layout.NumIndices ) : 0;
			if ( indexStride != 2 && indexStride != 4 )
			{
				error = $"layout {layoutIndex} declares a {indexStride} byte index stride; only 16- and 32-bit indices are supported.";
				return false;
			}

			var vertices = new Hd2Vertex[totalVertices];
			var indices = new int[totalIndices];
			var vertexBase = new int[source.Groups.Length];
			var indexBase = new int[source.Groups.Length];

			var vertexCursor = 0;
			for ( var g = 0; g < source.Groups.Length; g++ )
			{
				var group = source.Groups[g];
				vertexBase[g] = vertexCursor;

				var groupStart = (long)layout.VertexOffset + (long)group.VertexOffset * layout.VertexStride;
				for ( var i = 0; i < group.NumVertices; i++ )
				{
					var vertexOffset = groupStart + (long)i * layout.VertexStride;
					if ( !TryDecodeVertex( gpuData, vertexOffset, positionItem, normalItem, hasNormal, uvItem, hasUv, options, out vertices[vertexCursor], out error ) )
						return false;

					vertexCursor++;
				}
			}

			// Group indices are relative to the group's own first vertex, so they are copied as
			// they are and the group's base vertex is handed to the draw call instead.
			var indexCursor = 0;
			for ( var g = 0; g < source.Groups.Length; g++ )
			{
				var group = source.Groups[g];
				indexBase[g] = indexCursor;

				var groupStart = (long)layout.IndexOffset + (long)group.IndexOffset * indexStride;
				for ( var i = 0; i < group.NumIndices; i++ )
				{
					if ( !TryReadIndex( gpuData, groupStart + (long)i * indexStride, indexStride, out var index, out error ) ) return false;

					if ( index >= group.NumVertices )
					{
						error = $"group {g} index {i} is {index}, past the group's own {group.NumVertices} vertices.";
						return false;
					}

					indices[indexCursor] = (int)index;
					indexCursor++;
				}
			}

			var bounds = new BBox { Mins = float.MaxValue, Maxs = float.MinValue };
			foreach ( var vertex in vertices ) bounds = bounds.AddPoint( vertex.Position );

			// Each draw call takes the material its own group resolves to; without a resolver that
			// is simply the single material handed to this call.
			Material MaterialFor( int groupIndex ) =>
				options.ResolveMaterial is null ? material : options.ResolveMaterial( source.MaterialOf( groupIndex ) );

			var built = new Mesh( name, MaterialFor( 0 ) );
			built.CreateVertexBuffer( vertices.Length, vertices );
			built.CreateIndexBuffer( indices.Length, indices );
			built.Bounds = bounds;

			// A fresh mesh already owns one draw call, which the first group claims; the rest are
			// added as sub meshes carrying their own base vertex.
			built.SetVertexRange( 0, (int)source.Groups[0].NumVertices );
			built.SetIndexRange( 0, (int)source.Groups[0].NumIndices );

			for ( var g = 1; g < source.Groups.Length; g++ )
			{
				var group = source.Groups[g];
				if ( group.NumIndices == 0 ) continue;

				built.AddSubMesh( MaterialFor( g ), indexBase[g], (int)group.NumIndices, vertexBase[g], (int)group.NumVertices );
			}

			mesh = built;
			return true;
		}
		catch ( Exception exception )
		{
			mesh = null;
			error = $"building the mesh failed: {exception.Message}";
			return false;
		}
	}

	static bool TryDecodeVertex(
		ReadOnlySpan<byte> gpu,
		long vertexOffset,
		in Hd2MeshLayoutItem position,
		in Hd2MeshLayoutItem normal,
		bool hasNormal,
		in Hd2MeshLayoutItem uv,
		bool hasUv,
		in Hd2MeshBuildOptions options,
		out Hd2Vertex vertex,
		out string error )
	{
		vertex = default;
		error = null;

		Span<float> components = stackalloc float[4];

		if ( !TryReadComponents( gpu, vertexOffset + position.Offset, position, components, out var count, out error ) ) return false;
		if ( count < 3 )
		{
			error = $"the position item's {Hd2MeshLayoutFormats.Describe( position.Format )} has {count} components.";
			return false;
		}

		var decodedPosition = new Vector3( components[0], components[1], components[2] );
		vertex.Position = Hd2AxisConversion.ToEngine( decodedPosition, options.ConvertToZUp, options.EffectiveUnitScale );

		if ( hasNormal )
		{
			Vector3 decodedNormal;

			if ( normal.Format is Hd2MeshLayoutItemFormat.Vec4R10G10B10A2Unorm or Hd2MeshLayoutItemFormat.Vec4R10G10B10A2Typeless )
			{
				if ( !TryReadPackedWord( gpu, vertexOffset + normal.Offset, out var packed, out error ) ) return false;

				Hd2PackedNormal.Decode( packed, out decodedNormal, out _, out _ );
			}
			else
			{
				if ( !TryReadComponents( gpu, vertexOffset + normal.Offset, normal, components, out count, out error ) ) return false;
				if ( count < 3 )
				{
					error = $"the normal item's {Hd2MeshLayoutFormats.Describe( normal.Format )} has {count} components.";
					return false;
				}

				decodedNormal = new Vector3( components[0], components[1], components[2] );
			}

			// The decoded value is not exactly unit to begin with, and the optional rotation cannot
			// change a direction's length in exact maths, so it is re-normalized either way. The
			// uniform position scale never reaches normals.
			var rotatedNormal = options.ConvertToZUp ? Hd2AxisConversion.ToZUp( decodedNormal ) : decodedNormal;
			vertex.Normal = Hd2AxisConversion.Normalize( rotatedNormal );
		}

		if ( hasUv )
		{
			if ( !TryReadComponents( gpu, vertexOffset + uv.Offset, uv, components, out count, out error ) ) return false;
			if ( count < 2 )
			{
				error = $"the UV item's {Hd2MeshLayoutFormats.Describe( uv.Format )} has {count} components.";
				return false;
			}

			vertex.TexCoord = new Vector2( components[0], components[1] );
		}

		return true;
	}

	/// <summary>
	/// Reads one vertex item as up to four float components. Integer and packed formats are
	/// widened rather than rescaled, except the packed 10/10/10/2 word, which is normalised per
	/// channel; the caller decides what those numbers mean.
	/// </summary>
	static bool TryReadComponents( ReadOnlySpan<byte> gpu, long offset, in Hd2MeshLayoutItem item, Span<float> components, out int count, out string error )
	{
		count = 0;
		error = null;

		var reader = new Hd2PayloadReader( gpu );
		if ( !reader.TrySlice( offset, item.Size, out var data ) )
		{
			error = $"{reader.Error} (while reading the {item} of a vertex)";
			return false;
		}

		switch ( item.Format )
		{
			case Hd2MeshLayoutItemFormat.F32:
				count = 1;
				components[0] = BinaryPrimitives.ReadSingleLittleEndian( data );
				break;

			case Hd2MeshLayoutItemFormat.Vec2F:
			case Hd2MeshLayoutItemFormat.Vec3F:
			case Hd2MeshLayoutItemFormat.Vec4F:
				count = item.Size / 4;
				for ( var i = 0; i < count; i++ )
					components[i] = BinaryPrimitives.ReadSingleLittleEndian( data.Slice( i * 4, 4 ) );
				break;

			case Hd2MeshLayoutItemFormat.F16:
			case Hd2MeshLayoutItemFormat.Vec2F16:
			case Hd2MeshLayoutItemFormat.Vec3F16:
			case Hd2MeshLayoutItemFormat.Vec4F16:
				count = item.Size / 2;
				for ( var i = 0; i < count; i++ )
					components[i] = (float)BitConverter.UInt16BitsToHalf( BinaryPrimitives.ReadUInt16LittleEndian( data.Slice( i * 2, 2 ) ) );
				break;

			case Hd2MeshLayoutItemFormat.Rgba8:
				count = 4;
				for ( var i = 0; i < count; i++ )
					components[i] = data[i] / 255f;
				break;

			case Hd2MeshLayoutItemFormat.S8:
			case Hd2MeshLayoutItemFormat.Vec2S8:
			case Hd2MeshLayoutItemFormat.Vec3S8:
			case Hd2MeshLayoutItemFormat.Vec4S8:
				count = item.Size;
				for ( var i = 0; i < count; i++ )
					components[i] = (sbyte)data[i];
				break;

			case Hd2MeshLayoutItemFormat.U32:
			case Hd2MeshLayoutItemFormat.Vec2U32:
			case Hd2MeshLayoutItemFormat.Vec3U32:
			case Hd2MeshLayoutItemFormat.Vec4U32:
				count = item.Size / 4;
				for ( var i = 0; i < count; i++ )
					components[i] = BinaryPrimitives.ReadUInt32LittleEndian( data.Slice( i * 4, 4 ) );
				break;

			case Hd2MeshLayoutItemFormat.Vec4R10G10B10A2Typeless:
			case Hd2MeshLayoutItemFormat.Vec4R10G10B10A2Unorm:
			{
				var packed = BinaryPrimitives.ReadUInt32LittleEndian( data );
				count = 4;
				components[0] = (packed & 0x3ff) / 1023f;
				components[1] = ((packed >> 10) & 0x3ff) / 1023f;
				components[2] = ((packed >> 20) & 0x3ff) / 1023f;
				components[3] = ((packed >> 30) & 0x3) / 3f;
				break;
			}

			default:
				error = $"the vertex item format {Hd2MeshLayoutFormats.Describe( item.Format )} cannot be read.";
				return false;
		}

		return true;
	}

	static bool TryReadPackedWord( ReadOnlySpan<byte> gpu, long offset, out uint packed, out string error )
	{
		packed = 0;
		error = null;

		var reader = new Hd2PayloadReader( gpu );
		if ( !reader.TrySlice( offset, 4, out var data ) )
		{
			error = reader.Error;
			return false;
		}

		packed = BinaryPrimitives.ReadUInt32LittleEndian( data );
		return true;
	}

	static bool TryReadIndex( ReadOnlySpan<byte> gpu, long offset, int stride, out uint index, out string error )
	{
		index = 0;
		error = null;

		var reader = new Hd2PayloadReader( gpu );
		if ( !reader.TrySlice( offset, stride, out var data ) )
		{
			error = reader.Error;
			return false;
		}

		index = stride == 4
			? BinaryPrimitives.ReadUInt32LittleEndian( data )
			: BinaryPrimitives.ReadUInt16LittleEndian( data );

		return true;
	}
}
