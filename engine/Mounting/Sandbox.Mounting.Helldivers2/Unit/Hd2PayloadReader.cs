using System.Buffers.Binary;

namespace Sandbox.Mounting.Helldivers2;

/// <summary>
/// Bounds-checked little-endian reader over one Stingray resource payload. A payload is a flat
/// buffer whose sections are reached through offsets stored inside it, so a truncated or
/// unexpected resource can point anywhere; every read here is validated and reports a readable
/// reason through <see cref="Error"/> rather than throwing.
/// </summary>
internal ref struct Hd2PayloadReader
{
	/// <summary>Sanity limit for a count-prefixed array, so a bad count cannot ask for gigabytes.</summary>
	public const uint MaxArrayCount = 1u << 22;

	readonly ReadOnlySpan<byte> _data;

	public Hd2PayloadReader( ReadOnlySpan<byte> data )
	{
		_data = data;
		Offset = 0;
		Error = null;
	}

	/// <summary>Cursor into the payload, advanced by every sequential read.</summary>
	public int Offset { get; set; }

	public readonly int Length => _data.Length;

	/// <summary>The first failure seen, or null while everything read so far was in range.</summary>
	public string Error { get; private set; }

	/// <summary>Records a failure once and returns false, so callers can write <c>return reader.Fail( "..." )</c>.</summary>
	public bool Fail( string message )
	{
		Error ??= message;
		return false;
	}

	public bool TrySeek( long offset )
	{
		if ( offset < 0 || offset > _data.Length )
			return Fail( $"offset 0x{offset:x} is outside the {_data.Length} byte payload." );

		Offset = (int)offset;
		return true;
	}

	public bool TrySkip( int count ) => TrySeek( Offset + (long)count );

	/// <summary>Reads a span at an absolute offset without moving the cursor.</summary>
	public bool TrySlice( long offset, int count, out ReadOnlySpan<byte> slice )
	{
		slice = default;

		if ( count < 0 )
			return Fail( $"a negative read length ({count}) was requested." );

		if ( offset < 0 || offset + count > _data.Length )
			return Fail( $"reading {count} bytes at 0x{offset:x} runs past the {_data.Length} byte payload." );

		slice = _data.Slice( (int)offset, count );
		return true;
	}

	public bool TryReadByte( out byte value )
	{
		value = 0;

		if ( Offset + 1 > _data.Length )
			return Fail( $"reading a byte at 0x{Offset:x} runs past the {_data.Length} byte payload." );

		value = _data[Offset];
		Offset++;
		return true;
	}

	public bool TryReadUInt16( out ushort value )
	{
		value = 0;

		if ( !TrySlice( Offset, 2, out var slice ) ) return false;

		value = BinaryPrimitives.ReadUInt16LittleEndian( slice );
		Offset += 2;
		return true;
	}

	public bool TryReadUInt32( out uint value )
	{
		value = 0;

		if ( !TrySlice( Offset, 4, out var slice ) ) return false;

		value = BinaryPrimitives.ReadUInt32LittleEndian( slice );
		Offset += 4;
		return true;
	}

	public bool TryReadInt32( out int value )
	{
		value = 0;

		if ( !TryReadUInt32( out var raw ) ) return false;

		value = unchecked( (int)raw );
		return true;
	}

	public bool TryReadSingle( out float value )
	{
		value = 0;

		if ( !TrySlice( Offset, 4, out var slice ) ) return false;

		value = BinaryPrimitives.ReadSingleLittleEndian( slice );
		Offset += 4;
		return true;
	}

	public bool TryReadUInt64( out ulong value )
	{
		value = 0;

		if ( !TrySlice( Offset, 8, out var slice ) ) return false;

		value = BinaryPrimitives.ReadUInt64LittleEndian( slice );
		Offset += 8;
		return true;
	}

	public bool TryReadHash( out Hd2Hash value )
	{
		value = default;

		if ( !TryReadUInt64( out var raw ) ) return false;

		value = new Hd2Hash( raw );
		return true;
	}

	public bool TryReadThinHash( out Hd2ThinHash value )
	{
		value = default;

		if ( !TryReadUInt32( out var raw ) ) return false;

		value = new Hd2ThinHash( raw );
		return true;
	}

	/// <summary>Reads <paramref name="count"/> thin hashes at an absolute offset without moving the cursor.</summary>
	public bool TryReadThinHashes( long offset, uint count, out Hd2ThinHash[] hashes )
	{
		hashes = [];

		if ( count > MaxArrayCount )
			return Fail( $"{count} thin hashes is beyond the {MaxArrayCount} entry sanity limit." );

		// An empty list reads as empty whatever the offset says: some meshes carry no materials
		// at all and leave the offset field unset.
		if ( count == 0 ) return true;

		if ( !TrySlice( offset, (int)count * 4, out var data ) ) return false;

		hashes = new Hd2ThinHash[count];
		for ( var i = 0; i < hashes.Length; i++ )
			hashes[i] = new Hd2ThinHash( BinaryPrimitives.ReadUInt32LittleEndian( data.Slice( i * 4, 4 ) ) );

		return true;
	}

	/// <summary>Reads <paramref name="count"/> mesh groups at an absolute offset without moving the cursor.</summary>
	public bool TryReadMeshGroups( long offset, uint count, out Hd2MeshGroup[] groups )
	{
		groups = [];

		if ( count > MaxArrayCount )
			return Fail( $"{count} mesh groups is beyond the {MaxArrayCount} entry sanity limit." );

		// As with the material lists, a mesh can carry no groups and leave the offset unset.
		if ( count == 0 ) return true;

		if ( !TrySlice( offset, (int)count * Hd2MeshGroup.StoredSize, out var data ) ) return false;

		groups = new Hd2MeshGroup[count];
		for ( var i = 0; i < groups.Length; i++ )
			groups[i] = new Hd2MeshGroup( data.Slice( i * Hd2MeshGroup.StoredSize, Hd2MeshGroup.StoredSize ) );

		return true;
	}
}
