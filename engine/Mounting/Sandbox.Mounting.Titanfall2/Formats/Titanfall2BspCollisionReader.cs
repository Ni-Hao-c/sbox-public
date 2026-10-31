using System.Buffers.Binary;
using NumericsVector3 = System.Numerics.Vector3;

namespace Titanfall2;

public static partial class Titanfall2BspReader
{
	const int PlanesLumpId = 0x0001;
	const int TricollTrianglesLumpId = 0x0042;
	const int TricollHeadersLumpId = 0x0045;
	const int CollisionGridLumpId = 0x0055;
	const int CollisionGridCellsLumpId = 0x0056;
	const int CollisionGeoSetsLumpId = 0x0057;
	const int CollisionPrimitivesLumpId = 0x0059;
	const int CollisionUniqueContentsLumpId = 0x005B;
	const int CollisionBrushesLumpId = 0x005C;
	const int CollisionBrushSidePlaneOffsetsLumpId = 0x005D;
	const byte BrushPrimitiveType = 0x00;
	const byte TricollPrimitiveType = 0x02;
	const byte StaticPropPrimitiveType = 0x03;
	const uint CollisionPrimitiveIndexMask = 0x1FFFFF;
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
		ModelsLumpId,
		CollisionGridLumpId,
		CollisionGridCellsLumpId,
		CollisionGeoSetsLumpId,
		CollisionPrimitivesLumpId,
		CollisionUniqueContentsLumpId,
		CollisionBrushesLumpId,
		CollisionBrushSidePlaneOffsetsLumpId
	];

	public sealed class BspWorldCollision
	{
		public IReadOnlyList<BspCollisionPart> Parts { get; init; } = Array.Empty<BspCollisionPart>();
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
		public int RejectedPrimitiveCount { get; init; }
		public int PrimitiveCount => BrushCount + TricollCount;
		public int TriangleCount => Indices.Length / 3;
		public bool IsEmpty => Vertices.Length == 0 || Indices.Length < 3;
	}

	/// <summary>A source primitive's ranges in the collision buffers. Brush parts are convex;
	/// tricoll parts retain their original triangle soup. Contents are the original uint bits.</summary>
	public readonly record struct BspCollisionPart( bool IsConvex, int SourceIndex, uint Contents,
		int FirstVertex, int VertexCount, int FirstIndex, int IndexCount );

	readonly record struct CollisionPrimitive( byte Type, int Index, uint Contents, bool HasKnownContents );
	readonly record struct CollisionPlane( NumericsVector3 Normal, float Distance );

	static BspWorldCollision ParseWorldCollision( Stream stream, IReadOnlyList<LumpHeader> headers,
		IReadOnlyDictionary<int, byte[]> overrideLumps, int modelIndex = 0, bool playerOnly = true, bool includeParts = false )
	{
		var planes = ReadLumpBytes( stream, headers, overrideLumps, PlanesLumpId );
		var vertices = ReadLumpBytes( stream, headers, overrideLumps, VerticesLumpId );
		var tricollTriangles = ReadLumpBytes( stream, headers, overrideLumps, TricollTrianglesLumpId );
		var tricollHeaders = ReadLumpBytes( stream, headers, overrideLumps, TricollHeadersLumpId );
		var collisionGrid = ReadLumpBytes( stream, headers, overrideLumps, CollisionGridLumpId );
		var collisionCells = ReadLumpBytes( stream, headers, overrideLumps, CollisionGridCellsLumpId );
		var models = ReadLumpBytes( stream, headers, overrideLumps, ModelsLumpId );
		var geoSets = ReadLumpBytes( stream, headers, overrideLumps, CollisionGeoSetsLumpId );
		var primitives = ReadLumpBytes( stream, headers, overrideLumps, CollisionPrimitivesLumpId );
		var uniqueContents = ReadLumpBytes( stream, headers, overrideLumps, CollisionUniqueContentsLumpId );
		var brushes = ReadLumpBytes( stream, headers, overrideLumps, CollisionBrushesLumpId );
		var brushPlaneOffsets = ReadLumpBytes( stream, headers, overrideLumps, CollisionBrushSidePlaneOffsetsLumpId );
		if ( geoSets.Length < 8 && modelIndex == 0 ) return new BspWorldCollision();
		var (firstSet, setCount) = CollisionModelGeoSets( modelIndex, models, collisionGrid, collisionCells, geoSets );
		if ( primitives.Length % 4 != 0 || uniqueContents.Length % 4 != 0 )
			throw new InvalidDataException( "Invalid CM primitive or contents lump size." );

		var outputVertices = new List<NumericsVector3>();
		var outputIndices = new List<int>();
		var parts = new List<BspCollisionPart>();
		var brushCount = 0;
		var tricollCount = 0;
		var brushTriangleCount = 0;
		var tricollTriangleCount = 0;
		var sourcePrimitiveCount = 0;
		var nonBlockingPrimitiveCount = 0;
		var waterPrimitiveCount = 0;
		var blockingWaterPrimitiveCount = 0;
		var rejectedPrimitiveCount = 0;
		var firstBrushPlane = collisionGrid.Length >= 0x1C ? ReadInt32( collisionGrid, 0x18 ) : 0;

		foreach ( var primitive in EnumerateWorldPrimitives( geoSets, firstSet, setCount, primitives, uniqueContents ) )
		{
			sourcePrimitiveCount++;
			var isWater = primitive.HasKnownContents && IsWaterContents( primitive.Contents );
			if ( isWater ) waterPrimitiveCount++;
			if ( playerOnly && primitive.HasKnownContents && !BlocksPlayer( primitive.Contents ) )
			{
				nonBlockingPrimitiveCount++;
				continue;
			}
			if ( isWater && BlocksPlayer( primitive.Contents ) ) blockingWaterPrimitiveCount++;
			var partVertex = outputVertices.Count;
			var partIndex = outputIndices.Count;

			if ( primitive.Type == BrushPrimitiveType )
			{
				var firstIndex = outputIndices.Count;
				if ( AppendBrush( primitive.Index, planes, brushes, brushPlaneOffsets, firstBrushPlane,
					outputVertices, outputIndices ) )
				{
					brushCount++;
					brushTriangleCount += (outputIndices.Count - firstIndex) / 3;
				}
				else rejectedPrimitiveCount++;
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
				else rejectedPrimitiveCount++;
			}
			if ( includeParts && outputIndices.Count > partIndex )
				parts.Add( new BspCollisionPart( primitive.Type == BrushPrimitiveType, primitive.Index, primitive.Contents,
					partVertex, outputVertices.Count - partVertex, partIndex, outputIndices.Count - partIndex ) );
		}

		return new BspWorldCollision
		{
			Vertices = outputVertices.ToArray(),
			Indices = outputIndices.ToArray(),
			Parts = parts,
			BrushCount = brushCount,
			TricollCount = tricollCount,
			BrushTriangleCount = brushTriangleCount,
			TricollTriangleCount = tricollTriangleCount,
			SourcePrimitiveCount = sourcePrimitiveCount,
			NonBlockingPrimitiveCount = nonBlockingPrimitiveCount,
			WaterPrimitiveCount = waterPrimitiveCount,
			BlockingWaterPrimitiveCount = blockingWaterPrimitiveCount,
			RejectedPrimitiveCount = rejectedPrimitiveCount
		};
	}

	// Retail engine.dll: 0x18014c045..0x18014c096. Model 0 owns the
	// geoset prefix ending at model 1's first set (all sets for a one-model map).
	// This includes the global world cell and sets not reached by an XY cell.
	// Inline model N instead owns CM_GRID_CELLS[width * height + N].
	static (int First, int Count) CollisionModelGeoSets( int model, byte[] models, byte[] grid, byte[] cells, byte[] sets )
	{
		if ( models.Length == 0 || models.Length % 32 != 0 || grid.Length != 28
			|| cells.Length % 4 != 0 || sets.Length % 8 != 0 )
			throw new InvalidDataException( "Invalid CM model, grid, cell or geoset lump size." );
		var modelCount = models.Length / 32;
		var width = ReadInt32( grid, 12 ); var height = ReadInt32( grid, 16 );
		var gridCount = (long)width * height;
		if ( width < 0 || height < 0 || gridCount + modelCount != cells.Length / 4 || (uint)model >= modelCount )
			throw new InvalidDataException( "CM grid cells do not match the model table." );
		var first = model == 0 ? 0 : ReadUInt16( cells, checked((int)(gridCount + model) * 4) );
		var count = model == 0
			? (modelCount == 1 ? sets.Length / 8 : ReadUInt16( cells, checked((int)(gridCount + 1) * 4) ))
			: ReadUInt16( cells, checked((int)(gridCount + model) * 4 + 2) );
		if ( (long)first + count > sets.Length / 8 )
			throw new InvalidDataException( "CM model geoset range is out of bounds." );
		return (first, count);
	}

	static IEnumerable<CollisionPrimitive> EnumerateWorldPrimitives(
		byte[] geoSets,
		int firstSet,
		int setCount,
		byte[] primitives,
		byte[] uniqueContents )
	{
		var seen = new HashSet<uint>();
		var primitiveCount = primitives.Length / 4;
		for ( var offset = firstSet * 8; offset < (firstSet + setCount) * 8; offset += 8 )
		{
			var count = ReadUInt16( geoSets, offset + 2 );
			var raw = ReadUInt32( geoSets, offset + 4 );
			if ( count == 1 )
			{
				if ( TryDecodeWorldPrimitive( raw, seen, uniqueContents, out var primitive ) ) yield return primitive;
				continue;
			}

			if ( count == 0 ) continue;
			var first = (int)((raw >> 8) & CollisionPrimitiveIndexMask);
			var end = (long)first + count;
			if ( end > primitiveCount ) throw new InvalidDataException( "CM geoset primitive range is out of bounds." );
			for ( var primitiveIndex = first; primitiveIndex < end; primitiveIndex++ )
			{
				var childRaw = ReadUInt32( primitives, primitiveIndex * 4 );
				if ( TryDecodeWorldPrimitive( childRaw, seen, uniqueContents, out var primitive ) ) yield return primitive;
			}
		}
	}

	static bool TryDecodeWorldPrimitive(
		uint raw,
		HashSet<uint> seen,
		byte[] uniqueContents,
		out CollisionPrimitive primitive )
	{
		// Retail uses a 3-bit type and a 21-bit index, not an 8/16/8 split.
		// engine.dll 0x18014c106..122 and 0x18014c142..151.
		var type = (byte)(raw >> 29);
		var index = (int)((raw >> 8) & CollisionPrimitiveIndexMask);
		primitive = default;
		// Static props are imported separately, with instance transforms and VPHY.
		if ( type == StaticPropPrimitiveType ) return false;
		if ( type is not (BrushPrimitiveType or TricollPrimitiveType) )
			throw new InvalidDataException( $"Unsupported CM primitive type 0x{type:X2}." );
		var contentsIndex = (int)(raw & 0xFF);
		var hasKnownContents = contentsIndex >= 0 && contentsIndex < uniqueContents.Length / sizeof( uint );
		if ( !hasKnownContents ) throw new InvalidDataException( "CM primitive contents index is out of bounds." );
		var contents = hasKnownContents ? ReadUInt32( uniqueContents, contentsIndex * sizeof( uint ) ) : 0u;
		primitive = new CollisionPrimitive( type, index, contents, hasKnownContents );
		// Deduplicate references from overlapping cells, but retain different contents
		// on the same geometry. Otherwise an earlier water-only reference can hide a
		// later player-blocking reference before the query mask is evaluated.
		return seen.Add( raw );
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
		if ( ((long)firstVertex + vertexCount) * 12 > vertices.Length
			|| ((long)firstTriangle + triangleCount) * 4 > triangles.Length ) return false;

		var baseVertex = outputVertices.Count;
		for ( var localVertex = 0; localVertex < vertexCount; localVertex++ )
		{
			var position = ReadVector3( vertices, (firstVertex + localVertex) * 12 );
			if ( !IsFinite( position ) )
			{
				outputVertices.RemoveRange( baseVertex, outputVertices.Count - baseVertex );
				return false;
			}
			outputVertices.Add( position );
		}

		var firstOutputIndex = outputIndices.Count;
		for ( var triangle = 0; triangle < triangleCount; triangle++ )
		{
			var triangleOffset = ((long)firstTriangle + triangle) * 4;
			var raw = ReadUInt32( triangles, (int)triangleOffset );
			var a = (int)(raw & 0x3FF);
			var b = a + (int)((raw >> 10) & 0x7F);
			var c = a + (int)((raw >> 17) & 0x7F);
			if ( a >= vertexCount || b >= vertexCount || c >= vertexCount )
			{
				outputVertices.RemoveRange( baseVertex, vertexCount );
				outputIndices.RemoveRange( firstOutputIndex, outputIndices.Count - firstOutputIndex );
				return false;
			}
			// Retail tricolls share the render meshes' clockwise winding (offline audit:
			// >99% of matched triangles). The mount emits counter-clockwise faces for render,
			// render-fallback collision and CM brushes, so reverse tricolls the same way.
			outputIndices.Add( baseVertex + a );
			outputIndices.Add( baseVertex + c );
			outputIndices.Add( baseVertex + b );
		}

		if ( outputIndices.Count > firstOutputIndex ) return true;
		outputVertices.RemoveRange( baseVertex, vertexCount );
		return false;
	}

	static bool AppendBrush( int index, byte[] planeData, byte[] brushData, byte[] brushPlaneOffsets,
		int firstBrushPlane, List<NumericsVector3> outputVertices, List<int> outputIndices )
	{
		const int brushStride = 0x20;
		// Hull generation is planes^3 per face. Retail brushes stay far below this (a
		// high-resolution cylinder is the practical maximum), and rejecting the rest keeps
		// a corrupt side count from turning one brush into minutes of plane tests.
		const int maximumBrushSides = 128;
		if ( index < 0 || index >= brushData.Length / brushStride ) return false;
		var offset = index * brushStride;
		var origin = ReadVector3( brushData, offset );
		var extents = ReadVector3( brushData, offset + 0x10 );
		var sideCount = brushData[offset + 0x0D];
		var sideOffset = ReadInt32( brushData, offset + 0x1C );
		if ( sideCount > maximumBrushSides ) return false;
		if ( !IsFinite( origin ) || !IsFinite( extents ) || extents.X < 0 || extents.Y < 0 || extents.Z < 0 ) return false;

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
			if ( lookup < 0 || lookup >= planeOffsetCount ) return false;
			var brushPlaneOffset = (int)lookup - ReadUInt16( brushPlaneOffsets, (int)lookup * 2 );
			var planeIndex = firstBrushPlane + brushPlaneOffset;
			if ( planeIndex < 0 || planeIndex >= planeData.Length / 16 ) return false;
			var plane = new CollisionPlane( ReadVector3( planeData, planeIndex * 16 ), ReadSingle( planeData, planeIndex * 16 + 12 ) );
			if ( !IsFinite( plane.Normal ) || !float.IsFinite( plane.Distance ) || plane.Normal.LengthSquared() <= 1e-12f ) return false;
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
			var faceNormal = NumericsVector3.Normalize( planes[faceIndex].Normal );
			SortFace( points, faceNormal );
			var baseVertex = outputVertices.Count;
			outputVertices.AddRange( points );
			for ( var triangle = 1; triangle + 1 < points.Count; triangle++ )
			{
				// Bevel and replaced axial planes only touch the hull along an edge or at a
				// vertex; their near-duplicate points sort into zero-area or reversed slivers.
				var winding = NumericsVector3.Cross( points[triangle] - points[0], points[triangle + 1] - points[0] );
				if ( NumericsVector3.Dot( winding, faceNormal ) <= 1e-3f ) continue;
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
		// Nearly parallel triples amplify float error. Such a point can still pass the
		// inside test while lying off its face plane, producing a non-planar face.
		return IsFinite( point ) && IsOnPlane( point, a ) && IsOnPlane( point, b ) && IsOnPlane( point, c );
	}

	static bool IsOnPlane( NumericsVector3 point, CollisionPlane plane )
	{
		return MathF.Abs( NumericsVector3.Dot( plane.Normal, point ) - plane.Distance ) <= 0.05f;
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
		points.Sort( ( left, right ) =>
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
