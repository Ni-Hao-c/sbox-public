using System;
using System.Buffers.Binary;
using Titanfall2;
using NumericsVector3 = System.Numerics.Vector3;

namespace SceneTests;

[TestClass]
public class Titanfall2LightProbeLookupTests
{
	static byte[] Probes( params short[] redConstants )
	{
		var bytes = new byte[redConstants.Length * Titanfall2LightProbe.RecordBytes];
		for ( var i = 0; i < redConstants.Length; i++ )
			BinaryPrimitives.WriteInt16LittleEndian( bytes.AsSpan( i * Titanfall2LightProbe.RecordBytes + 6 ), redConstants[i] );
		return bytes;
	}

	static void Node( byte[] tree, int index, uint tag, uint value )
	{
		BinaryPrimitives.WriteUInt32LittleEndian( tree.AsSpan( index * 8 ), tag );
		BinaryPrimitives.WriteUInt32LittleEndian( tree.AsSpan( index * 8 + 4 ), value );
	}

	static void Reference( byte[] references, int index, NumericsVector3 origin, uint probe )
	{
		var span = references.AsSpan( index * 20 );
		BinaryPrimitives.WriteSingleLittleEndian( span, origin.X );
		BinaryPrimitives.WriteSingleLittleEndian( span[4..], origin.Y );
		BinaryPrimitives.WriteSingleLittleEndian( span[8..], origin.Z );
		BinaryPrimitives.WriteUInt32LittleEndian( span[12..], probe );
	}

	[TestMethod]
	public void FindDescendsSplitAndPicksNearestReferenceInLeaf()
	{
		// Root splits X at 0: children 1 (x < 0) and 2 (x >= 0).
		var tree = new byte[3 * 8];
		Node( tree, 0, (1u << 2) | 0, BitConverter.SingleToUInt32Bits( 0f ) );
		Node( tree, 1, 3, 1 ); // one reference starting at 0
		Node( tree, 2, (1u << 2) | 3, 2 ); // two references starting at 1
		var references = new byte[3 * 20];
		Reference( references, 0, new NumericsVector3( -100, 0, 0 ), 0 );
		Reference( references, 1, new NumericsVector3( 100, 0, 0 ), 1 );
		Reference( references, 2, new NumericsVector3( 400, 0, 0 ), 2 );
		var lookup = Titanfall2LightProbeLookup.TryCreate( Probes( 8192, 4096, 2048 ), tree, references );

		Assert.IsNotNull( lookup );
		Assert.AreEqual( 1f, lookup.Find( new NumericsVector3( -1, 0, 0 ) ).Red.W );
		Assert.AreEqual( 0.5f, lookup.Find( new NumericsVector3( 150, 0, 0 ) ).Red.W );
		Assert.AreEqual( 0.25f, lookup.Find( new NumericsVector3( 350, 0, 0 ) ).Red.W );
	}

	[TestMethod]
	public void FindRejectsEmptyLeavesAndOutOfRangeProbes()
	{
		var tree = new byte[8];
		Node( tree, 0, 3, 1 );
		var references = new byte[20];
		Reference( references, 0, NumericsVector3.Zero, 5 );
		var lookup = Titanfall2LightProbeLookup.TryCreate( Probes( 8192 ), tree, references );

		Assert.IsNull( lookup.Find( NumericsVector3.Zero ) );
		Assert.IsNull( Titanfall2LightProbeLookup.TryCreate( Probes( 8192 ), Array.Empty<byte>(), references ) );
	}

	[TestMethod]
	public void FirstReferenceAndCountAreNotInterchangeable()
	{
		var tree = new byte[8];
		Node( tree, 0, (2u << 2) | 3, 1 );
		var references = new byte[3 * 20];
		Reference( references, 0, NumericsVector3.Zero, 0 );
		Reference( references, 1, NumericsVector3.Zero, 1 );
		Reference( references, 2, new NumericsVector3( 100, 0, 0 ), 2 );
		var lookup = Titanfall2LightProbeLookup.TryCreate( Probes( 8192, 4096, 2048 ), tree, references );
		Assert.AreEqual( 0.25f, lookup.Find( NumericsVector3.Zero ).Red.W );
	}

	[TestMethod]
	public void EmptyLeafDoesNotScanAdjacentReferences()
	{
		var tree = new byte[8];
		Node( tree, 0, (1u << 2) | 3, 0 );
		var references = new byte[20];
		Reference( references, 0, NumericsVector3.Zero, 0 );
		var lookup = Titanfall2LightProbeLookup.TryCreate( Probes( 8192 ), tree, references );
		Assert.IsNull( lookup.Find( NumericsVector3.Zero ) );
	}
}
