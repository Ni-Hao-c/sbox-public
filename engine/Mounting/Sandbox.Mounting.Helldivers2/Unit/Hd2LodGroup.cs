using System.Buffers.Binary;

namespace Sandbox.Mounting.Helldivers2;

/// <summary>One detail level of a LOD group: its detail distances and the meshes it covers.</summary>
internal readonly struct Hd2LodEntry
{
	public readonly float Max;
	public readonly float Min;

	// UNVERIFIED: the reference implementation reads this array (as a count followed by that
	// many uint32s) but never interprets it. Reading it as indices into the unit's mesh info
	// list is the only interpretation that fits an entry carrying detail thresholds, but it has
	// not been confirmed against a real asset.
	public readonly uint[] MeshIndices;

	public Hd2LodEntry( float max, float min, uint[] meshIndices )
	{
		Max = max;
		Min = min;
		MeshIndices = meshIndices;
	}

	public override string ToString() => $"detail {Min:0.###}-{Max:0.###} over {MeshIndices.Length} meshes";
}

/// <summary>One 40-byte LOD footer.</summary>
internal readonly struct Hd2LodFooter
{
	public const int StoredSize = 40;

	// UNVERIFIED: the reference implementation names these six floats "MinMax" and reads them as
	// one array; whether they are two bounding boxes, three min/max pairs, or something else is
	// unknown, so they are kept raw.
	public readonly float[] MinMax;

	// UNVERIFIED: unlabelled by the reference implementation.
	public readonly float UnkFloat00;
	public readonly float UnkFloat01;
	public readonly uint Unk00;
	public readonly uint Unk01;

	public Hd2LodFooter( ReadOnlySpan<byte> data )
	{
		MinMax =
		[
			BinaryPrimitives.ReadSingleLittleEndian( data.Slice( 0, 4 ) ),
			BinaryPrimitives.ReadSingleLittleEndian( data.Slice( 4, 4 ) ),
			BinaryPrimitives.ReadSingleLittleEndian( data.Slice( 8, 4 ) ),
			BinaryPrimitives.ReadSingleLittleEndian( data.Slice( 12, 4 ) ),
			BinaryPrimitives.ReadSingleLittleEndian( data.Slice( 16, 4 ) ),
			BinaryPrimitives.ReadSingleLittleEndian( data.Slice( 20, 4 ) )
		];

		UnkFloat00 = BinaryPrimitives.ReadSingleLittleEndian( data.Slice( 24, 4 ) );
		Unk00 = BinaryPrimitives.ReadUInt32LittleEndian( data.Slice( 28, 4 ) );
		UnkFloat01 = BinaryPrimitives.ReadSingleLittleEndian( data.Slice( 32, 4 ) );
		Unk01 = BinaryPrimitives.ReadUInt32LittleEndian( data.Slice( 36, 4 ) );
	}

	public override string ToString() => $"[{string.Join( ", ", MinMax )}]";
}

/// <summary>
/// A group of detail levels, named by a thin hash, holding one entry per detail level and a
/// footer per entry.
/// </summary>
internal sealed class Hd2LodGroup
{
	/// <summary>Unknown. The reference implementation reads it and never uses it.</summary>
	public uint Unk00 { get; }

	/// <summary>Thin hash naming the group. The reference implementation calls it "UnkHash00".</summary>
	public Hd2ThinHash Name { get; }

	/// <summary>Unknown. The reference implementation reads it and never uses it.</summary>
	public uint Unk01 { get; }

	public Hd2LodEntry[] Entries { get; }

	public Hd2LodFooter[] Footers { get; }

	public Hd2LodGroup( uint unk00, Hd2ThinHash name, uint unk01, Hd2LodEntry[] entries, Hd2LodFooter[] footers )
	{
		Unk00 = unk00;
		Name = name;
		Unk01 = unk01;
		Entries = entries;
		Footers = footers;
	}

	public override string ToString() => $"{Name}: {Entries.Length} detail levels, {Footers.Length} footers";
}
