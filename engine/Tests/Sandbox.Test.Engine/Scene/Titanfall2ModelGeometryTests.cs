using System;
using System.Runtime.InteropServices;
using Titanfall2.Formats;
using NumericsVector3 = System.Numerics.Vector3;
using Mdl = Titanfall2.Formats.Titanfall2Mdl53Reader;
using Vtx = Titanfall2.Formats.Titanfall2VtxReader;

namespace SceneTests;

[TestClass]
public class Titanfall2ModelGeometryTests
{
	[TestMethod]
	public void PackedBodySelectsOneChoiceWithoutShiftingOtherBodyParts()
	{
		var lod = Titanfall2MeshExtractor.ExtractLod( Model( bodyBase: 2 ), body: 4 );
		CollectionAssert.AreEqual( new[] { 2, 0 }, lod.Meshes.Select( mesh => mesh.ModelIndex ).ToArray() );
		CollectionAssert.AreEqual( new[] { 6, 9 }, lod.Meshes.Select( mesh => mesh.Vertices[0].SourceVertexIndex ).ToArray() );
	}

	[DataTestMethod]
	[DataRow( 1 )]
	[DataRow( 2 )]
	[DataRow( 4 )]
	public void DefaultBodyUsesFirstChoiceAndPreservesFollowingVertexOffset( int bodyBase )
	{
		var lod = Titanfall2MeshExtractor.ExtractLod( Model( bodyBase ) );
		Assert.AreEqual( 2, lod.Meshes.Length );
		CollectionAssert.AreEqual( new[] { 0, 0 }, lod.Meshes.Select( mesh => mesh.ModelIndex ).ToArray() );
		CollectionAssert.AreEqual( new[] { 0, 9 }, lod.Meshes.Select( mesh => mesh.Vertices[0].SourceVertexIndex ).ToArray() );
		Assert.AreEqual( 109f, lod.Meshes[1].Vertices[0].Position.x );
	}

	[TestMethod]
	public void AllChoicesRemainAvailableForExplicitImport()
	{
		var lod = Titanfall2MeshExtractor.ExtractLod( Model(), allBodyGroupChoices: true );
		CollectionAssert.AreEqual( new[] { 0, 1, 2, 0 }, lod.Meshes.Select( mesh => mesh.ModelIndex ).ToArray() );
		CollectionAssert.AreEqual( new[] { 0, 3, 6, 9 }, lod.Meshes.Select( mesh => mesh.Vertices[0].SourceVertexIndex ).ToArray() );
	}

	[TestMethod]
	public void BlankDefaultChoiceDoesNotSelectVisibleAlternative()
	{
		var lod = Titanfall2MeshExtractor.ExtractLod( Model( blankDefault: true ) );
		Assert.AreEqual( 1, lod.Meshes.Length );
		Assert.AreEqual( 1, lod.Meshes[0].BodyPartIndex );
		Assert.AreEqual( 6, lod.Meshes[0].Vertices[0].SourceVertexIndex );
	}

	[TestMethod]
	public void ValidFixupsRemapVerticesBeforeBodySelection()
	{
		var model = Model( fixups:
		[
			new VvdFixup { SourceVertexId = 6, VertexCount = 6 },
			new VvdFixup { SourceVertexId = 0, VertexCount = 6 }
		] );
		var lod = Titanfall2MeshExtractor.ExtractLod( model );
		CollectionAssert.AreEqual( new[] { 6, 3 }, lod.Meshes.Select( mesh => mesh.Vertices[0].SourceVertexIndex ).ToArray() );
	}

	[TestMethod]
	public void EmptyFixupAtVertexBufferEndIsValid()
	{
		var lod = Titanfall2MeshExtractor.ExtractLod( Model( fixups:
		[
			new VvdFixup { SourceVertexId = 12, VertexCount = 0 },
			new VvdFixup { SourceVertexId = 0, VertexCount = 12 }
		] ) );
		Assert.AreEqual( 2, lod.TriangleCount );
	}

	[DataTestMethod]
	[DataRow( -1, 1 )]
	[DataRow( 11, 2 )]
	[DataRow( int.MaxValue, 1 )]
	[DataRow( 0, int.MaxValue )]
	[DataRow( 0, -1 )]
	[DataRow( 13, 0 )]
	public void InvalidFixupCannotProducePartialGeometry( int source, int count )
	{
		var model = Model( fixups: [new VvdFixup { SourceVertexId = source, VertexCount = count }] );
		Assert.ThrowsException<InvalidOperationException>( () => Titanfall2MeshExtractor.ExtractLod( model ) );
	}

	[TestMethod]
	public void TotalFixupSizeCannotBeSilentlyTruncated()
	{
		var model = Model( fixups:
		[
			new VvdFixup { SourceVertexId = 0, VertexCount = 7 },
			new VvdFixup { SourceVertexId = 0, VertexCount = 7 }
		] );
		Assert.ThrowsException<InvalidOperationException>( () => Titanfall2MeshExtractor.ExtractLod( model ) );
	}

	[TestMethod]
	public void StripsSliceTheirGroupsIndicesAtElementOffsets()
	{
		var group = Vtx.Parse( VtxFile() ).BodyParts[0].Models[0].Lods[0].Meshes[0].StripGroups[0];
		CollectionAssert.AreEqual( new ushort[] { 0, 1, 2 }, group.Strips[0].Indices );
		CollectionAssert.AreEqual( new ushort[] { 2, 1, 0 }, group.Strips[1].Indices );
		CollectionAssert.AreEqual( new ushort[] { 0, 1, 2, 2, 1, 0 }, group.Indices );
	}

	[TestMethod]
	public void EmptyStripAtIndexBufferEndIsValid()
	{
		var group = Vtx.Parse( VtxFile( 6, 0 ) ).BodyParts[0].Models[0].Lods[0].Meshes[0].StripGroups[0];
		Assert.AreEqual( 0, group.Strips[1].Indices.Length );
	}

	[DataTestMethod]
	[DataRow( -1, 3 )]
	[DataRow( 4, 3 )]
	[DataRow( 7, 0 )]
	[DataRow( int.MaxValue, int.MaxValue )]
	[DataRow( 3, -1 )]
	[DataRow( 0, 7 )]
	public void StripMustStayInsideItsOwnIndexBuffer( int offset, int count )
	{
		var bytes = VtxFile( offset, count );
		Assert.ThrowsException<InvalidOperationException>( () => Vtx.Parse( bytes ) );
	}

	[TestMethod]
	public void MdlWithoutEmbeddedVtxRemainsSupported()
	{
		Assert.IsNull( Mdl.Parse( MdlFile() ).EmbeddedVtx );
	}

	[TestMethod]
	public void MdlReadsValidEmbeddedVtx()
	{
		var parsed = Mdl.Parse( MdlFile( VtxFile() ) );
		var group = parsed.EmbeddedVtx.BodyParts[0].Models[0].Lods[0].Meshes[0].StripGroups[0];
		CollectionAssert.AreEqual( new ushort[] { 2, 1, 0 }, group.Strips[1].Indices );
	}

	[TestMethod]
	public void MdlDoesNotHideEmbeddedVtxVersionErrors()
	{
		var vtx = VtxFile();
		Write( vtx, 0, 6 );
		Assert.ThrowsException<InvalidOperationException>( () => Mdl.Parse( MdlFile( vtx ) ) );
	}

	[TestMethod]
	public void MdlDoesNotHideEmbeddedVtxRangeErrors()
	{
		Assert.ThrowsException<InvalidOperationException>( () => Mdl.Parse( MdlFile( VtxFile( 4, 3 ) ) ) );
	}

	static byte[] MdlFile( byte[] vtx = null )
	{
		var headerSize = Marshal.SizeOf<Mdl53StudioHeader>();
		var data = new byte[headerSize + (vtx?.Length ?? 0)];
		Write( data, 0, new Mdl53StudioHeader
		{
			Id = 0x54534449, Version = 53, Length = data.Length,
			VtxOffset = vtx is null ? 0 : headerSize, VtxSize = vtx?.Length ?? 0
		} );
		vtx?.CopyTo( data, headerSize );
		return data;
	}

	static Mdl.ParsedModel Model( int bodyBase = 1, bool blankDefault = false, VvdFixup[] fixups = null )
	{
		var parts = new Mdl.BodyPartInfo[2];
		var vtxParts = new Vtx.BodyPartInfo[2];
		var vertexCount = 0;
		for ( var part = 0; part < 2; part++ )
		{
			var choices = part == 0 ? 3 : 1;
			var models = new Mdl.ModelInfo[choices];
			var vtxModels = new Vtx.ModelInfo[choices];
			for ( var choice = 0; choice < choices; choice++ )
			{
				var blank = part == 0 && choice == 0 && blankDefault;
				models[choice] = new Mdl.ModelInfo { Meshes = blank ? [] : [new Mdl.MeshInfo { Mesh = MdlMesh() }] };
				vtxModels[choice] = new Vtx.ModelInfo
				{
					Lods = [new Vtx.LodInfo { Meshes = blank ? [] : [VtxMesh()] }]
				};
				if ( !blank ) vertexCount += 3;
			}
			parts[part] = new Mdl.BodyPartInfo
			{
				BodyPart = new Mdl53BodyPart { Base = part == 0 ? bodyBase : 3, ModelCount = choices },
				Models = models
			};
			vtxParts[part] = new Vtx.BodyPartInfo { Models = vtxModels };
		}
		return new Mdl.ParsedModel
		{
			BodyParts = parts,
			EmbeddedVtx = new Vtx.ParsedVtx { BodyParts = vtxParts },
			EmbeddedVertices = Enumerable.Range( 0, vertexCount ).Select( i => new VvdVertex { Position = new NumericsVector3( 100 + i, 0, 0 ) } ).ToArray(),
			EmbeddedVvdFixups = fixups ?? []
		};
	}

	static unsafe Mdl53Mesh MdlMesh()
	{
		var mesh = new Mdl53Mesh { VertexCount = 3 };
		mesh.VertexLodData.LodVertexCounts[0] = 3;
		return mesh;
	}

	static Vtx.MeshInfo VtxMesh() => new()
	{
		StripGroups =
		[
			new Vtx.StripGroupInfo
			{
				Vertices = Enumerable.Range( 0, 3 ).Select( i => new VtxVertex { OriginalMeshVertexId = (ushort)i } ).ToArray(),
				Indices = [0, 1, 2]
			}
		]
	};

	static byte[] VtxFile( int secondOffset = 3, int secondCount = 3 )
	{
		var body = Marshal.SizeOf<VtxHeader>();
		var model = body + Marshal.SizeOf<VtxBodyPartHeader>();
		var lod = model + Marshal.SizeOf<VtxModelHeader>();
		var mesh = lod + Marshal.SizeOf<VtxModelLodHeader>();
		var group = mesh + Marshal.SizeOf<VtxMeshHeader>();
		var vertices = group + Marshal.SizeOf<VtxStripGroupHeader>();
		var indices = vertices + 3 * Marshal.SizeOf<VtxVertex>();
		var strips = indices + 6 * sizeof( ushort );
		var bytes = new byte[strips + 2 * Marshal.SizeOf<VtxStripHeader>()];
		Write( bytes, 0, new VtxHeader { Version = 7, LodCount = 1, BodyPartCount = 1, BodyPartOffset = body } );
		Write( bytes, body, new VtxBodyPartHeader { ModelCount = 1, ModelOffset = model - body } );
		Write( bytes, model, new VtxModelHeader { LodCount = 1, LodOffset = lod - model } );
		Write( bytes, lod, new VtxModelLodHeader { MeshCount = 1, MeshOffset = mesh - lod } );
		Write( bytes, mesh, new VtxMeshHeader { StripGroupCount = 1, StripGroupHeaderOffset = group - mesh } );
		Write( bytes, group, new VtxStripGroupHeader
		{
			VertexCount = 3, VertexOffset = vertices - group,
			IndexCount = 6, IndexOffset = indices - group,
			StripCount = 2, StripOffset = strips - group
		} );
		for ( ushort i = 0; i < 3; i++ )
			Write( bytes, vertices + i * Marshal.SizeOf<VtxVertex>(), new VtxVertex { OriginalMeshVertexId = i } );
		ushort[] values = [0, 1, 2, 2, 1, 0];
		for ( var i = 0; i < values.Length; i++ ) Write( bytes, indices + i * sizeof( ushort ), values[i] );
		Write( bytes, strips, new VtxStripHeader { IndexCount = 3, IndexOffset = 0, VertexCount = 3, Flags = 1 } );
		Write( bytes, strips + Marshal.SizeOf<VtxStripHeader>(), new VtxStripHeader
		{
			IndexCount = secondCount, IndexOffset = secondOffset, VertexCount = 3, Flags = 1
		} );
		return bytes;
	}

	static void Write<T>( byte[] data, int offset, T value ) where T : unmanaged
		=> MemoryMarshal.Write( data.AsSpan( offset ), in value );
}
