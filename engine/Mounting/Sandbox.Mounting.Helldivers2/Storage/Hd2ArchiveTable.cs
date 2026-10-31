namespace Sandbox.Mounting.Helldivers2;

/// <summary>Which of a resource's three data streams is being read.</summary>
internal enum Hd2DataType
{
	Main = 0,
	Stream = 1,
	Gpu = 2
}

/// <summary>One 32-byte entry of an archive's type table.</summary>
internal readonly struct Hd2TypeEntry
{
	public const int StoredSize = 32;

	public readonly Hd2Hash Name;
	public readonly uint Count;
	public readonly uint MainAlignment;
	public readonly uint GpuAlignment;

	public Hd2TypeEntry( ReadOnlySpan<byte> data )
	{
		Name = new Hd2Hash( System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian( data[8..] ) );
		Count = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian( data[16..] );
		MainAlignment = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian( data[24..] );
		GpuAlignment = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian( data[28..] );
	}
}

/// <summary>One 80-byte resource entry of an archive's file table.</summary>
internal readonly struct Hd2FileEntry
{
	public const int StoredSize = 80;

	public readonly Hd2Hash Name;
	public readonly Hd2Hash Type;
	public readonly ulong OffsetMain;
	public readonly ulong OffsetStream;
	public readonly ulong OffsetGpu;
	public readonly uint SizeMain;
	public readonly uint SizeStream;
	public readonly uint SizeGpu;
	public readonly uint Index;

	public Hd2FileEntry( ReadOnlySpan<byte> data )
	{
		// Layout: id, type, toc offset, stream offset, gpu offset, two unused u64s,
		// then the three sizes, two alignments and the sequential index.
		Name = new Hd2Hash( System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian( data ) );
		Type = new Hd2Hash( System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian( data[8..] ) );
		OffsetMain = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian( data[16..] );
		OffsetStream = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian( data[24..] );
		OffsetGpu = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian( data[32..] );
		SizeMain = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian( data[56..] );
		SizeStream = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian( data[60..] );
		SizeGpu = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian( data[64..] );
		Index = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian( data[76..] );
	}

	public ulong OffsetOf( Hd2DataType type ) => type switch
	{
		Hd2DataType.Main => OffsetMain,
		Hd2DataType.Stream => OffsetStream,
		_ => OffsetGpu
	};

	public uint SizeOf( Hd2DataType type ) => type switch
	{
		Hd2DataType.Main => SizeMain,
		Hd2DataType.Stream => SizeStream,
		_ => SizeGpu
	};

	public bool Has( Hd2DataType type ) => SizeOf( type ) > 0;

	public override string ToString() => $"{Type}:{Name} (main {SizeMain}, stream {SizeStream}, gpu {SizeGpu})";
}

/// <summary>
/// The table that every DSAR bundle holds at uncompressed offset 0: a header, a per-type
/// summary and one entry per resource. Resource and type identities are hashes; the container
/// itself carries no readable names.
/// </summary>
internal sealed class Hd2ArchiveTable
{
	public const int HeaderSize = 72;
	static readonly byte[] Magic = [0x11, 0x00, 0x00, 0xF0];

	public Hd2Hash Id { get; private set; }

	public uint NumTypes { get; private set; }

	public uint NumFiles { get; private set; }

	public ulong ApproxMainSize { get; private set; }

	public ulong ApproxGpuSize { get; private set; }

	public Hd2TypeEntry[] Types { get; private set; } = [];

	public Hd2FileEntry[] Files { get; private set; } = [];

	public static int TableSize( uint numTypes, uint numFiles ) =>
		HeaderSize + (int)numTypes * Hd2TypeEntry.StoredSize + (int)numFiles * Hd2FileEntry.StoredSize;

	public static bool TryReadHeader( ReadOnlySpan<byte> data, out uint numTypes, out uint numFiles )
	{
		numTypes = 0;
		numFiles = 0;

		if ( data.Length < HeaderSize ) return false;
		if ( data[0] != Magic[0] || data[1] != Magic[1] || data[2] != Magic[2] || data[3] != Magic[3] ) return false;

		numTypes = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian( data[4..] );
		numFiles = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian( data[8..] );
		return true;
	}

	public static bool TryParse( ReadOnlySpan<byte> data, Hd2Hash id, out Hd2ArchiveTable table, out string error )
	{
		table = null;
		error = null;

		if ( !TryReadHeader( data, out var numTypes, out var numFiles ) )
		{
			error = "missing archive magic 0x110000F0";
			return false;
		}

		var required = TableSize( numTypes, numFiles );
		if ( numTypes > 4096 || numFiles > 4_000_000 || data.Length < required )
		{
			error = $"implausible archive table ({numTypes} types, {numFiles} files, {data.Length} bytes available, {required} required)";
			return false;
		}

		var types = new Hd2TypeEntry[numTypes];
		var offset = HeaderSize;
		for ( var i = 0; i < types.Length; i++ )
		{
			types[i] = new Hd2TypeEntry( data.Slice( offset, Hd2TypeEntry.StoredSize ) );
			offset += Hd2TypeEntry.StoredSize;
		}

		var files = new Hd2FileEntry[numFiles];
		for ( var i = 0; i < files.Length; i++ )
		{
			files[i] = new Hd2FileEntry( data.Slice( offset, Hd2FileEntry.StoredSize ) );
			offset += Hd2FileEntry.StoredSize;
		}

		table = new Hd2ArchiveTable
		{
			Id = id,
			NumTypes = numTypes,
			NumFiles = numFiles,
			ApproxMainSize = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian( data[32..] ),
			ApproxGpuSize = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian( data[40..] ),
			Types = types,
			Files = files
		};

		return true;
	}
}
