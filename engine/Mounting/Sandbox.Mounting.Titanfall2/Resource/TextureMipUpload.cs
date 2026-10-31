/// <summary>Adapts authored BC mip chains to the native runtime upload constraints.</summary>
static class TextureMipUpload
{
	// The current native uploader leaves BC mips smaller than a full 4x4 block
	// uninitialized. This affects both ordinary textures and cubemap faces.
	// Preserve the source/cache data and stop GPU sampling at valid authored mips.
	internal static (int MipCount, byte[] Data) Prepare(
		int width, int height, ImageFormat format, int mipCount, int surfaceCount, byte[] data )
	{
		var bytesPerBlock = format switch
		{
			ImageFormat.DXT1 or ImageFormat.DXT1_ONEBITALPHA or ImageFormat.ATI1N => 8,
			ImageFormat.DXT3 or ImageFormat.DXT5 or ImageFormat.ATI2N or ImageFormat.BC6H or ImageFormat.BC7 => 16,
			_ => 0
		};
		if ( bytesPerBlock == 0 || width < 4 || height < 4 ) return (mipCount, data);
		var retained = 1;
		while ( retained < mipCount && (width >> retained) >= 4 && (height >> retained) >= 4 ) retained++;
		if ( retained == mipCount ) return (mipCount, data);
		var skippedBytes = 0;
		for ( var level = retained; level < mipCount; level++ )
		{
			var mipWidth = Math.Max( 1, width >> level );
			var mipHeight = Math.Max( 1, height >> level );
			skippedBytes = checked(skippedBytes + Math.Max( 1, (mipWidth + 3) / 4 )
				* Math.Max( 1, (mipHeight + 3) / 4 ) * bytesPerBlock * surfaceCount);
		}
		return (retained, data.AsSpan( skippedBytes ).ToArray());
	}
}
