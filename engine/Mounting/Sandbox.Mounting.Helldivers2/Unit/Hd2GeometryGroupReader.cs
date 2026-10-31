using System.Buffers.Binary;

namespace Sandbox.Mounting.Helldivers2;

/// <summary>
/// Reads the Main payload of a <c>geometry_group</c>: the shared container several units draw
/// their geometry from. It holds one vertex layout list and, per unit, one entry per mesh giving
/// the layout index, material slots and groups of that mesh. Vertices and indices are read from
/// the geometry group's own GPU stream.
/// </summary>
internal static class Hd2GeometryGroupReader
{
	const int HeaderSize = 16;

	/// <summary>One mesh info item, as stored in a geometry group: layout, material list and group list.</summary>
	const int MeshInfoItemSize = 48;

	const uint MaxArrayCount = Hd2PayloadReader.MaxArrayCount;

	public static bool TryRead( ReadOnlySpan<byte> main, out Hd2GeometryGroup group, out string error )
	{
		group = null;
		error = null;

		try
		{
			var reader = new Hd2PayloadReader( main );
			if ( !reader.TrySlice( 0, HeaderSize, out var header ) )
			{
				error = $"the geometry group payload is {main.Length} bytes and cannot hold its {HeaderSize} byte header.";
				return false;
			}

			var unkHash = new Hd2Hash( BinaryPrimitives.ReadUInt64LittleEndian( header ) );
			var modelCount = BinaryPrimitives.ReadUInt32LittleEndian( header.Slice( 8, 4 ) );
			var layoutListOffset = BinaryPrimitives.ReadUInt32LittleEndian( header.Slice( 12, 4 ) );

			if ( modelCount > MaxArrayCount )
			{
				error = $"the geometry group claims {modelCount} units, beyond the {MaxArrayCount} entry sanity limit.";
				return false;
			}

			// One entry per unit: an unused hash followed by the unit's own name hash.
			if ( !reader.TrySeek( HeaderSize ) ) { error = reader.Error; return false; }
			if ( !reader.TrySlice( reader.Offset, (int)modelCount * 16, out var unitData ) ) { error = reader.Error; return false; }

			var unitNames = new Hd2Hash[modelCount];
			for ( var i = 0; i < unitNames.Length; i++ )
				unitNames[i] = new Hd2Hash( BinaryPrimitives.ReadUInt64LittleEndian( unitData.Slice( i * 16 + 8, 8 ) ) );

			reader.TrySkip( (int)modelCount * 16 );

			if ( !reader.TrySlice( reader.Offset, (int)modelCount * 4, out var offsetData ) ) { error = reader.Error; return false; }

			var meshInfoOffsets = new uint[modelCount];
			for ( var i = 0; i < meshInfoOffsets.Length; i++ )
				meshInfoOffsets[i] = BinaryPrimitives.ReadUInt32LittleEndian( offsetData.Slice( i * 4, 4 ) );

			reader.TrySkip( (int)modelCount * 4 );

			var units = new Dictionary<Hd2Hash, Hd2GeometryGroupUnit>( unitNames.Length );
			for ( var i = 0; i < unitNames.Length; i++ )
			{
				if ( !TryReadUnit( ref reader, meshInfoOffsets[i], out var unit, out error ) ) return false;

				units[unitNames[i]] = unit;
			}

			if ( !TryReadMeshLayouts( ref reader, layoutListOffset, out var layouts, out error ) ) return false;

			group = new Hd2GeometryGroup( unkHash, layouts, units );
			return true;
		}
		catch ( Exception exception )
		{
			group = null;
			error = $"reading the geometry group payload failed: {exception.Message}";
			return false;
		}
	}

	static bool TryReadUnit( ref Hd2PayloadReader reader, uint unitOffset, out Hd2GeometryGroupUnit unit, out string error )
	{
		unit = null;
		error = null;

		if ( !reader.TrySeek( unitOffset ) ) { error = reader.Error; return false; }
		if ( !reader.TryReadUInt32( out var count ) ) { error = reader.Error; return false; }

		if ( count > MaxArrayCount )
		{
			error = $"a geometry group unit holds {count} meshes, beyond the {MaxArrayCount} entry sanity limit.";
			return false;
		}

		if ( !reader.TryReadThinHashes( reader.Offset, count, out var names ) ) { error = reader.Error; return false; }
		reader.TrySkip( names.Length * 4 );

		if ( !reader.TrySlice( reader.Offset, (int)count * 4, out var offsetData ) ) { error = reader.Error; return false; }

		var itemOffsets = new uint[count];
		for ( var i = 0; i < itemOffsets.Length; i++ )
			itemOffsets[i] = BinaryPrimitives.ReadUInt32LittleEndian( offsetData.Slice( i * 4, 4 ) );

		reader.TrySkip( (int)count * 4 );

		var meshes = new Hd2UnitMesh[count];
		for ( var i = 0; i < meshes.Length; i++ )
		{
			var itemOffset = (long)unitOffset + itemOffsets[i];

			if ( !reader.TrySlice( itemOffset, MeshInfoItemSize, out var item ) ) { error = reader.Error; return false; }

			var layoutIndex = BinaryPrimitives.ReadUInt32LittleEndian( item );
			var materialCount = BinaryPrimitives.ReadUInt32LittleEndian( item.Slice( 24, 4 ) );
			var materialOffset = BinaryPrimitives.ReadUInt32LittleEndian( item.Slice( 28, 4 ) );
			var groupCount = BinaryPrimitives.ReadUInt32LittleEndian( item.Slice( 40, 4 ) );
			var groupOffset = BinaryPrimitives.ReadUInt32LittleEndian( item.Slice( 44, 4 ) );

			if ( layoutIndex > int.MaxValue )
			{
				error = $"mesh {i} of a geometry group unit declares layout index {layoutIndex}, which is out of range.";
				return false;
			}

			if ( !reader.TryReadThinHashes( itemOffset + materialOffset, materialCount, out var materials ) ) { error = reader.Error; return false; }
			if ( !reader.TryReadMeshGroups( itemOffset + groupOffset, groupCount, out var groups ) ) { error = reader.Error; return false; }

			meshes[i] = new Hd2UnitMesh(
				Hd2MeshHeader.Synthetic( names[i], (int)layoutIndex, default, 0, Hd2MeshHeaderType.Unknown1 ),
				materials,
				groups );
		}

		unit = new Hd2GeometryGroupUnit( names, meshes );
		return true;
	}

	static bool TryReadMeshLayouts( ref Hd2PayloadReader reader, uint listOffset, out Hd2MeshLayout[] layouts, out string error )
	{
		layouts = [];
		error = null;

		if ( listOffset == 0 ) return true;

		if ( !reader.TrySeek( listOffset ) ) { error = reader.Error; return false; }
		if ( !reader.TryReadUInt32( out var count ) ) { error = reader.Error; return false; }

		if ( count > MaxArrayCount )
		{
			error = $"a geometry group holds {count} mesh layouts, beyond the {MaxArrayCount} entry sanity limit.";
			return false;
		}

		if ( !reader.TrySlice( reader.Offset, (int)count * 4, out var offsetData ) ) { error = reader.Error; return false; }

		var offsets = new uint[count];
		for ( var i = 0; i < offsets.Length; i++ )
			offsets[i] = BinaryPrimitives.ReadUInt32LittleEndian( offsetData.Slice( i * 4, 4 ) );

		reader.TrySkip( (int)count * 4 );

		layouts = new Hd2MeshLayout[count];
		for ( var i = 0; i < layouts.Length; i++ )
		{
			if ( !reader.TrySeek( (long)listOffset + offsets[i] ) ) { error = reader.Error; return false; }
			if ( !Hd2MeshLayout.TryRead( ref reader, out layouts[i] ) ) { error = reader.Error; return false; }
		}

		return true;
	}
}
