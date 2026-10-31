namespace Sandbox.Mounting.Helldivers2;

/// <summary>One 16-byte DSAA pointer into a <c>bundles.NN.nxa</c> file.</summary>
internal readonly struct Hd2DsaaEntry
{
	public const int StoredSize = 16;

	/// <summary>Offset of this run inside the logical archive file it belongs to.</summary>
	public readonly uint ArchiveOffset;

	/// <summary>Offset of the same run inside the concatenated uncompressed space of one .nxa file.</summary>
	public readonly uint UncompressedBundleOffset;

	/// <summary>Which .nxa file (index into <see cref="Hd2Dsaa.NxaFilenames"/>) holds this run.</summary>
	public readonly byte BundleIndex;

	public Hd2DsaaEntry( ReadOnlySpan<byte> data )
	{
		ArchiveOffset = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian( data );
		UncompressedBundleOffset = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian( data[8..] );
		BundleIndex = data[15];
	}
}

/// <summary>One logical archive file: a 16-hex bundle name plus the runs that make it up.</summary>
internal sealed class Hd2DsaaArchiveItem
{
	public ulong Size;
	public string Filename;
	public Hd2DsaaEntry[] Entries;

	/// <summary>Archive name with any data-type extension removed.</summary>
	public string BundleName;
}

/// <summary>
/// The index stored inside <c>bundles.nxa</c>: it maps every logical archive (main, .stream and
/// .gpu_resources) onto runs inside the 30 multi-gigabyte <c>bundles.NN.nxa</c> files.
/// </summary>
internal sealed class Hd2Dsaa
{
	public string[] NxaFilenames = [];
	public Hd2DsaaArchiveItem[] Archives = [];
	public uint UnkHeader0;
	public uint UnkHeader1;

	public static bool TryParse( byte[] data, out Hd2Dsaa dsaa, out string error )
	{
		dsaa = null;
		error = null;

		try
		{
			if ( data.Length < 24 || data[0] != (byte)'D' || data[1] != (byte)'S' || data[2] != (byte)'A' || data[3] != (byte)'A' )
			{
				error = "missing DSAA magic";
				return false;
			}

			var unk0 = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian( data.AsSpan( 4 ) );
			var unk1 = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian( data.AsSpan( 8 ) );
			var filenameCount = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian( data.AsSpan( 12 ) );
			var archiveCount = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian( data.AsSpan( 16 ) );

			var offset = 24;
			var itemHeaders = new (ulong Size, uint FilenameOffset, uint EntriesCount, ulong EntriesOffset)[archiveCount];
			for ( var i = 0; i < archiveCount; i++ )
			{
				if ( offset + 24 > data.Length )
				{
					error = "truncated DSAA archive table";
					return false;
				}

				var span = data.AsSpan( offset );
				itemHeaders[i] = (
					System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian( span ),
					System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian( span[8..] ),
					System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian( span[12..] ),
					System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian( span[16..] ) );
				offset += 24;
			}

			var filenameOffsets = new uint[filenameCount];
			for ( var i = 0; i < filenameCount; i++ )
			{
				if ( offset + 4 > data.Length )
				{
					error = "truncated DSAA filename table";
					return false;
				}

				filenameOffsets[i] = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian( data.AsSpan( offset ) );
				offset += 4;
			}

			var result = new Hd2Dsaa
			{
				UnkHeader0 = unk0,
				UnkHeader1 = unk1,
				Archives = new Hd2DsaaArchiveItem[archiveCount],
				NxaFilenames = new string[filenameCount]
			};

			for ( var i = 0; i < archiveCount; i++ )
			{
				var (size, filenameOffset, entriesCount, entriesOffset) = itemHeaders[i];
				var filename = ReadNullTerminated( data, filenameOffset );

				if ( entriesOffset + (ulong)entriesCount * Hd2DsaaEntry.StoredSize > (ulong)data.Length )
				{
					error = $"DSAA archive '{filename}' has an entry table past the end of the index";
					return false;
				}

				var entries = new Hd2DsaaEntry[entriesCount];
				for ( var entry = 0; entry < entries.Length; entry++ )
					entries[entry] = new Hd2DsaaEntry( data.AsSpan( (int)entriesOffset + entry * Hd2DsaaEntry.StoredSize, Hd2DsaaEntry.StoredSize ) );

				var extension = System.IO.Path.GetExtension( filename );
				result.Archives[i] = new Hd2DsaaArchiveItem
				{
					Size = size,
					Filename = filename,
					Entries = entries,
					BundleName = extension.Length > 0 ? filename[..^extension.Length] : filename
				};
			}

			for ( var i = 0; i < filenameOffsets.Length; i++ )
				result.NxaFilenames[i] = ReadNullTerminated( data, filenameOffsets[i] );

			dsaa = result;
			return true;
		}
		catch ( Exception exception )
		{
			error = exception.Message;
			return false;
		}
	}

	static string ReadNullTerminated( byte[] data, uint offset )
	{
		if ( offset >= data.Length ) return string.Empty;

		var end = (int)offset;
		while ( end < data.Length && data[end] != 0 ) end++;

		return System.Text.Encoding.ASCII.GetString( data, (int)offset, end - (int)offset );
	}
}
