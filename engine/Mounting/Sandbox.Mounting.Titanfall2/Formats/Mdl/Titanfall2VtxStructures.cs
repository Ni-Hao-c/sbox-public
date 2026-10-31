using System.Runtime.InteropServices;

namespace Titanfall2.Formats;

public static class Titanfall2VtxConstants
{
	public const int OptimizedModelFileVersion = 7;
	public const int MaxBonesPerStrip = 512;
}

[StructLayout( LayoutKind.Sequential, Pack = 1 )]
public struct VtxBoneStateChange
{
	public int HardwareId;
	public int NewBoneId;
}

[StructLayout( LayoutKind.Sequential, Pack = 1 )]
public unsafe struct VtxVertex
{
	public fixed byte BoneWeightIndex[Titanfall2Mdl53Constants.MaxBonesPerVertex];
	public byte BoneCount;
	public ushort OriginalMeshVertexId;
	public fixed sbyte BoneId[Titanfall2Mdl53Constants.MaxBonesPerVertex];
}

[StructLayout( LayoutKind.Sequential, Pack = 1 )]
public struct VtxStripHeader
{
	public int IndexCount;
	public int IndexOffset;
	public int VertexCount;
	public int VertexOffset;
	public short BoneCount;
	public byte Flags;
	public int BoneStateChangeCount;
	public int BoneStateChangeOffset;
	public int TopologyIndexCount;
	public int TopologyOffset;
}

[StructLayout( LayoutKind.Sequential, Pack = 1 )]
public struct VtxStripGroupHeader
{
	public int VertexCount;
	public int VertexOffset;
	public int IndexCount;
	public int IndexOffset;
	public int StripCount;
	public int StripOffset;
	public byte Flags;
	public int TopologyIndexCount;
	public int TopologyOffset;
}

[StructLayout( LayoutKind.Sequential, Pack = 1 )]
public struct VtxMeshHeader
{
	public int StripGroupCount;
	public int StripGroupHeaderOffset;
	public byte Flags;
}

[StructLayout( LayoutKind.Sequential, Pack = 1 )]
public struct VtxModelLodHeader
{
	public int MeshCount;
	public int MeshOffset;
	public float SwitchPoint;
}

[StructLayout( LayoutKind.Sequential, Pack = 1 )]
public struct VtxModelHeader
{
	public int LodCount;
	public int LodOffset;
}

[StructLayout( LayoutKind.Sequential, Pack = 1 )]
public struct VtxBodyPartHeader
{
	public int ModelCount;
	public int ModelOffset;
}

[StructLayout( LayoutKind.Sequential, Pack = 1 )]
public struct VtxMaterialReplacementHeader
{
	public short MaterialId;
	public int ReplacementMaterialNameOffset;
}

[StructLayout( LayoutKind.Sequential, Pack = 1 )]
public struct VtxMaterialReplacementListHeader
{
	public int ReplacementCount;
	public int ReplacementOffset;
}

[StructLayout( LayoutKind.Sequential, Pack = 1 )]
public struct VtxHeader
{
	public int Version;
	public int VertexCacheSize;
	public ushort MaxBonesPerStrip;
	public ushort MaxBonesPerFace;
	public int MaxBonesPerVertex;
	public int Checksum;
	public int LodCount;
	public int MaterialReplacementListOffset;
	public int BodyPartCount;
	public int BodyPartOffset;
}

