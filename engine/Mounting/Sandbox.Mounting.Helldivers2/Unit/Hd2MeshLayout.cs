using System.Buffers.Binary;

namespace Sandbox.Mounting.Helldivers2;

/// <summary>What a vertex item of a mesh layout carries.</summary>
internal enum Hd2MeshLayoutItemType : uint
{
	Position = 0,
	Normal = 1,
	Tangent = 2,
	// Type 3 is missing from the format's own list; the reference implementation expects a
	// [4]float16 item there and reads nothing from it, so it is treated as skippable here.
	UVCoords = 4,
	Color = 5,
	BoneIndex = 6,
	BoneWeight = 7,
	SpeedTreeU = 8,
	SpeedTreeV = 9,
	SpeedTreeNormalX = 10,
	SpeedTreeNormalYZ = 11,
	Ignore = 12
}

/// <summary>The element formats a vertex item can use, numbered the way Stingray numbers them.</summary>
internal enum Hd2MeshLayoutItemFormat : uint
{
	F32 = 0,
	Vec2F = 1,
	Vec3F = 2,
	Vec4F = 3,
	Rgba8 = 4,
	U32 = 21,
	Vec2U32 = 22,
	Vec3U32 = 23,
	Vec4U32 = 24,
	S8 = 25,
	Vec2S8 = 26,
	Vec3S8 = 27,
	Vec4S8 = 28,
	Vec4R10G10B10A2Typeless = 29,
	Vec4R10G10B10A2Unorm = 30,
	F16 = 32,
	Vec2F16 = 33,
	Vec3F16 = 34,
	Vec4F16 = 35
}

/// <summary>Facts about vertex element formats, shared by the layout parser and the mesh builder.</summary>
internal static class Hd2MeshLayoutFormats
{
	/// <summary>Bytes one element occupies, or -1 for a format this mount cannot size.</summary>
	public static int SizeOf( Hd2MeshLayoutItemFormat format ) => format switch
	{
		Hd2MeshLayoutItemFormat.F32 => 4,
		Hd2MeshLayoutItemFormat.Vec2F => 8,
		Hd2MeshLayoutItemFormat.Vec3F => 12,
		Hd2MeshLayoutItemFormat.Vec4F => 16,
		Hd2MeshLayoutItemFormat.Rgba8 => 4,
		Hd2MeshLayoutItemFormat.U32 => 4,
		Hd2MeshLayoutItemFormat.Vec2U32 => 8,
		Hd2MeshLayoutItemFormat.Vec3U32 => 12,
		Hd2MeshLayoutItemFormat.Vec4U32 => 16,
		Hd2MeshLayoutItemFormat.S8 => 1,
		Hd2MeshLayoutItemFormat.Vec2S8 => 2,
		Hd2MeshLayoutItemFormat.Vec3S8 => 3,
		Hd2MeshLayoutItemFormat.Vec4S8 => 4,
		Hd2MeshLayoutItemFormat.Vec4R10G10B10A2Typeless => 4,
		Hd2MeshLayoutItemFormat.Vec4R10G10B10A2Unorm => 4,
		Hd2MeshLayoutItemFormat.F16 => 2,
		Hd2MeshLayoutItemFormat.Vec2F16 => 4,
		Hd2MeshLayoutItemFormat.Vec3F16 => 6,
		Hd2MeshLayoutItemFormat.Vec4F16 => 8,
		_ => -1
	};

	public static string Describe( Hd2MeshLayoutItemFormat format ) => format switch
	{
		Hd2MeshLayoutItemFormat.F32 => "float32",
		Hd2MeshLayoutItemFormat.Vec2F => "[2]float32",
		Hd2MeshLayoutItemFormat.Vec3F => "[3]float32",
		Hd2MeshLayoutItemFormat.Vec4F => "[4]float32",
		Hd2MeshLayoutItemFormat.Rgba8 => "[4]uint8",
		Hd2MeshLayoutItemFormat.U32 => "uint32",
		Hd2MeshLayoutItemFormat.Vec2U32 => "[2]uint32",
		Hd2MeshLayoutItemFormat.Vec3U32 => "[3]uint32",
		Hd2MeshLayoutItemFormat.Vec4U32 => "[4]uint32",
		Hd2MeshLayoutItemFormat.S8 => "int8",
		Hd2MeshLayoutItemFormat.Vec2S8 => "[2]int8",
		Hd2MeshLayoutItemFormat.Vec3S8 => "[3]int8",
		Hd2MeshLayoutItemFormat.Vec4S8 => "[4]int8",
		Hd2MeshLayoutItemFormat.Vec4R10G10B10A2Typeless => "[4]packed10",
		Hd2MeshLayoutItemFormat.Vec4R10G10B10A2Unorm => "[4]packed10unorm",
		Hd2MeshLayoutItemFormat.F16 => "float16",
		Hd2MeshLayoutItemFormat.Vec2F16 => "[2]float16",
		Hd2MeshLayoutItemFormat.Vec3F16 => "[3]float16",
		Hd2MeshLayoutItemFormat.Vec4F16 => "[4]float16",
		_ => $"unknown format {(uint)format}"
	};
}

/// <summary>
/// One vertex item of a mesh layout, resolved to the bytes it occupies inside a single vertex.
/// The items are listed in the order the bytes appear, so a vertex's field offsets are the
/// running sum of the item sizes.
/// </summary>
internal readonly struct Hd2MeshLayoutItem
{
	public readonly Hd2MeshLayoutItemType Type;
	public readonly Hd2MeshLayoutItemFormat Format;

	/// <summary>Which UV/colour/bone-index set this item is, when the layout carries several.</summary>
	public readonly uint Layer;

	/// <summary>Bytes this item occupies. Layouts naming a format this mount cannot size are rejected while parsing.</summary>
	public readonly int Size;

	/// <summary>Byte offset of this item inside one vertex.</summary>
	public readonly int Offset;

	public Hd2MeshLayoutItem( Hd2MeshLayoutItemType type, Hd2MeshLayoutItemFormat format, uint layer, int size, int offset )
	{
		Type = type;
		Format = format;
		Layer = layer;
		Size = size;
		Offset = offset;
	}

	public override string ToString() => $"{Type} {Hd2MeshLayoutFormats.Describe( Format )} layer {Layer} at +{Offset}";
}

/// <summary>
/// One 448-byte vertex layout: where a mesh's vertices and indices live in the GPU payload,
/// how far apart they are, and what each vertex field carries.
/// </summary>
internal sealed class Hd2MeshLayout
{
	public const int StoredSize = 448;
	public const int MaxItems = 16;

	/// <summary>Unknown; the reference implementation treats the three magic numbers as a sanity check.</summary>
	public uint Magic0 { get; private init; }
	public uint Magic1 { get; private init; }
	public uint Magic2 { get; private init; }

	public uint NumItems { get; private init; }

	/// <summary>Vertices across every mesh that uses this layout.</summary>
	public uint NumVertices { get; private init; }

	/// <summary>Bytes between consecutive vertices. May exceed <see cref="DeclaredVertexSize"/> when a vertex has trailing padding.</summary>
	public uint VertexStride { get; private init; }

	/// <summary>Indices across every mesh that uses this layout.</summary>
	public uint NumIndices { get; private init; }

	/// <summary>Absolute offset of the first vertex inside the unit's GPU payload.</summary>
	public uint VertexOffset { get; private init; }

	/// <summary>Size of the position data block, as declared by the layout.</summary>
	public uint PositionsSize { get; private init; }

	/// <summary>Absolute offset of the first index inside the unit's GPU payload.</summary>
	public uint IndexOffset { get; private init; }

	/// <summary>Size of the whole index block in bytes; divided by <see cref="NumIndices"/> it gives the index stride.</summary>
	public uint IndicesSize { get; private init; }

	/// <summary>The declared items, trimmed to <see cref="NumItems"/> entries, in vertex byte order.</summary>
	public Hd2MeshLayoutItem[] Items { get; private init; } = [];

	/// <summary>Bytes the items need in one vertex, as opposed to the padded <see cref="VertexStride"/>.</summary>
	public int DeclaredVertexSize { get; private init; }

	public static bool TryRead( ref Hd2PayloadReader reader, out Hd2MeshLayout layout )
	{
		layout = null;

		if ( !reader.TrySlice( reader.Offset, StoredSize, out var data ) )
			return false;

		var numItems = BinaryPrimitives.ReadUInt32LittleEndian( data.Slice( 328, 4 ) );
		if ( numItems > MaxItems )
			return reader.Fail( $"a mesh layout declares {numItems} vertex items; at most {MaxItems} fit in a vertex." );

		var items = new Hd2MeshLayoutItem[numItems];
		var declaredSize = 0;
		for ( var i = 0; i < items.Length; i++ )
		{
			// MeshLayoutItem: type, format, layer, then eight bytes the format does not use.
			var itemOffset = 8 + i * 20;
			var type = (Hd2MeshLayoutItemType)BinaryPrimitives.ReadUInt32LittleEndian( data.Slice( itemOffset, 4 ) );
			var format = (Hd2MeshLayoutItemFormat)BinaryPrimitives.ReadUInt32LittleEndian( data.Slice( itemOffset + 4, 4 ) );
			var layer = BinaryPrimitives.ReadUInt32LittleEndian( data.Slice( itemOffset + 8, 4 ) );
			var size = Hd2MeshLayoutFormats.SizeOf( format );
			if ( size <= 0 )
				return reader.Fail( $"vertex item {i} uses {Hd2MeshLayoutFormats.Describe( format )}, which this mount cannot size." );

			items[i] = new Hd2MeshLayoutItem( type, format, layer, size, declaredSize );
			declaredSize += size;
		}

		var vertexStride = BinaryPrimitives.ReadUInt32LittleEndian( data.Slice( 356, 4 ) );
		if ( (uint)declaredSize > vertexStride )
			return reader.Fail( $"the vertex items need {declaredSize} bytes but the layout's stride is {vertexStride}." );

		layout = new Hd2MeshLayout
		{
			Magic0 = BinaryPrimitives.ReadUInt32LittleEndian( data.Slice( 0, 4 ) ),
			Magic1 = BinaryPrimitives.ReadUInt32LittleEndian( data.Slice( 336, 4 ) ),
			Magic2 = BinaryPrimitives.ReadUInt32LittleEndian( data.Slice( 376, 4 ) ),
			NumItems = numItems,
			Items = items,
			DeclaredVertexSize = declaredSize,
			NumVertices = BinaryPrimitives.ReadUInt32LittleEndian( data.Slice( 352, 4 ) ),
			VertexStride = vertexStride,
			NumIndices = BinaryPrimitives.ReadUInt32LittleEndian( data.Slice( 392, 4 ) ),
			VertexOffset = BinaryPrimitives.ReadUInt32LittleEndian( data.Slice( 416, 4 ) ),
			PositionsSize = BinaryPrimitives.ReadUInt32LittleEndian( data.Slice( 420, 4 ) ),
			IndexOffset = BinaryPrimitives.ReadUInt32LittleEndian( data.Slice( 424, 4 ) ),
			IndicesSize = BinaryPrimitives.ReadUInt32LittleEndian( data.Slice( 428, 4 ) )
		};

		reader.TrySkip( StoredSize );
		return true;
	}

	/// <summary>
	/// Finds the item of a given type, preferring the lowest layer when a layout carries several
	/// (as it does for UV sets and bone index sets).
	/// </summary>
	public bool TryFindItem( Hd2MeshLayoutItemType type, out Hd2MeshLayoutItem item )
	{
		item = default;
		var found = false;

		foreach ( var candidate in Items )
		{
			if ( candidate.Type != type ) continue;
			if ( found && candidate.Layer >= item.Layer ) continue;

			item = candidate;
			found = true;
		}

		return found;
	}

	public override string ToString() =>
		$"{NumVertices} vertices (stride {VertexStride}, {NumItems} items), {NumIndices} indices (stride {( NumIndices == 0 ? 0 : IndicesSize / NumIndices )})";
}
