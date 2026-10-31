namespace Sandbox.Mounting.Helldivers2;

/// <summary>
/// The DDS header, plus the optional <c>DDS_HEADER_DXT10</c> extension, that a Stingray
/// <c>texture</c> payload carries from offset <see cref="Hd2TextureHeader.HeaderSize"/> onwards.
/// Parsing never throws: malformed headers come back as a readable error.
/// </summary>
internal readonly struct Hd2DdsHeader
{
	public const int MagicSize = 4;
	public const int DirectorySize = 124;
	public const int Dxt10Size = 20;

	/// <summary>Bytes a DDS file spends on its magic and directory.</summary>
	public const int SizeWithoutDxt10 = MagicSize + DirectorySize;

	/// <summary>Bytes a DX10 DDS file spends on its magic, directory and header extension.</summary>
	public const int SizeWithDxt10 = SizeWithoutDxt10 + Dxt10Size;

	const uint MagicValue = 0x20534444;
	const uint FourCcDx10 = 0x30315844;
	const uint FlagCaps = 0x1;
	const uint FlagHeight = 0x2;
	const uint FlagWidth = 0x4;
	const uint FlagPixelFormat = 0x1000;
	const uint FlagMipMapCount = 0x20000;
	const uint FlagDepth = 0x800000;
	const uint PixelFormatFourCc = 0x4;
	const uint PixelFormatRgb = 0x40;
	const uint PixelFormatAlphaOnly = 0x2;
	const uint PixelFormatLuminance = 0x20000;
	const uint Caps2Cubemap = 1u << 9;
	const uint Caps2Volume = 1u << 21;
	const uint Dxt10MiscTextureCube = 0x4;

	public readonly int Width;
	public readonly int Height;
	public readonly int Depth;
	public readonly int MipCount;
	public readonly int ArraySize;

	/// <summary>Bytes of the payload the header itself occupies: 128, or 148 with a DXT10 extension.</summary>
	public readonly int HeaderBytes;

	public readonly bool IsCubemap;
	public readonly bool IsVolume;
	public readonly bool IsDx10;

	/// <summary>The DXGI format id of a DX10 header, or zero.</summary>
	public readonly uint DxgiFormat;

	/// <summary>The four character code of a legacy header, or zero.</summary>
	public readonly uint FourCc;

	public readonly Hd2DdsFormat Format;

	/// <summary>The format as the DDS header spells it, for diagnostics.</summary>
	public readonly string FormatName;

	public Hd2DdsHeader( int width, int height, int depth, int mipCount, int arraySize, int headerBytes, bool isCubemap, bool isVolume, bool isDx10, uint dxgiFormat, uint fourCc, Hd2DdsFormat format, string formatName )
	{
		Width = width;
		Height = height;
		Depth = depth;
		MipCount = mipCount;
		ArraySize = arraySize;
		HeaderBytes = headerBytes;
		IsCubemap = isCubemap;
		IsVolume = isVolume;
		IsDx10 = isDx10;
		DxgiFormat = dxgiFormat;
		FourCc = fourCc;
		Format = format;
		FormatName = formatName;
	}

	public static bool TryParse( ReadOnlySpan<byte> data, out Hd2DdsHeader header, out string error )
	{
		header = default;
		error = null;

		if ( data.Length < SizeWithoutDxt10 )
		{
			error = $"only {data.Length} bytes are available for a DDS header, which needs at least {SizeWithoutDxt10}";
			return false;
		}

		if ( ReadU32( data, 0 ) != MagicValue )
		{
			error = "missing the DDS magic 'DDS '";
			return false;
		}

		var directorySize = ReadU32( data, 4 );
		if ( directorySize != DirectorySize )
		{
			error = $"unexpected DDS directory size {directorySize}, expected {DirectorySize}";
			return false;
		}

		var flags = ReadU32( data, 8 );
		if ( ( flags & FlagCaps ) == 0 || ( flags & FlagHeight ) == 0 || ( flags & FlagWidth ) == 0 || ( flags & FlagPixelFormat ) == 0 )
		{
			error = "the DDS header is missing its required caps, height, width or pixel format flag";
			return false;
		}

		var height = ReadU32( data, 12 );
		var width = ReadU32( data, 16 );
		var depth = ( flags & FlagDepth ) != 0 ? ReadU32( data, 24 ) : 1u;
		var mipCount = ( flags & FlagMipMapCount ) != 0 ? ReadU32( data, 28 ) : 1u;

		if ( width == 0 || height == 0 || width > 32768 || height > 32768 )
		{
			error = $"implausible DDS dimensions {width}x{height}";
			return false;
		}

		if ( mipCount == 0 || mipCount > 32 )
		{
			error = $"implausible DDS mip count {mipCount}";
			return false;
		}

		var pixelFormatSize = ReadU32( data, 76 );
		if ( pixelFormatSize != 32 )
		{
			error = $"unexpected DDS pixel format size {pixelFormatSize}, expected 32";
			return false;
		}

		var pixelFormatFlags = ReadU32( data, 80 );
		var fourCc = ReadU32( data, 84 );
		var bitCount = ReadU32( data, 88 );
		var caps2 = ReadU32( data, 112 );

		var isCubemap = ( caps2 & Caps2Cubemap ) != 0;
		var isVolume = ( caps2 & Caps2Volume ) != 0 && depth > 1;
		var headerBytes = SizeWithoutDxt10;
		var isDx10 = false;
		var dxgi = 0u;
		var arraySize = 1;

		if ( ( pixelFormatFlags & PixelFormatFourCc ) != 0 && fourCc == FourCcDx10 )
		{
			if ( data.Length < SizeWithDxt10 )
			{
				error = $"the DDS file declares a DXT10 header but only {data.Length} bytes are available";
				return false;
			}

			isDx10 = true;
			headerBytes = SizeWithDxt10;
			dxgi = ReadU32( data, 128 );
			var dimension = ReadU32( data, 132 );
			var miscFlag = ReadU32( data, 136 );
			arraySize = (int)ReadU32( data, 140 );

			// 1 is a buffer, 2 a 1D texture, 3 a 2D texture and 4 a volume texture.
			if ( dimension is < 3 or > 4 )
			{
				error = $"unsupported DDS resource dimension {dimension}; only 2D and 3D textures are handled";
				return false;
			}

			if ( arraySize < 1 || arraySize > 4096 )
			{
				error = $"implausible DDS array size {arraySize}";
				return false;
			}

			if ( ( miscFlag & Dxt10MiscTextureCube ) != 0 ) isCubemap = true;
		}

		if ( !TryIdentify( isDx10, dxgi, pixelFormatFlags, fourCc, bitCount, data, out var format ) ) format = Hd2DdsFormat.Unknown;

		header = new Hd2DdsHeader(
			(int)width,
			(int)height,
			(int)( isVolume ? depth : 1 ),
			(int)mipCount,
			arraySize,
			headerBytes,
			isCubemap,
			isVolume,
			isDx10,
			dxgi,
			fourCc,
			format,
			FormatNameOf( isDx10, dxgi, pixelFormatFlags, fourCc, format ) );

		return true;
	}

	/// <summary>
	/// Total bytes every mip of every array element or cube face occupies, or false when the pixel
	/// layout is one this reader cannot size.
	/// </summary>
	public bool TryGetPixelBytes( out long bytes )
	{
		bytes = 0;

		var blockBytes = Format.BlockBytes();
		var bitsPerPixel = Format.BitsPerPixel();
		if ( blockBytes == 0 && bitsPerPixel == 0 ) return false;

		var elements = IsCubemap ? ArraySize * 6 : ( IsVolume ? 1 : ArraySize );

		var width = Width;
		var height = Height;
		var depth = IsVolume ? Depth : 1;
		var total = 0L;

		for ( var mip = 0; mip < MipCount; mip++ )
		{
			var level = blockBytes > 0
				? (long)Math.Max( 1, ( width + 3 ) / 4 ) * Math.Max( 1, ( height + 3 ) / 4 ) * blockBytes
				: (long)width * height * bitsPerPixel / 8;

			total += level * depth;

			width = Math.Max( 1, width >> 1 );
			height = Math.Max( 1, height >> 1 );
			depth = Math.Max( 1, depth >> 1 );
		}

		bytes = total * elements;
		return true;
	}

	static bool TryIdentify( bool isDx10, uint dxgi, uint pixelFormatFlags, uint fourCc, uint bitCount, ReadOnlySpan<byte> data, out Hd2DdsFormat format )
	{
		if ( isDx10 ) return Hd2DdsFormatExtensions.TryFromDxgi( dxgi, out format );

		if ( ( pixelFormatFlags & PixelFormatFourCc ) != 0 ) return Hd2DdsFormatExtensions.TryFromFourCc( fourCc, out format );

		if ( ( pixelFormatFlags & PixelFormatRgb ) != 0 )
		{
			var redMask = ReadU32( data, 92 );
			var blueMask = ReadU32( data, 100 );
			var alphaMask = ReadU32( data, 104 );

			switch ( bitCount )
			{
				case 32 when redMask == 0x000000ff && blueMask == 0x00ff0000: format = Hd2DdsFormat.RGBA8; return true;
				case 32 when redMask == 0x00ff0000 && blueMask == 0x000000ff && alphaMask == 0: format = Hd2DdsFormat.BGRX8; return true;
				case 32 when redMask == 0x00ff0000 && blueMask == 0x000000ff: format = Hd2DdsFormat.BGRA8; return true;
				case 32: format = Hd2DdsFormat.RGBA8; return true;
				case 24 when redMask == 0x000000ff: format = Hd2DdsFormat.RGB8; return true;
				case 24: format = Hd2DdsFormat.BGR8; return true;
				case 16 when redMask == 0xf800: format = Hd2DdsFormat.RGB565; return true;
				case 16: format = Hd2DdsFormat.BGR565; return true;
				default: format = Hd2DdsFormat.Unknown; return false;
			}
		}

		if ( ( pixelFormatFlags & PixelFormatAlphaOnly ) != 0 && bitCount == 8 )
		{
			format = Hd2DdsFormat.A8;
			return true;
		}

		if ( ( pixelFormatFlags & PixelFormatLuminance ) != 0 )
		{
			if ( bitCount == 16 )
			{
				format = Hd2DdsFormat.IA88;
				return true;
			}

			if ( bitCount == 8 )
			{
				format = Hd2DdsFormat.I8;
				return true;
			}
		}

		format = Hd2DdsFormat.Unknown;
		return false;
	}

	static string FormatNameOf( bool isDx10, uint dxgi, uint pixelFormatFlags, uint fourCc, Hd2DdsFormat format )
	{
		if ( isDx10 ) return $"DXGI {dxgi} {Hd2DdsFormatExtensions.DxgiName( dxgi )}";
		if ( ( pixelFormatFlags & PixelFormatFourCc ) != 0 ) return $"FourCC {Hd2DdsFormatExtensions.FourCcName( fourCc )}";

		return $"uncompressed {format.NameOf()}";
	}

	static uint ReadU32( ReadOnlySpan<byte> data, int offset ) => System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian( data[offset..] );

	public override string ToString() => $"{Width}x{Height}{( IsVolume ? $"x{Depth}" : "" )} {FormatName}, {MipCount} mip(s), {ArraySize} array(s){( IsCubemap ? ", cubemap" : "" )}";
}
