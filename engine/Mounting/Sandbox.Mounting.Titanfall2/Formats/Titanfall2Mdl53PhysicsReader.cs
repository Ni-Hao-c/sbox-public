using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using NumericsVector3 = System.Numerics.Vector3;

namespace Titanfall2.Formats;

/// <summary>Reads embedded legacy IVPS/VPHY convex collision from an MDL53 file.</summary>
public static class Titanfall2Mdl53PhysicsReader
{
	const int VphysicsId = 0x59485056; // 'VPHY'
	const short IvpsVersion = 0x0100;
	const short PolyModelType = 0;
	const float InchesPerMeter = 39.37007874015748f;

	public sealed class PhysicsHull
	{
		public int SolidIndex { get; init; }
		public int BoneIndex { get; init; } = -1;
		public NumericsVector3[] Points { get; init; } = Array.Empty<NumericsVector3>();
		public int[] Indices { get; init; } = Array.Empty<int>();
	}

	public sealed class PhysicsSolid
	{
		public int Index { get; init; } = -1;
		public string Name { get; init; } = string.Empty;
		public string Parent { get; init; } = string.Empty;
		public float Mass { get; init; }
	}

	public sealed class RagdollConstraint
	{
		public int ParentIndex { get; init; } = -1;
		public int ChildIndex { get; init; } = -1;
		public float XMin { get; init; }
		public float XMax { get; init; }
		public float YMin { get; init; }
		public float YMax { get; init; }
		public float ZMin { get; init; }
		public float ZMax { get; init; }
	}

	public sealed class PhysicsData
	{
		public PhysicsHull[] Hulls { get; init; } = Array.Empty<PhysicsHull>();
		public PhysicsSolid[] Solids { get; init; } = Array.Empty<PhysicsSolid>();
		public RagdollConstraint[] Constraints { get; init; } = Array.Empty<RagdollConstraint>();
	}

	public static PhysicsHull[] Parse( byte[] data, Mdl53StudioHeader modelHeader, int boneCount ) =>
		ParseData( data.AsSpan(), modelHeader, boneCount ).Hulls;

	public static PhysicsHull[] Parse( ReadOnlySpan<byte> data, Mdl53StudioHeader modelHeader, int boneCount )
		=> ParseData( data, modelHeader, boneCount ).Hulls;

	public static PhysicsData ParseData( byte[] data, Mdl53StudioHeader modelHeader, int boneCount ) =>
		ParseData( data.AsSpan(), modelHeader, boneCount );

	public static PhysicsData ParseData( ReadOnlySpan<byte> data, Mdl53StudioHeader modelHeader, int boneCount )
	{
		if ( modelHeader.PhysicsOffset <= 0 || modelHeader.PhysicsSize < 16 )
			return new PhysicsData();

		var physicsOffset = modelHeader.PhysicsOffset;
		var physicsEnd = checked(physicsOffset + modelHeader.PhysicsSize);
		EnsureRange( data, physicsOffset, modelHeader.PhysicsSize );
		var headerSize = ReadInt32( data, physicsOffset );
		var physicsType = ReadInt32( data, physicsOffset + 4 );
		var solidCount = ReadInt32( data, physicsOffset + 8 );
		if ( headerSize < 16 || headerSize > modelHeader.PhysicsSize || physicsType != 0 || solidCount <= 0 || solidCount > 4096 )
			return new PhysicsData();

		var hulls = new List<PhysicsHull>();
		var surfaceOffset = checked(physicsOffset + headerSize);
		for ( var solidIndex = 0; solidIndex < solidCount; ++solidIndex )
		{
			EnsureRangeWithin( data, surfaceOffset, 32, physicsEnd );
			var surfaceByteCount = ReadInt32( data, surfaceOffset );
			var nextSurfaceOffset = checked(surfaceOffset + surfaceByteCount + sizeof(int));
			if ( surfaceByteCount < 76 || nextSurfaceOffset > physicsEnd )
				throw new InvalidDataException( "Embedded VPHY surface has an invalid size." );

			var id = ReadInt32( data, surfaceOffset + 4 );
			var version = ReadInt16( data, surfaceOffset + 8 );
			var modelType = ReadInt16( data, surfaceOffset + 10 );
			if ( id == VphysicsId && version == IvpsVersion && modelType == PolyModelType )
				ParsePolySurface( data, surfaceOffset, nextSurfaceOffset, solidIndex, boneCount, hulls );

			surfaceOffset = nextSurfaceOffset;
		}

		var solids = new List<PhysicsSolid>();
		var constraints = new List<RagdollConstraint>();
		ParseKeyData( data.Slice( surfaceOffset, physicsEnd - surfaceOffset ), solids, constraints );
		return new PhysicsData
		{
			Hulls = hulls.ToArray(),
			Solids = solids.ToArray(),
			Constraints = constraints.ToArray()
		};
	}

	static void ParseKeyData(
		ReadOnlySpan<byte> data,
		List<PhysicsSolid> solids,
		List<RagdollConstraint> constraints )
	{
		var terminator = data.IndexOf( (byte)0 );
		if ( terminator >= 0 ) data = data[..terminator];
		if ( data.IsEmpty ) return;

		var text = Encoding.ASCII.GetString( data );
		var position = 0;
		while ( TryReadToken( text, ref position, out var blockName ) )
		{
			if ( !TryReadToken( text, ref position, out var openingBrace ) || openingBrace != "{" )
				continue;

			var values = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase );
			while ( TryReadToken( text, ref position, out var key ) && key != "}" )
			{
				if ( !TryReadToken( text, ref position, out var value ) || value == "}" ) break;
				values[key] = value;
			}

			if ( blockName.Equals( "solid", StringComparison.OrdinalIgnoreCase ) )
			{
				solids.Add( new PhysicsSolid
				{
					Index = ReadInt( values, "index", -1 ),
					Name = ReadString( values, "name" ),
					Parent = ReadString( values, "parent" ),
					Mass = ReadFloat( values, "mass" )
				} );
			}
			else if ( blockName.Equals( "ragdollconstraint", StringComparison.OrdinalIgnoreCase ) )
			{
				constraints.Add( new RagdollConstraint
				{
					ParentIndex = ReadInt( values, "parent", -1 ),
					ChildIndex = ReadInt( values, "child", -1 ),
					XMin = ReadFloat( values, "xmin" ),
					XMax = ReadFloat( values, "xmax" ),
					YMin = ReadFloat( values, "ymin" ),
					YMax = ReadFloat( values, "ymax" ),
					ZMin = ReadFloat( values, "zmin" ),
					ZMax = ReadFloat( values, "zmax" )
				} );
			}
		}
	}

	static bool TryReadToken( string text, ref int position, out string token )
	{
		token = string.Empty;
		while ( position < text.Length )
		{
			if ( char.IsWhiteSpace( text[position] ) )
			{
				position++;
				continue;
			}
			if ( text[position] == '/' && position + 1 < text.Length && text[position + 1] == '/' )
			{
				position += 2;
				while ( position < text.Length && text[position] != '\n' ) position++;
				continue;
			}
			break;
		}

		if ( position >= text.Length ) return false;
		var first = text[position++];
		if ( first is '{' or '}' )
		{
			token = first.ToString();
			return true;
		}

		if ( first == '"' )
		{
			var builder = new StringBuilder();
			while ( position < text.Length )
			{
				var value = text[position++];
				if ( value == '"' ) break;
				if ( value == '\\' && position < text.Length ) value = text[position++];
				builder.Append( value );
			}
			token = builder.ToString();
			return true;
		}

		var start = position - 1;
		while ( position < text.Length && !char.IsWhiteSpace( text[position] ) && text[position] is not ('{' or '}') )
			position++;
		token = text[start..position];
		return token.Length > 0;
	}

	static string ReadString( IReadOnlyDictionary<string, string> values, string key )
		=> values.TryGetValue( key, out var value ) ? value : string.Empty;

	static int ReadInt( IReadOnlyDictionary<string, string> values, string key, int fallback = 0 )
		=> values.TryGetValue( key, out var value )
			&& int.TryParse( value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed )
			? parsed
			: fallback;

	static float ReadFloat( IReadOnlyDictionary<string, string> values, string key, float fallback = 0.0f )
		=> values.TryGetValue( key, out var value )
			&& float.TryParse( value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed )
			? parsed
			: fallback;

	static void ParsePolySurface(
		ReadOnlySpan<byte> data,
		int surfaceOffset,
		int surfaceEnd,
		int solidIndex,
		int boneCount,
		List<PhysicsHull> hulls )
	{
		const int compactSurfaceHeaderSize = 32;
		const int legacySurfaceHeaderSize = 48;
		const int compactLedgeNodeSize = 28;
		const int compactLedgeHeaderSize = 16;
		const int compactTriangleSize = 16;
		const int compactPointSize = 16;

		var legacyOffset = checked(surfaceOffset + compactSurfaceHeaderSize);
		EnsureRangeWithin( data, legacyOffset, legacySurfaceHeaderSize, surfaceEnd );
		var packedByteSize = ReadUInt32( data, legacyOffset + 28 );
		var legacyByteSize = (int)(packedByteSize >> 8);
		var rootNodeOffset = ReadInt32( data, legacyOffset + 32 );
		if ( legacyByteSize < legacySurfaceHeaderSize || rootNodeOffset < legacySurfaceHeaderSize ) return;

		var legacyEnd = Math.Min( checked(legacyOffset + legacyByteSize), surfaceEnd );
		var rootNode = checked(legacyOffset + rootNodeOffset);
		if ( rootNode < legacyOffset + legacySurfaceHeaderSize
			|| rootNode > legacyEnd - compactLedgeNodeSize ) return;

		// Ledges are not stored as a flat contiguous array. Their point arrays are
		// shared and interleaved with ledge data, while the compact tree at the end
		// of the surface owns the authoritative leaf references. Walking by
		// ledgeByteSize therefore only imported the first convex piece of compound
		// props (for example, Angel City's lion statue only retained its base).
		var pendingNodes = new Stack<int>();
		var visitedNodes = new HashSet<int>();
		var visitedLedges = new HashSet<int>();
		pendingNodes.Push( rootNode );
		while ( pendingNodes.Count > 0 && visitedNodes.Count < 65536 )
		{
			var nodeOffset = pendingNodes.Pop();
			if ( !visitedNodes.Add( nodeOffset ) ) continue;
			EnsureRangeWithin( data, nodeOffset, compactLedgeNodeSize, legacyEnd );

			var rightNodeOffset = ReadInt32( data, nodeOffset );
			var compactLedgeOffset = ReadInt32( data, nodeOffset + 4 );
			if ( rightNodeOffset != 0 )
			{
				var leftNode = checked(nodeOffset + compactLedgeNodeSize);
				var rightNode = checked(nodeOffset + rightNodeOffset);
				if ( leftNode < rootNode || leftNode > legacyEnd - compactLedgeNodeSize
					|| rightNode < rootNode || rightNode > legacyEnd - compactLedgeNodeSize )
					throw new InvalidDataException( "Embedded VPHY compact ledge tree has an invalid child offset." );
				pendingNodes.Push( rightNode );
				pendingNodes.Push( leftNode );
				continue;
			}

			if ( compactLedgeOffset == 0 ) continue;
			var ledgeOffset = checked(nodeOffset + compactLedgeOffset);
			if ( !visitedLedges.Add( ledgeOffset ) ) continue;
			ParseCompactLedge(
				data, ledgeOffset, legacyOffset, legacyEnd, solidIndex, boneCount,
				compactLedgeHeaderSize, compactTriangleSize, compactPointSize, hulls );
		}
	}

	static void ParseCompactLedge(
		ReadOnlySpan<byte> data,
		int ledgeOffset,
		int legacyOffset,
		int legacyEnd,
		int solidIndex,
		int boneCount,
		int compactLedgeHeaderSize,
		int compactTriangleSize,
		int compactPointSize,
		List<PhysicsHull> hulls )
	{
		EnsureRangeWithin( data, ledgeOffset, compactLedgeHeaderSize, legacyEnd );
		if ( ledgeOffset < legacyOffset ) throw new InvalidDataException( "Embedded VPHY ledge precedes its surface." );

		var pointOffset = ReadInt32( data, ledgeOffset );
		var clientData = ReadInt32( data, ledgeOffset + 4 );
		var flags = ReadUInt32( data, ledgeOffset + 8 );
		var ledgeByteSize = checked((int)(flags >> 8) * 16);
		var triangleCount = ReadInt16( data, ledgeOffset + 12 );
		var hasChildren = (flags & 0x3) != 0;
		var compact = ((flags >> 2) & 0x3) != 0;
		if ( hasChildren || !compact || triangleCount <= 0 || triangleCount > 8192 ) return;
		if ( ledgeByteSize < compactLedgeHeaderSize || ledgeOffset > legacyEnd - ledgeByteSize )
			throw new InvalidDataException( "Embedded VPHY compact ledge has an invalid size." );

		var triangleBytes = checked(triangleCount * compactTriangleSize);
		if ( compactLedgeHeaderSize + triangleBytes > ledgeByteSize )
			throw new InvalidDataException( "Embedded VPHY compact ledge has truncated triangles." );

		var pointIndices = new Dictionary<ushort, int>();
		var points = new List<NumericsVector3>();
		var indices = new List<int>( triangleCount * 3 );
		for ( var triangleIndex = 0; triangleIndex < triangleCount; ++triangleIndex )
		{
			var triangleOffset = checked(ledgeOffset + compactLedgeHeaderSize + triangleIndex * compactTriangleSize);
			for ( var edgeIndex = 0; edgeIndex < 3; ++edgeIndex )
			{
				var sourcePointIndex = ReadUInt16( data, triangleOffset + 4 + edgeIndex * sizeof(uint) );
				if ( !pointIndices.TryGetValue( sourcePointIndex, out var targetPointIndex ) )
				{
					var sourcePointOffset = checked(ledgeOffset + pointOffset + sourcePointIndex * compactPointSize);
					EnsureRangeWithin( data, sourcePointOffset, 12, legacyEnd );
					if ( sourcePointOffset < legacyOffset )
						throw new InvalidDataException( "Embedded VPHY compact point precedes its surface." );
					targetPointIndex = points.Count;
					pointIndices.Add( sourcePointIndex, targetPointIndex );
					points.Add( ConvertIvpsPosition( ReadVector3( data, sourcePointOffset ) ) );
				}
				indices.Add( targetPointIndex );
			}
		}

		if ( points.Count < 4 || indices.Count < 12 ) return;
		var boneIndex = clientData > 0 && clientData <= boneCount ? clientData - 1 : -1;
		hulls.Add( new PhysicsHull
		{
			SolidIndex = solidIndex,
			BoneIndex = boneIndex,
			Points = points.ToArray(),
			Indices = indices.ToArray()
		} );
	}

	static NumericsVector3 ConvertIvpsPosition( NumericsVector3 position )
		=> new( position.X * InchesPerMeter, position.Z * InchesPerMeter, -position.Y * InchesPerMeter );

	static NumericsVector3 ReadVector3( ReadOnlySpan<byte> data, int offset )
		=> new( ReadSingle( data, offset ), ReadSingle( data, offset + 4 ), ReadSingle( data, offset + 8 ) );

	static short ReadInt16( ReadOnlySpan<byte> data, int offset )
	{
		EnsureRange( data, offset, sizeof(short) );
		return BinaryPrimitives.ReadInt16LittleEndian( data.Slice( offset, sizeof(short) ) );
	}

	static ushort ReadUInt16( ReadOnlySpan<byte> data, int offset )
	{
		EnsureRange( data, offset, sizeof(ushort) );
		return BinaryPrimitives.ReadUInt16LittleEndian( data.Slice( offset, sizeof(ushort) ) );
	}

	static int ReadInt32( ReadOnlySpan<byte> data, int offset )
	{
		EnsureRange( data, offset, sizeof(int) );
		return BinaryPrimitives.ReadInt32LittleEndian( data.Slice( offset, sizeof(int) ) );
	}

	static uint ReadUInt32( ReadOnlySpan<byte> data, int offset )
	{
		EnsureRange( data, offset, sizeof(uint) );
		return BinaryPrimitives.ReadUInt32LittleEndian( data.Slice( offset, sizeof(uint) ) );
	}

	static float ReadSingle( ReadOnlySpan<byte> data, int offset )
		=> BitConverter.Int32BitsToSingle( ReadInt32( data, offset ) );

	static void EnsureRangeWithin( ReadOnlySpan<byte> data, int offset, int length, int end )
	{
		EnsureRange( data, offset, length );
		if ( offset > end - length ) throw new InvalidDataException( "Embedded VPHY range crosses its containing surface." );
	}

	static void EnsureRange( ReadOnlySpan<byte> data, int offset, int length )
	{
		if ( offset < 0 || length < 0 || offset > data.Length - length )
			throw new InvalidDataException( $"Embedded VPHY range is outside the MDL. Offset={offset}, Length={length}, File={data.Length}" );
	}
}
