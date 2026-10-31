using System.Numerics;

namespace Titanfall2;

// Offline-only partial (the mount projects exclude Perf/CollisionVerification/**).
// Checks the emitted CM geometry against the retail BSP itself:
//  - tricoll winding against the render mesh sharing the same three positions
//    (the mount reverses render winding, so emitted tricolls must be reversed too);
//  - brush hulls: outward faces, no degenerate triangles, AABB equal to CM_BRUSHES extents;
//  - static-prop CM primitives (type 3) against 0x00EB0280 & ~CollisionFlagsRemove.
public static partial class Titanfall2BspReader
{
	public sealed record GeometryAuditResult(
		int TricollMatchedMountWinding, int TricollMatchedReversed, int TricollAmbiguous, int TricollUnmatched,
		int BrushTriangles, int BrushInwardTriangles, double BrushInwardArea, double BrushOutwardArea,
		int BrushLooseBounds, int DegenerateTriangles,
		int SolidProps, int CmPropPrimitives, int PropContentsRuleMismatches, int SolidPropsNotPlayerSolid,
		int FallbackOnlyStaticProps, int RenderTrianglesAlignedWithNormals, int RenderTrianglesOpposedToNormals )
	{
		public bool Passed => TricollMatchedMountWinding >= 0.95 * Math.Max( 1, TricollMatchedMountWinding + TricollMatchedReversed )
			&& BrushInwardArea <= 1e-4 * Math.Max( 1, BrushOutwardArea ) && BrushLooseBounds == 0 && DegenerateTriangles == 0
			&& PropContentsRuleMismatches == 0;
	}

	public static GeometryAuditResult AuditGeometry( byte[] bytes, IReadOnlyDictionary<int, byte[]> overrides )
	{
		using var stream = new MemoryStream( bytes, false );
		var headers = ReadBrushHeaders( stream );
		byte[] Lump( int id ) => ReadLumpBytes( stream, headers, overrides, id );
		var all = ParseWorldCollision( stream, headers, overrides, 0, playerOnly: false, includeParts: true );
		if ( !TryRead( bytes, "audit", overrides, false, false, false, out var parsed, out var error ) )
			throw new InvalidDataException( error );

		// Render orientation per position triple: bit 1 = along the canonical normal, bit 2 = against.
		var render = new Dictionary<(Vector3, Vector3, Vector3), int>();
		int alignedNormals = 0, opposedNormals = 0;
		foreach ( var mesh in parsed.World.Meshes )
		{
			for ( var index = 0; index + 2 < mesh.Indices.Length; index += 3 )
			{
				var a = mesh.Vertices[mesh.Indices[index]].Position;
				var b = mesh.Vertices[mesh.Indices[index + 1]].Position;
				var c = mesh.Vertices[mesh.Indices[index + 2]].Position;
				var normal = mesh.Vertices[mesh.Indices[index]].Normal + mesh.Vertices[mesh.Indices[index + 1]].Normal + mesh.Vertices[mesh.Indices[index + 2]].Normal;
				var orientation = Vector3.Dot( Vector3.Cross( b - a, c - a ), normal );
				if ( orientation > 1e-4f ) alignedNormals++;
				else if ( orientation < -1e-4f ) opposedNormals++;
				if ( !TryOrientation( a, b, c, out var key, out var along ) ) continue;
				// Mount render/fallback collision uses reversed winding.
				render[key] = render.GetValueOrDefault( key ) | (along ? 2 : 1);
			}
		}

		// Two-sided tricolls (cloth, trims, grates) store both windings; they match the
		// render triangle once each way and say nothing about orientation.
		var tricollSides = new Dictionary<(Vector3, Vector3, Vector3), int>();
		foreach ( var part in all.Parts.Where( part => !part.IsConvex ) )
		{
			for ( var triangle = 0; triangle < part.IndexCount; triangle += 3 )
			{
				if ( !TryOrientation( all.Vertices[all.Indices[part.FirstIndex + triangle]], all.Vertices[all.Indices[part.FirstIndex + triangle + 1]],
					all.Vertices[all.Indices[part.FirstIndex + triangle + 2]], out var key, out var along ) ) continue;
				tricollSides[key] = tricollSides.GetValueOrDefault( key ) | (along ? 1 : 2);
			}
		}

		int same = 0, reversed = 0, ambiguous = 0, unmatched = 0, brushTriangles = 0, inward = 0, loose = 0, degenerate = 0;
		double inwardArea = 0, outwardArea = 0;
		var brushes = Lump( CollisionBrushesLumpId );
		foreach ( var part in all.Parts )
		{
			var centroid = Vector3.Zero;
			for ( var vertex = 0; vertex < part.VertexCount; vertex++ ) centroid += all.Vertices[part.FirstVertex + vertex];
			centroid /= Math.Max( 1, part.VertexCount );
			if ( part.IsConvex )
			{
				var min = new Vector3( float.MaxValue ); var max = new Vector3( float.MinValue );
				for ( var vertex = 0; vertex < part.VertexCount; vertex++ )
				{
					min = Vector3.Min( min, all.Vertices[part.FirstVertex + vertex] );
					max = Vector3.Max( max, all.Vertices[part.FirstVertex + vertex] );
				}
				var origin = ReadVector3( brushes, part.SourceIndex * 32 );
				var extents = ReadVector3( brushes, part.SourceIndex * 32 + 16 );
				var slack = Vector3.Max( min - (origin - extents), (origin + extents) - max );
				if ( MathF.Max( slack.X, MathF.Max( slack.Y, slack.Z ) ) > 0.5f ) loose++;
			}
			for ( var triangle = 0; triangle < part.IndexCount; triangle += 3 )
			{
				var a = all.Vertices[all.Indices[part.FirstIndex + triangle]];
				var b = all.Vertices[all.Indices[part.FirstIndex + triangle + 1]];
				var c = all.Vertices[all.Indices[part.FirstIndex + triangle + 2]];
				var cross = Vector3.Cross( b - a, c - a );
				var area = cross.Length() * 0.5f;
				if ( area <= 1e-4f ) { degenerate++; continue; }
				if ( part.IsConvex )
				{
					brushTriangles++;
					if ( Vector3.Dot( cross / (2 * area), (a + b + c) / 3 - centroid ) < -0.1f ) { inward++; inwardArea += area; }
					else outwardArea += area;
					continue;
				}
				if ( !TryOrientation( a, b, c, out var key, out var along ) ) continue;
				if ( !render.TryGetValue( key, out var seen ) ) unmatched++;
				else if ( seen == 3 || tricollSides[key] == 3 ) ambiguous++;
				else if ( (seen == 1) == along ) same++;
				else reversed++;
			}
		}

		// Static props: CM type-3 primitives carry the retail per-instance contents.
		var models = Lump( ModelsLumpId ); var grid = Lump( CollisionGridLumpId ); var cells = Lump( CollisionGridCellsLumpId );
		var sets = Lump( CollisionGeoSetsLumpId ); var primitives = Lump( CollisionPrimitivesLumpId ); var unique = Lump( CollisionUniqueContentsLumpId );
		var (firstSet, setCount) = CollisionModelGeoSets( 0, models, grid, cells, sets );
		var propContents = new Dictionary<int, uint>();
		for ( var offset = firstSet * 8; offset < (firstSet + setCount) * 8; offset += 8 )
		{
			var count = ReadUInt16( sets, offset + 2 );
			var raw = ReadUInt32( sets, offset + 4 );
			for ( var item = 0; item < count; item++ )
			{
				var reference = count == 1 ? raw : ReadUInt32( primitives, ((int)((raw >> 8) & CollisionPrimitiveIndexMask) + item) * 4 );
				if ( reference >> 29 != StaticPropPrimitiveType ) continue;
				propContents[(int)((reference >> 8) & CollisionPrimitiveIndexMask)] = ReadUInt32( unique, (int)(reference & 0xFF) * 4 );
			}
		}
		int solidProps = 0, mismatches = 0, notPlayerSolid = 0, fallbackOnly = 0;
		foreach ( var prop in parsed.StaticProps )
		{
			var hasCm = propContents.TryGetValue( prop.SourceIndex, out var contents );
			if ( prop.SolidType == 0 )
			{
				if ( hasCm ) mismatches++;
				continue;
			}
			solidProps++;
			if ( !hasCm ) { fallbackOnly++; continue; }
			// The model contents can select the window/grate expansions; all three keep
			// the player bit, so compare the player-solid classification exactly.
			if ( BlocksPlayer( contents ) != BlocksPlayer( 0x00EB0280u & ~prop.CollisionFlagsRemove ) ) mismatches++;
			if ( !BlocksPlayer( contents ) ) notPlayerSolid++;
		}
		return new GeometryAuditResult( same, reversed, ambiguous, unmatched, brushTriangles, inward, inwardArea, outwardArea,
			loose, degenerate, solidProps, propContents.Count, mismatches, notPlayerSolid, fallbackOnly, alignedNormals, opposedNormals );
	}

	static bool TryOrientation( Vector3 a, Vector3 b, Vector3 c, out (Vector3, Vector3, Vector3) key, out bool along )
	{
		var sorted = new[] { a, b, c };
		Array.Sort( sorted, static ( left, right ) => left.X != right.X ? left.X.CompareTo( right.X )
			: left.Y != right.Y ? left.Y.CompareTo( right.Y ) : left.Z.CompareTo( right.Z ) );
		key = (sorted[0], sorted[1], sorted[2]);
		var canonical = Vector3.Cross( sorted[1] - sorted[0], sorted[2] - sorted[0] );
		var normal = Vector3.Cross( b - a, c - a );
		along = Vector3.Dot( canonical, normal ) > 0;
		return canonical.LengthSquared() > 1e-8f;
	}
}
