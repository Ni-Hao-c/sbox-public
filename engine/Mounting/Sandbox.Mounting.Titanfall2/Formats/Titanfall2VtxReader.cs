using System;

namespace Titanfall2.Formats;

public static class Titanfall2VtxReader
{
	public sealed class ParsedVtx
	{
		public VtxHeader Header { get; init; }
		public MaterialReplacementListInfo[] MaterialReplacementLists { get; init; } = Array.Empty<MaterialReplacementListInfo>();
		public BodyPartInfo[] BodyParts { get; init; } = Array.Empty<BodyPartInfo>();

		public int StripGroupCount
		{
			get
			{
				int count = 0;
				foreach ( var bodyPart in BodyParts )
				{
					foreach ( var model in bodyPart.Models )
					{
						foreach ( var lod in model.Lods )
						{
							foreach ( var mesh in lod.Meshes )
							{
								count += mesh.StripGroups.Length;
							}
						}
					}
				}

				return count;
			}
		}

		public int MeshCount
		{
			get
			{
				int count = 0;
				foreach ( var bodyPart in BodyParts )
				{
					foreach ( var model in bodyPart.Models )
					{
						foreach ( var lod in model.Lods )
						{
							count += lod.Meshes.Length;
						}
					}
				}

				return count;
			}
		}

		public int StripCount
		{
			get
			{
				int count = 0;
				foreach ( var bodyPart in BodyParts )
				{
					foreach ( var model in bodyPart.Models )
					{
						foreach ( var lod in model.Lods )
						{
							foreach ( var mesh in lod.Meshes )
							{
								foreach ( var stripGroup in mesh.StripGroups )
								{
									count += stripGroup.Strips.Length;
								}
							}
						}
					}
				}

				return count;
			}
		}

		public int TriangleCount
		{
			get
			{
				int count = 0;
				foreach ( var bodyPart in BodyParts )
				{
					foreach ( var model in bodyPart.Models )
					{
						foreach ( var lod in model.Lods )
						{
							foreach ( var mesh in lod.Meshes )
							{
								count += mesh.TriangleCount;
							}
						}
					}
				}

				return count;
			}
		}
	}

	public sealed class MaterialReplacementListInfo
	{
		public int LodIndex { get; init; }
		public MaterialReplacementInfo[] Replacements { get; init; } = Array.Empty<MaterialReplacementInfo>();
	}

	public sealed class MaterialReplacementInfo
	{
		public int Index { get; init; }
		public VtxMaterialReplacementHeader Header { get; init; }
		public string MaterialName { get; init; }
	}

	public sealed class BodyPartInfo
	{
		public int Index { get; init; }
		public VtxBodyPartHeader Header { get; init; }
		public ModelInfo[] Models { get; init; } = Array.Empty<ModelInfo>();
	}

	public sealed class ModelInfo
	{
		public int Index { get; init; }
		public VtxModelHeader Header { get; init; }
		public LodInfo[] Lods { get; init; } = Array.Empty<LodInfo>();
	}

	public sealed class LodInfo
	{
		public int Index { get; init; }
		public VtxModelLodHeader Header { get; init; }
		public MeshInfo[] Meshes { get; init; } = Array.Empty<MeshInfo>();
	}

	public sealed class MeshInfo
	{
		public int Index { get; init; }
		public VtxMeshHeader Header { get; init; }
		public StripGroupInfo[] StripGroups { get; init; } = Array.Empty<StripGroupInfo>();

		public int TriangleCount
		{
			get
			{
				int count = 0;
				foreach ( var stripGroup in StripGroups )
				{
					foreach ( var strip in stripGroup.Strips )
					{
						count += strip.TriangleCount;
					}
				}

				return count;
			}
		}
	}

	public sealed class StripGroupInfo
	{
		public int Index { get; init; }
		public VtxStripGroupHeader Header { get; init; }
		public VtxVertex[] Vertices { get; init; } = Array.Empty<VtxVertex>();
		public ushort[] Indices { get; init; } = Array.Empty<ushort>();
		public ushort[] TopologyIndices { get; init; } = Array.Empty<ushort>();
		public StripInfo[] Strips { get; init; } = Array.Empty<StripInfo>();
	}

	public sealed class StripInfo
	{
		public int Index { get; init; }
		public VtxStripHeader Header { get; init; }
		public ushort[] Indices { get; init; } = Array.Empty<ushort>();
		public ushort[] TopologyIndices { get; init; } = Array.Empty<ushort>();
		public VtxBoneStateChange[] BoneStateChanges { get; init; } = Array.Empty<VtxBoneStateChange>();
		public int TriangleCount => (Header.Flags & 0x1) != 0 ? Header.IndexCount / 3 : 0;
	}

	public static ParsedVtx Parse( ReadOnlySpan<byte> data )
	{
		EnsureReadable<VtxHeader>( data, 0 );

		var header = Titanfall2Mdl53Binary.ReadStruct<VtxHeader>( data, 0 );
		if ( header.Version != Titanfall2VtxConstants.OptimizedModelFileVersion )
			throw new InvalidOperationException( $"Unsupported VTX version: {header.Version}" );

		return new ParsedVtx
		{
			Header = header,
			MaterialReplacementLists = ParseMaterialReplacementLists( data, header ),
			BodyParts = ParseBodyParts( data, header )
		};
	}

	private static MaterialReplacementListInfo[] ParseMaterialReplacementLists( ReadOnlySpan<byte> data, VtxHeader header )
	{
		if ( header.LodCount <= 0 || header.MaterialReplacementListOffset <= 0 )
			return Array.Empty<MaterialReplacementListInfo>();

		var lists = new MaterialReplacementListInfo[header.LodCount];
		for ( int i = 0; i < lists.Length; i++ )
		{
			int offset = header.MaterialReplacementListOffset + (i * SizeOf<VtxMaterialReplacementListHeader>());
			EnsureReadable<VtxMaterialReplacementListHeader>( data, offset );

			var listHeader = Titanfall2Mdl53Binary.ReadStruct<VtxMaterialReplacementListHeader>( data, offset );
			lists[i] = new MaterialReplacementListInfo
			{
				LodIndex = i,
				Replacements = ParseMaterialReplacements( data, offset, listHeader )
			};
		}

		return lists;
	}

	private static MaterialReplacementInfo[] ParseMaterialReplacements( ReadOnlySpan<byte> data, int listOffset, VtxMaterialReplacementListHeader listHeader )
	{
		if ( listHeader.ReplacementCount <= 0 || listHeader.ReplacementOffset <= 0 )
			return Array.Empty<MaterialReplacementInfo>();

		int replacementBase = listOffset + listHeader.ReplacementOffset;
		var replacements = new MaterialReplacementInfo[listHeader.ReplacementCount];
		for ( int i = 0; i < replacements.Length; i++ )
		{
			int offset = replacementBase + (i * SizeOf<VtxMaterialReplacementHeader>());
			EnsureReadable<VtxMaterialReplacementHeader>( data, offset );

			var replacement = Titanfall2Mdl53Binary.ReadStruct<VtxMaterialReplacementHeader>( data, offset );
			replacements[i] = new MaterialReplacementInfo
			{
				Index = i,
				Header = replacement,
				MaterialName = Titanfall2Mdl53Binary.ReadRelativeString( data, offset, replacement.ReplacementMaterialNameOffset )
			};
		}

		return replacements;
	}

	private static BodyPartInfo[] ParseBodyParts( ReadOnlySpan<byte> data, VtxHeader header )
	{
		if ( header.BodyPartCount <= 0 || header.BodyPartOffset <= 0 )
			return Array.Empty<BodyPartInfo>();

		var bodyParts = new BodyPartInfo[header.BodyPartCount];
		for ( int i = 0; i < bodyParts.Length; i++ )
		{
			int offset = header.BodyPartOffset + (i * SizeOf<VtxBodyPartHeader>());
			EnsureReadable<VtxBodyPartHeader>( data, offset );

			var bodyPart = Titanfall2Mdl53Binary.ReadStruct<VtxBodyPartHeader>( data, offset );
			bodyParts[i] = new BodyPartInfo
			{
				Index = i,
				Header = bodyPart,
				Models = ParseModels( data, offset, bodyPart )
			};
		}

		return bodyParts;
	}

	private static ModelInfo[] ParseModels( ReadOnlySpan<byte> data, int bodyPartOffset, VtxBodyPartHeader bodyPart )
	{
		if ( bodyPart.ModelCount <= 0 || bodyPart.ModelOffset <= 0 )
			return Array.Empty<ModelInfo>();

		int modelBase = bodyPartOffset + bodyPart.ModelOffset;
		var models = new ModelInfo[bodyPart.ModelCount];
		for ( int i = 0; i < models.Length; i++ )
		{
			int offset = modelBase + (i * SizeOf<VtxModelHeader>());
			EnsureReadable<VtxModelHeader>( data, offset );

			var model = Titanfall2Mdl53Binary.ReadStruct<VtxModelHeader>( data, offset );
			models[i] = new ModelInfo
			{
				Index = i,
				Header = model,
				Lods = ParseLods( data, offset, model )
			};
		}

		return models;
	}

	private static LodInfo[] ParseLods( ReadOnlySpan<byte> data, int modelOffset, VtxModelHeader model )
	{
		if ( model.LodCount <= 0 || model.LodOffset <= 0 )
			return Array.Empty<LodInfo>();

		int lodBase = modelOffset + model.LodOffset;
		var lods = new LodInfo[model.LodCount];
		for ( int i = 0; i < lods.Length; i++ )
		{
			int offset = lodBase + (i * SizeOf<VtxModelLodHeader>());
			EnsureReadable<VtxModelLodHeader>( data, offset );

			var lod = Titanfall2Mdl53Binary.ReadStruct<VtxModelLodHeader>( data, offset );
			lods[i] = new LodInfo
			{
				Index = i,
				Header = lod,
				Meshes = ParseMeshes( data, offset, lod )
			};
		}

		return lods;
	}

	private static MeshInfo[] ParseMeshes( ReadOnlySpan<byte> data, int lodOffset, VtxModelLodHeader lod )
	{
		if ( lod.MeshCount <= 0 || lod.MeshOffset <= 0 )
			return Array.Empty<MeshInfo>();

		int meshBase = lodOffset + lod.MeshOffset;
		var meshes = new MeshInfo[lod.MeshCount];
		for ( int i = 0; i < meshes.Length; i++ )
		{
			int offset = meshBase + (i * SizeOf<VtxMeshHeader>());
			EnsureReadable<VtxMeshHeader>( data, offset );

			var mesh = Titanfall2Mdl53Binary.ReadStruct<VtxMeshHeader>( data, offset );
			meshes[i] = new MeshInfo
			{
				Index = i,
				Header = mesh,
				StripGroups = ParseStripGroups( data, offset, mesh )
			};
		}

		return meshes;
	}

	private static StripGroupInfo[] ParseStripGroups( ReadOnlySpan<byte> data, int meshOffset, VtxMeshHeader mesh )
	{
		if ( mesh.StripGroupCount <= 0 || mesh.StripGroupHeaderOffset <= 0 )
			return Array.Empty<StripGroupInfo>();

		int stripGroupBase = meshOffset + mesh.StripGroupHeaderOffset;
		var stripGroups = new StripGroupInfo[mesh.StripGroupCount];
		for ( int i = 0; i < stripGroups.Length; i++ )
		{
			int offset = stripGroupBase + (i * SizeOf<VtxStripGroupHeader>());
			EnsureReadable<VtxStripGroupHeader>( data, offset );

			var stripGroup = Titanfall2Mdl53Binary.ReadStruct<VtxStripGroupHeader>( data, offset );
			stripGroups[i] = new StripGroupInfo
			{
				Index = i,
				Header = stripGroup,
				Vertices = ParseVertices( data, offset, stripGroup ),
				Indices = ParseIndices( data, offset, stripGroup ),
				TopologyIndices = ParseTopologyIndices( data, offset, stripGroup.TopologyOffset, stripGroup.TopologyIndexCount ),
				Strips = ParseStrips( data, offset, stripGroup )
			};
		}

		return stripGroups;
	}

	private static VtxVertex[] ParseVertices( ReadOnlySpan<byte> data, int stripGroupOffset, VtxStripGroupHeader stripGroup )
	{
		if ( stripGroup.VertexCount <= 0 || stripGroup.VertexOffset <= 0 )
			return Array.Empty<VtxVertex>();

		int vertexBase = stripGroupOffset + stripGroup.VertexOffset;
		var vertices = new VtxVertex[stripGroup.VertexCount];
		for ( int i = 0; i < vertices.Length; i++ )
		{
			int offset = vertexBase + (i * SizeOf<VtxVertex>());
			EnsureReadable<VtxVertex>( data, offset );
			vertices[i] = Titanfall2Mdl53Binary.ReadStruct<VtxVertex>( data, offset );
		}

		return vertices;
	}

	private static ushort[] ParseIndices( ReadOnlySpan<byte> data, int stripGroupOffset, VtxStripGroupHeader stripGroup )
	{
		return ParseTopologyIndices( data, stripGroupOffset, stripGroup.IndexOffset, stripGroup.IndexCount );
	}

	private static ushort[] ParseTopologyIndices( ReadOnlySpan<byte> data, int baseOffset, int relativeOffset, int count )
	{
		if ( count <= 0 || relativeOffset <= 0 )
			return Array.Empty<ushort>();

		int offset = baseOffset + relativeOffset;
		int byteLength = checked( count * sizeof(ushort) );
		EnsureRange( data, offset, byteLength );

		var indices = new ushort[count];
		for ( int i = 0; i < indices.Length; i++ )
		{
			indices[i] = Titanfall2Mdl53Binary.ReadStruct<ushort>( data, offset + (i * sizeof(ushort)) );
		}

		return indices;
	}

	private static StripInfo[] ParseStrips( ReadOnlySpan<byte> data, int stripGroupOffset, VtxStripGroupHeader stripGroup )
	{
		if ( stripGroup.StripCount <= 0 || stripGroup.StripOffset <= 0 )
			return Array.Empty<StripInfo>();

		int stripBase = stripGroupOffset + stripGroup.StripOffset;
		var strips = new StripInfo[stripGroup.StripCount];
		for ( int i = 0; i < strips.Length; i++ )
		{
			int offset = stripBase + (i * SizeOf<VtxStripHeader>());
			EnsureReadable<VtxStripHeader>( data, offset );

			var strip = Titanfall2Mdl53Binary.ReadStruct<VtxStripHeader>( data, offset );
			strips[i] = new StripInfo
			{
				Index = i,
				Header = strip,
				Indices = ParseTopologyIndices( data, offset, strip.IndexOffset, strip.IndexCount ),
				TopologyIndices = ParseTopologyIndices( data, offset, strip.TopologyOffset, strip.TopologyIndexCount ),
				BoneStateChanges = ParseBoneStateChanges( data, offset, strip )
			};
		}

		return strips;
	}

	private static VtxBoneStateChange[] ParseBoneStateChanges( ReadOnlySpan<byte> data, int stripOffset, VtxStripHeader strip )
	{
		if ( strip.BoneStateChangeCount <= 0 || strip.BoneStateChangeOffset <= 0 )
			return Array.Empty<VtxBoneStateChange>();

		int boneStateBase = stripOffset + strip.BoneStateChangeOffset;
		var changes = new VtxBoneStateChange[strip.BoneStateChangeCount];
		for ( int i = 0; i < changes.Length; i++ )
		{
			int offset = boneStateBase + (i * SizeOf<VtxBoneStateChange>());
			EnsureReadable<VtxBoneStateChange>( data, offset );
			changes[i] = Titanfall2Mdl53Binary.ReadStruct<VtxBoneStateChange>( data, offset );
		}

		return changes;
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
			throw new InvalidOperationException( $"Requested range is outside the VTX buffer. Offset={offset}, Length={length}, Buffer={data.Length}" );
	}
}

