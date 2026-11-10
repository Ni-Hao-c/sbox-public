using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Titanfall2;
using static Titanfall2.Titanfall2BspReader;

namespace SceneTests;

[TestClass]
public class Titanfall2CompiledSceneTests
{
	static readonly BindingFlags PrivateStatic = BindingFlags.NonPublic | BindingFlags.Static;

	static BspVisibility Visibility( int cells, params VisibilityPortal[] portals ) => new()
	{
		CellCount = cells,
		Planes = [new( System.Numerics.Vector3.UnitX, 0 )],
		Nodes = [new( -1, 0 )],
		Portals = portals,
		AlwaysVisibleCells = new VisibilityCellMask().Add( cells - 1 )
	};

	[DataTestMethod]
	[DataRow( 0 )]
	[DataRow( 1 )]
	[DataRow( 16 )]
	[DataRow( 128 )]
	public void CompiledPvsMatchesReferenceForEveryCell( int depth )
	{
		var portals = Enumerable.Range( 0, 126 ).Select( i => new VisibilityPortal( i, i + 1, default, default, false ) )
			.Concat( [new( 10, 2, default, default, false ), new( 2, 3, default, default, false ),
				new( -1, 5, default, default, false ), new( 0, 128, default, default, false )] ).ToArray();
		var visibility = Visibility( 128, portals );
		var compiled = Titanfall2CompiledVisibility.Compile( visibility, depth );
		for ( var cell = 0; cell < visibility.CellCount; ++cell )
			Assert.AreEqual( visibility.GetReachableCells( cell, depth ).Union( visibility.AlwaysVisibleCells ), compiled[cell], $"Cell {cell}, depth {depth}" );
		Assert.IsTrue( compiled[-1].IsEmpty );
		Assert.IsTrue( compiled[128].IsEmpty );
	}

	[TestMethod]
	public void CompiledPvsRoundTripsAndRejectsDifferentPolicy()
	{
		var visibility = Visibility( 3, new VisibilityPortal( 0, 1, default, default, false ) );
		var compiled = Titanfall2CompiledVisibility.Compile( visibility, 16 );
		using var stream = new MemoryStream();
		using var writer = new BinaryWriter( stream, System.Text.Encoding.UTF8, true );
		compiled.Write( writer );
		stream.Position = 0;
		using var reader = new BinaryReader( stream, System.Text.Encoding.UTF8, true );
		var restored = Titanfall2CompiledVisibility.Read( reader, visibility, 16 );
		for ( var i = 0; i < 3; ++i ) Assert.AreEqual( compiled[i], restored[i] );
		stream.Position = 0;
		Assert.ThrowsException<InvalidDataException>( () => Titanfall2CompiledVisibility.Read( reader, visibility, 2 ) );
	}

	[TestMethod]
	public void CompiledLayoutPreservesDenseIdsNegativeCellsAndSkins()
	{
		var instances = new[]
		{
			new Titanfall2CompiledPropLayout.Instance( new global::Vector3( -1, -513, 2 ), 0, 0 ),
			new Titanfall2CompiledPropLayout.Instance( new global::Vector3( 1, 0, 0 ), 1, 2 ),
			new Titanfall2CompiledPropLayout.Instance( new global::Vector3( -511, -600, 0 ), 0, 1 ),
			new Titanfall2CompiledPropLayout.Instance( new global::Vector3( 513, 0, 0 ), 1, 2 )
		};
		var layout = Titanfall2CompiledPropLayout.Compile( instances );
		Assert.AreEqual( 3, layout.Cells.Length );
		Assert.AreEqual( 3, layout.Models.Length );
		Assert.AreEqual( -1, layout.Cells[0].X );
		Assert.AreEqual( -2, layout.Cells[0].Y );
		CollectionAssert.AreEqual( new[] { 0, 2, 1, 3 }, layout.CellInstances );
		using var stream = new MemoryStream();
		using var writer = new BinaryWriter( stream, System.Text.Encoding.UTF8, true );
		layout.Write( writer );
		stream.Position = 0;
		using var reader = new BinaryReader( stream );
		var restored = Titanfall2CompiledPropLayout.Read( reader, instances );
		CollectionAssert.AreEqual( layout.Cells, restored.Cells );
		CollectionAssert.AreEqual( layout.Models, restored.Models );
		CollectionAssert.AreEqual( layout.ModelInstances, restored.ModelInstances );
	}

	[DataTestMethod]
	[DataRow( "duplicate" )]
	[DataRow( "wrong-cell" )]
	[DataRow( "wrong-model" )]
	[DataRow( "range" )]
	public void CompiledLayoutRejectsCorruptMembership( string corruption )
	{
		var instances = new[]
		{
			new Titanfall2CompiledPropLayout.Instance( new global::Vector3( 0 ), 0, 0 ),
			new Titanfall2CompiledPropLayout.Instance( new global::Vector3( 1 ), 0, 0 )
		};
		var layout = Titanfall2CompiledPropLayout.Compile( instances );
		if ( corruption == "duplicate" ) layout.CellInstances[1] = 0;
		if ( corruption == "wrong-cell" ) layout.Cells[0] = layout.Cells[0] with { X = 99 };
		if ( corruption == "wrong-model" ) layout.Models[0] = layout.Models[0] with { Skin = 1 };
		if ( corruption == "range" ) layout.Cells[0] = layout.Cells[0] with { Count = int.MaxValue };
		using var stream = new MemoryStream();
		using var writer = new BinaryWriter( stream, System.Text.Encoding.UTF8, true );
		layout.Write( writer );
		stream.Position = 0;
		using var reader = new BinaryReader( stream );
		Assert.ThrowsException<InvalidDataException>( () => Titanfall2CompiledPropLayout.Read( reader, instances ) );
	}

	[TestMethod]
	public void EmptyCompiledLayoutRoundTrips()
	{
		using var stream = new MemoryStream();
		using var writer = new BinaryWriter( stream, System.Text.Encoding.UTF8, true );
		Titanfall2CompiledPropLayout.Compile( [] ).Write( writer );
		stream.Position = 0;
		using var reader = new BinaryReader( stream );
		Assert.AreEqual( 0, Titanfall2CompiledPropLayout.Read( reader, [] ).InstanceCount );
	}

	[TestMethod]
	public void ScenePayloadChecksumRejectsOtherwiseValidEdits()
	{
		var source = new[] { new StaticPropInstance { SourceIndex = 97, ModelPath = "models/test.mdl" } };
		var type = typeof( Titanfall2StaticPropStreamer );
		var encoded = (string)type.GetMethod( "Encode", PrivateStatic ).Invoke( null, [source, null] );
		var payload = Convert.FromBase64String( encoded );
		// Change the model name without changing any count or membership invariant.
		var offset = Array.IndexOf( payload, (byte)'t', 12 );
		Assert.IsTrue( offset >= 0 );
		payload[offset] = (byte)'x';
		var error = Assert.ThrowsException<TargetInvocationException>( () => type.GetMethod( "ReadCompiledScene", PrivateStatic )
			.Invoke( null, [Convert.ToBase64String( payload )] ) );
		Assert.IsInstanceOfType<InvalidDataException>( error.InnerException );
	}

	[TestMethod]
	public void LegacyVisibilityIsCompiledOnceAtLoadAndV2HasIntegrity()
	{
		var visibility = Visibility( 3, new VisibilityPortal( 0, 1, default, default, false ) );
		var type = typeof( Titanfall2StaticPropStreamer );
		var encoded = (string)type.GetMethod( "EncodeVisibility", PrivateStatic ).Invoke( null, [visibility] );
		var payload = Convert.FromBase64String( encoded );
		var restored = (BspVisibility)type.GetMethod( "DecodeVisibility", PrivateStatic ).Invoke( null, [encoded] );
		Assert.AreEqual( 3, restored.CellCount );
		Array.Resize( ref payload, payload.Length - 32 - 8 - 16 * 3 );
		BitConverter.GetBytes( 1 ).CopyTo( payload, 4 );
		restored = (BspVisibility)type.GetMethod( "DecodeVisibility", PrivateStatic ).Invoke( null, [Convert.ToBase64String( payload )] );
		Assert.AreEqual( visibility.GetReachableCells( 0, 16 ), restored.GetReachableCells( 0, 16 ) );
	}
}
