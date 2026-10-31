using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Titanfall2.Formats;

public static class Titanfall2Mdl53Constants
{
	public const int MaxLods = 8;
	public const int MaxBonesPerVertex = 3;
	public const int StudioHeaderVersion = 53;
}

[StructLayout( LayoutKind.Sequential, Pack = 4 )]
public struct Matrix3x4
{
	public float M00;
	public float M01;
	public float M02;
	public float M03;
	public float M10;
	public float M11;
	public float M12;
	public float M13;
	public float M20;
	public float M21;
	public float M22;
	public float M23;
}

[StructLayout( LayoutKind.Sequential, Pack = 4 )]
public unsafe struct Mdl53StudioHeader
{
	public int Id;
	public int Version;
	public int Checksum;
	public int NameOffset;
	public fixed byte Name[64];
	public int Length;
	public Vector3 EyePosition;
	public Vector3 IlluminationPosition;
	public Vector3 HullMin;
	public Vector3 HullMax;
	public Vector3 ViewBoundingBoxMin;
	public Vector3 ViewBoundingBoxMax;
	public int Flags;
	public int BoneCount;
	public int BoneOffset;
	public int BoneControllerCount;
	public int BoneControllerOffset;
	public int HitboxSetCount;
	public int HitboxSetOffset;
	public int LocalAnimationCount;
	public int LocalAnimationOffset;
	public int LocalSequenceCount;
	public int LocalSequenceOffset;
	public int ActivityListVersion;
	public int EventsIndexed;
	public int TextureCount;
	public int TextureOffset;
	public int CdTextureCount;
	public int CdTextureOffset;
	public int SkinReferenceCount;
	public int SkinFamilyCount;
	public int SkinOffset;
	public int BodyPartCount;
	public int BodyPartOffset;
	public int LocalAttachmentCount;
	public int LocalAttachmentOffset;
	public int LocalNodeCount;
	public int LocalNodeOffset;
	public int LocalNodeNameOffset;
	public int DeprecatedFlexDescriptionCount;
	public int DeprecatedFlexDescriptionOffset;
	public int DeprecatedFlexControllerCount;
	public int DeprecatedFlexControllerOffset;
	public int DeprecatedFlexRuleCount;
	public int DeprecatedFlexRuleOffset;
	public int IkChainCount;
	public int IkChainOffset;
	public int UiPanelCount;
	public int UiPanelOffset;
	public int LocalPoseParameterCount;
	public int LocalPoseParameterOffset;
	public int SurfacePropertyOffset;
	public int KeyValueOffset;
	public int KeyValueSize;
	public int LocalIkAutoplayLockCount;
	public int LocalIkAutoplayLockOffset;
	public float Mass;
	public int Contents;
	public int IncludeModelCount;
	public int IncludeModelOffset;
	public int VirtualModel;
	public int BoneTableByNameOffset;
	public byte ConstantDirectionalLightDot;
	public byte RootLod;
	public byte AllowedRootLodCount;
	public byte Unused0;
	public float DefaultFadeDistance;
	public int DeprecatedFlexControllerUiCount;
	public int DeprecatedFlexControllerUiOffset;
	public float VertexAnimationFixedPointScale;
	public int SurfacePropertyLookup;
	public int SourceFilenameOffset;
	public int SourceBoneTransformCount;
	public int SourceBoneTransformOffset;
	public int IlluminationPositionAttachmentIndex;
	public int LinearBoneOffset;
	public int BoneFlexDriverCount;
	public int BoneFlexDriverOffset;
	public int PerTriAabbOffset;
	public int PerTriAabbNodeCount;
	public int PerTriAabbLeafCount;
	public int PerTriAabbVertexCount;
	public int UnknownStringOffset;
	public int VtxOffset;
	public int VvdOffset;
	public int VvcOffset;
	public int PhysicsOffset;
	public int VtxSize;
	public int VvdSize;
	public int VvcSize;
	public int PhysicsSize;
	public int UnknownOffset;
	public int UnknownCount;
	public int BoneFollowerCount;
	public int BoneFollowerOffset;
	public fixed int Unused1[60];
}

[StructLayout( LayoutKind.Sequential, Pack = 4 )]
public unsafe struct Mdl53Bone
{
	public int NameOffset;
	public int Parent;
	public fixed int BoneControllers[6];
	public Vector3 Position;
	public Quaternion RotationQuaternion;
	public Vector3 RotationEuler;
	public Vector3 Scale;
	public Vector3 PositionScale;
	public Vector3 RotationScale;
	public Vector3 ScaleScale;
	public Matrix3x4 PoseToBone;
	public Quaternion AlignmentQuaternion;
	public int Flags;
	public int ProcedureType;
	public int ProcedureOffset;
	public int PhysicsBone;
	public int SurfacePropertyOffset;
	public int Contents;
	public int SurfacePropertyLookup;
	public short UnknownIndex;
	public short UnknownCount;
	public fixed int Unused[7];
}

[StructLayout( LayoutKind.Sequential, Pack = 4 )]
public struct Mdl53JiggleBone
{
	public int Flags;
	public float Length;
	public float TipMass;
	public float YawStiffness;
	public float YawDamping;
	public float PitchStiffness;
	public float PitchDamping;
	public float AlongStiffness;
	public float AlongDamping;
	public float AngleLimit;
	public float MinYaw;
	public float MaxYaw;
	public float YawFriction;
	public float YawBounce;
	public float MinPitch;
	public float MaxPitch;
	public float PitchFriction;
	public float PitchBounce;
	public float BaseMass;
	public float BaseStiffness;
	public float BaseDamping;
	public float BaseMinLeft;
	public float BaseMaxLeft;
	public float BaseLeftFriction;
	public float BaseMinUp;
	public float BaseMaxUp;
	public float BaseUpFriction;
	public float BaseMinForward;
	public float BaseMaxForward;
	public float BaseForwardFriction;
}

[StructLayout( LayoutKind.Sequential, Pack = 4 )]
public unsafe struct Mdl53LinearBoneTable
{
	public int BoneCount;
	public int FlagsOffset;
	public int ParentOffset;
	public int PositionOffset;
	public int QuaternionOffset;
	public int RotationOffset;
	public int PoseToBoneOffset;
	public int PositionScaleOffset;
	public int RotationScaleOffset;
	public int AlignmentQuaternionOffset;
	public fixed int Unused[6];
}

[StructLayout( LayoutKind.Sequential, Pack = 4 )]
public struct Mdl53SourceBoneTransform
{
	public int NameOffset;
	public Matrix3x4 PreTransform;
	public Matrix3x4 PostTransform;
}

[StructLayout( LayoutKind.Sequential, Pack = 4 )]
public unsafe struct Mdl53Attachment
{
	public int NameOffset;
	public uint Flags;
	public int LocalBone;
	public Matrix3x4 LocalTransform;
	public fixed int Unused[8];
}

[StructLayout( LayoutKind.Sequential, Pack = 4 )]
public struct Mdl53HitboxSet
{
	public int NameOffset;
	public int HitboxCount;
	public int HitboxOffset;
}

[StructLayout( LayoutKind.Sequential, Pack = 4 )]
public unsafe struct Mdl53Hitbox
{
	public int Bone;
	public int Group;
	public Vector3 BoundingBoxMin;
	public Vector3 BoundingBoxMax;
	public int HitboxNameOffset;
	public int CriticalOverride;
	public int KeyValueOffset;
	public fixed int Unused[6];
}

[StructLayout( LayoutKind.Sequential, Pack = 4 )]
public struct Mdl53AnimValue
{
	public short RawValue;
}

[StructLayout( LayoutKind.Sequential, Pack = 4 )]
public unsafe struct Mdl53AnimValuePointer
{
	public fixed short Offsets[3];
}

[StructLayout( LayoutKind.Sequential, Pack = 4 )]
public struct Mdl53RleAnimation
{
	public float PositionScale;
	public byte Bone;
	public byte Flags;
}

[StructLayout( LayoutKind.Sequential, Pack = 4 )]
public struct Mdl53AnimationSection
{
	public int AnimationOffset;
}

[StructLayout( LayoutKind.Sequential, Pack = 4 )]
public struct Mdl53Movement
{
	public int EndFrame;
	public int MotionFlags;
	public float StartVelocity;
	public float EndVelocity;
	public float EndYaw;
	public Vector3 MovementVector;
	public Vector3 Position;
}

[StructLayout( LayoutKind.Sequential, Pack = 4 )]
public unsafe struct Mdl53FrameMovement
{
	public fixed float Scale[4];
	public fixed short Offsets[4];
}

[StructLayout( LayoutKind.Sequential, Pack = 4 )]
public unsafe struct Mdl53AnimationDescription
{
	public int BasePointer;
	public int NameOffset;
	public float FramesPerSecond;
	public int Flags;
	public int FrameCount;
	public int MovementCount;
	public int MovementOffset;
	public int FrameMovementOffset;
	public int AnimationOffset;
	public int IkRuleCount;
	public int IkRuleOffset;
	public int LocalHierarchyCount;
	public int LocalHierarchyOffset;
	public int SectionOffset;
	public int SectionFrameCount;
	public fixed int Unused[8];
}

[StructLayout( LayoutKind.Sequential, Pack = 4 )]
public struct Mdl53Event
{
	public float Cycle;
	public int Event;
	public int Type;
	public unsafe fixed byte Options[64];
	public int EventNameOffset;
}

[StructLayout( LayoutKind.Sequential, Pack = 4 )]
public struct Mdl53AutoLayer
{
	public short SequenceIndex;
	public short PoseIndex;
	public int Flags;
	public float Start;
	public float Peak;
	public float Tail;
	public float End;
}

[StructLayout( LayoutKind.Sequential, Pack = 4 )]
public unsafe struct Mdl53IkLock
{
	public int Chain;
	public float PositionWeight;
	public float LocalQuaternionWeight;
	public int Flags;
	public fixed int Unused[4];
}

[StructLayout( LayoutKind.Sequential, Pack = 4 )]
public unsafe struct Mdl53ActivityModifier
{
	public int NameOffset;
	public byte Negate;
	public fixed byte Padding[3];
}

[StructLayout( LayoutKind.Sequential, Pack = 4 )]
public unsafe struct Mdl53SequenceDescription
{
	public int BasePointer;
	public int LabelOffset;
	public int ActivityNameOffset;
	public int Flags;
	public int Activity;
	public int ActivityWeight;
	public int EventCount;
	public int EventOffset;
	public Vector3 BoundingBoxMin;
	public Vector3 BoundingBoxMax;
	public int BlendCount;
	public int AnimationIndexOffset;
	public int MovementOffset;
	public fixed int GroupSize[2];
	public fixed int ParameterIndex[2];
	public fixed float ParameterStart[2];
	public fixed float ParameterEnd[2];
	public int ParameterParent;
	public float FadeInTime;
	public float FadeOutTime;
	public int LocalEntryNode;
	public int LocalExitNode;
	public int NodeFlags;
	public float EntryPhase;
	public float ExitPhase;
	public float LastFrame;
	public int NextSequence;
	public int Pose;
	public int IkRuleCount;
	public int AutoLayerCount;
	public int AutoLayerOffset;
	public int WeightListOffset;
	public int PoseKeyOffset;
	public int IkLockCount;
	public int IkLockOffset;
	public int KeyValueOffset;
	public int KeyValueSize;
	public int CyclePoseIndex;
	public int ActivityModifierOffset;
	public int ActivityModifierCount;
	public int IkResetMask;
	public int Unknown1;
	public fixed int Unused[8];
}

[StructLayout( LayoutKind.Sequential, Pack = 4 )]
public struct Mdl53IncludedModel
{
	public int LabelOffset;
	public int NameOffset;
}

[StructLayout( LayoutKind.Sequential, Pack = 4 )]
public unsafe struct Mdl53MeshVertexData
{
	public int ModelVertexData;
	public fixed int LodVertexCounts[Titanfall2Mdl53Constants.MaxLods];
}

[StructLayout( LayoutKind.Sequential, Pack = 4 )]
public unsafe struct Mdl53Mesh
{
	public int MaterialIndex;
	public int ModelOffset;
	public int VertexCount;
	public int VertexOffset;
	public int DeprecatedFlexCount;
	public int DeprecatedFlexOffset;
	public int DeprecatedMaterialType;
	public int DeprecatedMaterialParameter;
	public int MeshId;
	public Vector3 Center;
	public Mdl53MeshVertexData VertexLodData;
	public ulong UnknownPointer;
	public fixed int Unused[6];
}

[StructLayout( LayoutKind.Sequential, Pack = 4 )]
public unsafe struct Mdl53Model
{
	public fixed byte Name[64];
	public int Type;
	public float BoundingRadius;
	public int MeshCount;
	public int MeshOffset;
	public int VertexCount;
	public int VertexOffset;
	public int TangentOffset;
	public int AttachmentCount;
	public int AttachmentOffset;
	public int DeprecatedEyeballCount;
	public int DeprecatedEyeballOffset;
	public fixed int Padding[4];
	public int ColorOffset;
	public int Uv2Offset;
	public fixed int Unused[4];
}

[StructLayout( LayoutKind.Sequential, Pack = 4 )]
public struct Mdl53BodyPart
{
	public int NameOffset;
	public int ModelCount;
	public int Base;
	public int ModelOffset;
}

[StructLayout( LayoutKind.Sequential, Pack = 4 )]
public struct Mdl53IkLink
{
	public int Bone;
	public Vector3 KneeDirection;
	public Vector3 Unused0;
}

[StructLayout( LayoutKind.Sequential, Pack = 4 )]
public unsafe struct Mdl53IkChain
{
	public int NameOffset;
	public int LinkType;
	public int LinkCount;
	public int LinkOffset;
	public float Unknown;
	public fixed int Unused[3];
}

[StructLayout( LayoutKind.Sequential, Pack = 4 )]
public struct Mdl53PoseParameter
{
	public int NameOffset;
	public int Flags;
	public float Start;
	public float End;
	public float Loop;
}

[StructLayout( LayoutKind.Sequential, Pack = 4 )]
public unsafe struct Mdl53Texture
{
	public int NameOffset;
	public int UnusedFlags;
	public int Used;
	public fixed int Unused[8];
}

[StructLayout( LayoutKind.Sequential, Pack = 4 )]
public unsafe struct Mdl53PerTriHeader
{
	public short Version;
	public short Unknown;
	public Vector3 BoundingBoxMin;
	public Vector3 BoundingBoxMax;
	public fixed int Unused[8];
}

[StructLayout( LayoutKind.Sequential, Pack = 4 )]
public struct VvdWeightExtra
{
	public short Weight0;
	public short Weight1;
	public short Weight2;
	public short Padding;
	public int ExtraWeightOffset;
}

[StructLayout( LayoutKind.Sequential, Pack = 4 )]
public unsafe struct VvdBoneWeight
{
	public fixed float Weight[Titanfall2Mdl53Constants.MaxBonesPerVertex];
	public fixed byte Bone[Titanfall2Mdl53Constants.MaxBonesPerVertex];
	public sbyte BoneCount;
}

[StructLayout( LayoutKind.Sequential, Pack = 4 )]
public struct VvdVertex
{
	public VvdBoneWeight BoneWeights;
	public Vector3 Position;
	public Vector3 Normal;
	public Vector2 TexCoord;
}

[StructLayout( LayoutKind.Sequential, Pack = 4 )]
public struct VvdFixup
{
	public int Lod;
	public int SourceVertexId;
	public int VertexCount;
}

[StructLayout( LayoutKind.Sequential, Pack = 4 )]
public unsafe struct VvdHeader
{
	public int Id;
	public int Version;
	public int Checksum;
	public int LodCount;
	public fixed int LodVertexCounts[Titanfall2Mdl53Constants.MaxLods];
	public int FixupCount;
	public int FixupTableOffset;
	public int VertexDataOffset;
	public int TangentDataOffset;
}

[StructLayout( LayoutKind.Sequential, Pack = 4 )]
public unsafe struct VvcHeader
{
	public int Id;
	public int Version;
	public int Checksum;
	public int LodCount;
	public fixed int LodVertexCounts[Titanfall2Mdl53Constants.MaxLods];
	public int ColorDataOffset;
	public int Uv2DataOffset;
}

[StructLayout( LayoutKind.Sequential, Pack = 1 )]
public struct VvcColor32
{
	public byte R;
	public byte G;
	public byte B;
	public byte A;
}

public static class Titanfall2Mdl53Binary
{
	public static T ReadStruct<T>( ReadOnlySpan<byte> data, int offset ) where T : unmanaged
	{
		var size = Unsafe.SizeOf<T>();
		return MemoryMarshal.Read<T>( data.Slice( offset, size ) );
	}

	public static ReadOnlySpan<T> ReadStructArray<T>( ReadOnlySpan<byte> data, int offset, int count ) where T : unmanaged
	{
		var size = Unsafe.SizeOf<T>() * count;
		return MemoryMarshal.Cast<byte, T>( data.Slice( offset, size ) );
	}

	public static string ReadNullTerminatedString( ReadOnlySpan<byte> data, int absoluteOffset )
	{
		if ( absoluteOffset < 0 || absoluteOffset >= data.Length )
			return string.Empty;

		int end = absoluteOffset;
		while ( end < data.Length && data[end] != 0 )
			end++;

		return System.Text.Encoding.UTF8.GetString( data.Slice( absoluteOffset, end - absoluteOffset ) );
	}

	public static string ReadRelativeString( ReadOnlySpan<byte> data, int baseOffset, int relativeOffset )
	{
		if ( relativeOffset <= 0 )
			return string.Empty;

		return ReadNullTerminatedString( data, baseOffset + relativeOffset );
	}
}
