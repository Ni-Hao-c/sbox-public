using System.Buffers.Binary;

namespace Sandbox.Mounting.Helldivers2;

/// <summary>
/// The 116-byte header of a <c>unit</c> payload: the resource hashes the unit refers to and the
/// offsets of every section inside the same payload. All of the section offsets are absolute,
/// and zero means the section is absent.
/// </summary>
internal readonly struct Hd2UnitHeader
{
	public const int StoredSize = 116;

	/// <summary>Hash of the bone (skeleton) resource, or zero when the unit is not skinned.</summary>
	public readonly Hd2Hash Bones;

	/// <summary>Hash of the shared <c>geometry_group</c> resource that holds this unit's geometry, or zero.</summary>
	public readonly Hd2Hash GeometryGroup;

	/// <summary>Unknown. The reference implementation reads it and never uses it.</summary>
	public readonly Hd2Hash UnkHash00;

	/// <summary>Hash of the state machine resource, or zero.</summary>
	public readonly Hd2Hash StateMachine;

	public readonly uint LodGroupListOffset;
	public readonly uint JointListOffset;
	public readonly uint LightListOffset;

	// UNVERIFIED: three further offsets the reference implementation names UnkOffset02/03/04 and
	// reads but never follows. They are kept for diagnostics only.
	public readonly uint UnkOffset02;
	public readonly uint UnkOffset03;
	public readonly uint UnkOffset04;

	public readonly uint SkeletonMapListOffset;
	public readonly uint MeshLayoutListOffset;
	public readonly uint MeshDataOffset;
	public readonly uint MeshInfoListOffset;
	public readonly uint TerrainInfoListOffset;
	public readonly uint MaterialListOffset;

	Hd2UnitHeader(
		Hd2Hash bones,
		Hd2Hash geometryGroup,
		Hd2Hash unkHash00,
		Hd2Hash stateMachine,
		uint lodGroupListOffset,
		uint jointListOffset,
		uint lightListOffset,
		uint unkOffset02,
		uint unkOffset03,
		uint unkOffset04,
		uint skeletonMapListOffset,
		uint meshLayoutListOffset,
		uint meshDataOffset,
		uint meshInfoListOffset,
		uint terrainInfoListOffset,
		uint materialListOffset )
	{
		Bones = bones;
		GeometryGroup = geometryGroup;
		UnkHash00 = unkHash00;
		StateMachine = stateMachine;
		LodGroupListOffset = lodGroupListOffset;
		JointListOffset = jointListOffset;
		LightListOffset = lightListOffset;
		UnkOffset02 = unkOffset02;
		UnkOffset03 = unkOffset03;
		UnkOffset04 = unkOffset04;
		SkeletonMapListOffset = skeletonMapListOffset;
		MeshLayoutListOffset = meshLayoutListOffset;
		MeshDataOffset = meshDataOffset;
		MeshInfoListOffset = meshInfoListOffset;
		TerrainInfoListOffset = terrainInfoListOffset;
		MaterialListOffset = materialListOffset;
	}

	/// <summary>Reads the header at the reader's current offset and advances past it.</summary>
	public static bool TryRead( ref Hd2PayloadReader reader, out Hd2UnitHeader header )
	{
		header = default;

		if ( !reader.TrySlice( reader.Offset, StoredSize, out var data ) )
			return false;

		header = new Hd2UnitHeader(
			new Hd2Hash( BinaryPrimitives.ReadUInt64LittleEndian( data.Slice( 8, 8 ) ) ),
			new Hd2Hash( BinaryPrimitives.ReadUInt64LittleEndian( data.Slice( 16, 8 ) ) ),
			new Hd2Hash( BinaryPrimitives.ReadUInt64LittleEndian( data.Slice( 24, 8 ) ) ),
			new Hd2Hash( BinaryPrimitives.ReadUInt64LittleEndian( data.Slice( 32, 8 ) ) ),
			BinaryPrimitives.ReadUInt32LittleEndian( data.Slice( 48, 4 ) ),
			BinaryPrimitives.ReadUInt32LittleEndian( data.Slice( 52, 4 ) ),
			BinaryPrimitives.ReadUInt32LittleEndian( data.Slice( 56, 4 ) ),
			BinaryPrimitives.ReadUInt32LittleEndian( data.Slice( 60, 4 ) ),
			BinaryPrimitives.ReadUInt32LittleEndian( data.Slice( 76, 4 ) ),
			BinaryPrimitives.ReadUInt32LittleEndian( data.Slice( 80, 4 ) ),
			BinaryPrimitives.ReadUInt32LittleEndian( data.Slice( 88, 4 ) ),
			BinaryPrimitives.ReadUInt32LittleEndian( data.Slice( 92, 4 ) ),
			BinaryPrimitives.ReadUInt32LittleEndian( data.Slice( 96, 4 ) ),
			BinaryPrimitives.ReadUInt32LittleEndian( data.Slice( 100, 4 ) ),
			BinaryPrimitives.ReadUInt32LittleEndian( data.Slice( 104, 4 ) ),
			BinaryPrimitives.ReadUInt32LittleEndian( data.Slice( 112, 4 ) ) );

		reader.TrySkip( StoredSize );
		return true;
	}

	public override string ToString() =>
		$"bones {Bones}, geometry group {GeometryGroup}, meshes at {MeshInfoListOffset}, layouts at {MeshLayoutListOffset}, materials at {MaterialListOffset}";
}
