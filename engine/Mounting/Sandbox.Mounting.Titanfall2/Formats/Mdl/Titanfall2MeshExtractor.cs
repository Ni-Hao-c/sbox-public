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
		/// <summary>The mesh's skin reference before the skin-family lookup.</summary>
		public int SkinReference { get; init; }
		public ExtractedVertex[] Vertices { get; init; } = Array.Empty<ExtractedVertex>();
		public int[] Indices { get; init; } = Array.Empty<int>();
		public int TriangleCount => Indices.Length / 3;
	}

	/// <summary>
	/// One extracted vertex, addressed by index inside the mesh's vertex array. This is a value
	/// type on purpose: extracting a static prop decodes every vertex of four LOD bands, and the
	/// previous class shape allocated one object, one byte[] and one float[] per vertex, plus two
	/// throwaway object copies whenever UV2 or vertex colours were present. Bone influences now
	/// live in two buffers owned by the whole mesh and are exposed as slices, so a vertex costs
	/// no allocation of its own.
	/// </summary>
	public readonly struct ExtractedVertex
	{
		public int SourceVertexIndex { get; init; }
		public Vector3 Position { get; init; }
		public Vector3 Normal { get; init; }
		public Vector2 TexCoord { get; init; }
		public Vector2 TexCoord2 { get; init; }
		public bool HasTexCoord2 { get; init; }
		public VvcColor32 Color { get; init; }
		public bool HasColor { get; init; }

		/// <summary>Bone ids of the whole mesh; this vertex occupies BoneOffset..BoneOffset+BoneCount.</summary>
		internal byte[] BoneIdBuffer { get; init; }
		/// <summary>Bone weights of the whole mesh, parallel to <see cref="BoneIdBuffer"/>.</summary>
		internal float[] BoneWeightBuffer { get; init; }
		internal int BoneOffset { get; init; }
		internal int BoneCount { get; init; }

		/// <summary>Bone ids of this vertex's influences, in the order the VVD stored them.</summary>
		public ReadOnlySpan<byte> BoneIds => BoneIdBuffer is null
			? ReadOnlySpan<byte>.Empty
			: new ReadOnlySpan<byte>( BoneIdBuffer, BoneOffset, BoneCount );

		/// <summary>Bone weights of this vertex's influences, parallel to <see cref="BoneIds"/>.</summary>
		public ReadOnlySpan<float> BoneWeights => BoneWeightBuffer is null
			? ReadOnlySpan<float>.Empty
			: new ReadOnlySpan<float>( BoneWeightBuffer, BoneOffset, BoneCount );
	}

	public static ExtractedLod ExtractLod( Titanfall2Mdl53Reader.ParsedModel parsedModel, int lodIndex = 0, bool allBodyGroupChoices = false,
		int body = 0, int skin = 0 )
	{
		ArgumentOutOfRangeException.ThrowIfNegative( body );
		ArgumentOutOfRangeException.ThrowIfNegative( skin );
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
			var selectedModel = mdlBodyPart.BodyPart.Base > 0 && mdlBodyPart.Models.Length > 0
				? body / mdlBodyPart.BodyPart.Base % mdlBodyPart.Models.Length : 0;
			// Base is the multiplier for this body's digit in a packed body value,
			// not a model index. With no body override (body = 0), every part uses
			// model 0, including an authored blank choice.

			for ( int modelIndex = 0; modelIndex < mdlBodyPart.Models.Length; modelIndex++ )
			{
				var mdlModel = mdlBodyPart.Models[modelIndex];
				var vtxModel = vtxBodyPart != null && modelIndex < vtxBodyPart.Models.Length ? vtxBodyPart.Models[modelIndex] : null;
				var vtxLod = vtxModel != null && lodIndex >= 0 && lodIndex < vtxModel.Lods.Length ? vtxModel.Lods[lodIndex] : null;
				var includeModel = allBodyGroupChoices || modelIndex == selectedModel;

				for ( int meshIndex = 0; meshIndex < mdlModel.Meshes.Length; meshIndex++ )
				{
					var mdlMesh = mdlModel.Meshes[meshIndex];
					int lodMeshVertexCount = GetMeshLodVertexCount( mdlMesh.Mesh, lodIndex );

					if ( includeModel && vtxLod != null && meshIndex < vtxLod.Meshes.Length )
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
							meshIndex, lodIndex, skin );

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
		int meshIndex, int lodIndex, int skin )
	{
		// Every vertex of a strip group is emitted, so the exact output size is known before
		// anything is filled. Write straight into the final arrays instead of growing a List of
		// reference-typed vertices: one dense struct array plus two bone buffers per mesh.
		var vertexCount = 0;
		foreach ( var stripGroup in vtxMesh.StripGroups )
		{
			vertexCount += stripGroup.Vertices.Length;
		}

		var vertices = vertexCount == 0 ? Array.Empty<ExtractedVertex>() : new ExtractedVertex[vertexCount];
		var boneIds = vertexCount == 0
			? Array.Empty<byte>()
			: new byte[vertexCount * Titanfall2Mdl53Constants.MaxBonesPerVertex];
		var boneWeights = vertexCount == 0
			? Array.Empty<float>()
			: new float[vertexCount * Titanfall2Mdl53Constants.MaxBonesPerVertex];
		var indices = new List<int>();
		var vertexCursor = 0;

		foreach ( var stripGroup in vtxMesh.StripGroups )
		{
			int baseVertexIndex = vertexCursor;

			for ( int stripVertexIndex = 0; stripVertexIndex < stripGroup.Vertices.Length; stripVertexIndex++ )
			{
				var vtxVertex = stripGroup.Vertices[stripVertexIndex];
				int lodVertexIndex = checked(lodVertexCursor + vtxVertex.OriginalMeshVertexId);
				if ( (uint)lodVertexIndex >= lodVertexMap.Length )
					throw new InvalidOperationException( "VTX vertex is outside the VVD LOD vertex map." );
				int sourceVertexIndex = lodVertexMap[lodVertexIndex];

				vertices[vertexCursor] = CreateExtractedVertex( parsedModel, sourceVertexIndex, boneIds, boneWeights,
					vertexCursor * Titanfall2Mdl53Constants.MaxBonesPerVertex );
				vertexCursor++;
			}

			void AddTriangle( int a, int b, int c )
			{
				if ( (uint)a >= stripGroup.Vertices.Length || (uint)b >= stripGroup.Vertices.Length || (uint)c >= stripGroup.Vertices.Length )
					throw new InvalidOperationException( "VTX triangle is outside its strip-group vertex buffer." );
				// Degenerate triangles join separate runs in a triangle strip. They
				// still advance its parity, but must not become render/physics faces.
				if ( a == b || b == c || c == a ) return;
				indices.Add( baseVertexIndex + a );
				indices.Add( baseVertexIndex + b );
				indices.Add( baseVertexIndex + c );
			}

			void AddTriangleList( ushort[] source )
			{
				if ( source.Length % 3 != 0 ) throw new InvalidOperationException( "VTX triangle list has incomplete triangles." );
				for ( var i = 0; i < source.Length; i += 3 ) AddTriangle( source[i], source[i + 1], source[i + 2] );
			}

			if ( stripGroup.Strips.Length == 0 )
			{
				AddTriangleList( stripGroup.Indices );
				continue;
			}
			foreach ( var strip in stripGroup.Strips )
			{
				// optimize.h: 0x01 is TRILIST, 0x02 is TRISTRIP. Strip indices
				// already reference the group's vertices; VertexOffset is not added.
				var topology = strip.Header.Flags & 3;
				if ( topology == 1 ) AddTriangleList( strip.Indices );
				else if ( topology == 2 )
				{
					for ( var i = 2; i < strip.Indices.Length; ++i )
					{
						var a = strip.Indices[i - 2]; var b = strip.Indices[i - 1]; var c = strip.Indices[i];
						if ( (i & 1) != 0 ) AddTriangle( b, a, c );
						else AddTriangle( a, b, c );
					}
				}
				else if ( strip.Indices.Length > 0 ) throw new InvalidOperationException( $"Unsupported VTX strip topology {topology}." );
			}
		}

		var material = ResolveMaterial( parsedModel, mesh.Mesh.MaterialIndex, lodIndex, skin );
		return new MeshGeometry
		{
			BodyPartIndex = bodyPartIndex,
			ModelIndex = modelIndex,
			MeshIndex = meshIndex,
			BodyPartName = bodyPart.Name,
			ModelName = model.Name,
			MaterialIndex = material.Index,
			MaterialName = material.Name,
			SkinReference = mesh.Mesh.MaterialIndex,
			Vertices = vertices,
			Indices = indices.ToArray()
		};
	}

	private static ExtractedVertex CreateExtractedVertex(
		Titanfall2Mdl53Reader.ParsedModel parsedModel,
		int sourceVertexIndex,
		byte[] boneIds,
		float[] boneWeights,
		int boneOffset )
	{
		if ( sourceVertexIndex < 0 || sourceVertexIndex >= parsedModel.EmbeddedVertices.Length )
		{
			throw new InvalidOperationException( "VTX vertex references a missing VVD vertex." );
		}

		var vertex = parsedModel.EmbeddedVertices[sourceVertexIndex];
		// Optional channels are still indexed by the source vertex; an absent channel keeps the
		// default value and its Has flag stays false, exactly as the previous three-pass copy did.
		var hasTexCoord2 = sourceVertexIndex < parsedModel.EmbeddedUv2.Length;
		var hasColor = sourceVertexIndex < parsedModel.EmbeddedVertexColors.Length;
		var boneCount = CopyBoneInfluences( vertex, boneIds, boneWeights, boneOffset );

		return new ExtractedVertex
		{
			SourceVertexIndex = sourceVertexIndex,
			Position = vertex.Position,
			Normal = vertex.Normal,
			TexCoord = vertex.TexCoord,
			TexCoord2 = hasTexCoord2 ? parsedModel.EmbeddedUv2[sourceVertexIndex] : default,
			HasTexCoord2 = hasTexCoord2,
			Color = hasColor ? parsedModel.EmbeddedVertexColors[sourceVertexIndex] : default,
			HasColor = hasColor,
			BoneIdBuffer = boneIds,
			BoneWeightBuffer = boneWeights,
			BoneOffset = boneOffset,
			BoneCount = boneCount
		};
	}

	/// <summary>Material name a mesh skin reference uses in one skin family and LOD, including VTX LOD replacements.</summary>
	public static string ResolveMaterialName( Titanfall2Mdl53Reader.ParsedModel parsedModel, int skinReference, int lod, int skin )
		=> ResolveMaterial( parsedModel, skinReference, lod, skin ).Name;

	private static (int Index, string Name) ResolveMaterial( Titanfall2Mdl53Reader.ParsedModel parsedModel, int materialIndex, int lod, int skin )
	{
		var references = parsedModel.Header.SkinReferenceCount;
		var families = parsedModel.Header.SkinFamilyCount;
		if ( references > 0 && families > 0 )
		{
			if ( skin >= families ) throw new ArgumentOutOfRangeException( nameof( skin ), $"Model has {families} skin families." );
			var index = checked(skin * references + materialIndex);
			if ( materialIndex < 0 || materialIndex >= references || (uint)index >= parsedModel.SkinTable.Length )
				throw new InvalidOperationException( "MDL material is outside its skin reference table." );
			materialIndex = parsedModel.SkinTable[index];
		}
		else if ( skin != 0 ) throw new ArgumentOutOfRangeException( nameof( skin ), "Model has no skin families." );
		if ( materialIndex < 0 || materialIndex >= parsedModel.Textures.Length )
			return (materialIndex, string.Empty);

		var replacements = parsedModel.EmbeddedVtx?.MaterialReplacementLists;
		if ( replacements is not null )
		{
			foreach ( var list in replacements )
			{
				if ( list.LodIndex != lod ) continue;
				foreach ( var replacement in list.Replacements )
					if ( replacement.Header.MaterialId == materialIndex && !string.IsNullOrWhiteSpace( replacement.MaterialName ) )
						return (materialIndex, replacement.MaterialName);
			}
		}
		return (materialIndex, parsedModel.Textures[materialIndex].Name);
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

			// Reject corrupt ranges rather than truncating them or mapping missing
			// vertices to the origin. Validate before growing the remap list.
			var vertexCount = parsedModel.EmbeddedVertices.Length;
			if ( fixup.SourceVertexId < 0 || fixup.VertexCount < 0
				|| fixup.SourceVertexId > vertexCount || fixup.VertexCount > vertexCount - fixup.SourceVertexId
				|| fixup.VertexCount > vertexCount - remapped.Count )
				throw new InvalidOperationException( "VVD fixup is outside the vertex buffer." );
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

	/// <summary>Copies one source vertex's VVD bone influences into the mesh's shared buffers.</summary>
	/// <returns>The number of influences copied (0 to MaxBonesPerVertex).</returns>
	private static unsafe int CopyBoneInfluences( VvdVertex vertex, byte[] boneIds, float[] boneWeights, int boneOffset )
	{
		int count = Math.Clamp( (int)vertex.BoneWeights.BoneCount, 0, Titanfall2Mdl53Constants.MaxBonesPerVertex );
		for ( int i = 0; i < count; i++ )
		{
			boneIds[boneOffset + i] = vertex.BoneWeights.Bone[i];
			boneWeights[boneOffset + i] = vertex.BoneWeights.Weight[i];
		}

		return count;
	}
}

