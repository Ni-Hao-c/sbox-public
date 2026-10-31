using static Titanfall2ProcessedCache;

static class Titanfall2TextureCache
{
	internal static bool TryRead( string identity, out RpakTextureData data )
	{
		RpakTextureData result = default;
		var hit = Titanfall2ProcessedCache.TryRead( "texture", identity, reader =>
		{
			var width = reader.ReadInt32(); var height = reader.ReadInt32();
			var format = (ImageFormat)reader.ReadInt32(); var mips = reader.ReadInt32();
			var arrays = reader.ReadInt32(); var cube = reader.ReadBoolean();
			if ( width <= 0 || height <= 0 || width > 16384 || height > 16384 || mips < 1 || mips > 32
				|| arrays < 1 || arrays > 2048 || (cube && (arrays % 6 != 0 || width != height))
				|| mips > System.Numerics.BitOperations.Log2( (uint)Math.Max( width, height ) ) + 1 )
				throw new InvalidDataException( "Invalid cached texture metadata." );
			// These are exactly the formats RpakTextureDecoder emits, after RG8
			// expansion. Validate the complete chain so bad caches become importer
			// misses, instead of successful hits that fail later during GPU creation.
			var (blockSize, blockBytes) = format switch
			{
				ImageFormat.DXT1 or ImageFormat.ATI1N => (4, 8),
				ImageFormat.DXT3 or ImageFormat.DXT5 or ImageFormat.ATI2N or ImageFormat.BC6H or ImageFormat.BC7 => (4, 16),
				ImageFormat.RGBA8888 => (1, 4),
				ImageFormat.I8 or ImageFormat.A8 => (1, 1),
				_ => throw new InvalidDataException( "Unsupported cached RPAK texture format." )
			};
			long expectedBytes = 0;
			for ( var level = 0; level < mips; ++level )
			{
				var mipWidth = Math.Max( 1, width >> level );
				var mipHeight = Math.Max( 1, height >> level );
				expectedBytes += (long)((mipWidth + blockSize - 1) / blockSize)
					* ((mipHeight + blockSize - 1) / blockSize) * blockBytes * arrays;
			}
			var bytes = reader.Array<byte>( 256 * 1024 * 1024 );
			if ( bytes.LongLength != expectedBytes ) throw new InvalidDataException( "Cached texture mip data size mismatch." );
			result = new RpakTextureData( width, height, format, mips, arrays, cube, bytes );
		} );
		data = result;
		return hit;
	}

	internal static void Write( string identity, RpakTextureData data ) =>
		Titanfall2ProcessedCache.Write( "texture", identity, writer =>
		{
			writer.Write( data.Width ); writer.Write( data.Height ); writer.Write( (int)data.Format );
			writer.Write( data.MipCount ); writer.Write( data.ArraySize ); writer.Write( data.IsCube );
			WriteArray( writer, data.Data );
		} );
}
