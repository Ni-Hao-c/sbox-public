namespace Sandbox.Mounting.Helldivers2;

/// <summary>
/// The fixed 136-byte header at the start of a <c>material</c> resource's main payload. What
/// follows it is a texture slot table (thin hash keys, then the full hashes of the named
/// <c>texture</c> resources), a definitions table of 20 bytes per setting, and the float blobs
/// those definitions point at.
/// </summary>
internal readonly struct Hd2MaterialHeader
{
	public const int StoredSize = 136;

	/// <summary>The value the four bytes at 0x14 carry in every material examined so far.</summary>
	public const uint ExpectedPrefix = 0x18;

	// UNVERIFIED: SectionMainSize's exact meaning. filediver records it as "payload size - 0x18",
	// which does not hold for Helldivers 2: measured over 200 shipped materials it is always the
	// payload size minus a 24 to 36 byte trailer, and it is smaller than the header plus both tables
	// for 175 of them. Nothing here depends on it; SectionGpuSize, by contrast, matched the file
	// table's GPU stream size for all 200 payloads.

	/// <summary>The main section size the header records; its exact meaning is unconfirmed.</summary>
	public readonly uint SectionMainSize;

	/// <summary>Bytes of the GPU stream; the compiled DXBC shaders live there.</summary>
	public readonly uint SectionGpuSize;

	/// <summary>The dword at 0x14; every Helldivers 2 material measured carries zero there, not the 0x18 filediver records.</summary>
	public readonly uint Prefix;

	/// <summary>The material this one inherits from, or zero for a root material.</summary>
	public readonly Hd2Hash BaseMaterial;

	public readonly uint NumTextures;

	public readonly uint NumSettings;

	public Hd2MaterialHeader( uint sectionMainSize, uint sectionGpuSize, uint prefix, Hd2Hash baseMaterial, uint numTextures, uint numSettings )
	{
		SectionMainSize = sectionMainSize;
		SectionGpuSize = sectionGpuSize;
		Prefix = prefix;
		BaseMaterial = baseMaterial;
		NumTextures = numTextures;
		NumSettings = numSettings;
	}

	/// <summary>
	/// Reads the header and checks that the two tables it describes fit inside the payload. Returns
	/// false with a readable reason instead of throwing.
	/// </summary>
	public static bool TryParse( ReadOnlySpan<byte> main, out Hd2MaterialHeader header, out string error )
	{
		header = default;
		error = null;

		if ( main.Length < StoredSize )
		{
			error = $"the main stream is {main.Length} bytes, too small for the {StoredSize} byte material header";
			return false;
		}

		var sectionMainSize = ReadU32( main, 12 );
		var sectionGpuSize = ReadU32( main, 16 );
		var prefix = ReadU32( main, 20 );
		var baseMaterial = new Hd2Hash( ReadU64( main, 24 ) );
		var numTextures = ReadU32( main, 64 );
		var numSettings = ReadU32( main, 104 );

		if ( numTextures > 4096 || numSettings > 4096 )
		{
			error = $"implausible material tables ({numTextures} texture slots, {numSettings} settings)";
			return false;
		}

		var required = StoredSize + (long)numTextures * 12 + (long)numSettings * 20;
		if ( required > main.Length )
		{
			error = $"the material header describes {numTextures} texture slots and {numSettings} settings needing {required:n0} bytes, but the main stream is {main.Length:n0} bytes";
			return false;
		}

		header = new Hd2MaterialHeader( sectionMainSize, sectionGpuSize, prefix, baseMaterial, numTextures, numSettings );
		return true;
	}

	/// <summary>True when the four bytes at 0x14 hold the constant value.</summary>
	public bool HasExpectedPrefix => Prefix == ExpectedPrefix;

	/// <summary>The base material hash, the way the containers print hashes.</summary>
	public string BaseMaterialText => $"0x{BaseMaterial}";

	/// <summary>True when this material inherits from another one rather than being a root.</summary>
	public bool HasBaseMaterial => BaseMaterial.Value != 0;

	static uint ReadU32( ReadOnlySpan<byte> data, int offset ) => System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian( data[offset..] );

	static ulong ReadU64( ReadOnlySpan<byte> data, int offset ) => System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian( data[offset..] );
}
