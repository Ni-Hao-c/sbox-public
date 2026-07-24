using System.Buffers.Binary;

/// <summary>Creates a runtime texture directly from RPAK/STARPAK BC mip data.</summary>
static class RpakTextureDecoder
{
	static readonly Sandbox.Diagnostics.Logger Log = new( "Titanfall2Texture" );
	static long _createdTextures;
	static long _uploadedBytes;
	static long _failedTextures;

	public static bool TryCreate( RpakArchive archive, RpakAsset asset, out Texture texture, out string error )
	{
		texture = null;
		if ( !TryRead( archive, asset, out var decoded, out error ) )
		{
			System.Threading.Interlocked.Increment( ref _failedTextures );
			return false;
		}
		try
		{
			if ( decoded.IsCube )
			{
				var builder = Texture.CreateCube( decoded.Width, decoded.Height, decoded.Format )
					.WithMips( decoded.MipCount )
					.WithStaticUsage()
					.WithData( decoded.Data );
				if ( decoded.ArraySize > 6 ) builder = builder.WithArrayCount( decoded.ArraySize / 6 );
				texture = builder.Finish();
			}
			else if ( decoded.ArraySize > 1 )
			{
				texture = Texture.CreateArray( decoded.Width, decoded.Height, decoded.ArraySize, decoded.Format )
					.WithMips( decoded.MipCount )
					.WithStaticUsage()
					.WithData( decoded.Data )
					.Finish();
			}
			else
			{
				texture = Texture.Create( decoded.Width, decoded.Height, decoded.Format )
					.WithMips( decoded.MipCount )
					.WithStaticUsage()
					.WithData( decoded.Data )
					.Finish();
			}
			if ( !texture.IsValid() )
			{
				System.Threading.Interlocked.Increment( ref _failedTextures );
				error = "Runtime texture creation returned an invalid texture.";
				return false;
			}

			var created = System.Threading.Interlocked.Increment( ref _createdTextures );
			var uploaded = System.Threading.Interlocked.Add( ref _uploadedBytes, decoded.Data.LongLength );
			if ( created % 512 == 0 )
			{
				Log.Info( $"Titanfall 2 resident textures: {created} created, {uploaded / (1024.0 * 1024.0):0.0} MB uploaded, "
					+ $"maximum dimension {Titanfall2StreamingSettings.TextureMaxDimension}." );
			}
			return true;
		}
		catch ( Exception exception )
		{
			System.Threading.Interlocked.Increment( ref _failedTextures );
			error = exception.Message;
			texture = null;
			return false;
		}
	}

	internal static RpakTextureStatistics GetStatistics() => new(
		System.Threading.Interlocked.Read( ref _createdTextures ),
		System.Threading.Interlocked.Read( ref _uploadedBytes ),
		System.Threading.Interlocked.Read( ref _failedTextures ),
		Titanfall2StreamingSettings.TextureMaxDimension );

	public static bool TryRead( RpakArchive archive, RpakAsset asset, out RpakTextureData texture, out string error )
	{
		texture = default;
		error = null;
		if ( asset.Type != "txtr" || asset.Version != 8 )
		{
			error = $"Unsupported texture asset version {asset.Version}.";
			return false;
		}
		if ( !archive.TryReadAsset( asset, out var assetData, out error ) ) return false;
		var header = assetData.Header;
		if ( header.Length < 56 )
		{
			error = "Texture v8 header is truncated.";
			return false;
		}

		var width = BinaryPrimitives.ReadUInt16LittleEndian( header.AsSpan( 16, 2 ) );
		var height = BinaryPrimitives.ReadUInt16LittleEndian( header.AsSpan( 18, 2 ) );
		var depth = BinaryPrimitives.ReadUInt16LittleEndian( header.AsSpan( 20, 2 ) );
		var formatValue = BinaryPrimitives.ReadUInt16LittleEndian( header.AsSpan( 22, 2 ) );
		var optionalMipCount = header[29];
		var declaredArraySize = Math.Max( 1, (int)header[30] );
		var layerFlags = header[31];
		var isCube = (layerFlags & 2) != 0;
		var arraySize = isCube ? Math.Max( 6, declaredArraySize ) : declaredArraySize;
		var residentMipCount = header[33];
		var streamedMipCount = header[34];
		var totalMipCount = optionalMipCount + streamedMipCount + residentMipCount;
		if ( width == 0 || height == 0 || depth > 1 || (isCube && (width != height || arraySize % 6 != 0)) )
		{
			error = $"Unsupported texture dimensions/array: {width}x{height}x{depth}, array {arraySize}, layers 0x{layerFlags:X2}.";
			return false;
		}
		if ( totalMipCount <= 0 || totalMipCount > 16 )
		{
			error = $"Invalid texture mip count {totalMipCount}.";
			return false;
		}
		if ( !TryGetFormat( formatValue, out var imageFormat, out var bytesPerBlock, out var blockSize ) )
		{
			error = $"Unsupported Titanfall 2 texture format {formatValue}.";
			return false;
		}

		// Titanfall stores the smallest mip levels in the RPAK and the useful
		// medium/high levels in STARPAK. Runtime textures cannot request individual
		// mips after creation, so create a capped contiguous mip chain: all mips from
		// the configured largest dimension down to 1x1. This preserves texture detail
		// while avoiding a 2K/4K upload for every material on a map.
		var streamedCount = optionalMipCount + streamedMipCount;
		var baseLevel = 0;
		while ( baseLevel + 1 < totalMipCount
			&& Math.Max( Math.Max( 1, width >> baseLevel ), Math.Max( 1, height >> baseLevel ) ) > Titanfall2StreamingSettings.TextureMaxDimension )
		{
			baseLevel++;
		}

		var mips = new List<Mip>( totalMipCount - baseLevel );
		for ( var level = totalMipCount - 1; level >= baseLevel; level-- )
		{
			var mipWidth = Math.Max( 1, width >> level );
			var mipHeight = Math.Max( 1, height >> level );
			var blocksWide = Math.Max( 1, (mipWidth + blockSize - 1) / blockSize );
			var blocksHigh = Math.Max( 1, (mipHeight + blockSize - 1) / blockSize );
			var faceSize = checked(blocksWide * blocksHigh * bytesPerBlock);
			mips.Add( new Mip( faceSize, Align16( faceSize ), arraySize, level < streamedCount ) );
		}
		if ( arraySize > 1 && mips.Any( static mip => mip.Streamed ) )
		{
			error = "RPAK texture arrays/cubemaps with streamed mips are not supported by the Titanfall 2 format.";
			return false;
		}

		var residentBytesNeeded = mips.Where( static mip => !mip.Streamed ).Sum( static mip => mip.AlignedSize );
		var streamedBytesNeeded = mips.Where( static mip => mip.Streamed ).Sum( static mip => mip.AlignedSize );
		var residentBytes = Array.Empty<byte>();
		if ( residentBytesNeeded > 0 && !archive.TryReadPagePointer( asset.Data, residentBytesNeeded, out residentBytes, out error ) ) return false;
		var streamedBytes = Array.Empty<byte>();
		if ( streamedBytesNeeded > 0 && !TryReadStreamedMips( archive, asset, streamedBytesNeeded, out streamedBytes, out error ) ) return false;

		var totalBytes = mips.Sum( static mip => mip.Size );
		var packed = GC.AllocateUninitializedArray<byte>( totalBytes );
		var residentOffset = 0;
		var streamedOffset = 0;
		var destinationOffset = 0;
		foreach ( var mip in mips )
		{
			var source = mip.Streamed ? streamedBytes : residentBytes;
			var sourceOffset = mip.Streamed ? streamedOffset : residentOffset;
			for ( var surface = 0; surface < mip.SurfaceCount; surface++ )
			{
				Buffer.BlockCopy( source, sourceOffset + surface * mip.AlignedFaceSize,
					packed, destinationOffset + surface * mip.FaceSize, mip.FaceSize );
			}
			if ( mip.Streamed ) streamedOffset += mip.AlignedSize;
			else residentOffset += mip.AlignedSize;
			destinationOffset += mip.Size;
		}

		if ( formatValue == 44 )
		{
			var expanded = GC.AllocateUninitializedArray<byte>( packed.Length * 2 );
			for ( var sourceOffset = 0; sourceOffset < packed.Length; sourceOffset += 2 )
			{
				var targetOffset = sourceOffset * 2;
				expanded[targetOffset] = packed[sourceOffset];
				expanded[targetOffset + 1] = packed[sourceOffset + 1];
				expanded[targetOffset + 2] = 0;
				expanded[targetOffset + 3] = 255;
			}
			packed = expanded;
		}

		texture = new RpakTextureData(
			Math.Max( 1, width >> baseLevel ),
			Math.Max( 1, height >> baseLevel ),
			imageFormat,
			totalMipCount - baseLevel,
			arraySize,
			isCube,
			packed );
		return true;
	}

	static bool TryReadStreamedMips( RpakArchive archive, RpakAsset asset, int length, out byte[] bytes, out string error )
	{
		bytes = null;
		error = null;
		if ( asset.StarpakOffset < 0 )
		{
			error = "Texture requests STARPAK mips but has no STARPAK entry.";
			return false;
		}

		var streamIndex = (int)(asset.StarpakOffset & 0xFFF);
		var streamOffset = asset.StarpakOffset & ~0xFFFL;
		var streamPath = archive.GetStreamingFilePath( streamIndex );
		if ( string.IsNullOrWhiteSpace( streamPath ) )
		{
			error = $"STARPAK stream {streamIndex} could not be resolved.";
			return false;
		}
		if ( !StarPakIndex.TryGetEntrySize( streamPath, streamOffset, out var entrySize, out error ) ) return false;
		if ( length > entrySize )
		{
			error = $"STARPAK texture range needs {length} bytes but its entry contains {entrySize}.";
			return false;
		}
		return StarPakIndex.TryRead( streamPath, streamOffset, length, out bytes, out error );
	}

	static bool TryGetFormat( ushort format, out ImageFormat imageFormat, out int bytesPerBlock, out int blockSize )
	{
		(imageFormat, bytesPerBlock, blockSize) = format switch
		{
			0 or 1 => (ImageFormat.DXT1, 8, 4),
			2 or 3 => (ImageFormat.DXT3, 16, 4),
			4 or 5 => (ImageFormat.DXT5, 16, 4),
			6 or 7 => (ImageFormat.ATI1N, 8, 4),
			8 or 9 => (ImageFormat.ATI2N, 16, 4),
			10 or 11 => (ImageFormat.BC6H, 16, 4),
			12 or 13 => (ImageFormat.BC7, 16, 4),
			31 or 32 => (ImageFormat.RGBA8888, 4, 1),
			44 => (ImageFormat.RGBA8888, 2, 1),
			53 or 54 or 55 or 56 => (ImageFormat.I8, 1, 1),
			57 => (ImageFormat.A8, 1, 1),
			_ => (ImageFormat.None, 0, 0)
		};
		return imageFormat != ImageFormat.None;
	}

	static int Align16( int value ) => (value + 15) & ~15;
	readonly record struct Mip( int FaceSize, int AlignedFaceSize, int SurfaceCount, bool Streamed )
	{
		public int Size => checked(FaceSize * SurfaceCount);
		public int AlignedSize => checked(AlignedFaceSize * SurfaceCount);
	}
}

readonly record struct RpakTextureData(
	int Width,
	int Height,
	ImageFormat Format,
	int MipCount,
	int ArraySize,
	bool IsCube,
	byte[] Data );

readonly record struct RpakTextureStatistics(
	long Created,
	long UploadedBytes,
	long Failed,
	int MaximumDimension );
