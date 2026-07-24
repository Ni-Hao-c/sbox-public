using System.Buffers.Binary;

/// <summary>Decodes Source/Respawn VTF 7.x images directly into s&amp;box runtime textures.</summary>
static class VtfTextureDecoder
{
	const uint Signature = 0x00465456;
	const uint TextureFlagEnvMap = 0x00004000;
	const int ResourceCountOffset = 68;
	const int ResourceEntriesOffset = 80;
	const uint HighResolutionImageResource = 0x30;
	const uint ParticleSheetResource = 0x10;

	public static bool IsVtf( ReadOnlySpan<byte> bytes ) => bytes.Length >= 4
		&& BinaryPrimitives.ReadUInt32LittleEndian( bytes ) == Signature;
	internal static bool IsCube( ReadOnlySpan<byte> bytes ) => bytes.Length >= 24 && IsVtf( bytes )
		&& (BinaryPrimitives.ReadUInt32LittleEndian( bytes.Slice( 20, 4 ) ) & TextureFlagEnvMap) != 0;

	internal static int GetFrameCount( ReadOnlySpan<byte> bytes ) =>
		bytes.Length >= 26 && IsVtf( bytes )
			? Math.Max( 1, (int)BinaryPrimitives.ReadUInt16LittleEndian( bytes.Slice( 24, 2 ) ) )
			: 0;

	public static bool TryCreate( byte[] bytes, bool animatedAtlas, out Texture texture, out string error )
	{
		texture = null;
		if ( IsCube( bytes ) ) return TryCreateCube( bytes, out texture, out error );
		if ( !TryRead( bytes, animatedAtlas, out var decoded, out error ) ) return false;

		try
		{
			texture = Texture.Create( decoded.Width, decoded.Height, decoded.Format )
				.WithMips( decoded.MipCount )
				.WithStaticUsage()
				.WithData( decoded.Data )
				.Finish();
			if ( texture is null || !texture.IsValid() )
			{
				error = "Runtime VTF texture creation returned an invalid texture.";
				texture = null;
				return false;
			}
			return true;
		}
		catch ( Exception exception )
		{
			error = exception.Message;
			texture = null;
			return false;
		}
	}

	internal static bool TryCreateCube( byte[] bytes, out Texture texture, out string error )
	{
		texture = null;
		if ( !TryReadCube( bytes, out var decoded, out error ) ) return false;
		try
		{
			texture = Texture.CreateCube( decoded.Width, decoded.Height, decoded.Format )
				.WithMips( decoded.MipCount )
				.WithStaticUsage()
				.WithData( decoded.Data )
				.Finish();
			if ( texture is null || !texture.IsValid() )
			{
				error = "Runtime VTF cubemap creation returned an invalid texture.";
				texture = null;
				return false;
			}
			return true;
		}
		catch ( Exception exception )
		{
			error = exception.Message;
			texture = null;
			return false;
		}
	}

	/// <summary>Creates one runtime cubemap for every frame in a map's cubemaps.hdr.vtf.</summary>
	internal static bool TryCreateCubeFrames( byte[] bytes, out Texture[] textures, out string error )
	{
		textures = Array.Empty<Texture>();
		var frameCount = GetFrameCount( bytes );
		if ( frameCount <= 0 || !IsCube( bytes ) )
		{
			error = "VTF does not contain cubemap frames.";
			return false;
		}

		var created = new List<Texture>( frameCount );
		try
		{
			for ( var frameIndex = 0; frameIndex < frameCount; frameIndex++ )
			{
				if ( !TryReadCubeFrame( bytes, frameIndex, out var decoded, out error ) )
					throw new InvalidDataException( error );
				var texture = Texture.CreateCube( decoded.Width, decoded.Height, decoded.Format )
					.WithMips( decoded.MipCount )
					.WithStaticUsage()
					.WithData( decoded.Data )
					.Finish();
				if ( texture is null || !texture.IsValid() )
					throw new InvalidOperationException( $"Runtime cubemap frame {frameIndex} is invalid." );
				created.Add( texture );
			}
			textures = created.ToArray();
			error = null;
			return true;
		}
		catch ( Exception exception )
		{
			foreach ( var texture in created ) texture?.Dispose();
			textures = Array.Empty<Texture>();
			error = exception.Message;
			return false;
		}
	}

	/// <summary>
	/// Creates one runtime texture per VTF header frame. Source particle sprites
	/// cannot consume the vertically packed material atlas, while s&amp;box Sprite
	/// can advance a list of ordinary textures without engine changes.
	/// </summary>
	internal static bool TryCreateFrames( byte[] bytes, out Texture[] textures, out string error )
	{
		textures = Array.Empty<Texture>();
		if ( GetFrameCount( bytes ) <= 1 )
		{
			error = "VTF contains no header animation frames.";
			return false;
		}
		if ( !TryRead( bytes, true, out var atlas, out error ) ) return false;
		if ( atlas.FrameCount <= 1 || atlas.Height <= 0 || atlas.Height % atlas.FrameCount != 0
			|| atlas.Data.Length == 0 || atlas.Data.Length % atlas.FrameCount != 0 )
		{
			error = "Animated VTF atlas has inconsistent frame dimensions.";
			return false;
		}

		var frameHeight = atlas.Height / atlas.FrameCount;
		var frameBytes = atlas.Data.Length / atlas.FrameCount;
		var created = new List<Texture>( atlas.FrameCount );
		try
		{
			for ( var frameIndex = 0; frameIndex < atlas.FrameCount; frameIndex++ )
			{
				var frameData = atlas.Data.AsSpan( frameIndex * frameBytes, frameBytes ).ToArray();
				var texture = Texture.Create( atlas.Width, frameHeight, atlas.Format )
					.WithMips( 1 )
					.WithStaticUsage()
					.WithData( frameData )
					.Finish();
				if ( texture is null || !texture.IsValid() ) throw new InvalidOperationException( $"Frame {frameIndex} texture creation failed." );
				created.Add( texture );
			}
			textures = created.ToArray();
			error = null;
			return true;
		}
		catch ( Exception exception )
		{
			foreach ( var texture in created ) texture.Dispose();
			error = exception.Message;
			return false;
		}
	}

	/// <summary>
	/// Extracts sequence zero from Valve's particle-sheet VTF resource. Runtime
	/// textures cannot be assigned native sheet metadata through the public API,
	/// so each UV rectangle is copied into an ordinary texture for Sprite playback.
	/// Block-compressed frames remain compressed and are copied on block boundaries.
	/// </summary>
	internal static bool TryCreateSheetFrames( byte[] bytes, out Texture[] textures, out float frameRate, out string error )
	{
		textures = Array.Empty<Texture>();
		frameRate = 0f;
		if ( !TryReadFirstSheetSequence( bytes, out var coordinates, out var totalTime, out error ) ) return false;
		if ( !TryRead( bytes, false, out var decoded, out error ) ) return false;
		if ( coordinates.Count <= 1 )
		{
			error = "Particle sheet sequence zero has fewer than two frames.";
			return false;
		}

		var largestMipBytes = GetMipSize( decoded.Width, decoded.Height, decoded.BytesPerBlock, decoded.BlockSize );
		if ( largestMipBytes <= 0 || largestMipBytes > decoded.Data.Length )
		{
			error = "Decoded VTF does not contain a complete largest mip.";
			return false;
		}
		var surface = decoded.Data.AsSpan( decoded.Data.Length - largestMipBytes, largestMipBytes );
		var created = new List<Texture>( coordinates.Count );
		try
		{
			foreach ( var coordinate in coordinates )
			{
				if ( !TryCropFrame( surface, decoded, coordinate, out var width, out var height, out var frameData, out error ) )
					throw new InvalidDataException( error );
				var texture = Texture.Create( width, height, decoded.Format )
					.WithMips( 1 )
					.WithStaticUsage()
					.WithData( frameData )
					.Finish();
				if ( texture is null || !texture.IsValid() ) throw new InvalidOperationException( "Particle sheet frame texture creation failed." );
				created.Add( texture );
			}
			textures = created.ToArray();
			frameRate = totalTime > 0.001f ? coordinates.Count / totalTime : 15f;
			error = null;
			return true;
		}
		catch ( Exception exception )
		{
			foreach ( var texture in created ) texture.Dispose();
			error = exception.Message;
			return false;
		}
	}

	static bool TryReadFirstSheetSequence( byte[] bytes, out List<VtfSheetCoordinate> coordinates, out float totalTime, out string error )
	{
		coordinates = [];
		totalTime = 0f;
		error = null;
		var span = bytes.AsSpan();
		if ( span.Length < ResourceEntriesOffset || !TryGetResourceOffset( span, ParticleSheetResource, out var resourceOffset ) )
		{
			error = "VTF has no particle-sheet resource.";
			return false;
		}
		if ( resourceOffset < 0 || resourceOffset > span.Length - 12 )
		{
			error = "Particle-sheet resource offset is outside the VTF.";
			return false;
		}
		var resourceLength = BinaryPrimitives.ReadInt32LittleEndian( span.Slice( resourceOffset, 4 ) );
		var start = resourceOffset + 4;
		if ( resourceLength < 8 || start > span.Length - resourceLength )
		{
			error = "Particle-sheet resource length is invalid.";
			return false;
		}
		var resource = span.Slice( start, resourceLength );
		var version = BinaryPrimitives.ReadInt32LittleEndian( resource.Slice( 0, 4 ) );
		var sequenceCount = BinaryPrimitives.ReadInt32LittleEndian( resource.Slice( 4, 4 ) );
		if ( version is < 0 or > 1 || sequenceCount is <= 0 or > 64 )
		{
			error = $"Unsupported particle-sheet header v{version} with {sequenceCount} sequences.";
			return false;
		}

		var offset = 8;
		List<VtfSheetCoordinate> first = null;
		float firstTime = 0f;
		for ( var sequenceIndex = 0; sequenceIndex < sequenceCount; sequenceIndex++ )
		{
			if ( offset > resource.Length - 16 ) { error = "Particle-sheet sequence header is truncated."; return false; }
			var sequenceId = BinaryPrimitives.ReadInt32LittleEndian( resource.Slice( offset, 4 ) );
			var frameCount = BinaryPrimitives.ReadInt32LittleEndian( resource.Slice( offset + 8, 4 ) );
			var sequenceTime = ReadSingle( resource, offset + 12 );
			offset += 16;
			if ( frameCount is < 0 or > 4096 ) { error = $"Particle-sheet sequence {sequenceId} has invalid frame count {frameCount}."; return false; }
			var sequence = new List<VtfSheetCoordinate>( frameCount );
			for ( var frameIndex = 0; frameIndex < frameCount; frameIndex++ )
			{
				var frameBytes = version == 0 ? 20 : 68;
				if ( offset > resource.Length - frameBytes ) { error = "Particle-sheet frame data is truncated."; return false; }
				// Duration is stored first. The first coordinate set is the full-resolution image.
				sequence.Add( new VtfSheetCoordinate(
					ReadSingle( resource, offset + 4 ),
					ReadSingle( resource, offset + 8 ),
					ReadSingle( resource, offset + 12 ),
					ReadSingle( resource, offset + 16 ) ) );
				offset += frameBytes;
			}
			if ( sequenceId == 0 )
			{
				coordinates = sequence;
				totalTime = sequenceTime;
			}
			else if ( first is null )
			{
				first = sequence;
				firstTime = sequenceTime;
			}
		}
		if ( coordinates.Count == 0 && first is not null )
		{
			coordinates = first;
			totalTime = firstTime;
		}
		if ( coordinates.Count > 0 ) return true;
		error = "Particle-sheet contains no usable sequence.";
		return false;
	}

	static bool TryCropFrame( ReadOnlySpan<byte> source, VtfTextureData decoded, VtfSheetCoordinate coordinate,
		out int width, out int height, out byte[] data, out string error )
	{
		width = height = 0;
		data = null;
		error = null;
		if ( !coordinate.IsFinite || coordinate.Right <= coordinate.Left || coordinate.Bottom <= coordinate.Top )
		{
			error = "Particle-sheet frame has invalid UV coordinates.";
			return false;
		}
		var x0 = Math.Clamp( (int)MathF.Floor( coordinate.Left * decoded.Width ), 0, decoded.Width - 1 );
		var y0 = Math.Clamp( (int)MathF.Floor( coordinate.Top * decoded.Height ), 0, decoded.Height - 1 );
		var x1 = Math.Clamp( (int)MathF.Ceiling( coordinate.Right * decoded.Width ), x0 + 1, decoded.Width );
		var y1 = Math.Clamp( (int)MathF.Ceiling( coordinate.Bottom * decoded.Height ), y0 + 1, decoded.Height );
		var block = decoded.BlockSize;
		var sourceBlocksWide = Math.Max( 1, (decoded.Width + block - 1) / block );
		var sourceBlocksHigh = Math.Max( 1, (decoded.Height + block - 1) / block );
		var blockX0 = Math.Clamp( x0 / block, 0, sourceBlocksWide - 1 );
		var blockY0 = Math.Clamp( y0 / block, 0, sourceBlocksHigh - 1 );
		var blockX1 = Math.Clamp( (x1 + block - 1) / block, blockX0 + 1, sourceBlocksWide );
		var blockY1 = Math.Clamp( (y1 + block - 1) / block, blockY0 + 1, sourceBlocksHigh );
		var targetBlocksWide = blockX1 - blockX0;
		var targetBlocksHigh = blockY1 - blockY0;
		width = block == 1 ? x1 - x0 : Math.Min( decoded.Width - blockX0 * block, targetBlocksWide * block );
		height = block == 1 ? y1 - y0 : Math.Min( decoded.Height - blockY0 * block, targetBlocksHigh * block );
		var rowBytes = checked(targetBlocksWide * decoded.BytesPerBlock);
		data = GC.AllocateUninitializedArray<byte>( checked(rowBytes * targetBlocksHigh) );
		for ( var row = 0; row < targetBlocksHigh; row++ )
		{
			var sourceOffset = checked(((blockY0 + row) * sourceBlocksWide + blockX0) * decoded.BytesPerBlock);
			if ( sourceOffset < 0 || sourceOffset > source.Length - rowBytes )
			{
				error = "Particle-sheet frame crosses the decoded VTF surface.";
				return false;
			}
			source.Slice( sourceOffset, rowBytes ).CopyTo( data.AsSpan( row * rowBytes, rowBytes ) );
		}
		return true;
	}

	internal static bool TryRead( byte[] bytes, bool animatedAtlas, out VtfTextureData texture, out string error )
	{
		texture = default;
		error = null;
		if ( bytes is null || bytes.Length < 64 || !IsVtf( bytes ) )
		{
			error = "Not a VTF image.";
			return false;
		}

		var span = bytes.AsSpan();
		var majorVersion = BinaryPrimitives.ReadUInt32LittleEndian( span.Slice( 4, 4 ) );
		var minorVersion = BinaryPrimitives.ReadUInt32LittleEndian( span.Slice( 8, 4 ) );
		var headerSize = BinaryPrimitives.ReadUInt32LittleEndian( span.Slice( 12, 4 ) );
		var width = BinaryPrimitives.ReadUInt16LittleEndian( span.Slice( 16, 2 ) );
		var height = BinaryPrimitives.ReadUInt16LittleEndian( span.Slice( 18, 2 ) );
		var flags = BinaryPrimitives.ReadUInt32LittleEndian( span.Slice( 20, 4 ) );
		var frameCount = Math.Max( 1, (int)BinaryPrimitives.ReadUInt16LittleEndian( span.Slice( 24, 2 ) ) );
		var formatValue = BinaryPrimitives.ReadInt32LittleEndian( span.Slice( 52, 4 ) );
		var mipCount = span[56];
		if ( majorVersion != 7 || minorVersion > 5 || headerSize < 64 || headerSize > bytes.Length )
		{
			error = $"Unsupported VTF version/header: {majorVersion}.{minorVersion}, {headerSize} bytes.";
			return false;
		}
		if ( width == 0 || height == 0 || mipCount == 0 || mipCount > 16 || frameCount > 256 )
		{
			error = $"Invalid VTF dimensions/frame/mip count: {width}x{height}, {frameCount} frames, {mipCount} mips.";
			return false;
		}
		if ( (flags & TextureFlagEnvMap) != 0 )
		{
			error = "VTF cubemaps are not supported by the legacy 2D material loader.";
			return false;
		}
		if ( minorVersion >= 2 && bytes.Length >= 65
			&& BinaryPrimitives.ReadUInt16LittleEndian( span.Slice( 63, 2 ) ) > 1 )
		{
			error = "VTF volume textures are not supported by the legacy 2D material loader.";
			return false;
		}
		if ( !TryGetFormat( formatValue, out var format, out var bytesPerBlock, out var blockSize ) )
		{
			error = $"Unsupported VTF image format {formatValue}.";
			return false;
		}

		if ( !TryGetHighResolutionOffset( span, minorVersion, headerSize, out var imageOffset, out error ) ) return false;
		var mipSizes = new int[mipCount];
		var fullImageSize = 0L;
		for ( var storageIndex = 0; storageIndex < mipCount; storageIndex++ )
		{
			var level = mipCount - 1 - storageIndex;
			var mipWidth = Math.Max( 1, width >> level );
			var mipHeight = Math.Max( 1, height >> level );
			var size = GetMipSize( mipWidth, mipHeight, bytesPerBlock, blockSize );
			mipSizes[storageIndex] = size;
			fullImageSize += (long)size * frameCount;
		}
		if ( imageOffset < 0 || fullImageSize > bytes.Length - imageOffset )
		{
			error = $"Truncated VTF image data: need {fullImageSize} bytes at {imageOffset}, have {Math.Max( 0, bytes.Length - imageOffset )}.";
			return false;
		}

		var skippedLargeMips = 0;
		while ( skippedLargeMips + 1 < mipCount
			&& Math.Max( Math.Max( 1, width >> skippedLargeMips ), Math.Max( 1, height >> skippedLargeMips ) )
				> Titanfall2StreamingSettings.TextureMaxDimension )
		{
			skippedLargeMips++;
		}
		var retainedMipCount = mipCount - skippedLargeMips;
		var retainedWidth = Math.Max( 1, width >> skippedLargeMips );
		var retainedHeight = Math.Max( 1, height >> skippedLargeMips );

		if ( animatedAtlas && frameCount > 1 )
		{
			var largestRetainedStorageIndex = retainedMipCount - 1;
			var sourceOffset = imageOffset;
			for ( var index = 0; index < largestRetainedStorageIndex; index++ )
				sourceOffset = checked(sourceOffset + mipSizes[index] * frameCount);
			var frameSize = mipSizes[largestRetainedStorageIndex];
			var atlasHeight = checked(retainedHeight * frameCount);
			if ( blockSize > 1 && retainedHeight < blockSize )
			{
				error = "Animated block-compressed VTF frames smaller than one compression block cannot be atlased safely.";
				return false;
			}
			if ( atlasHeight > 16_384 )
			{
				error = $"Animated VTF atlas is too tall: {retainedWidth}x{atlasHeight}.";
				return false;
			}
			var atlas = GC.AllocateUninitializedArray<byte>( checked(frameSize * frameCount) );
			span.Slice( sourceOffset, atlas.Length ).CopyTo( atlas );
			texture = new VtfTextureData( retainedWidth, atlasHeight, format, 1, frameCount, bytesPerBlock, blockSize, atlas );
			return true;
		}

		var outputSize = 0;
		for ( var index = 0; index < retainedMipCount; index++ ) outputSize = checked(outputSize + mipSizes[index]);
		var output = GC.AllocateUninitializedArray<byte>( outputSize );
		var readOffset = imageOffset;
		var writeOffset = 0;
		for ( var index = 0; index < retainedMipCount; index++ )
		{
			var size = mipSizes[index];
			span.Slice( readOffset, size ).CopyTo( output.AsSpan( writeOffset, size ) );
			readOffset = checked(readOffset + size * frameCount);
			writeOffset += size;
		}
		texture = new VtfTextureData( retainedWidth, retainedHeight, format, retainedMipCount, frameCount, bytesPerBlock, blockSize, output );
		return true;
	}

	internal static bool TryReadCube( byte[] bytes, out VtfCubeTextureData texture, out string error ) =>
		TryReadCubeFrame( bytes, 0, out texture, out error );

	internal static bool TryReadCubeFrame( byte[] bytes, int frameIndex, out VtfCubeTextureData texture, out string error )
	{
		texture = default;
		error = null;
		if ( bytes is null || bytes.Length < 64 || !IsCube( bytes ) )
		{
			error = "Not a VTF cubemap.";
			return false;
		}
		var span = bytes.AsSpan();
		var majorVersion = BinaryPrimitives.ReadUInt32LittleEndian( span.Slice( 4, 4 ) );
		var minorVersion = BinaryPrimitives.ReadUInt32LittleEndian( span.Slice( 8, 4 ) );
		var headerSize = BinaryPrimitives.ReadUInt32LittleEndian( span.Slice( 12, 4 ) );
		var width = BinaryPrimitives.ReadUInt16LittleEndian( span.Slice( 16, 2 ) );
		var height = BinaryPrimitives.ReadUInt16LittleEndian( span.Slice( 18, 2 ) );
		var frameCount = Math.Max( 1, (int)BinaryPrimitives.ReadUInt16LittleEndian( span.Slice( 24, 2 ) ) );
		var firstFrame = BinaryPrimitives.ReadUInt16LittleEndian( span.Slice( 26, 2 ) );
		var formatValue = BinaryPrimitives.ReadInt32LittleEndian( span.Slice( 52, 4 ) );
		var mipCount = span[56];
		if ( majorVersion != 7 || minorVersion > 5 || headerSize < 64 || headerSize > bytes.Length
			|| width == 0 || height == 0 || width != height || mipCount is 0 or > 16 || frameCount > 256
			|| frameIndex < 0 || frameIndex >= frameCount )
		{
			error = $"Invalid VTF cubemap header: v{majorVersion}.{minorVersion}, {width}x{height}, {frameCount} frames, {mipCount} mips.";
			return false;
		}
		if ( !TryGetFormat( formatValue, out var format, out var bytesPerBlock, out var blockSize ) )
		{
			error = $"Unsupported VTF cubemap format {formatValue}.";
			return false;
		}
		if ( !TryGetHighResolutionOffset( span, minorVersion, headerSize, out var imageOffset, out error ) ) return false;

		// VTF 7.0-7.4 may append a legacy spheremap face. It is accounted for
		// while stepping source data but intentionally omitted from the runtime cube.
		var sourceFaceCount = minorVersion < 5 && firstFrame != ushort.MaxValue ? 7 : 6;
		var mipSizes = new int[mipCount];
		var fullSize = 0L;
		for ( var storageIndex = 0; storageIndex < mipCount; storageIndex++ )
		{
			var level = mipCount - 1 - storageIndex;
			var size = GetMipSize( Math.Max( 1, width >> level ), Math.Max( 1, height >> level ), bytesPerBlock, blockSize );
			mipSizes[storageIndex] = size;
			fullSize += (long)size * sourceFaceCount * frameCount;
		}
		if ( imageOffset < 0 || fullSize > bytes.Length - imageOffset )
		{
			error = $"Truncated VTF cubemap data: need {fullSize} bytes at {imageOffset}.";
			return false;
		}

		var skippedLargeMips = 0;
		while ( skippedLargeMips + 1 < mipCount && Math.Max( 1, width >> skippedLargeMips ) > Titanfall2StreamingSettings.TextureMaxDimension )
			skippedLargeMips++;
		var retainedMipCount = mipCount - skippedLargeMips;
		var outputSize = 0;
		for ( var index = 0; index < retainedMipCount; index++ ) outputSize = checked(outputSize + mipSizes[index] * 6);
		var output = GC.AllocateUninitializedArray<byte>( outputSize );
		var readOffset = imageOffset;
		var writeOffset = 0;
		for ( var index = 0; index < retainedMipCount; index++ )
		{
			var faceSize = mipSizes[index];
			var frameOffset = checked(readOffset + frameIndex * faceSize * sourceFaceCount);
			for ( var face = 0; face < 6; face++ )
			{
				span.Slice( frameOffset + face * faceSize, faceSize ).CopyTo( output.AsSpan( writeOffset, faceSize ) );
				writeOffset += faceSize;
			}
			readOffset = checked(readOffset + faceSize * sourceFaceCount * frameCount);
		}
		texture = new VtfCubeTextureData(
			Math.Max( 1, width >> skippedLargeMips ), Math.Max( 1, height >> skippedLargeMips ),
			format, retainedMipCount, bytesPerBlock, blockSize, output );
		return true;
	}

	static bool TryGetHighResolutionOffset( ReadOnlySpan<byte> bytes, uint minorVersion, uint headerSize, out int offset, out string error )
	{
		offset = -1;
		error = null;
		if ( minorVersion >= 3 && headerSize >= ResourceEntriesOffset && bytes.Length >= ResourceEntriesOffset )
		{
			var resourceCount = BinaryPrimitives.ReadUInt32LittleEndian( bytes.Slice( ResourceCountOffset, 4 ) );
			if ( resourceCount > 128 || ResourceEntriesOffset + resourceCount * 8L > headerSize )
			{
				error = $"Invalid VTF resource dictionary ({resourceCount} entries in a {headerSize}-byte header).";
				return false;
			}
			if ( TryGetResourceOffset( bytes, HighResolutionImageResource, out offset ) ) return true;
		}

		var lowFormat = BinaryPrimitives.ReadInt32LittleEndian( bytes.Slice( 57, 4 ) );
		var lowWidth = bytes[61];
		var lowHeight = bytes[62];
		var lowSize = 0;
		if ( lowWidth > 0 && lowHeight > 0 && TryGetFormat( lowFormat, out _, out var lowBytesPerBlock, out var lowBlockSize ) )
			lowSize = GetMipSize( lowWidth, lowHeight, lowBytesPerBlock, lowBlockSize );
		offset = checked((int)headerSize + lowSize);
		return true;
	}

	static bool TryGetResourceOffset( ReadOnlySpan<byte> bytes, uint resourceTag, out int offset )
	{
		offset = -1;
		if ( bytes.Length < ResourceEntriesOffset ) return false;
		var resourceCount = BinaryPrimitives.ReadUInt32LittleEndian( bytes.Slice( ResourceCountOffset, 4 ) );
		if ( resourceCount > 128 || ResourceEntriesOffset + resourceCount * 8L > bytes.Length ) return false;
		for ( var index = 0; index < resourceCount; index++ )
		{
			var entryOffset = ResourceEntriesOffset + index * 8;
			var tagAndFlags = BinaryPrimitives.ReadUInt32LittleEndian( bytes.Slice( entryOffset, 4 ) );
			if ( (tagAndFlags & 0x00FFFFFF) != resourceTag ) continue;
			offset = checked((int)BinaryPrimitives.ReadUInt32LittleEndian( bytes.Slice( entryOffset + 4, 4 ) ));
			return true;
		}
		return false;
	}

	static float ReadSingle( ReadOnlySpan<byte> bytes, int offset ) =>
		BitConverter.Int32BitsToSingle( BinaryPrimitives.ReadInt32LittleEndian( bytes.Slice( offset, 4 ) ) );

	static int GetMipSize( int width, int height, int bytesPerBlock, int blockSize )
	{
		var blocksWide = Math.Max( 1, (width + blockSize - 1) / blockSize );
		var blocksHigh = Math.Max( 1, (height + blockSize - 1) / blockSize );
		return checked(blocksWide * blocksHigh * bytesPerBlock);
	}

	static bool TryGetFormat( int value, out ImageFormat format, out int bytesPerBlock, out int blockSize )
	{
		(format, bytesPerBlock, blockSize) = value switch
		{
			0 => (ImageFormat.RGBA8888, 4, 1),
			1 => (ImageFormat.ABGR8888, 4, 1),
			2 => (ImageFormat.RGB888, 3, 1),
			3 => (ImageFormat.BGR888, 3, 1),
			4 => (ImageFormat.RGB565, 2, 1),
			5 => (ImageFormat.I8, 1, 1),
			6 => (ImageFormat.IA88, 2, 1),
			8 => (ImageFormat.A8, 1, 1),
			11 => (ImageFormat.ARGB8888, 4, 1),
			12 => (ImageFormat.BGRA8888, 4, 1),
			13 => (ImageFormat.DXT1, 8, 4),
			14 => (ImageFormat.DXT3, 16, 4),
			15 => (ImageFormat.DXT5, 16, 4),
			16 => (ImageFormat.BGRX8888, 4, 1),
			17 => (ImageFormat.BGR565, 2, 1),
			18 => (ImageFormat.BGRX5551, 2, 1),
			19 => (ImageFormat.BGRA4444, 2, 1),
			20 => (ImageFormat.DXT1_ONEBITALPHA, 8, 4),
			21 => (ImageFormat.BGRA5551, 2, 1),
			24 => (ImageFormat.RGBA16161616F, 8, 1),
			25 => (ImageFormat.RGBA16161616, 8, 1),
			// Respawn's PC cubemaps.hdr.vtf uses format 66. Its byte layout is
			// six 16-byte 4x4 blocks per mip and the payload is HDR BC6H data.
			66 => (ImageFormat.BC6H, 16, 4),
			_ => (ImageFormat.None, 0, 0)
		};
		return format != ImageFormat.None;
	}
}

readonly record struct VtfTextureData(
	int Width,
	int Height,
	ImageFormat Format,
	int MipCount,
	int FrameCount,
	int BytesPerBlock,
	int BlockSize,
	byte[] Data );

readonly record struct VtfCubeTextureData(
	int Width,
	int Height,
	ImageFormat Format,
	int MipCount,
	int BytesPerBlock,
	int BlockSize,
	byte[] Data );

readonly record struct VtfSheetCoordinate( float Left, float Top, float Right, float Bottom )
{
	internal bool IsFinite => float.IsFinite( Left ) && float.IsFinite( Top ) && float.IsFinite( Right ) && float.IsFinite( Bottom );
}
