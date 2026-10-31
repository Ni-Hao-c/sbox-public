namespace Sandbox.Mounting.Helldivers2;

/// <summary>
/// The 0xC0-byte Stingray wrapper in front of the DDS image inside a <c>texture</c> resource's
/// main payload: twelve unknown bytes followed by fifteen <see cref="Hd2TextureSection"/> records.
/// The DDS file therefore starts at <see cref="HeaderSize"/> of that payload, which is the offset
/// the reader uses when it stitches the resource's pixel streams back together.
/// </summary>
internal sealed class Hd2TextureHeader
{
	/// <summary>Size of the wrapper; the DDS payload begins here.</summary>
	public const int HeaderSize = 0xC0;

	/// <summary>How many streamable section records the wrapper always reserves.</summary>
	public const int MaxSections = 15;

	const int SectionTableOffset = 12;

	// UNVERIFIED: filediver parses these records but never consults them, and no locally readable
	// Helldivers 2 install was available when this was written, so what the offsets are relative to
	// (most likely the concatenated main/stream/gpu pixel stream) is unconfirmed. Decoding does not
	// depend on them; they are parsed and reported for diagnostics only.

	/// <summary>All fifteen records, in file order.</summary>
	public Hd2TextureSection[] Sections { get; }

	/// <summary>How many leading records carry data; the remainder is padding.</summary>
	public int SectionCount { get; }

	/// <summary>True when every record after <see cref="SectionCount"/> is empty.</summary>
	public bool SectionsArePrefix { get; }

	/// <summary>Sum of the sizes of the records that carry data.</summary>
	public uint SectionBytes { get; }

	/// <summary>Offset of the first record that carries data.</summary>
	public uint FirstSectionOffset { get; }

	/// <summary>True when each record starts exactly where the one before it ends.</summary>
	public bool SectionsAreContiguous { get; }

	public static bool TryParse( ReadOnlySpan<byte> main, out Hd2TextureHeader header, out string error )
	{
		header = null;
		error = null;

		if ( main.Length < HeaderSize )
		{
			error = $"the main stream is {main.Length} bytes, which is too small for the {HeaderSize} byte streamable texture header";
			return false;
		}

		var sections = new Hd2TextureSection[MaxSections];
		for ( var i = 0; i < MaxSections; i++ )
			sections[i] = new Hd2TextureSection( main.Slice( SectionTableOffset + i * Hd2TextureSection.StoredSize, Hd2TextureSection.StoredSize ) );

		var count = 0;
		while ( count < MaxSections && !sections[count].IsEmpty ) count++;

		var prefix = true;
		var contiguous = true;
		var bytes = 0u;
		var first = 0u;

		for ( var i = 0; i < MaxSections; i++ )
		{
			if ( i >= count )
			{
				if ( !sections[i].IsEmpty ) prefix = false;
				continue;
			}

			if ( i == 0 ) first = sections[i].Offset;
			else if ( sections[i].Offset != sections[i - 1].Offset + sections[i - 1].Size ) contiguous = false;

			bytes += sections[i].Size;
		}

		header = new Hd2TextureHeader( sections, count, prefix, bytes, first, contiguous );
		return true;
	}

	Hd2TextureHeader( Hd2TextureSection[] sections, int sectionCount, bool sectionsArePrefix, uint sectionBytes, uint firstSectionOffset, bool sectionsAreContiguous )
	{
		Sections = sections;
		SectionCount = sectionCount;
		SectionsArePrefix = sectionsArePrefix;
		SectionBytes = sectionBytes;
		FirstSectionOffset = firstSectionOffset;
		SectionsAreContiguous = sectionsAreContiguous;
	}

	public override string ToString() => $"{SectionCount}/{MaxSections} sections holding {SectionBytes:n0} bytes from 0x{FirstSectionOffset:x}"
		+ $", contiguous={SectionsAreContiguous}, prefix={SectionsArePrefix}";
}
