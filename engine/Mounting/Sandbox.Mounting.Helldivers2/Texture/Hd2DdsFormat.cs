namespace Sandbox.Mounting.Helldivers2;

/// <summary>
/// The DDS pixel layouts this reader recognises. The names spell the DDS pixel format the way DDS
/// tooling does rather than quoting the DXGI enumeration, so a diagnostic reads like the format a
/// DDS viewer would report.
/// </summary>
internal enum Hd2DdsFormat
{
	Unknown = 0,

	/// <summary>Four colour channels, 8 bytes per 4x4 block (DXGI BC1, FourCC DXT1).</summary>
	BC1,
	/// <summary>Four colour channels with explicit alpha, 16 bytes per 4x4 block (DXGI BC2, FourCC DXT3).</summary>
	BC2,
	/// <summary>Four colour channels with interpolated alpha, 16 bytes per 4x4 block (DXGI BC3, FourCC DXT5).</summary>
	BC3,
	/// <summary>One channel, 8 bytes per 4x4 block (DXGI BC4, FourCC ATI1).</summary>
	BC4,
	/// <summary>Two channels, 16 bytes per 4x4 block (DXGI BC5, FourCC ATI2).</summary>
	BC5,
	/// <summary>HDR colour, 16 bytes per 4x4 block (DXGI BC6H).</summary>
	BC6H,
	/// <summary>Colour with alpha, 16 bytes per 4x4 block (DXGI BC7).</summary>
	BC7,

	/// <summary>Uncompressed 24-bit colour.</summary>
	RGB8,
	/// <summary>Uncompressed 24-bit colour with the blue component first.</summary>
	BGR8,
	/// <summary>Uncompressed 32-bit colour.</summary>
	RGBA8,
	/// <summary>Uncompressed 32-bit colour with the blue component first.</summary>
	BGRA8,
	/// <summary>Uncompressed 32-bit colour with the alpha bits unused.</summary>
	BGRX8,
	/// <summary>Uncompressed 16-bit 5-6-5 colour.</summary>
	RGB565,
	/// <summary>Uncompressed 16-bit 5-6-5 colour with the blue component first.</summary>
	BGR565,
	/// <summary>Uncompressed single 8-bit alpha channel.</summary>
	A8,
	/// <summary>Uncompressed single 8-bit luminance channel.</summary>
	I8,
	/// <summary>Uncompressed 8-bit luminance plus 8-bit alpha.</summary>
	IA88,
	/// <summary>Uncompressed 16-bit single channel.</summary>
	R16,
	/// <summary>Uncompressed 16-bit half-precision single channel.</summary>
	R16F,
	/// <summary>Uncompressed two 16-bit half-precision channels.</summary>
	RG16F,
	/// <summary>Uncompressed two 32-bit float channels.</summary>
	RG32F,
	/// <summary>Uncompressed 32-bit float single channel.</summary>
	R32F,
	/// <summary>Uncompressed four 16-bit integer channels.</summary>
	RGBA16,
	/// <summary>Uncompressed four 16-bit half-precision channels.</summary>
	RGBA16F,
	/// <summary>Uncompressed three 32-bit float channels.</summary>
	RGB32F,
	/// <summary>Uncompressed four 32-bit float channels.</summary>
	RGBA32F
}

/// <summary>
/// Size and identity helpers for <see cref="Hd2DdsFormat"/>: the numbers needed to walk a mip
/// chain, and the mapping from a DDS header's DXGI id or four character code onto a layout.
/// </summary>
internal static class Hd2DdsFormatExtensions
{
	/// <summary>Bytes one 4x4 block occupies, or zero for the uncompressed layouts.</summary>
	public static int BlockBytes( this Hd2DdsFormat format ) => format switch
	{
		Hd2DdsFormat.BC1 or Hd2DdsFormat.BC4 => 8,
		Hd2DdsFormat.BC2 or Hd2DdsFormat.BC3 or Hd2DdsFormat.BC5 or Hd2DdsFormat.BC6H or Hd2DdsFormat.BC7 => 16,
		_ => 0
	};

	/// <summary>Bits one pixel occupies, or zero for the block compressed layouts.</summary>
	public static int BitsPerPixel( this Hd2DdsFormat format ) => format switch
	{
		Hd2DdsFormat.RGB8 or Hd2DdsFormat.BGR8 or Hd2DdsFormat.RGB32F => 24,
		Hd2DdsFormat.RGBA8 or Hd2DdsFormat.BGRA8 or Hd2DdsFormat.BGRX8 or Hd2DdsFormat.RGBA32F or Hd2DdsFormat.RG32F => 32,
		Hd2DdsFormat.RGB565 or Hd2DdsFormat.BGR565 or Hd2DdsFormat.IA88 or Hd2DdsFormat.R16
			or Hd2DdsFormat.R16F or Hd2DdsFormat.RG16F or Hd2DdsFormat.RGBA16 or Hd2DdsFormat.RGBA16F => 16,
		Hd2DdsFormat.A8 or Hd2DdsFormat.I8 => 8,
		_ => 0
	};

	/// <summary>The layout's name, for diagnostics.</summary>
	public static string NameOf( this Hd2DdsFormat format ) => format switch
	{
		Hd2DdsFormat.BC1 => "BC1",
		Hd2DdsFormat.BC2 => "BC2",
		Hd2DdsFormat.BC3 => "BC3",
		Hd2DdsFormat.BC4 => "BC4",
		Hd2DdsFormat.BC5 => "BC5",
		Hd2DdsFormat.BC6H => "BC6H",
		Hd2DdsFormat.BC7 => "BC7",
		Hd2DdsFormat.RGB8 => "RGB8",
		Hd2DdsFormat.BGR8 => "BGR8",
		Hd2DdsFormat.RGBA8 => "RGBA8",
		Hd2DdsFormat.BGRA8 => "BGRA8",
		Hd2DdsFormat.BGRX8 => "BGRX8",
		Hd2DdsFormat.RGB565 => "RGB565",
		Hd2DdsFormat.BGR565 => "BGR565",
		Hd2DdsFormat.A8 => "A8",
		Hd2DdsFormat.I8 => "I8",
		Hd2DdsFormat.IA88 => "IA88",
		Hd2DdsFormat.R16 => "R16",
		Hd2DdsFormat.R16F => "R16F",
		Hd2DdsFormat.RG16F => "RG16F",
		Hd2DdsFormat.RG32F => "RG32F",
		Hd2DdsFormat.R32F => "R32F",
		Hd2DdsFormat.RGBA16 => "RGBA16",
		Hd2DdsFormat.RGBA16F => "RGBA16F",
		Hd2DdsFormat.RGB32F => "RGB32F",
		Hd2DdsFormat.RGBA32F => "RGBA32F",
		_ => "unknown"
	};

	/// <summary>
	/// Maps a <c>DDS_HEADER_DXT10</c> format id onto a layout. The BCn ids cover what Helldivers 2
	/// stores; the uncompressed ids cover the surface formats the engine can consume.
	/// </summary>
	public static bool TryFromDxgi( uint dxgi, out Hd2DdsFormat format )
	{
		switch ( dxgi )
		{
			case 71 or 72: format = Hd2DdsFormat.BC1; return true;
			case 74 or 75: format = Hd2DdsFormat.BC2; return true;
			case 77 or 78: format = Hd2DdsFormat.BC3; return true;
			case 80 or 81: format = Hd2DdsFormat.BC4; return true;
			case 83 or 84: format = Hd2DdsFormat.BC5; return true;
			case 95 or 96: format = Hd2DdsFormat.BC6H; return true;
			case 98 or 99: format = Hd2DdsFormat.BC7; return true;
			case 28 or 29: format = Hd2DdsFormat.RGBA8; return true;
			case 87 or 91: format = Hd2DdsFormat.BGRA8; return true;
			case 88 or 92 or 93: format = Hd2DdsFormat.BGRX8; return true;
			case 10: format = Hd2DdsFormat.RGBA16F; return true;
			case 11: format = Hd2DdsFormat.RGBA16; return true;
			case 2: format = Hd2DdsFormat.RGBA32F; return true;
			case 6: format = Hd2DdsFormat.RGB32F; return true;
			case 16: format = Hd2DdsFormat.RG32F; return true;
			case 41: format = Hd2DdsFormat.R32F; return true;
			case 54: format = Hd2DdsFormat.R16F; return true;
			case 56: format = Hd2DdsFormat.R16; return true;
			default: format = Hd2DdsFormat.Unknown; return false;
		}
	}

	/// <summary>Maps a legacy four character code onto a layout.</summary>
	public static bool TryFromFourCc( uint fourCc, out Hd2DdsFormat format )
	{
		switch ( fourCc )
		{
			case 0x31545844: format = Hd2DdsFormat.BC1; return true;
			case 0x32545844 or 0x33545844: format = Hd2DdsFormat.BC2; return true;
			case 0x34545844 or 0x35545844: format = Hd2DdsFormat.BC3; return true;
			case 0x31495441 or 0x55344342 or 0x53344342: format = Hd2DdsFormat.BC4; return true;
			case 0x32495441 or 0x55354342 or 0x53354342: format = Hd2DdsFormat.BC5; return true;
			default: format = Hd2DdsFormat.Unknown; return false;
		}
	}

	/// <summary>The DXGI spelling of a format id, for diagnostics.</summary>
	public static string DxgiName( uint dxgi ) => dxgi switch
	{
		2 => "R32G32B32A32Float",
		6 => "R32G32B32Float",
		10 => "R16G16B16A16Float",
		11 => "R16G16B16A16UNorm",
		16 => "R32G32Float",
		28 => "R8G8B8A8UNorm",
		29 => "R8G8B8A8UNormSRGB",
		41 => "R32Float",
		54 => "R16G16Float",
		56 => "R16UNorm",
		71 => "BC1UNorm",
		72 => "BC1UNormSRGB",
		74 => "BC2UNorm",
		75 => "BC2UNormSRGB",
		77 => "BC3UNorm",
		78 => "BC3UNormSRGB",
		80 => "BC4UNorm",
		81 => "BC4SNorm",
		83 => "BC5UNorm",
		84 => "BC5SNorm",
		87 => "B8G8R8A8UNorm",
		88 => "B8G8R8X8UNorm",
		91 => "B8G8R8A8UNormSRGB",
		92 => "B8G8R8X8Typeless",
		93 => "B8G8R8X8UNormSRGB",
		95 => "BC6HUF16",
		96 => "BC6HSF16",
		98 => "BC7UNorm",
		99 => "BC7UNormSRGB",
		_ => "unlisted"
	};

	/// <summary>The four characters of a four character code, for diagnostics.</summary>
	public static string FourCcName( uint fourCc )
	{
		var characters = new char[4];
		for ( var i = 0; i < characters.Length; i++ ) characters[i] = (char)( ( fourCc >> ( 8 * i ) ) & 0xff );

		return new string( characters );
	}
}
