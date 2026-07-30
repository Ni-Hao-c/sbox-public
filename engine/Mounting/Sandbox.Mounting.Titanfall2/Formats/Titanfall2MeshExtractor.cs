using System;
using System.Collections.Generic;
using System.Numerics;

namespace Titanfall2.Formats;

public static class Titanfall2MeshExtractor
{
	public sealed class ExtractedLod
	{
		public int LodIndex { get; init; }
		public MeshGeometry[] Meshes { get; init; } = Array.Empty<MeshGeometry>();

		public int VertexCount
		{
			get
			{
				int count = 0;
				foreach ( var mesh in Meshes )
				{
					count += mesh.Vertices.Length;
				}

				return count;
			}
		}

		public int TriangleCount
		{
			get
			{
				int count = 0;
				foreach ( var mesh in Meshes )
				{
					count += mesh.TriangleCount;
				}

				return count;
			}
		}
	}

	public sealed class MeshGeometry
	{
		public int BodyPartIndex { get; init; }
		public int ModelIndex { get; init; }
		public int MeshIndex { get; init; }
		public string BodyPartName { get; init; }
		public string ModelName { get; init; }
		public string MaterialName { get; init; }
		public int MaterialIndex { get; init; }
		public ExtractedVertex[] Vertices { get; init; } = Array.Empty<ExtractedVertex>();
		public int[] Indices { get; init; } = Array.Empty<int>();
		public int TriangleCount => Indices.Length / 3;
	}

	public sealed class ExtractedVertex
	{
		public int SourceVertexIndex { get; init; }
		public Vector3 Position { get; init; }
		public Vector3 Normal { get; init; }
		public Vector2 TexCoord { get; init; }
		public Vector2 TexCoord2 { get; init; }
		public bool HasTexCoord2 { get; init; }
		public VvcColor32 Color { get; init; }
		public bool HasColor { get; init; }
		public byte[] BoneIds { get; init; } = Array.Empty<byte>();
		public float[] BoneWeights { get; init; } = Array.Empty<float>();
	}

	public static ExtractedLod ExtractLod( Titanfall2Mdl53Reader.ParsedModel parsedModel, int lodIndex = 0 )
	{
		if ( parsedModel == null || parsedModel.EmbeddedVtx == null || parsedModel.EmbeddedVertices.Length == 0 )
		{
			return new ExtractedLod
			{
				LodIndex = lodIndex
			};
		}

		var lodVertexMap = BuildLodVertexMap( parsedModel, lodIndex );
		var meshes = new List<MeshGeometry>();
		int lodVertexCursor = 0;

		for ( int bodyPartIndex = 0; bodyPartIndex < parsedModel.BodyParts.Length; bodyPartIndex++ )
		{
			var mdlBodyPart = parsedModel.BodyParts[bodyPartIndex];
			var vtxBodyPart = bodyPartIndex < parsedModel.EmbeddedVtx.BodyParts.Length ? parsedModel.EmbeddedVtx.BodyParts[bodyPartIndex] : null;

			for ( int modelIndex = 0; modelIndex < mdlBodyPart.Models.Length; modelIndex++ )
			{
				var mdlModel = mdlBodyPart.Models[modelIndex];
				var vtxModel = vtxBodyPart != null && modelIndex < vtxBodyPart.Models.Length ? vtxBodyPart.Models[modelIndex] : null;
				var vtxLod = vtxModel != null && lodIndex >= 0 && lodIndex < vtxModel.Lods.Length ? vtxModel.Lods[lodIndex] : null;

				for ( int meshIndex = 0; meshIndex < mdlModel.Meshes.Length; meshIndex++ )
				{
					var mdlMesh = mdlModel.Meshes[meshIndex];
					int lodMeshVertexCount = GetMeshLodVertexCount( mdlMesh.Mesh, lodIndex );

					if ( vtxLod != null && meshIndex < vtxLod.Meshes.Length )
					{
						var extractedMesh = ExtractMesh(
							parsedModel,
							mdlBodyPart,
							mdlModel,
							mdlMesh,
							vtxLod.Meshes[meshIndex],
							lodVertexMap,
							lodVertexCursor,
							bodyPartIndex,
							modelIndex,
							meshIndex );

						if ( extractedMesh != null )
							meshes.Add( extractedMesh );
					}

					lodVertexCursor += lodMeshVertexCount;
				}
			}
		}

		return new ExtractedLod
		{
			LodIndex = lodIndex,
			Meshes = meshes.ToArray()
		};
	}

	private static MeshGeometry ExtractMesh(
		Titanfall2Mdl53Reader.ParsedModel parsedModel,
		Titanfall2Mdl53Reader.BodyPartInfo bodyPart,
		Titanfall2Mdl53Reader.ModelInfo model,
		Titanfall2Mdl53Reader.MeshInfo mesh,
		Titanfall2VtxReader.MeshInfo vtxMesh,
		int[] lodVertexMap,
		int lodVertexCursor,
		int bodyPartIndex,
		int modelIndex,
		int meshIndex )
	{
		var vertices = new List<ExtractedVertex>();
		var indices = new List<int>();

		foreach ( var stripGroup in vtxMesh.StripGroups )
		{
			int baseVertexIndex = vertices.Count;

			for ( int stripVertexIndex = 0; stripVertexIndex < stripGroup.Vertices.Length; stripVertexIndex++ )
			{
				var vtxVertex = stripGroup.Vertices[stripVertexIndex];
				int lodVertexIndex = lodVertexCursor + vtxVertex.OriginalMeshVertexId;
				int sourceVertexIndex = lodVertexIndex >= 0 && lodVertexIndex < lodVertexMap.Length ? lodVertexMap[lodVertexIndex] : -1;

				vertices.Add( CreateExtractedVertex( parsedModel, sourceVertexIndex ) );
			}

			for ( int i = 0; i < stripGroup.Indices.Length; i++ )
			{
				int localVertexIndex = stripGroup.Indices[i];
				if ( localVertexIndex < 0 || localVertexIndex >= stripGroup.Vertices.Length )
					continue;

				indices.Add( baseVertexIndex + localVertexIndex );
			}
		}

		return new MeshGeometry
		{
			BodyPartIndex = bodyPartIndex,
			ModelIndex = modelIndex,
			MeshIndex = meshIndex,
			BodyPartName = bodyPart.Name,
			ModelName = model.Name,
			MaterialIndex = mesh.Mesh.MaterialIndex,
			MaterialName = GetMaterialName( parsedModel, mesh.Mesh.MaterialIndex ),
			Vertices = vertices.ToArray(),
			Indices = indices.ToArray()
		};
	}

	private static ExtractedVertex CreateExtractedVertex( Titanfall2Mdl53Reader.ParsedModel parsedModel, int sourceVertexIndex )
	{
		if ( sourceVertexIndex < 0 || sourceVertexIndex >= parsedModel.EmbeddedVertices.Length )
		{
			return new ExtractedVertex
			{
				SourceVertexIndex = -1
			};
		}

		var vertex = parsedModel.EmbeddedVertices[sourceVertexIndex];
		var extracted = new ExtractedVertex
		{
			SourceVertexIndex = sourceVertexIndex,
			Position = vertex.Position,
			Normal = vertex.Normal,
			TexCoord = vertex.TexCoord,
			BoneIds = ReadBoneIds( vertex ),
			BoneWeights = ReadBoneWeights( vertex )
		};

		if ( sourceVertexIndex < parsedModel.EmbeddedUv2.Length )
		{
			extracted = new ExtractedVertex
			{
				SourceVertexIndex = extracted.SourceVertexIndex,
				Position = extracted.Position,
				Normal = extracted.Normal,
				TexCoord = extracted.TexCoord,
				TexCoord2 = parsedModel.EmbeddedUv2[sourceVertexIndex],
				HasTexCoord2 = true,
				Color = extracted.Color,
				HasColor = extracted.HasColor,
				BoneIds = extracted.BoneIds,
				BoneWeights = extracted.BoneWeights
			};
		}

		if ( sourceVertexIndex < parsedModel.EmbeddedVertexColors.Length )
		{
			extracted = new ExtractedVertex
			{
				SourceVertexIndex = extracted.SourceVertexIndex,
				Position = extracted.Position,
				Normal = extracted.Normal,
				TexCoord = extracted.TexCoord,
				TexCoord2 = extracted.TexCoord2,
				HasTexCoord2 = extracted.HasTexCoord2,
				Color = parsedModel.EmbeddedVertexColors[sourceVertexIndex],
				HasColor = true,
				BoneIds = extracted.BoneIds,
				BoneWeights = extracted.BoneWeights
			};
		}

		return extracted;
	}

	private static string GetMaterialName( Titanfall2Mdl53Reader.ParsedModel parsedModel, int materialIndex )
	{
		if ( materialIndex < 0 || materialIndex >= parsedModel.Textures.Length )
			return string.Empty;

		return parsedModel.Textures[materialIndex].Name;
	}

	private static int[] BuildLodVertexMap( Titanfall2Mdl53Reader.ParsedModel parsedModel, int lodIndex )
	{
		if ( parsedModel.EmbeddedVertices.Length == 0 )
			return Array.Empty<int>();

		if ( parsedModel.EmbeddedVvdFixups.Length == 0 )
		{
			int count = GetIdentityVertexCount( parsedModel, lodIndex );
			var map = new int[count];
			for ( int i = 0; i < count; i++ )
			{
				map[i] = i;
			}

			return map;
		}

		var remapped = new List<int>();
		foreach ( var fixup in parsedModel.EmbeddedVvdFixups )
		{
			if ( fixup.Lod < lodIndex )
				continue;

			for ( int i = 0; i < fixup.VertexCount; i++ )
			{
				remapped.Add( fixup.SourceVertexId + i );
			}
		}

		return remapped.ToArray();
	}

	private static int GetIdentityVertexCount( Titanfall2Mdl53Reader.ParsedModel parsedModel, int lodIndex )
	{
		if ( parsedModel.EmbeddedVvdHeader == null )
			return parsedModel.EmbeddedVertices.Length;

		var vvdHeader = parsedModel.EmbeddedVvdHeader.Value;

		unsafe
		{
			if ( lodIndex >= 0 && lodIndex < Titanfall2Mdl53Constants.MaxLods )
			{
				int lodCount = vvdHeader.LodVertexCounts[lodIndex];
				if ( lodCount > 0 )
					return Math.Min( lodCount, parsedModel.EmbeddedVertices.Length );
			}
		}

		return parsedModel.EmbeddedVertices.Length;
	}

	private static unsafe int GetMeshLodVertexCount( Mdl53Mesh mesh, int lodIndex )
	{
		if ( lodIndex < 0 || lodIndex >= Titanfall2Mdl53Constants.MaxLods )
			return mesh.VertexCount;

		var vertexLodData = mesh.VertexLodData;
		return vertexLodData.LodVertexCounts[lodIndex];
	}

	private static unsafe byte[] ReadBoneIds( VvdVertex vertex )
	{
		int count = Math.Clamp( (int)vertex.BoneWeights.BoneCount, 0, Titanfall2Mdl53Constants.MaxBonesPerVertex );
		var ids = new byte[count];
		for ( int i = 0; i < count; i++ )
		{
			ids[i] = vertex.BoneWeights.Bone[i];
		}

		return ids;
	}

	private static unsafe float[] ReadBoneWeights( VvdVertex vertex )
	{
		int count = Math.Clamp( (int)vertex.BoneWeights.BoneCount, 0, Titanfall2Mdl53Constants.MaxBonesPerVertex );
		var weights = new float[count];
		for ( int i = 0; i < count; i++ )
		{
			weights[i] = vertex.BoneWeights.Weight[i];
		}

		return weights;
	}
}

