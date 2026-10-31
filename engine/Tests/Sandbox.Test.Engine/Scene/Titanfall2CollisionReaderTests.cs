using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;
using Titanfall2;

namespace SceneTests;

[TestClass]
public class Titanfall2CollisionReaderTests
{
	sealed class Fixture
	{
		public readonly Dictionary<int, byte[]> Lumps = new();
		public Fixture( int models = 2, int sets = 3 )
		{
			Lumps[14] = new byte[models * 32];
			Lumps[85] = new byte[28];
			Int( Lumps[85], 12, 1 ); Int( Lumps[85], 16, 1 );
			Lumps[86] = new byte[(models + 1) * 4];
			Cell( 0, 0, 1 ); Cell( 1, 1, sets - 1 );
			if ( models > 1 ) { Cell( 1, 1, 1 ); Cell( 2, sets - 1, 1 ); }
			Lumps[87] = new byte[sets * 8];
			Lumps[89] = [];
			Lumps[91] = new byte[8];
			Int( Lumps[91], 0, 0x00EB0280 ); Int( Lumps[91], 4, 0x20 );
			Lumps[92] = new byte[sets * 32];
			for ( var i = 0; i < sets; ++i )
			{
				Set( i, (uint)i << 8 );
				Float( Lumps[92], i * 32, i * 100 );
				for ( var j = 0; j < 3; ++j ) Float( Lumps[92], i * 32 + 16 + j * 4, 10 );
			}
		}

		public void Cell( int cell, int first, int count )
		{
			Short( Lumps[86], cell * 4, first ); Short( Lumps[86], cell * 4 + 2, count );
		}
		public void Set( int set, uint primitive, int count = 1 )
		{
			Short( Lumps[87], set * 8 + 2, count );
			BinaryPrimitives.WriteUInt32LittleEndian( Lumps[87].AsSpan( set * 8 + 4 ), primitive );
		}

		public bool Read( out Titanfall2BspReader.ParsedBsp bsp, out string error )
			=> Titanfall2BspReader.TryRead( Bytes(), "collision_fixture", null, true, false, false, out bsp, out error );

		public byte[] Bytes()
		{
			var bytes = new byte[16 + 128 * 16 + Lumps.Sum( x => x.Value.Length )];
			Encoding.ASCII.GetBytes( "rBSP" ).CopyTo( bytes, 0 );
			Int( bytes, 4, 37 ); Int( bytes, 12, 127 );
			var offset = 16 + 128 * 16;
			foreach ( var (id, data) in Lumps )
			{
				Int( bytes, 16 + id * 16, offset ); Int( bytes, 20 + id * 16, data.Length );
				data.CopyTo( bytes, offset ); offset += data.Length;
			}
			return bytes;
		}
	}

	static void Int( byte[] bytes, int offset, int value ) => BinaryPrimitives.WriteInt32LittleEndian( bytes.AsSpan( offset ), value );
	static void Short( byte[] bytes, int offset, int value ) => BinaryPrimitives.WriteUInt16LittleEndian( bytes.AsSpan( offset ), checked((ushort)value) );
	static void Float( byte[] bytes, int offset, float value ) => Int( bytes, offset, BitConverter.SingleToInt32Bits( value ) );
	static Titanfall2BspReader.BspWorldCollision Read( Fixture fixture )
	{
		Assert.IsTrue( fixture.Read( out var bsp, out var error ), error );
		return bsp.WorldCollision;
	}

	[TestMethod]
	public void WorldKeepsGlobalCellButExcludesInlineModel()
	{
		var collision = Read( new Fixture() );
		Assert.AreEqual( 2, collision.SourcePrimitiveCount );
		Assert.AreEqual( 2, collision.BrushCount );
		Assert.AreEqual( 110f, collision.Vertices.Max( x => x.X ) );
	}

	[TestMethod]
	public void WorldUsesRetailGeosetPrefixIncludingUnreferencedSets()
	{
		var fixture = new Fixture( sets: 4 );
		fixture.Cell( 1, 1, 1 ); // Set 2 is outside the XY/global cell ranges, still before model 1.
		var collision = Read( fixture );
		Assert.AreEqual( 3, collision.SourcePrimitiveCount );
		Assert.AreEqual( 3, collision.BrushCount );
		Assert.AreEqual( 210f, collision.Vertices.Max( x => x.X ) );
	}

	[TestMethod]
	public void SingleModelOwnsAllGeosets()
	{
		var collision = Read( new Fixture( models: 1 ) );
		Assert.AreEqual( 3, collision.BrushCount );
		Assert.AreEqual( 0, collision.RejectedPrimitiveCount );
	}

	[TestMethod]
	public void ExpandedSolidMatchesPlayerMaskAndPureWaterDoesNot()
	{
		// Corrects the old audit's arithmetic: EB0280 & 0201400B = 00010000.
		Assert.AreEqual( 0x00010000u, 0x00EB0280u & 0x0201400Bu );
		var fixture = new Fixture( models: 1, sets: 2 ); fixture.Set( 1, 0x101 );
		var collision = Read( fixture );
		Assert.AreEqual( 1, collision.BrushCount );
		Assert.AreEqual( 1, collision.WaterPrimitiveCount );
		Assert.AreEqual( 0, collision.BlockingWaterPrimitiveCount );
	}

	[TestMethod]
	public void SameGeometryWithDifferentContentsDoesNotLoseLaterBlocker()
	{
		var fixture = new Fixture( models: 1, sets: 2 );
		fixture.Set( 0, 1 ); // Water-only reference to brush 0 comes first.
		fixture.Set( 1, 0 ); // Same brush, player-blocking contents.
		var collision = Read( fixture );
		Assert.AreEqual( 2, collision.SourcePrimitiveCount );
		Assert.AreEqual( 1, collision.NonBlockingPrimitiveCount );
		Assert.AreEqual( 1, collision.BrushCount );
	}

	[TestMethod]
	public void DuplicateCellReferencesAreDeduplicated()
	{
		var fixture = new Fixture( models: 1, sets: 2 ); fixture.Set( 1, 0 );
		Assert.AreEqual( 1, Read( fixture ).SourcePrimitiveCount );
	}

	[TestMethod]
	public void StaticPropsAreLeftToThePropImporter()
	{
		var fixture = new Fixture( models: 1, sets: 2 ); fixture.Set( 1, 0x60000000 );
		Assert.AreEqual( 1, Read( fixture ).SourcePrimitiveCount );
	}

	[TestMethod]
	public void BrushIndexUsesAllTwentyOneBits()
	{
		var fixture = new Fixture( models: 1, sets: 1 );
		const int index = 65536;
		fixture.Set( 0, (uint)index << 8 );
		fixture.Lumps[92] = new byte[(index + 1) * 32];
		Float( fixture.Lumps[92], index * 32, 1000 );
		for ( var i = 0; i < 3; ++i ) Float( fixture.Lumps[92], index * 32 + 16 + i * 4, 10 );
		var collision = Read( fixture );
		Assert.AreEqual( 1, collision.BrushCount );
		Assert.AreEqual( 1010f, collision.Vertices.Max( x => x.X ) );
	}

	[TestMethod]
	public void FogVolumeAlsoUsesTheFullBrushIndex()
	{
		var fixture = new Fixture();
		const int index = 65536;
		fixture.Set( 2, (uint)index << 8 );
		fixture.Lumps[92] = new byte[(index + 1) * 32];
		Float( fixture.Lumps[92], index * 32, 1000 );
		for ( var i = 0; i < 3; ++i ) Float( fixture.Lumps[92], index * 32 + 16 + i * 4, 10 );
		fixture.Lumps[0] = Encoding.ASCII.GetBytes( "{\"classname\" \"fog_volume\" \"model\" \"*1\"}" );
		Assert.IsTrue( fixture.Read( out var bsp, out var error ), error );
		Assert.AreEqual( "1 0 0 1010", bsp.Entities.Single()["*trigger_brush_0_plane_0"] );
	}

	[TestMethod]
	public void GeosetPrimitiveOffsetUsesAllTwentyOneBits()
	{
		var fixture = new Fixture( models: 1, sets: 2 );
		const int first = 65536;
		fixture.Set( 0, (uint)first << 8, count: 2 ); fixture.Set( 1, 0, count: 0 );
		fixture.Lumps[89] = new byte[(first + 2) * 4];
		Int( fixture.Lumps[89], (first + 1) * 4, 1 << 8 );
		var collision = Read( fixture );
		Assert.AreEqual( 2, collision.BrushCount );
		Assert.AreEqual( 110f, collision.Vertices.Max( x => x.X ) );
	}

	[DataTestMethod]
	[DataRow( "cells" )]
	[DataRow( "model-range" )]
	[DataRow( "primitive-range" )]
	[DataRow( "contents" )]
	[DataRow( "type" )]
	public void InvalidReferencesRejectImportRatherThanGuessing( string kind )
	{
		var fixture = new Fixture();
		switch ( kind )
		{
			case "cells": Int( fixture.Lumps[85], 12, int.MaxValue ); break;
			case "model-range": fixture.Cell( 2, 100, 1 ); break;
			case "primitive-range": fixture.Set( 0, 0, count: 2 ); break;
			case "contents": fixture.Set( 0, 0xFF ); break;
			case "type": fixture.Set( 0, 0x80000000 ); break;
		}
		Assert.IsFalse( fixture.Read( out _, out var error ) );
		StringAssert.Contains( error, "CM" );
	}

	[TestMethod]
	public void MissingBrushPlaneRejectsPrimitiveInsteadOfImportingItsAabb()
	{
		var fixture = new Fixture( models: 1, sets: 1 );
		fixture.Lumps[92][13] = 1; fixture.Lumps[93] = new byte[2];
		var collision = Read( fixture );
		Assert.IsTrue( collision.IsEmpty );
		Assert.AreEqual( 1, collision.RejectedPrimitiveCount );
	}

	[DataTestMethod]
	[DataRow( false )]
	[DataRow( true )]
	public void InvalidTricollDoesNotLeavePartialTriangles( bool overflow )
	{
		var fixture = new Fixture( models: 1, sets: 1 ); fixture.Set( 0, 0x40000000 );
		fixture.Lumps[3] = new byte[36]; fixture.Lumps[69] = new byte[44]; fixture.Lumps[66] = new byte[8];
		Short( fixture.Lumps[69], 6, 3 ); Short( fixture.Lumps[69], 8, 2 );
		Int( fixture.Lumps[69], 12, overflow ? int.MaxValue : 0 );
		Int( fixture.Lumps[66], 0, (1 << 10) | (2 << 17) );
		Int( fixture.Lumps[66], 4, (1 << 10) | (5 << 17) );
		var collision = Read( fixture );
		Assert.IsTrue( collision.IsEmpty ); Assert.AreEqual( 0, collision.Vertices.Length );
		Assert.AreEqual( 1, collision.RejectedPrimitiveCount );
	}

	[TestMethod]
	public void NonFiniteTricollRollsBackAlreadyWrittenVertices()
	{
		var fixture = new Fixture( models: 1, sets: 1 ); fixture.Set( 0, 0x40000000 );
		fixture.Lumps[3] = new byte[36]; fixture.Lumps[69] = new byte[44]; fixture.Lumps[66] = new byte[4];
		Short( fixture.Lumps[69], 6, 3 ); Short( fixture.Lumps[69], 8, 1 );
		Float( fixture.Lumps[3], 12, float.NaN );
		var collision = Read( fixture );
		Assert.IsTrue( collision.IsEmpty ); Assert.AreEqual( 0, collision.Vertices.Length );
		Assert.AreEqual( 1, collision.RejectedPrimitiveCount );
	}

	[TestMethod]
	public void SidecarContractIncludesModelsAndGridCells()
	{
		CollectionAssert.Contains( Titanfall2BspReader.RequiredCollisionLumpIds.ToArray(), 14 );
		CollectionAssert.Contains( Titanfall2BspReader.RequiredCollisionLumpIds.ToArray(), 86 );
	}

	[TestMethod]
	public void InlineModelKeepsItsOwnCollisionAndRawNonPlayerContents()
	{
		var fixture = new Fixture();
		fixture.Set( 2, 0x201 ); // Inline brush 2, water-only. Do not player-filter a game-owned volume.
		Assert.IsTrue( Titanfall2BspReader.TryReadBrushModels( fixture.Bytes(), null, false, out var catalog, out var error ), error );
		var model = catalog.Models.Single();
		Assert.AreEqual( 1, model.Index );
		Assert.AreEqual( 1, model.Collision.Parts.Count );
		Assert.AreEqual( 0x20u, model.Collision.Parts[0].Contents );
		Assert.IsTrue( model.Collision.Parts[0].IsConvex );
		Assert.AreEqual( 190f, model.Collision.Vertices.Min( point => point.X ) );
		Assert.AreEqual( 210f, model.Collision.Vertices.Max( point => point.X ) );
		Assert.AreEqual( 2, Read( fixture ).SourcePrimitiveCount );
	}

	[TestMethod]
	public void SeparateInlineBrushesRemainSeparateConvexParts()
	{
		var fixture = new Fixture( sets: 4 ); fixture.Cell( 2, 2, 2 );
		Assert.IsTrue( Titanfall2BspReader.TryReadBrushModels( fixture.Bytes(), null, false, out var catalog, out var error ), error );
		var collision = catalog.Models.Single().Collision;
		Assert.AreEqual( 2, collision.Parts.Count );
		Assert.AreEqual( 2, collision.Parts[0].SourceIndex ); Assert.AreEqual( 3, collision.Parts[1].SourceIndex );
		Assert.AreEqual( collision.Vertices.Length, collision.Parts.Sum( part => part.VertexCount ) );
		Assert.AreEqual( collision.Indices.Length, collision.Parts.Sum( part => part.IndexCount ) );
		foreach ( var part in collision.Parts )
			Assert.IsTrue( collision.Indices.Skip( part.FirstIndex ).Take( part.IndexCount )
				.All( value => value >= part.FirstVertex && value < part.FirstVertex + part.VertexCount ) );
	}

	[TestMethod]
	public void InlineModelRetainsLocalPositionsNormalsColorAndBothUvs()
	{
		var fixture = new Fixture();
		Int( fixture.Lumps[14], 32 + 28, 1 ); // Model 1 alone owns render mesh 0.
		fixture.Lumps[3] = new byte[36]; fixture.Lumps[30] = new byte[12]; Float( fixture.Lumps[30], 8, 1 );
		fixture.Lumps[72] = new byte[36 * 3]; fixture.Lumps[79] = new byte[6];
		fixture.Lumps[80] = new byte[28]; Short( fixture.Lumps[80], 4, 1 );
		fixture.Lumps[82] = new byte[12]; Short( fixture.Lumps[82], 2, 7 );
		for ( var i = 0; i < 3; i++ )
		{
			Float( fixture.Lumps[3], i * 12, 12 + i );
			Int( fixture.Lumps[72], i * 36, i );
			Float( fixture.Lumps[72], i * 36 + 8, 0.25f ); Float( fixture.Lumps[72], i * 36 + 12, 0.75f );
			fixture.Lumps[72][i * 36 + 16] = 123;
			Float( fixture.Lumps[72], i * 36 + 20, 0.3f ); Float( fixture.Lumps[72], i * 36 + 24, 0.6f );
			Short( fixture.Lumps[79], i * 2, i );
		}
		fixture.Lumps[0] = Encoding.ASCII.GetBytes( "{\"classname\" \"func_brush\" \"model\" \"*1\" \"origin\" \"10000 20000 30000\"}" );
		Assert.IsTrue( Titanfall2BspReader.TryReadBrushModels( fixture.Bytes(), null, false, out var catalog, out var error ), error );
		var mesh = catalog.Models.Single().Geometry.Meshes.Single(); var vertex = mesh.Vertices[0];
		Assert.AreEqual( 12f, vertex.Position.X ); Assert.AreEqual( 1f, vertex.Normal.Z );
		Assert.AreEqual( 0.75f, vertex.TexCoord.Y ); Assert.AreEqual( 0.6f, vertex.LightmapTexCoord.Y );
		Assert.AreEqual( (byte)123, vertex.Color.R ); Assert.AreEqual( 7, mesh.LightmapPage );
		Assert.AreEqual( 0, Read( fixture ).SourcePrimitiveCount - 2 );
		Assert.IsTrue( fixture.Read( out var world, out error ), error ); Assert.AreEqual( 0, world.World.Meshes.Count );
	}

	[TestMethod]
	public void IncompleteInlineCollisionFailsInsteadOfReturningAHollowDoor()
	{
		var fixture = new Fixture(); fixture.Lumps[92][2 * 32 + 13] = 1;
		Assert.IsFalse( Titanfall2BspReader.TryReadBrushModels( fixture.Bytes(), null, false, out _, out var error ) );
		StringAssert.Contains( error, "Incomplete inline model 1 collision" );
	}

	[TestMethod]
	public void InlineCatalogSupportsSidecarsAndRejectsInvalidModelTables()
	{
		var fixture = new Fixture(); var bytes = fixture.Bytes();
		Assert.AreEqual( 1, Titanfall2BspReader.ReadBrushModelCountFromBsp( bytes ) );
		Assert.ThrowsException<System.IO.InvalidDataException>( () => Titanfall2BspReader.ReadBrushModelCount( new byte[33] ) );
		var sidecars = new Dictionary<int, byte[]> { [14] = new byte[32] };
		Assert.IsTrue( Titanfall2BspReader.TryReadBrushModels( bytes, sidecars, false, out var catalog, out var error ), error );
		Assert.AreEqual( 0, catalog.Models.Count );
	}

	[DataTestMethod]
	[DataRow( true )]
	[DataRow( false )]
	public void MissingInlineMeshOrCollisionTablesRejectImport( bool mesh )
	{
		var fixture = new Fixture();
		if ( mesh ) Int( fixture.Lumps[14], 32 + 28, 1 );
		else { fixture.Lumps[85] = []; fixture.Lumps[87] = []; }
		Assert.IsFalse( Titanfall2BspReader.TryReadBrushModels( fixture.Bytes(), null, false, out _, out _ ) );
	}
}
