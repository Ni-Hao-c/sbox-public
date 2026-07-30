using System;
using System.Collections.Generic;
using System.Numerics;

namespace Titanfall2.Formats;

public static class Titanfall2Mdl53Reader
{
	public sealed class ParsedModel
	{
		public Mdl53StudioHeader Header { get; init; }
		public string InternalName { get; init; }
		public string SurfaceProperty { get; init; }
		public string UnknownString { get; init; }
		public BoneInfo[] Bones { get; init; } = Array.Empty<BoneInfo>();
		public LinearBoneInfo[] LinearBones { get; init; } = Array.Empty<LinearBoneInfo>();
		public HitboxSetInfo[] HitboxSets { get; init; } = Array.Empty<HitboxSetInfo>();
		public AnimationInfo[] Animations { get; init; } = Array.Empty<AnimationInfo>();
		public SequenceInfo[] Sequences { get; init; } = Array.Empty<SequenceInfo>();
		public IncludedModelInfo[] IncludedModels { get; init; } = Array.Empty<IncludedModelInfo>();
		public TextureInfo[] Textures { get; init; } = Array.Empty<TextureInfo>();
		public BodyPartInfo[] BodyParts { get; init; } = Array.Empty<BodyPartInfo>();
		public short[] SkinTable { get; init; } = Array.Empty<short>();
		public VvdHeader? EmbeddedVvdHeader { get; init; }
		public VvcHeader? EmbeddedVvcHeader { get; init; }
		public VvdFixup[] EmbeddedVvdFixups { get; init; } = Array.Empty<VvdFixup>();
		public VvdVertex[] EmbeddedVertices { get; init; } = Array.Empty<VvdVertex>();
		public VvcColor32[] EmbeddedVertexColors { get; init; } = Array.Empty<VvcColor32>();
		public Vector2[] EmbeddedUv2 { get; init; } = Array.Empty<Vector2>();
		public Titanfall2VtxReader.ParsedVtx EmbeddedVtx { get; init; }
		public byte[] BoneTableByName { get; init; } = Array.Empty<byte>();

		public int MeshCount
		{
			get
			{
				int count = 0;
				foreach ( var bodyPart in BodyParts )
				{
					foreach ( var model in bodyPart.Models )
					{
						count += model.Meshes.Length;
					}
				}

				return count;
			}
		}
	}

	public sealed class BoneInfo
	{
		public int Index { get; init; }
		public Mdl53Bone Bone { get; init; }
		public string Name { get; init; }
		public string SurfaceProperty { get; init; }
	}

	public sealed class LinearBoneInfo
	{
		public int Index { get; init; }
		public int Flags { get; init; }
		public int Parent { get; init; }
		public Vector3 Position { get; init; }
		public Quaternion RotationQuaternion { get; init; }
		public Vector3 RotationEuler { get; init; }
		public Vector3 RotationScale { get; init; }
		public Quaternion AlignmentQuaternion { get; init; }
	}

	public sealed class AnimationInfo
	{
		public int Index { get; init; }
		public int Offset { get; init; }
		public string Name { get; init; }
		public Mdl53AnimationDescription Animation { get; init; }
	}

	public sealed class SequenceInfo
	{
		public int Index { get; init; }
		public int Offset { get; init; }
		public string Label { get; init; }
		public string ActivityName { get; init; }
		public Mdl53SequenceDescription Sequence { get; init; }
		public short[] AnimationIndices { get; init; } = Array.Empty<short>();
	}

	public sealed class IncludedModelInfo
	{
		public int Index { get; init; }
		public string Label { get; init; }
		public string Name { get; init; }
	}

	public sealed class HitboxSetInfo
	{
		public int Index { get; init; }
		public string Name { get; init; }
		public HitboxInfo[] Hitboxes { get; init; } = Array.Empty<HitboxInfo>();
	}

	public sealed class HitboxInfo
	{
		public int Index { get; init; }
		public Mdl53Hitbox Hitbox { get; init; }
		public string Name { get; init; }
		public string KeyValue { get; init; }
	}

	public sealed class TextureInfo
	{
		public int Index { get; init; }
		public Mdl53Texture Texture { get; init; }
		public string Name { get; init; }
	}

	public sealed class BodyPartInfo
	{
		public int Index { get; init; }
		public Mdl53BodyPart BodyPart { get; init; }
		public string Name { get; init; }
		public ModelInfo[] Models { get; init; } = Array.Empty<ModelInfo>();
	}

	public sealed class ModelInfo
	{
		public int Index { get; init; }
		public Mdl53Model Model { get; init; }
		public string Name { get; init; }
		public MeshInfo[] Meshes { get; init; } = Array.Empty<MeshInfo>();
	}

	public sealed class MeshInfo
	{
		public int Index { get; init; }
		public Mdl53Mesh Mesh { get; init; }
		public int GlobalVertexOffset { get; init; }
		public int VertexCount => Mesh.VertexCount;
	}

	public static ParsedModel Parse( byte[] data ) => Parse( data.AsSpan() );

	public static ParsedModel Parse( ReadOnlySpan<byte> data )
	{
		EnsureReadable<Mdl53StudioHeader>( data, 0 );

		var header = Titanfall2Mdl53Binary.ReadStruct<Mdl53StudioHeader>( data, 0 );
		if ( header.Version != Titanfall2Mdl53Constants.StudioHeaderVersion )
			throw new InvalidOperationException( $"Unsupported Titanfall 2 MDL version: {header.Version}" );

		var internalName = ReadHeaderName( header );
		if ( string.IsNullOrWhiteSpace( internalName ) )
			internalName = Titanfall2Mdl53Binary.ReadRelativeString( data, 0, header.NameOffset );

		var parsed = new ParsedModel
		{
			Header = header,
			InternalName = internalName,
			SurfaceProperty = Titanfall2Mdl53Binary.ReadRelativeString( data, 0, header.SurfacePropertyOffset ),
			UnknownString = Titanfall2Mdl53Binary.ReadRelativeString( data, 0, header.UnknownStringOffset ),
			Bones = ParseBones( data, header ),
			LinearBones = ParseLinearBones( data, header ),
			HitboxSets = ParseHitboxSets( data, header ),
			Animations = ParseAnimations( data, header ),
			Sequences = ParseSequences( data, header ),
			IncludedModels = ParseIncludedModels( data, header ),
			Textures = ParseTextures( data, header ),
			BodyParts = ParseBodyParts( data, header ),
			SkinTable = ParseSkinTable( data, header ),
			EmbeddedVvdHeader = ParseOptionalHeader<VvdHeader>( data, header.VvdOffset, header.VvdSize ),
			EmbeddedVvcHeader = ParseOptionalHeader<VvcHeader>( data, header.VvcOffset, header.VvcSize ),
			EmbeddedVvdFixups = ParseVvdFixups( data, header ),
			EmbeddedVertices = ParseEmbeddedVertices( data, header ),
			EmbeddedVertexColors = ParseEmbeddedVertexColors( data, header ),
			EmbeddedUv2 = ParseEmbeddedUv2( data, header ),
			EmbeddedVtx = ParseEmbeddedVtx( data, header ),
			BoneTableByName = ParseBoneTableByName( data, header )
		};

		return parsed;
	}

	private static LinearBoneInfo[] ParseLinearBones( ReadOnlySpan<byte> data, Mdl53StudioHeader header )
	{
		if ( header.LinearBoneOffset <= 0 || header.BoneCount <= 0 )
			return Array.Empty<LinearBoneInfo>();

		try
		{
			EnsureReadable<Mdl53LinearBoneTable>( data, header.LinearBoneOffset );
			var table = Titanfall2Mdl53Binary.ReadStruct<Mdl53LinearBoneTable>( data, header.LinearBoneOffset );
			var count = Math.Min( table.BoneCount, header.BoneCount );
			if ( count <= 0 ) return Array.Empty<LinearBoneInfo>();

			var bones = new LinearBoneInfo[count];
			for ( var i = 0; i < count; ++i )
			{
				bones[i] = new LinearBoneInfo
				{
					Index = i,
					Flags = ReadLinearValue<int>( data, header.LinearBoneOffset, table.FlagsOffset, i ),
					Parent = ReadLinearValue<int>( data, header.LinearBoneOffset, table.ParentOffset, i ),
					Position = ReadLinearValue<Vector3>( data, header.LinearBoneOffset, table.PositionOffset, i ),
					RotationQuaternion = ReadLinearValue<Quaternion>( data, header.LinearBoneOffset, table.QuaternionOffset, i ),
					RotationEuler = ReadLinearValue<Vector3>( data, header.LinearBoneOffset, table.RotationOffset, i ),
					RotationScale = ReadLinearValue<Vector3>( data, header.LinearBoneOffset, table.RotationScaleOffset, i ),
					AlignmentQuaternion = ReadLinearValue<Quaternion>( data, header.LinearBoneOffset, table.AlignmentQuaternionOffset, i )
				};
			}

			return bones;
		}
		catch
		{
			// Linear bones are an animation acceleration table. The regular bone
			// table remains a valid fallback if a model omits or truncates it.
			return Array.Empty<LinearBoneInfo>();
		}
	}

	private static T ReadLinearValue<T>( ReadOnlySpan<byte> data, int tableOffset, int relativeOffset, int index ) where T : unmanaged
	{
		if ( relativeOffset <= 0 ) return default;
		var offset = checked(tableOffset + relativeOffset + index * SizeOf<T>());
		EnsureReadable<T>( data, offset );
		return Titanfall2Mdl53Binary.ReadStruct<T>( data, offset );
	}

	private static AnimationInfo[] ParseAnimations( ReadOnlySpan<byte> data, Mdl53StudioHeader header )
	{
		if ( header.LocalAnimationCount <= 0 || header.LocalAnimationOffset <= 0 )
			return Array.Empty<AnimationInfo>();

		var animations = new AnimationInfo[header.LocalAnimationCount];
		for ( var i = 0; i < animations.Length; ++i )
		{
			var offset = checked(header.LocalAnimationOffset + i * SizeOf<Mdl53AnimationDescription>());
			EnsureReadable<Mdl53AnimationDescription>( data, offset );
			var animation = Titanfall2Mdl53Binary.ReadStruct<Mdl53AnimationDescription>( data, offset );
			animations[i] = new AnimationInfo
			{
				Index = i,
				Offset = offset,
				Name = Titanfall2Mdl53Binary.ReadRelativeString( data, offset, animation.NameOffset ),
				Animation = animation
			};
		}
		return animations;
	}

	private static unsafe SequenceInfo[] ParseSequences( ReadOnlySpan<byte> data, Mdl53StudioHeader header )
	{
		if ( header.LocalSequenceCount <= 0 || header.LocalSequenceOffset <= 0 )
			return Array.Empty<SequenceInfo>();

		var sequences = new SequenceInfo[header.LocalSequenceCount];
		for ( var i = 0; i < sequences.Length; ++i )
		{
			var offset = checked(header.LocalSequenceOffset + i * SizeOf<Mdl53SequenceDescription>());
			EnsureReadable<Mdl53SequenceDescription>( data, offset );
			var sequence = Titanfall2Mdl53Binary.ReadStruct<Mdl53SequenceDescription>( data, offset );
			var blendCount = Math.Max( 1, checked(sequence.GroupSize[0] * sequence.GroupSize[1]) );
			var animationIndices = Array.Empty<short>();
			if ( sequence.AnimationIndexOffset > 0 && blendCount <= 4096 )
			{
				var animationOffset = checked(offset + sequence.AnimationIndexOffset);
				EnsureRange( data, animationOffset, checked(blendCount * sizeof(short)) );
				animationIndices = new short[blendCount];
				for ( var blend = 0; blend < blendCount; ++blend )
					animationIndices[blend] = Titanfall2Mdl53Binary.ReadStruct<short>( data, animationOffset + blend * sizeof(short) );
			}

			sequences[i] = new SequenceInfo
			{
				Index = i,
				Offset = offset,
				Label = Titanfall2Mdl53Binary.ReadRelativeString( data, offset, sequence.LabelOffset ),
				ActivityName = Titanfall2Mdl53Binary.ReadRelativeString( data, offset, sequence.ActivityNameOffset ),
				Sequence = sequence,
				AnimationIndices = animationIndices
			};
		}
		return sequences;
	}

	private static IncludedModelInfo[] ParseIncludedModels( ReadOnlySpan<byte> data, Mdl53StudioHeader header )
	{
		if ( header.IncludeModelCount <= 0 || header.IncludeModelOffset <= 0 )
			return Array.Empty<IncludedModelInfo>();

		var models = new IncludedModelInfo[header.IncludeModelCount];
		for ( var i = 0; i < models.Length; ++i )
		{
			var offset = checked(header.IncludeModelOffset + i * SizeOf<Mdl53IncludedModel>());
			EnsureReadable<Mdl53IncludedModel>( data, offset );
			var model = Titanfall2Mdl53Binary.ReadStruct<Mdl53IncludedModel>( data, offset );
			models[i] = new IncludedModelInfo
			{
				Index = i,
				Label = Titanfall2Mdl53Binary.ReadRelativeString( data, offset, model.LabelOffset ),
				Name = Titanfall2Mdl53Binary.ReadRelativeString( data, offset, model.NameOffset )
			};
		}
		return models;
	}

	private static BoneInfo[] ParseBones( ReadOnlySpan<byte> data, Mdl53StudioHeader header )
	{
		if ( header.BoneCount <= 0 || header.BoneOffset <= 0 )
			return Array.Empty<BoneInfo>();

		var bones = new BoneInfo[header.BoneCount];
		for ( int i = 0; i < header.BoneCount; i++ )
		{
			int offset = header.BoneOffset + (i * SizeOf<Mdl53Bone>());
			EnsureReadable<Mdl53Bone>( data, offset );

			var bone = Titanfall2Mdl53Binary.ReadStruct<Mdl53Bone>( data, offset );
			bones[i] = new BoneInfo
			{
				Index = i,
				Bone = bone,
				Name = Titanfall2Mdl53Binary.ReadRelativeString( data, offset, bone.NameOffset ),
				SurfaceProperty = Titanfall2Mdl53Binary.ReadRelativeString( data, offset, bone.SurfacePropertyOffset )
			};
		}

		return bones;
	}

	private static HitboxSetInfo[] ParseHitboxSets( ReadOnlySpan<byte> data, Mdl53StudioHeader header )
	{
		if ( header.HitboxSetCount <= 0 || header.HitboxSetOffset <= 0 )
			return Array.Empty<HitboxSetInfo>();

		var hitboxSets = new HitboxSetInfo[header.HitboxSetCount];
		for ( int i = 0; i < header.HitboxSetCount; i++ )
		{
			int offset = header.HitboxSetOffset + (i * SizeOf<Mdl53HitboxSet>());
			EnsureReadable<Mdl53HitboxSet>( data, offset );

			var set = Titanfall2Mdl53Binary.ReadStruct<Mdl53HitboxSet>( data, offset );
			hitboxSets[i] = new HitboxSetInfo
			{
				Index = i,
				Name = Titanfall2Mdl53Binary.ReadRelativeString( data, offset, set.NameOffset ),
				Hitboxes = ParseHitboxes( data, offset, set )
			};
		}

		return hitboxSets;
	}

	private static HitboxInfo[] ParseHitboxes( ReadOnlySpan<byte> data, int hitboxSetOffset, Mdl53HitboxSet set )
	{
		if ( set.HitboxCount <= 0 || set.HitboxOffset <= 0 )
			return Array.Empty<HitboxInfo>();

		int baseOffset = hitboxSetOffset + set.HitboxOffset;
		var hitboxes = new HitboxInfo[set.HitboxCount];
		for ( int i = 0; i < set.HitboxCount; i++ )
		{
			int offset = baseOffset + (i * SizeOf<Mdl53Hitbox>());
			EnsureReadable<Mdl53Hitbox>( data, offset );

			var hitbox = Titanfall2Mdl53Binary.ReadStruct<Mdl53Hitbox>( data, offset );
			hitboxes[i] = new HitboxInfo
			{
				Index = i,
				Hitbox = hitbox,
				Name = Titanfall2Mdl53Binary.ReadRelativeString( data, offset, hitbox.HitboxNameOffset ),
				KeyValue = Titanfall2Mdl53Binary.ReadRelativeString( data, offset, hitbox.KeyValueOffset )
			};
		}

		return hitboxes;
	}

	private static TextureInfo[] ParseTextures( ReadOnlySpan<byte> data, Mdl53StudioHeader header )
	{
		if ( header.TextureCount <= 0 || header.TextureOffset <= 0 )
			return Array.Empty<TextureInfo>();

		var textures = new TextureInfo[header.TextureCount];
		for ( int i = 0; i < header.TextureCount; i++ )
		{
			int offset = header.TextureOffset + (i * SizeOf<Mdl53Texture>());
			EnsureReadable<Mdl53Texture>( data, offset );

			var texture = Titanfall2Mdl53Binary.ReadStruct<Mdl53Texture>( data, offset );
			textures[i] = new TextureInfo
			{
				Index = i,
				Texture = texture,
				Name = Titanfall2Mdl53Binary.ReadRelativeString( data, offset, texture.NameOffset )
			};
		}

		return textures;
	}

	private static BodyPartInfo[] ParseBodyParts( ReadOnlySpan<byte> data, Mdl53StudioHeader header )
	{
		if ( header.BodyPartCount <= 0 || header.BodyPartOffset <= 0 )
			return Array.Empty<BodyPartInfo>();

		var bodyParts = new BodyPartInfo[header.BodyPartCount];
		for ( int i = 0; i < header.BodyPartCount; i++ )
		{
			int offset = header.BodyPartOffset + (i * SizeOf<Mdl53BodyPart>());
			EnsureReadable<Mdl53BodyPart>( data, offset );

			var bodyPart = Titanfall2Mdl53Binary.ReadStruct<Mdl53BodyPart>( data, offset );
			bodyParts[i] = new BodyPartInfo
			{
				Index = i,
				BodyPart = bodyPart,
				Name = Titanfall2Mdl53Binary.ReadRelativeString( data, offset, bodyPart.NameOffset ),
				Models = ParseModels( data, offset, bodyPart )
			};
		}

		return bodyParts;
	}

	private static ModelInfo[] ParseModels( ReadOnlySpan<byte> data, int bodyPartOffset, Mdl53BodyPart bodyPart )
	{
		if ( bodyPart.ModelCount <= 0 || bodyPart.ModelOffset <= 0 )
			return Array.Empty<ModelInfo>();

		int modelBaseOffset = bodyPartOffset + bodyPart.ModelOffset;
		var models = new ModelInfo[bodyPart.ModelCount];
		for ( int i = 0; i < bodyPart.ModelCount; i++ )
		{
			int offset = modelBaseOffset + (i * SizeOf<Mdl53Model>());
			EnsureReadable<Mdl53Model>( data, offset );

			var model = Titanfall2Mdl53Binary.ReadStruct<Mdl53Model>( data, offset );
			models[i] = new ModelInfo
			{
				Index = i,
				Model = model,
				Name = ReadModelName( model ),
				Meshes = ParseMeshes( data, offset, model )
			};
		}

		return models;
	}

	private static MeshInfo[] ParseMeshes( ReadOnlySpan<byte> data, int modelOffset, Mdl53Model model )
	{
		if ( model.MeshCount <= 0 || model.MeshOffset <= 0 )
			return Array.Empty<MeshInfo>();

		int meshBaseOffset = modelOffset + model.MeshOffset;
		var meshes = new MeshInfo[model.MeshCount];
		for ( int i = 0; i < model.MeshCount; i++ )
		{
			int offset = meshBaseOffset + (i * SizeOf<Mdl53Mesh>());
			EnsureReadable<Mdl53Mesh>( data, offset );

			meshes[i] = new MeshInfo
			{
				Index = i,
				Mesh = Titanfall2Mdl53Binary.ReadStruct<Mdl53Mesh>( data, offset ),
				GlobalVertexOffset = model.VertexOffset + Titanfall2Mdl53Binary.ReadStruct<Mdl53Mesh>( data, offset ).VertexOffset
			};
		}

		return meshes;
	}

	private static short[] ParseSkinTable( ReadOnlySpan<byte> data, Mdl53StudioHeader header )
	{
		if ( header.SkinReferenceCount <= 0 || header.SkinFamilyCount <= 0 || header.SkinOffset <= 0 )
			return Array.Empty<short>();

		int count = checked( header.SkinReferenceCount * header.SkinFamilyCount );
		int byteLength = checked( count * sizeof(short) );
		EnsureRange( data, header.SkinOffset, byteLength );

		var values = new short[count];
		for ( int i = 0; i < count; i++ )
		{
			values[i] = Titanfall2Mdl53Binary.ReadStruct<short>( data, header.SkinOffset + (i * sizeof(short)) );
		}

		return values;
	}

	private static Titanfall2VtxReader.ParsedVtx ParseEmbeddedVtx( ReadOnlySpan<byte> data, Mdl53StudioHeader header )
	{
		if ( header.VtxOffset <= 0 || header.VtxSize < SizeOf<VtxHeader>() )
			return null;

		EnsureRange( data, header.VtxOffset, header.VtxSize );
		try
		{
			return Titanfall2VtxReader.Parse( data.Slice( header.VtxOffset, header.VtxSize ) );
		}
		catch
		{
			return null;
		}
	}

	private static VvdFixup[] ParseVvdFixups( ReadOnlySpan<byte> data, Mdl53StudioHeader header )
	{
		var vvdHeader = ParseOptionalHeader<VvdHeader>( data, header.VvdOffset, header.VvdSize );
		if ( vvdHeader == null || vvdHeader.Value.FixupCount <= 0 || vvdHeader.Value.FixupTableOffset <= 0 )
			return Array.Empty<VvdFixup>();

		int fixupBase = header.VvdOffset + vvdHeader.Value.FixupTableOffset;
		var fixups = new VvdFixup[vvdHeader.Value.FixupCount];
		for ( int i = 0; i < fixups.Length; i++ )
		{
			int offset = fixupBase + (i * SizeOf<VvdFixup>());
			EnsureReadable<VvdFixup>( data, offset );
			fixups[i] = Titanfall2Mdl53Binary.ReadStruct<VvdFixup>( data, offset );
		}

		return fixups;
	}

	private static VvdVertex[] ParseEmbeddedVertices( ReadOnlySpan<byte> data, Mdl53StudioHeader header )
	{
		var vvdHeader = ParseOptionalHeader<VvdHeader>( data, header.VvdOffset, header.VvdSize );
		if ( vvdHeader == null || vvdHeader.Value.VertexDataOffset <= 0 )
			return Array.Empty<VvdVertex>();

		int vertexCount = GetEmbeddedVertexCount( header, vvdHeader.Value );
		if ( vertexCount <= 0 )
			return Array.Empty<VvdVertex>();

		int vertexBase = header.VvdOffset + vvdHeader.Value.VertexDataOffset;
		var vertices = new VvdVertex[vertexCount];
		for ( int i = 0; i < vertexCount; i++ )
		{
			int offset = vertexBase + (i * SizeOf<VvdVertex>());
			EnsureReadable<VvdVertex>( data, offset );
			vertices[i] = Titanfall2Mdl53Binary.ReadStruct<VvdVertex>( data, offset );
		}

		return vertices;
	}

	private static VvcColor32[] ParseEmbeddedVertexColors( ReadOnlySpan<byte> data, Mdl53StudioHeader header )
	{
		var vvcHeader = ParseOptionalHeader<VvcHeader>( data, header.VvcOffset, header.VvcSize );
		if ( vvcHeader == null || vvcHeader.Value.ColorDataOffset <= 0 )
			return Array.Empty<VvcColor32>();

		int count = GetEmbeddedColorCount( header, vvcHeader.Value );
		if ( count <= 0 )
			return Array.Empty<VvcColor32>();

		int colorBase = header.VvcOffset + vvcHeader.Value.ColorDataOffset;
		var colors = new VvcColor32[count];
		for ( int i = 0; i < count; i++ )
		{
			int offset = colorBase + (i * SizeOf<VvcColor32>());
			EnsureReadable<VvcColor32>( data, offset );
			colors[i] = Titanfall2Mdl53Binary.ReadStruct<VvcColor32>( data, offset );
		}

		return colors;
	}

	private static Vector2[] ParseEmbeddedUv2( ReadOnlySpan<byte> data, Mdl53StudioHeader header )
	{
		var vvcHeader = ParseOptionalHeader<VvcHeader>( data, header.VvcOffset, header.VvcSize );
		if ( vvcHeader == null || vvcHeader.Value.Uv2DataOffset <= 0 )
			return Array.Empty<Vector2>();

		int count = GetEmbeddedUv2Count( header, vvcHeader.Value );
		if ( count <= 0 )
			return Array.Empty<Vector2>();

		int uv2Base = header.VvcOffset + vvcHeader.Value.Uv2DataOffset;
		var uv2 = new Vector2[count];
		for ( int i = 0; i < count; i++ )
		{
			int offset = uv2Base + (i * SizeOf<Vector2>());
			EnsureReadable<Vector2>( data, offset );
			uv2[i] = Titanfall2Mdl53Binary.ReadStruct<Vector2>( data, offset );
		}

		return uv2;
	}

	private static byte[] ParseBoneTableByName( ReadOnlySpan<byte> data, Mdl53StudioHeader header )
	{
		if ( header.BoneCount <= 0 || header.BoneTableByNameOffset <= 0 )
			return Array.Empty<byte>();

		EnsureRange( data, header.BoneTableByNameOffset, header.BoneCount );
		return data.Slice( header.BoneTableByNameOffset, header.BoneCount ).ToArray();
	}

	private static T? ParseOptionalHeader<T>( ReadOnlySpan<byte> data, int offset, int size ) where T : unmanaged
	{
		if ( offset <= 0 || size < SizeOf<T>() )
			return null;

		EnsureReadable<T>( data, offset );
		return Titanfall2Mdl53Binary.ReadStruct<T>( data, offset );
	}

	private static int GetEmbeddedVertexCount( Mdl53StudioHeader header, VvdHeader vvdHeader )
	{
		if ( vvdHeader.TangentDataOffset > vvdHeader.VertexDataOffset )
		{
			int byteCount = vvdHeader.TangentDataOffset - vvdHeader.VertexDataOffset;
			return byteCount / SizeOf<VvdVertex>();
		}

		int max = 0;
		unsafe
		{
			for ( int i = 0; i < Titanfall2Mdl53Constants.MaxLods; i++ )
			{
				max = Math.Max( max, vvdHeader.LodVertexCounts[i] );
			}
		}

		return max;
	}

	private static int GetEmbeddedColorCount( Mdl53StudioHeader header, VvcHeader vvcHeader )
	{
		if ( vvcHeader.Uv2DataOffset > vvcHeader.ColorDataOffset )
		{
			int byteCount = vvcHeader.Uv2DataOffset - vvcHeader.ColorDataOffset;
			return byteCount / SizeOf<VvcColor32>();
		}

		return GetMaxLodVertexCount( vvcHeader );
	}

	private static int GetEmbeddedUv2Count( Mdl53StudioHeader header, VvcHeader vvcHeader )
	{
		if ( header.VvcSize > vvcHeader.Uv2DataOffset )
		{
			int byteCount = header.VvcSize - vvcHeader.Uv2DataOffset;
			return byteCount / SizeOf<Vector2>();
		}

		return GetMaxLodVertexCount( vvcHeader );
	}

	private static unsafe int GetMaxLodVertexCount( VvcHeader header )
	{
		int max = 0;
		for ( int i = 0; i < Titanfall2Mdl53Constants.MaxLods; i++ )
		{
			max = Math.Max( max, header.LodVertexCounts[i] );
		}

		return max;
	}

	private static unsafe string ReadHeaderName( Mdl53StudioHeader header )
	{
		Span<byte> bytes = stackalloc byte[64];
		for ( int i = 0; i < bytes.Length; i++ )
		{
			bytes[i] = header.Name[i];
		}

		return ReadFixedName( bytes );
	}

	private static unsafe string ReadModelName( Mdl53Model model )
	{
		Span<byte> bytes = stackalloc byte[64];
		for ( int i = 0; i < bytes.Length; i++ )
		{
			bytes[i] = model.Name[i];
		}

		return ReadFixedName( bytes );
	}

	private static string ReadFixedName( ReadOnlySpan<byte> bytes )
	{
		int actualLength = 0;
		while ( actualLength < bytes.Length && bytes[actualLength] != 0 )
			actualLength++;

		if ( actualLength == 0 )
			return string.Empty;

		return System.Text.Encoding.UTF8.GetString( bytes.Slice( 0, actualLength ) );
	}

	private static int SizeOf<T>() where T : unmanaged
	{
		return System.Runtime.CompilerServices.Unsafe.SizeOf<T>();
	}

	private static void EnsureReadable<T>( ReadOnlySpan<byte> data, int offset ) where T : unmanaged
	{
		EnsureRange( data, offset, SizeOf<T>() );
	}

	private static void EnsureRange( ReadOnlySpan<byte> data, int offset, int length )
	{
		if ( offset < 0 || length < 0 || offset > data.Length - length )
			throw new InvalidOperationException( $"Requested range is outside the MDL buffer. Offset={offset}, Length={length}, Buffer={data.Length}" );
	}
}
