using System;
using System.Buffers.Binary;
using NumericsVector3 = System.Numerics.Vector3;

namespace Titanfall2;

/// <summary>
/// Position lookup over the retail LIGHTPROBE_TREE (0x67) and LIGHTPROBE_REFERENCES (0x68)
/// lumps. Static props carry a baked probe index; entities such as prop_dynamic are lit by
/// the probe nearest their lighting origin inside the tree leaf that contains it.
/// </summary>
public sealed class Titanfall2LightProbeLookup
{
	const int NodeBytes = 8;
	const int ReferenceBytes = 20;
	const int MaxDepth = 128;

	readonly byte[] _probes;
	readonly byte[] _tree;
	readonly byte[] _references;
	readonly int _probeCount;
	readonly int _nodeCount;
	readonly int _referenceCount;

	Titanfall2LightProbeLookup( byte[] probes, byte[] tree, byte[] references )
	{
		_probes = probes;
		_tree = tree;
		_references = references;
		_probeCount = probes.Length / Titanfall2LightProbe.RecordBytes;
		_nodeCount = tree.Length / NodeBytes;
		_referenceCount = references.Length / ReferenceBytes;
	}

	public int ReferenceCount => _referenceCount;

	public static Titanfall2LightProbeLookup TryCreate( byte[] probes, byte[] tree, byte[] references )
	{
		if ( probes is null || tree is null || references is null ) return null;
		if ( probes.Length < Titanfall2LightProbe.RecordBytes || tree.Length < NodeBytes || references.Length < ReferenceBytes ) return null;
		return new Titanfall2LightProbeLookup( probes, tree, references );
	}

	/// <summary>
	/// Node layout: <c>uint tag; uint value</c>. <c>tag &amp; 3</c> is the split axis, or 3 for a
	/// leaf. Interior nodes keep their children at <c>tag &gt;&gt; 2</c> (below the split) and the
	/// next index (at or above it), with <c>value</c> as the float split. Leaves start at
	/// reference <c>tag &gt;&gt; 2</c> and contain <c>value</c> records. Retail TF2 leaves contain
	/// at most four references; community builders can use larger leaves. Checking that
	/// reference origins belong to a leaf alone cannot detect a swapped first/count:
	/// the resulting oversized ranges overlap and can still contain those origins.
	/// </summary>
	public Titanfall2LightProbe Find( NumericsVector3 position )
	{
		var node = 0;
		for ( var depth = 0; depth < MaxDepth && node >= 0 && node < _nodeCount; depth++ )
		{
			var span = _tree.AsSpan( node * NodeBytes, NodeBytes );
			var tag = BinaryPrimitives.ReadUInt32LittleEndian( span );
			var axis = (int)(tag & 3);
			var value = BinaryPrimitives.ReadUInt32LittleEndian( span[4..] );
			if ( axis == 3 ) return FindNearest( position, tag >> 2, value );
			var split = BitConverter.UInt32BitsToSingle( value );
			var coordinate = axis == 0 ? position.X : axis == 1 ? position.Y : position.Z;
			var child = (long)(tag >> 2) + (coordinate < split ? 0 : 1);
			if ( child >= _nodeCount ) return null;
			node = (int)child;
		}
		return null;
	}

	Titanfall2LightProbe FindNearest( NumericsVector3 position, uint first, uint count )
	{
		if ( first >= _referenceCount || count == 0 ) return null;
		var end = Math.Min( (long)first + count, _referenceCount );
		var best = -1;
		var bestDistance = float.MaxValue;
		for ( var index = (long)first; index < end; index++ )
		{
			var span = _references.AsSpan( (int)index * ReferenceBytes, ReferenceBytes );
			var origin = new NumericsVector3(
				BinaryPrimitives.ReadSingleLittleEndian( span ),
				BinaryPrimitives.ReadSingleLittleEndian( span[4..] ),
				BinaryPrimitives.ReadSingleLittleEndian( span[8..] ) );
			var probe = BinaryPrimitives.ReadUInt32LittleEndian( span[12..] );
			if ( probe >= _probeCount ) continue;
			var distance = NumericsVector3.DistanceSquared( origin, position );
			if ( distance >= bestDistance ) continue;
			bestDistance = distance;
			best = (int)probe;
		}
		if ( best < 0 ) return null;
		return Titanfall2LightProbe.TryRead( _probes.AsSpan( best * Titanfall2LightProbe.RecordBytes, Titanfall2LightProbe.RecordBytes ), out var result )
			? result : null;
	}
}
