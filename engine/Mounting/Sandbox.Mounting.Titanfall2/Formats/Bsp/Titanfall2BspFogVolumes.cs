using System.Globalization;
using NumericsVector3 = System.Numerics.Vector3;

namespace Titanfall2;

public static partial class Titanfall2BspReader
{
	// Inline brush models have one CM_GRID_CELL each, after the XY world grid.
	// Recover convex planes without building world collision or converting contents.
	static void PopulateFogVolumeBrushes( List<Dictionary<string, string>> entities, Stream stream,
		IReadOnlyList<LumpHeader> headers, IReadOnlyDictionary<int, byte[]> overrides )
	{
		var volumes = entities.Where( e => e.GetValueOrDefault( "classname" ) == "fog_volume"
			&& !e.ContainsKey( "*trigger_bounds_mins" ) ).ToArray();
		if ( volumes.Length == 0 ) return;
		var models = ReadModels( ReadLumpBytes( stream, headers, overrides, ModelsLumpId ) );
		var grid = ReadLumpBytes( stream, headers, overrides, CollisionGridLumpId );
		if ( grid.Length < 28 ) return;
		var width = ReadInt32( grid, 12 ); var height = ReadInt32( grid, 16 );
		if ( width <= 0 || height <= 0 ) return;
		var gridCount = (long)width * height;
		var firstPlane = ReadInt32( grid, 24 );
		var cells = ReadLumpBytes( stream, headers, overrides, CollisionGridCellsLumpId );
		var sets = ReadLumpBytes( stream, headers, overrides, CollisionGeoSetsLumpId );
		var primitives = ReadLumpBytes( stream, headers, overrides, CollisionPrimitivesLumpId );
		var brushes = ReadLumpBytes( stream, headers, overrides, CollisionBrushesLumpId );
		var offsets = ReadLumpBytes( stream, headers, overrides, CollisionBrushSidePlaneOffsetsLumpId );
		var planes = ReadLumpBytes( stream, headers, overrides, PlanesLumpId );
		foreach ( var volume in volumes )
		{
			var modelText = volume.GetValueOrDefault( "model", "" );
			if ( !modelText.StartsWith( '*' ) || !int.TryParse( modelText.AsSpan( 1 ), out var model )
				|| model <= 0 || model >= models.Length || gridCount + model >= cells.Length / 4 ) continue;
			var cell = (int)(gridCount + model) * 4;
			var first = ReadUInt16( cells, cell ); var count = ReadUInt16( cells, cell + 2 );
			if ( count == 0 || (long)first + count > sets.Length / 8 ) continue;
			var decoded = new List<List<CollisionPlane>>();
			var valid = true;
			for ( var set = (int)first; set < first + count && valid; ++set )
			{
				var raw = ReadUInt32( sets, set * 8 + 4 );
				var primitiveCount = ReadUInt16( sets, set * 8 + 2 );
				var start = (int)((raw >> 8) & CollisionPrimitiveIndexMask);
				if ( primitiveCount == 0 || (primitiveCount != 1 && (long)start + primitiveCount > primitives.Length / 4) ) { valid = false; break; }
				for ( var p = 0; p < primitiveCount; ++p )
				{
					var item = primitiveCount == 1 ? raw : ReadUInt32( primitives, (start + p) * 4 );
					if ( item >> 29 != BrushPrimitiveType ) { valid = false; break; }
					var index = (int)((item >> 8) & CollisionPrimitiveIndexMask);
					if ( index >= brushes.Length / 32 ) { valid = false; break; }
					var origin = ReadVector3( brushes, index * 32 );
					var extents = ReadVector3( brushes, index * 32 + 16 );
					if ( !IsFinite( origin ) || !IsFinite( extents ) || extents.X < 0 || extents.Y < 0 || extents.Z < 0 ) { valid = false; break; }
					var min = origin - extents; var max = origin + extents;
					var hull = new List<CollisionPlane>
					{
						new( NumericsVector3.UnitX, max.X ), new( -NumericsVector3.UnitX, -min.X ),
						new( NumericsVector3.UnitY, max.Y ), new( -NumericsVector3.UnitY, -min.Y ),
						new( NumericsVector3.UnitZ, max.Z ), new( -NumericsVector3.UnitZ, -min.Z )
					};
					var sides = brushes[index * 32 + 13];
					var sideOffset = ReadInt32( brushes, index * 32 + 28 );
					for ( var side = 0; side < sides; ++side )
					{
						var lookup = (long)sideOffset + side;
						if ( lookup < 0 || lookup >= offsets.Length / 2 ) { valid = false; break; }
						var planeIndex = (long)firstPlane + lookup - ReadUInt16( offsets, (int)lookup * 2 );
						if ( planeIndex < 0 || planeIndex >= planes.Length / 16 ) { valid = false; break; }
						var normal = ReadVector3( planes, (int)planeIndex * 16 );
						var distance = ReadSingle( planes, (int)planeIndex * 16 + 12 );
						if ( !IsFinite( normal ) || !float.IsFinite( distance ) || normal.LengthSquared() < 1e-12f ) { valid = false; break; }
						hull.Add( new( normal, distance ) );
					}
					decoded.Add( hull );
				}
			}
			if ( !valid || decoded.Count == 0 || !IsFinite( models[model].Mins ) || !IsFinite( models[model].Maxs ) ) continue;
			static string Number( float n ) => n.ToString( "R", CultureInfo.InvariantCulture );
			static string Vector( NumericsVector3 v ) => $"{Number( v.X )} {Number( v.Y )} {Number( v.Z )}";
			volume["*trigger_bounds_mins"] = Vector( models[model].Mins );
			volume["*trigger_bounds_maxs"] = Vector( models[model].Maxs );
			for ( var b = 0; b < decoded.Count; ++b )
				for ( var p = 0; p < decoded[b].Count; ++p )
					volume[$"*trigger_brush_{b}_plane_{p}"] = $"{Vector( decoded[b][p].Normal )} {Number( decoded[b][p].Distance )}";
		}
	}
}
