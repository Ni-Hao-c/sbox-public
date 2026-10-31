namespace Sandbox.Mounting.Helldivers2;

/// <summary>
/// One 24-byte draw group of a mesh: which material slot it draws with and the vertex and
/// index range it covers. Index offsets are counted in indices, not bytes.
/// </summary>
internal readonly struct Hd2MeshGroup
{
	public const int StoredSize = 24;

	/// <summary>Index into the owning mesh's material list.</summary>
	public readonly uint MaterialIndex;

	/// <summary>First vertex of this group, relative to the mesh's vertex data.</summary>
	public readonly uint VertexOffset;

	public readonly uint NumVertices;

	/// <summary>First index of this group, relative to the mesh's index data.</summary>
	public readonly uint IndexOffset;

	public readonly uint NumIndices;

	/// <summary>Sequential group number inside the mesh.</summary>
	public readonly uint GroupIndex;

	public Hd2MeshGroup( ReadOnlySpan<byte> data )
	{
		MaterialIndex = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian( data );
		VertexOffset = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian( data[4..] );
		NumVertices = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian( data[8..] );
		IndexOffset = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian( data[12..] );
		NumIndices = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian( data[16..] );
		GroupIndex = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian( data[20..] );
	}

	public override string ToString() =>
		$"material {MaterialIndex}, {NumVertices} vertices at {VertexOffset}, {NumIndices} indices at {IndexOffset}";
}
