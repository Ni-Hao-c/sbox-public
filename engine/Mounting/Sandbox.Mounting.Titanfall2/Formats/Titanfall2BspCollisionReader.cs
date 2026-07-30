using System.Buffers.Binary;
using NumericsVector3 = System.Numerics.Vector3;

namespace Titanfall2;

public static partial class Titanfall2BspReader
{
	const int PlanesLumpId = 0x0001;
	const int TricollTrianglesLumpId = 0x0042;
	const int TricollHeadersLumpId = 0x0045;
	const int CollisionGridLumpId = 0x0055;
	const int CollisionGeoSetsLumpId = 0x0057;
	const int CollisionPrimitivesLumpId = 0x0059;
	const int CollisionUniqueContentsLumpId = 0x005B;
	const int CollisionBrushesLumpId = 0x005C;
	const int CollisionBrushSidePlaneOffsetsLumpId = 0x005D;
	const byte BrushPrimitiveType = 0x00;
	const byte TricollPrimitiveType = 0x40;
	const uint ContentsSolid = 0x00000001;
	const uint ContentsWindow = 0x00000002;
	const uint ContentsGrate = 0x00000008;
	const uint ContentsSlime = 0x00000010;
	const uint ContentsWater = 0x00000020;
	const uint ContentsMoveable = 0x00004000;
	const uint ContentsPlayerClip = 0x00010000;
	const uint ContentsMonster = 0x02000000;
	const uint PlayerSolidMask = ContentsMonster | ContentsPlayerClip | ContentsMoveable
		| ContentsGrate | ContentsWindow | ContentsSolid;

	public static IReadOnlyList<int> RequiredCollisionLumpIds { get; } =
	[
		PlanesLumpId,
		VerticesLumpId,
		TricollTrianglesLumpId,
		TricollHeadersLumpId,
		CollisionGridLumpId,
		CollisionGeoSetsLumpId,
		CollisionPrimitivesLumpId,
		CollisionUniqueContentsLumpId,
		CollisionBrushesLumpId,
		CollisionBrushSidePlaneOffsetsLumpId
	];

	public sealed class BspWorldCollision
	{
		public NumericsVector3[] Vertices { get; init; } = Array.Empty<NumericsVector3>();
		public int[] Indices { get; init; } = Array.Empty<int>();
		public int BrushCount { get; init; }
		public int TricollCount { get; init; }
		public int BrushTriangleCount { get; init; }
		public int TricollTriangleCount { get; init; }
		public int SourcePrimitiveCount { get; init; }
		public int NonBlockingPrimitiveCount { get; init; }
		public int WaterPrimitiveCount { get; init; }
		public int BlockingWaterPrimitiveCount { get; init; }
		public int PrimitiveCount => BrushCount + TricollCount;
		public int TriangleCount => Indices.Length / 3;
		public bool IsEmpty => Vertices.Length == 0 || Indices.Length < 3;
	}

	readonly record struct CollisionPrimitive( byte Type, int Index, uint Contents, bool HasKnownContents );
	readonly record struct CollisionPlane( NumericsVector3 Normal, float Distance );

	static BspWorldCollision ParseWorldCollision( Stream stream, IReadOnlyList<LumpHeader> headers,
		IReadOnlyDictionary<int, byte[]> overrideLumps )
	{
		var planes = ReadLumpBytes( stream, headers, overrideLumps, PlanesLumpId );
		var vertices = ReadLumpBytes( stream, headers, overrideLumps, VerticesLumpId );
		var tricollTriangles = ReadLumpBytes( stream, headers, overrideLumps, TricollTrianglesLumpId );
		var tricollHeaders = ReadLumpBytes( stream, headers, overrideLumps, TricollHeadersLumpId );
		var collisionGrid = ReadLumpBytes( stream, headers, overrideLumps, CollisionGridLumpId );
		var geoSets = ReadLumpBytes( stream, headers, overrideLumps, CollisionGeoSetsLumpId );
		var primitives = ReadLumpBytes( stream, headers, overrideLumps, CollisionPrimitivesLumpId );
		var uniqueContents = ReadLumpBytes( stream, headers, overrideLumps, CollisionUniqueContentsLumpId );
		var brushes = ReadLumpBytes( stream, headers, overrideLumps, CollisionBrushesLumpId );
		var brushPlaneOffsets = ReadLumpBytes( stream, headers, overrideLumps, CollisionBrushSidePlaneOffsetsLumpId );
		if ( geoSets.Length < 8 ) return new BspWorldCollision();

		var outputVertices = new List<NumericsVector3>();
		var outputIndices = new List<int>();
		var brushCount = 0;
		var tricollCount = 0;
		var brushTriangleCount = 0;
		var tricollTriangleCount = 0;
		var sourcePrimitiveCount = 0;
		var nonBlockingPrimitiveCount = 0;
		var waterPrimitiveCount = 0;
		var blockingWaterPrimitiveCount = 0;
		var firstBrushPlane = collisionGrid.Length >= 0x1C ? ReadInt32( collisionGrid, 0x18 ) : 0;

		foreach ( var primitive in EnumerateWorldPrimitives( geoSets, primitives, uniqueContents ) )
		{
			sourcePrimitiveCount++;
			var isWater = primitive.HasKnownContents && IsWaterContents( primitive.Contents );
			if ( isWater ) waterPrimitiveCount++;
			if ( primitive.HasKnownContents && !BlocksPlayer( primitive.Contents ) )
			{
				nonBlockingPrimitiveCount++;
				continue;
			}
			if ( isWater ) blockingWaterPrimitiveCount++;

			if ( primitive.Type == BrushPrimitiveType )
			{
				var firstIndex = outputIndices.Count;
				if ( AppendBrush( primitive.Index, planes, brushes, brushPlaneOffsets, firstBrushPlane,
					outputVertices, outputIndices ) )
				{
					brushCount++;
					brushTriangleCount += (outputIndices.Count - firstIndex) / 3;
				}
			}
			else if ( primitive.Type == TricollPrimitiveType )
			{
				var firstIndex = outputIndices.Count;
				if ( AppendTricoll( primitive.Index, vertices, tricollHeaders, tricollTriangles,
					outputVertices, outputIndices ) )
				{
					tricollCount++;
					tricollTriangleCount += (outputIndices.Count - firstIndex) / 3;
				}
			}
		}

		return new BspWorldCollision
		{
			Vertices = outputVertices.ToArray(),
			Indices = outputIndices.ToArray(),
			BrushCount = brushCount,
			TricollCount = tricollCount,
			BrushTriangleCount = brushTriangleCount,
			TricollTriangleCount = tricollTriangleCount,
			SourcePrimitiveCount = sourcePrimitiveCount,
			NonBlockingPrimitiveCount = nonBlockingPrimitiveCount,
			WaterPrimitiveCount = waterPrimitiveCount,
			BlockingWaterPrimitiveCount = blockingWaterPrimitiveCount
		};
	}

	static IEnumerable<CollisionPrimitive> EnumerateWorldPrimitives(
		byte[] geoSets,
		byte[] primitives,
		byte[] uniqueContents )
	{
		var seen = new HashSet<int>();
		var primitiveCount = primitives.Length / 4;
		for ( var offset = 0; offset + 8 <= geoSets.Length; offset += 8 )
		{
			var count = ReadUInt16( geoSets, offset + 2 );
			var raw = ReadUInt32( geoSets, offset + 4 );
			if ( count == 1 )
			{
				if ( TryDecodeWorldPrimitive( raw, seen, uniqueContents, out var primitive ) ) yield return primitive;
				continue;
			}

			if ( count == 0 ) continue;
			var first = (int)((raw >> 8) & 0xFFFF);
			var end = Math.Min( (long)first + count, primitiveCount );
			for ( var primitiveIndex = first; primitiveIndex < end; primitiveIndex++ )
			{
				var childRaw = ReadUInt32( primitives, primitiveIndex * 4 );
				if ( TryDecodeWorldPrimitive( childRaw, seen, uniqueContents, out var primitive ) ) yield return primitive;
			}
		}
	}

	static bool TryDecodeWorldPrimitive(
		uint raw,
		HashSet<int> seen,
		byte[] uniqueContents,
		out CollisionPrimitive primitive )
	{
		var type = (byte)(raw >> 24);
		var index = (int)((raw >> 8) & 0xFFFF);
		var contentsIndex = (int)(raw & 0xFF);
		var hasKnownContents = contentsIndex >= 0 && contentsIndex < uniqueContents.Length / sizeof(uint);
		var contents = hasKnownContents ? ReadUInt32( uniqueContents, contentsIndex * sizeof(uint) ) : 0u;
		primitive = new CollisionPrimitive( type, index, contents, hasKnownContents );
		if ( type is not (BrushPrimitiveType or TricollPrimitiveType) ) return false;
		return seen.Add( (type << 16) | index );
	}

	static bool BlocksPlayer( uint contents ) => (contents & PlayerSolidMask) != 0;
	static bool IsWaterContents( uint contents ) => (contents & (ContentsWater | ContentsSlime)) != 0;

	static bool AppendTricoll( int index, byte[] vertices, byte[] headers, byte[] triangles,
		List<NumericsVector3> outputVertices, List<int> outputIndices )
	{
		const int headerStride = 0x2C;
		if ( index < 0 || index >= headers.Length / headerStride ) return false;
		var headerOffset = index * headerStride;
		var vertexCount = ReadInt16( headers, headerOffset + 6 );
		var triangleCount = ReadUInt16( headers, headerOffset + 8 );
		var firstVertex = ReadInt32( headers, headerOffset + 0x0C );
		var firstTriangle = ReadUInt32( headers, headerOffset + 0x10 );
		if ( vertexCount <= 0 || triangleCount == 0 || firstVertex < 0 ) return false;
		if ( (long)(firstVertex + vertexCount) * 12 > vertices.Length ) return false;

		var baseVertex = outputVertices.Count;
		for ( var localVertex = 0; localVertex < vertexCount; localVertex++ )
			outputVertices.Add( ReadVector3( vertices, (firstVertex + localVertex) * 12 ) );

		var firstOutputIndex = outputIndices.Count;
		for ( var triangle = 0; triangle < triangleCount; triangle++ )
		{
			var triangleOffset = ((long)firstTriangle + triangle) * 4;
			if ( triangleOffset < 0 || triangleOffset + 4 > triangles.Length ) continue;
			var raw = ReadUInt32( triangles, (int)triangleOffset );
			var a = (int)(raw & 0x3FF);
			var b = a + (int)((raw >> 10) & 0x7F);
			var c = a + (int)((raw >> 17) & 0x7F);
			if ( a >= vertexCount || b >= vertexCount || c >= vertexCount ) continue;
			outputIndices.Add( baseVertex + a );
			outputIndices.Add( baseVertex + b );
			outputIndices.Add( baseVertex + c );
		}

		if ( outputIndices.Count > firstOutputIndex ) return true;
		outputVertices.RemoveRange( baseVertex, vertexCount );
		return false;
	}

	static bool AppendBrush( int index, byte[] planeData, byte[] brushData, byte[] brushPlaneOffsets,
		int firstBrushPlane, List<NumericsVector3> outputVertices, List<int> outputIndices )
	{
		const int brushStride = 0x20;
		if ( index < 0 || index >= brushData.Length / brushStride ) return false;
		var offset = index * brushStride;
		var origin = ReadVector3( brushData, offset );
		var extents = ReadVector3( brushData, offset + 0x10 );
		var sideCount = brushData[offset + 0x0D];
		var sideOffset = ReadInt32( brushData, offset + 0x1C );
		if ( !IsFinite( origin ) || !IsFinite( extents ) ) return false;

		var minimum = origin - extents;
		var maximum = origin + extents;
		var planes = new List<CollisionPlane>( 6 + sideCount )
		{
			new( NumericsVector3.UnitX, maximum.X ),
			new( -NumericsVector3.UnitX, -minimum.X ),
			new( NumericsVector3.UnitY, maximum.Y ),
			new( -NumericsVector3.UnitY, -minimum.Y ),
			new( NumericsVector3.UnitZ, maximum.Z ),
			new( -NumericsVector3.UnitZ, -minimum.Z )
		};

		var planeOffsetCount = brushPlaneOffsets.Length / 2;
		for ( var side = 0; side < sideCount; side++ )
		{
			var lookup = (long)sideOffset + side;
			if ( lookup < 0 || lookup >= planeOffsetCount ) continue;
			var brushPlaneOffset = (int)lookup - ReadUInt16( brushPlaneOffsets, (int)lookup * 2 );
			var planeIndex = firstBrushPlane + brushPlaneOffset;
			if ( planeIndex < 0 || planeIndex >= planeData.Length / 16 ) continue;
			var plane = new CollisionPlane( ReadVector3( planeData, planeIndex * 16 ), ReadSingle( planeData, planeIndex * 16 + 12 ) );
			if ( IsFinite( plane.Normal ) && float.IsFinite( plane.Distance ) && plane.Normal.LengthSquared() > 1e-12f )
				planes.Add( plane );
		}

		var firstOutputIndex = outputIndices.Count;
		for ( var faceIndex = 0; faceIndex < planes.Count; faceIndex++ )
		{
			var points = new List<NumericsVector3>();
			for ( var secondIndex = 0; secondIndex < planes.Count; secondIndex++ )
			{
				if ( secondIndex == faceIndex ) continue;
				for ( var thirdIndex = secondIndex + 1; thirdIndex < planes.Count; thirdIndex++ )
				{
					if ( thirdIndex == faceIndex ) continue;
					if ( !TryIntersectPlanes( planes[faceIndex], planes[secondIndex], planes[thirdIndex], out var point ) ) continue;
					if ( !IsInsideAllPlanes( point, planes ) || points.Any( existing => IsSamePoint( existing, point ) ) ) continue;
					points.Add( point );
				}
			}

			if ( points.Count < 3 ) continue;
			SortFace( points, NumericsVector3.Normalize( planes[faceIndex].Normal ) );
			var baseVertex = outputVertices.Count;
			outputVertices.AddRange( points );
			for ( var triangle = 1; triangle + 1 < points.Count; triangle++ )
			{
				outputIndices.Add( baseVertex );
				outputIndices.Add( baseVertex + triangle );
				outputIndices.Add( baseVertex + triangle + 1 );
			}
		}

		return outputIndices.Count > firstOutputIndex;
	}

	static bool TryIntersectPlanes( CollisionPlane a, CollisionPlane b, CollisionPlane c, out NumericsVector3 point )
	{
		var bCrossC = NumericsVector3.Cross( b.Normal, c.Normal );
		var denominator = NumericsVector3.Dot( a.Normal, bCrossC );
		if ( MathF.Abs( denominator ) < 1e-7f )
		{
			point = default;
			return false;
		}

		point = (bCrossC * a.Distance
			+ NumericsVector3.Cross( c.Normal, a.Normal ) * b.Distance
			+ NumericsVector3.Cross( a.Normal, b.Normal ) * c.Distance) / denominator;
		return IsFinite( point );
	}

	static bool IsInsideAllPlanes( NumericsVector3 point, IReadOnlyList<CollisionPlane> planes )
	{
		for ( var index = 0; index < planes.Count; index++ )
		{
			if ( NumericsVector3.Dot( planes[index].Normal, point ) > planes[index].Distance + 0.05f ) return false;
		}
		return true;
	}

	static bool IsSamePoint( NumericsVector3 a, NumericsVector3 b ) =>
		MathF.Abs( a.X - b.X ) < 0.01f && MathF.Abs( a.Y - b.Y ) < 0.01f && MathF.Abs( a.Z - b.Z ) < 0.01f;

	static void SortFace( List<NumericsVector3> points, NumericsVector3 normal )
	{
		var center = NumericsVector3.Zero;
		foreach ( var point in points ) center += point;
		center /= points.Count;
		var helper = MathF.Abs( normal.Z ) < 0.9f ? NumericsVector3.UnitZ : NumericsVector3.UnitY;
		var u = NumericsVector3.Normalize( NumericsVector3.Cross( helper, normal ) );
		var v = NumericsVector3.Normalize( NumericsVector3.Cross( normal, u ) );
		points.Sort( (left, right) =>
		{
			var leftDelta = left - center;
			var rightDelta = right - center;
			var leftAngle = MathF.Atan2( NumericsVector3.Dot( leftDelta, v ), NumericsVector3.Dot( leftDelta, u ) );
			var rightAngle = MathF.Atan2( NumericsVector3.Dot( rightDelta, v ), NumericsVector3.Dot( rightDelta, u ) );
			return leftAngle.CompareTo( rightAngle );
		} );
	}

	static bool IsFinite( NumericsVector3 value ) => float.IsFinite( value.X ) && float.IsFinite( value.Y ) && float.IsFinite( value.Z );
	static short ReadInt16( byte[] data, int offset ) => BinaryPrimitives.ReadInt16LittleEndian( data.AsSpan( offset, 2 ) );
	static ushort ReadUInt16( byte[] data, int offset ) => BinaryPrimitives.ReadUInt16LittleEndian( data.AsSpan( offset, 2 ) );
	static int ReadInt32( byte[] data, int offset ) => BinaryPrimitives.ReadInt32LittleEndian( data.AsSpan( offset, 4 ) );
	static uint ReadUInt32( byte[] data, int offset ) => BinaryPrimitives.ReadUInt32LittleEndian( data.AsSpan( offset, 4 ) );
	static float ReadSingle( byte[] data, int offset ) => BitConverter.Int32BitsToSingle( ReadInt32( data, offset ) );
	static NumericsVector3 ReadVector3( byte[] data, int offset ) => new(
		ReadSingle( data, offset ), ReadSingle( data, offset + 4 ), ReadSingle( data, offset + 8 ) );
}
