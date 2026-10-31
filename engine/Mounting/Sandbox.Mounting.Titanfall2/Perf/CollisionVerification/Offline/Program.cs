using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Titanfall2;

if ( args.Length < 2 )
{
	Console.Error.WriteLine( "Usage: CollisionAudit <Titanfall2 install> <output.json> [map names ...]" );
	return 2;
}
var vpkRoot = Path.Combine( Path.GetFullPath( args[0] ), "vpk" );
var output = Path.GetFullPath( args[1] );
var brushAudit = args.Contains( "--brush" );
var geometryAudit = args.Contains( "--geometry" );
var maps = args.Skip( 2 ).Where( argument => argument is not ("--brush" or "--geometry") ).ToArray();
if ( maps.Length == 0 ) maps = ["mp_angel_city", "mp_wargames", "sp_beacon", "sp_tday"];
var rows = new List<object>();
var failures = 0;
foreach ( var map in maps )
{
	var timer = Stopwatch.StartNew();
	try
	{
		var directory = Path.Combine( vpkRoot, $"englishclient_{map}.bsp.pak000_dir.vpk" );
		if ( !VpkArchive.TryOpen( directory, out var archive, out var error ) ) throw new InvalidDataException( error );
		if ( !archive.TryReadAllBytes( $"maps/{map}.bsp", out var bytes, out error ) ) throw new InvalidDataException( error );
		var overrides = new Dictionary<int, byte[]>();
		// The geometry audit compares against render meshes and static props, so it needs every sidecar.
		foreach ( var id in geometryAudit ? Enumerable.Range( 0, 128 )
			: brushAudit ? Titanfall2BspReader.RequiredBrushModelLumpIds : Titanfall2BspReader.RequiredCollisionLumpIds )
		{
			if ( archive.TryReadAllBytes( $"maps/{map}.bsp.{id:X4}.bsp_lump.client", out var data, out _ )
				|| archive.TryReadAllBytes( $"maps/{map}.bsp.{id:X4}.bsp_lump", out data, out _ ) ) overrides[id] = data;
		}
		byte[] Lump( int id )
		{
			if ( overrides.TryGetValue( id, out var data ) ) return data;
			var first = BinaryPrimitives.ReadInt32LittleEndian( bytes.AsSpan( 16 + id * 16 ) );
			var size = BinaryPrimitives.ReadInt32LittleEndian( bytes.AsSpan( 20 + id * 16 ) );
			return bytes.AsSpan( first, size ).ToArray();
		}
		var models = Lump( 14 ); var grid = Lump( 85 ); var cells = Lump( 86 ); var sets = Lump( 87 ); var primitives = Lump( 89 );
		var width = BinaryPrimitives.ReadInt32LittleEndian( grid.AsSpan( 12 ) );
		var height = BinaryPrimitives.ReadInt32LittleEndian( grid.AsSpan( 16 ) );
		var modelCount = models.Length / 32;
		var worldEnd = modelCount == 1 ? sets.Length / 8 : BinaryPrimitives.ReadUInt16LittleEndian( cells.AsSpan( (width * height + 1) * 4 ) );
		HashSet<uint> References( int first, int end )
		{
			var result = new HashSet<uint>();
			for ( var set = first; set < end; ++set )
			{
				var count = BinaryPrimitives.ReadUInt16LittleEndian( sets.AsSpan( set * 8 + 2 ) );
				var raw = BinaryPrimitives.ReadUInt32LittleEndian( sets.AsSpan( set * 8 + 4 ) );
				for ( var i = 0; i < count; ++i )
				{
					var item = count == 1 ? raw : BinaryPrimitives.ReadUInt32LittleEndian( primitives.AsSpan( ((int)((raw >> 8) & 0x1FFFFF) + i) * 4 ) );
					if ( item >> 29 is 0 or 2 ) result.Add( item );
				}
			}
			return result;
		}
		var world = References( 0, worldEnd );
		var inline = References( worldEnd, sets.Length / 8 );
		inline.ExceptWith( world );
		if ( !Titanfall2BspReader.TryRead( bytes, map, overrides, true, false, false, out var bsp, out error ) ) throw new InvalidDataException( error );
		var collision = bsp.WorldCollision;
		object brushModels = null;
		if ( brushAudit )
		{
			if ( !Titanfall2BspReader.TryReadBrushModels( bytes, overrides, true, out var catalog, out error ) ) throw new InvalidDataException( error );
			if ( catalog.Models.Count != modelCount - 1 ) throw new InvalidDataException( "Inline catalog size mismatch." );
			foreach ( var model in catalog.Models )
			{
				var cell = (width * height + model.Index) * 4;
				var first = BinaryPrimitives.ReadUInt16LittleEndian( cells.AsSpan( cell ) );
				var count = BinaryPrimitives.ReadUInt16LittleEndian( cells.AsSpan( cell + 2 ) );
				if ( model.Collision.SourcePrimitiveCount != References( first, first + count ).Count
					|| model.Collision.Parts.Count != model.Collision.PrimitiveCount
					|| !model.Collision.Indices.All( value => (uint)value < model.Collision.Vertices.Length )
					|| !model.Collision.Vertices.All( point => float.IsFinite( point.X ) && float.IsFinite( point.Y ) && float.IsFinite( point.Z ) ) )
					throw new InvalidDataException( $"Inline model {model.Index} ownership/buffer validation failed." );
			}
			brushModels = catalog.Models.Select( model => new
			{
				model.Index, meshes = model.Geometry.Meshes.Count, sourceMeshes = model.Geometry.SourceMeshCount,
				vertices = model.Geometry.Meshes.Sum( mesh => mesh.Vertices.Length ),
				model.Collision.BrushCount, model.Collision.TricollCount, model.Collision.SourcePrimitiveCount,
				model.Collision.RejectedPrimitiveCount, model.Collision.TriangleCount,
				contents = model.Collision.Parts.Select( part => $"{part.Contents:X8}" ).Distinct().ToArray(),
				mins = new[] { model.Geometry.Mins.X, model.Geometry.Mins.Y, model.Geometry.Mins.Z },
				maxs = new[] { model.Geometry.Maxs.X, model.Geometry.Maxs.Y, model.Geometry.Maxs.Z },
				finite = model.Collision.Vertices.All( point => float.IsFinite( point.X ) && float.IsFinite( point.Y ) && float.IsFinite( point.Z ) ),
				indicesValid = model.Collision.Indices.All( value => (uint)value < model.Collision.Vertices.Length )
			} ).ToArray();
		}
		var geometry = geometryAudit ? Titanfall2BspReader.AuditGeometry( bytes, overrides ) : null;
		var passed = collision.SourcePrimitiveCount == world.Count && collision.RejectedPrimitiveCount == 0
			&& !collision.IsEmpty && collision.Vertices.All( p => float.IsFinite( p.X ) && float.IsFinite( p.Y ) && float.IsFinite( p.Z ) )
			&& collision.Indices.All( i => (uint)i < collision.Vertices.Length ) && geometry?.Passed != false;
		if ( !passed ) failures++;
		rows.Add( new
		{
			map, passed, bspSha256 = Convert.ToHexString( SHA256.HashData( bytes ) ), modelCount,
			worldGeosets = worldEnd, totalGeosets = sets.Length / 8, worldRawReferences = world.Count,
			excludedInlineReferences = inline.Count, collision.SourcePrimitiveCount, collision.PrimitiveCount,
			collision.BrushCount, collision.TricollCount, collision.TriangleCount,
			collision.NonBlockingPrimitiveCount, collision.WaterPrimitiveCount, collision.BlockingWaterPrimitiveCount,
			collision.RejectedPrimitiveCount, brushModels, geometry, seconds = timer.Elapsed.TotalSeconds,
			sidecars = overrides.ToDictionary( x => x.Key, x => Convert.ToHexString( SHA256.HashData( x.Value ) ) )
		} );
		Console.WriteLine( $"{map}: {(passed ? "PASS" : "FAIL")}; world {world.Count}, inline excluded {inline.Count}, "
			+ $"rejected {collision.RejectedPrimitiveCount}, {collision.TriangleCount} triangles, {timer.Elapsed.TotalSeconds:0.00}s"
			+ (geometry is null ? "" : $"; tricoll winding {geometry.TricollMatchedMountWinding}/{geometry.TricollMatchedReversed} "
				+ $"(ambiguous {geometry.TricollAmbiguous}), brush inward {geometry.BrushInwardTriangles}, loose {geometry.BrushLooseBounds}, "
				+ $"degenerate {geometry.DegenerateTriangles}, props solid {geometry.SolidProps} / CM {geometry.CmPropPrimitives} / "
				+ $"not player-solid {geometry.SolidPropsNotPlayerSolid} / rule mismatches {geometry.PropContentsRuleMismatches}") );
	}
	catch ( Exception exception )
	{
		failures++;
		rows.Add( new { map, passed = false, error = exception.ToString() } );
		Console.WriteLine( $"{map}: FAIL {exception.Message}" );
	}
	Directory.CreateDirectory( Path.GetDirectoryName( output )! );
	File.WriteAllText( output, JsonSerializer.Serialize( rows, new JsonSerializerOptions { WriteIndented = true } ) );
	// This audit intentionally walks maps sequentially while the user may have an
	// editor open. The report retains only statistics, not large parsed geometry.
	GC.Collect();
}
return failures == 0 ? 0 : 1;
