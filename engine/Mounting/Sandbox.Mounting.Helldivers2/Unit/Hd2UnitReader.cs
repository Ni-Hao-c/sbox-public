namespace Sandbox.Mounting.Helldivers2;

/// <summary>
/// Reads the Main payload of a Stingray <c>unit</c>: its vertex layouts, mesh headers (with their
/// material slots and groups), LOD groups and the thin-hash to <c>material</c> map. Vertices and
/// indices are not in this payload; they live in the resource's GPU stream and are read by
/// <see cref="Hd2MeshBuilder"/>.
/// </summary>
internal static class Hd2UnitReader
{
	const uint MaxArrayCount = Hd2PayloadReader.MaxArrayCount;

	/// <summary>All section offsets a unit header can carry are relative to the start of the payload.</summary>
	public static bool TryRead( ReadOnlySpan<byte> main, out Hd2Unit unit, out string error )
	{
		unit = null;
		error = null;

		try
		{
			var reader = new Hd2PayloadReader( main );
			if ( !Hd2UnitHeader.TryRead( ref reader, out var header ) )
			{
				error = $"the unit payload is {main.Length} bytes and cannot hold its {Hd2UnitHeader.StoredSize} byte header.";
				return false;
			}

			if ( !TryReadLodGroups( ref reader, header.LodGroupListOffset, out var lodGroups, out error ) ) return false;
			if ( !TryReadMeshLayouts( ref reader, header.MeshLayoutListOffset, out var layouts, out error ) ) return false;
			if ( !TryReadMeshes( ref reader, header, out var meshes, out var groupBones, out error ) ) return false;
			if ( !TryReadMaterialLookup( ref reader, header.MaterialListOffset, out var materials, out error ) ) return false;
			if ( !ValidateMeshData( ref reader, header.MeshDataOffset, meshes.Length, out error ) ) return false;

			unit = new Hd2Unit( header, layouts, meshes, lodGroups, groupBones, materials );
			return true;
		}
		catch ( Exception exception )
		{
			unit = null;
			error = $"reading the unit payload failed: {exception.Message}";
			return false;
		}
	}

	// UNVERIFIED: for a non-zero LOD the mesh is looked up through the LOD group whose entry
	// index lists mention the requested mesh, taking the first usable index of that group's
	// entry at the requested detail level. Nothing in the reference implementation interprets
	// those lists, so this resolution is a reading of the format rather than a verified fact;
	// it fails loudly instead of guessing when it cannot be resolved. LOD 0 is always the mesh
	// itself, which is the only case the reference implementation exercises.
	/// <summary>
	/// Resolves the mesh a caller asked for. <paramref name="lodIndex"/> 0 always means the mesh
	/// itself, whatever the LOD groups say.
	/// </summary>
	public static bool TryResolveMesh( Hd2Unit unit, int meshIndex, int lodIndex, out Hd2UnitMesh mesh, out string error )
	{
		mesh = null;
		error = null;

		if ( unit is null )
		{
			error = "no unit was supplied.";
			return false;
		}

		if ( (uint)meshIndex >= (uint)unit.Meshes.Length )
		{
			error = $"mesh index {meshIndex} is out of range of the unit's {unit.Meshes.Length} meshes.";
			return false;
		}

		mesh = unit.Meshes[meshIndex];
		if ( lodIndex <= 0 ) return true;

		foreach ( var group in unit.LodGroups )
		{
			if ( !GroupMentions( group, meshIndex ) ) continue;

			if ( lodIndex >= group.Entries.Length )
			{
				error = $"LOD {lodIndex} is out of range of the LOD group holding mesh {meshIndex}, which has {group.Entries.Length} detail levels.";
				return false;
			}

			foreach ( var candidate in group.Entries[lodIndex].MeshIndices )
			{
				if ( candidate >= (uint)unit.Meshes.Length ) continue;

				mesh = unit.Meshes[candidate];
				return true;
			}

			error = $"LOD {lodIndex} of the group holding mesh {meshIndex} lists no usable mesh index.";
			return false;
		}

		error = $"mesh {meshIndex} is not mentioned by any LOD group, so LOD {lodIndex} cannot be resolved; ask for LOD 0 instead.";
		return false;
	}

	/// <summary>
	/// Builds the mesh list of a unit whose geometry lives in a geometry group: the group supplies
	/// the layout, materials and groups, while bounds and their bone transform are taken from the
	/// unit's own mesh info list by matching mesh name hashes. The result is built against
	/// <see cref="Hd2GeometryGroup.MeshLayouts"/> and the geometry group's GPU stream.
	/// </summary>
	public static bool TryCreateGeometryGroupMeshes( Hd2Unit unit, Hd2GeometryGroup group, Hd2Hash unitName, out Hd2UnitMesh[] meshes, out string error )
	{
		meshes = [];
		error = null;

		if ( unit is null )
		{
			error = "no unit was supplied.";
			return false;
		}

		if ( group is null )
		{
			error = "no geometry group was supplied.";
			return false;
		}

		if ( !group.Units.TryGetValue( unitName, out var entry ) )
		{
			error = $"the geometry group does not contain unit {unitName}.";
			return false;
		}

		meshes = new Hd2UnitMesh[entry.Meshes.Length];
		for ( var i = 0; i < meshes.Length; i++ )
		{
			var source = entry.Meshes[i];
			var name = i < entry.MeshNames.Length ? entry.MeshNames[i] : source.Header.MeshName;

			var aabb = default( Hd2Aabb );
			var aabbTransformIndex = 0u;
			var type = Hd2MeshHeaderType.Unknown1;

			foreach ( var candidate in unit.Meshes )
			{
				if ( candidate.Header.MeshName != name ) continue;

				aabb = candidate.Header.Aabb;
				aabbTransformIndex = candidate.Header.AabbTransformIndex;
				type = candidate.Header.Type;
				break;
			}

			meshes[i] = new Hd2UnitMesh(
				Hd2MeshHeader.Synthetic( name, source.Header.LayoutIndex, aabb, aabbTransformIndex, type ),
				source.Materials,
				source.Groups );
		}

		return true;
	}

	/// <summary>
	/// Picks the mesh to show by default: the one with the most indices, ignoring meshes the
	/// format marks as non-renderable (header type 0x000). This is the reference implementation's
	/// own fallback for the units it cannot group into LODs by mesh name suffix, which is every
	/// unit here because Helldivers 2 ships no names, only hashes.
	/// </summary>
	public static bool TryFindHighestDetailMesh( Hd2Unit unit, out int meshIndex, out string error ) =>
		TryFindHighestDetailMesh( unit?.Meshes, out meshIndex, out error );

	/// <summary>Picks the mesh to show by default from a mesh list; see the unit overload.</summary>
	public static bool TryFindHighestDetailMesh( Hd2UnitMesh[] meshes, out int meshIndex, out string error )
	{
		meshIndex = -1;
		error = null;

		if ( meshes is null || meshes.Length == 0 )
		{
			error = "there are no meshes to choose from.";
			return false;
		}

		var best = -1L;
		for ( var i = 0; i < meshes.Length; i++ )
		{
			if ( meshes[i].Header.Type == Hd2MeshHeaderType.Unknown0 ) continue;

			var indices = meshes[i].IndexCount;
			if ( indices <= best ) continue;

			best = indices;
			meshIndex = i;
		}

		if ( meshIndex < 0 )
		{
			error = $"none of the {meshes.Length} meshes is renderable (all are header type 0x000).";
			return false;
		}

		return true;
	}

	/// <summary>A one-line summary of what a unit payload held, for mount self-tests.</summary>
	public static string Describe( Hd2Unit unit )
	{
		if ( unit is null ) return "no unit was parsed.";

		var lodEntries = 0;
		foreach ( var group in unit.LodGroups ) lodEntries += group.Entries.Length;

		var geometry = unit.UsesExternalGeometry
			? $"geometry group {unit.Header.GeometryGroup}"
			: "local geometry";

		return $"{unit.Meshes.Length} meshes, {unit.LodGroups.Length} LOD groups ({lodEntries} detail levels), "
			+ $"{unit.MeshLayouts.Length} layouts, {unit.TotalGroupCount} groups, {unit.TotalVertexCount} vertices, "
			+ $"{unit.TotalIndexCount} indices, {unit.MaterialLookup.Count} materials, {geometry}"
			+ (unit.HasTerrain ? ", terrain" : string.Empty);
	}

	/// <summary>A one-line summary of a geometry group payload, for mount self-tests.</summary>
	public static string Describe( Hd2GeometryGroup group )
	{
		if ( group is null ) return "no geometry group was parsed.";

		return $"{group.Units.Count} units, {group.TotalMeshCount} meshes, {group.MeshLayouts.Length} layouts, "
			+ $"{group.TotalVertexCount} vertices, unknown hash {group.UnkHash}";
	}

	static bool GroupMentions( Hd2LodGroup group, int meshIndex )
	{
		foreach ( var entry in group.Entries )
			foreach ( var candidate in entry.MeshIndices )
				if ( candidate == (uint)meshIndex ) return true;

		return false;
	}

	static bool TryReadLodGroups( ref Hd2PayloadReader reader, uint listOffset, out Hd2LodGroup[] groups, out string error )
	{
		groups = [];
		error = null;

		if ( listOffset == 0 ) return true;

		if ( !reader.TrySeek( listOffset ) ) { error = reader.Error; return false; }
		if ( !TryReadUInt32Array( ref reader, out var groupOffsets, out error ) ) return false;

		groups = new Hd2LodGroup[groupOffsets.Length];
		for ( var i = 0; i < groups.Length; i++ )
		{
			var groupOffset = (long)listOffset + groupOffsets[i];

			if ( !reader.TrySeek( groupOffset ) ) { error = reader.Error; return false; }
			if ( !reader.TryReadUInt32( out var unk00 ) || !reader.TryReadThinHash( out var name ) || !reader.TryReadUInt32( out var unk01 ) )
			{
				error = reader.Error;
				return false;
			}

			// The footer count comes first, then the entry count, then both offset lists back to
			// back. The reference implementation's type definitions carry a further "other count"
			// and offset list between them, but its parser leaves that field commented out, so an
			// other-count field here is UNVERIFIED. Following the reference exactly is what keeps
			// the offsets correct on real payloads.
			if ( !reader.TryReadUInt32( out var footerCount ) || !reader.TryReadUInt32( out var entryCount ) )
			{
				error = reader.Error;
				return false;
			}

			if ( entryCount > MaxArrayCount || footerCount > MaxArrayCount )
			{
				error = $"a LOD group declares {entryCount} detail levels and {footerCount} footers, beyond the {MaxArrayCount} entry sanity limit.";
				return false;
			}

			var entryOffsets = new uint[entryCount];
			for ( var j = 0; j < entryOffsets.Length; j++ )
				if ( !reader.TryReadUInt32( out entryOffsets[j] ) ) { error = reader.Error; return false; }

			var footerOffsets = new uint[footerCount];
			for ( var j = 0; j < footerOffsets.Length; j++ )
				if ( !reader.TryReadUInt32( out footerOffsets[j] ) ) { error = reader.Error; return false; }

			var entries = new Hd2LodEntry[entryCount];
			for ( var j = 0; j < entries.Length; j++ )
			{
				if ( !reader.TrySeek( groupOffset + entryOffsets[j] ) ) { error = reader.Error; return false; }
				if ( !reader.TryReadSingle( out var max ) || !reader.TryReadSingle( out var min ) ) { error = reader.Error; return false; }
				if ( !TryReadUInt32Array( ref reader, out var meshIndices, out error ) ) return false;

				entries[j] = new Hd2LodEntry( max, min, meshIndices );
			}

			var footers = new Hd2LodFooter[footerCount];
			for ( var j = 0; j < footers.Length; j++ )
			{
				if ( !reader.TrySeek( groupOffset + footerOffsets[j] ) ) { error = reader.Error; return false; }
				if ( !reader.TrySlice( reader.Offset, Hd2LodFooter.StoredSize, out var footer ) ) { error = reader.Error; return false; }

				footers[j] = new Hd2LodFooter( footer );
				reader.TrySkip( Hd2LodFooter.StoredSize );
			}

			groups[i] = new Hd2LodGroup( unk00, name, unk01, entries, footers );
		}

		return true;
	}

	static bool TryReadMeshLayouts( ref Hd2PayloadReader reader, uint listOffset, out Hd2MeshLayout[] layouts, out string error )
	{
		layouts = [];
		error = null;

		if ( listOffset == 0 ) return true;

		if ( !reader.TrySeek( listOffset ) ) { error = reader.Error; return false; }
		if ( !TryReadUInt32Array( ref reader, out var offsets, out error ) ) return false;

		layouts = new Hd2MeshLayout[offsets.Length];
		for ( var i = 0; i < layouts.Length; i++ )
		{
			if ( !reader.TrySeek( (long)listOffset + offsets[i] ) ) { error = reader.Error; return false; }
			if ( !Hd2MeshLayout.TryRead( ref reader, out layouts[i] ) ) { error = reader.Error; return false; }
		}

		return true;
	}

	static bool TryReadMeshes( ref Hd2PayloadReader reader, in Hd2UnitHeader header, out Hd2UnitMesh[] meshes, out Hd2ThinHash[] groupBones, out string error )
	{
		meshes = [];
		groupBones = [];
		error = null;

		var listOffset = header.MeshInfoListOffset;
		if ( listOffset == 0 ) return true;

		if ( !reader.TrySeek( listOffset ) ) { error = reader.Error; return false; }
		if ( !TryReadUInt32Array( ref reader, out var meshOffsets, out error ) ) return false;

		// One thin hash per mesh follows the offset list: the bone group the mesh binds to.
		if ( !reader.TryReadThinHashes( reader.Offset, (uint)meshOffsets.Length, out groupBones ) ) { error = reader.Error; return false; }
		reader.TrySkip( meshOffsets.Length * 4 );

		meshes = new Hd2UnitMesh[meshOffsets.Length];
		for ( var i = 0; i < meshes.Length; i++ )
		{
			var meshOffset = (long)listOffset + meshOffsets[i];

			if ( !reader.TrySlice( meshOffset, Hd2MeshHeader.StoredSize, out var headerData ) ) { error = reader.Error; return false; }
			var meshHeader = new Hd2MeshHeader( headerData );

			// Both lists are stored relative to the start of this mesh's header.
			if ( !reader.TryReadThinHashes( meshOffset + meshHeader.MaterialOffset, meshHeader.NumMaterials, out var materials ) ) { error = reader.Error; return false; }
			if ( !reader.TryReadMeshGroups( meshOffset + meshHeader.GroupOffset, meshHeader.NumGroups, out var groups ) ) { error = reader.Error; return false; }

			meshes[i] = new Hd2UnitMesh( meshHeader, materials, groups );
		}

		return true;
	}

	static bool TryReadMaterialLookup( ref Hd2PayloadReader reader, uint listOffset, out Dictionary<Hd2ThinHash, Hd2Hash> lookup, out string error )
	{
		lookup = new Dictionary<Hd2ThinHash, Hd2Hash>();
		error = null;

		if ( listOffset == 0 ) return true;

		if ( !reader.TrySeek( listOffset ) ) { error = reader.Error; return false; }
		if ( !reader.TryReadUInt32( out var count ) ) { error = reader.Error; return false; }

		if ( count > MaxArrayCount )
		{
			error = $"the material map holds {count} entries, beyond the {MaxArrayCount} entry sanity limit.";
			return false;
		}

		var keys = new Hd2ThinHash[count];
		for ( var i = 0; i < keys.Length; i++ )
			if ( !reader.TryReadThinHash( out keys[i] ) ) { error = reader.Error; return false; }

		var values = new Hd2Hash[count];
		for ( var i = 0; i < values.Length; i++ )
			if ( !reader.TryReadHash( out values[i] ) ) { error = reader.Error; return false; }

		for ( var i = 0; i < keys.Length; i++ ) lookup[keys[i]] = values[i];

		return true;
	}

	static bool ValidateMeshData( ref Hd2PayloadReader reader, uint meshDataOffset, int meshCount, out string error )
	{
		error = null;

		if ( meshDataOffset == 0 ) return true;

		if ( !reader.TrySeek( meshDataOffset ) ) { error = reader.Error; return false; }
		if ( !reader.TryReadUInt32( out var count ) ) { error = reader.Error; return false; }

		if ( count != (uint)meshCount )
		{
			error = $"the mesh data block lists {count} meshes but {meshCount} mesh infos were parsed.";
			return false;
		}

		return true;
	}

	/// <summary>Reads a count followed by that many uint32s from the reader's current offset.</summary>
	static bool TryReadUInt32Array( ref Hd2PayloadReader reader, out uint[] values, out string error )
	{
		values = [];
		error = null;

		if ( !reader.TryReadUInt32( out var count ) ) { error = reader.Error; return false; }

		if ( count > MaxArrayCount )
		{
			error = $"a list of {count} entries is beyond the {MaxArrayCount} entry sanity limit.";
			return false;
		}

		values = new uint[count];
		for ( var i = 0; i < values.Length; i++ )
			if ( !reader.TryReadUInt32( out values[i] ) ) { error = reader.Error; return false; }

		return true;
	}
}
