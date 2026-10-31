namespace Sandbox.Mounting.Helldivers2;

/// <summary>
/// What the reader could establish about one <c>texture</c> resource: the streamable header in
/// front of the image, the DDS header itself, how many bytes each data stream contributed and
/// whether those bytes add up to a complete image.
/// </summary>
internal sealed class Hd2TextureInfo
{
	public Hd2TextureHeader Stingray { get; }

	public Hd2DdsHeader Dds { get; }

	public int MainLength { get; }

	public int StreamLength { get; }

	public int GpuLength { get; }

	/// <summary>Bytes of DDS image available across the three streams, minus the DDS header.</summary>
	public long PixelBytesAvailable { get; }

	/// <summary>Bytes the mip chain occupies according to the DDS header, or -1 when unsized.</summary>
	public long PixelBytesExpected { get; }

	public Hd2TextureInfo( Hd2TextureHeader stingray, Hd2DdsHeader dds, int mainLength, int streamLength, int gpuLength )
	{
		Stingray = stingray;
		Dds = dds;
		MainLength = mainLength;
		StreamLength = streamLength;
		GpuLength = gpuLength;

		PixelBytesExpected = dds.TryGetPixelBytes( out var expected ) ? expected : -1;
		PixelBytesAvailable = (long)mainLength - Hd2TextureHeader.HeaderSize + streamLength + gpuLength - dds.HeaderBytes;
	}

	/// <summary>True when the streams hold exactly the bytes the DDS header describes.</summary>
	public bool IsComplete => PixelBytesExpected >= 0 && PixelBytesAvailable == PixelBytesExpected;

	/// <summary>A short verdict on the completeness of the pixel data.</summary>
	public string Status
	{
		get
		{
			if ( PixelBytesExpected < 0 ) return "pixel size unverified";
			if ( PixelBytesAvailable == PixelBytesExpected ) return "complete";
			if ( PixelBytesAvailable < PixelBytesExpected ) return $"truncated by {PixelBytesExpected - PixelBytesAvailable:n0} bytes";

			return $"{PixelBytesAvailable - PixelBytesExpected:n0} bytes beyond the mip chain";
		}
	}

	public override string ToString() => $"{Dds}; {PixelBytesAvailable:n0}/{PixelBytesExpected:n0} pixel bytes ({Status}) "
		+ $"from {MainLength:n0}/{StreamLength:n0}/{GpuLength:n0} bytes of main/stream/gpu data, DDS header {Dds.HeaderBytes} bytes; {Stingray}";
}
