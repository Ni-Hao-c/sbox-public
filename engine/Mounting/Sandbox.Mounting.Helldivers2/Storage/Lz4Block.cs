namespace Sandbox.Mounting.Helldivers2;

/// <summary>
/// Raw LZ4 block decompression, which is the only compression the DSAR container uses
/// (compression id 3). The managed runtime ships no block decoder and adding a native
/// dependency for a few dozen lines would be a worse trade, so this is the format's
/// reference algorithm written out directly.
/// </summary>
internal static class Lz4Block
{
	/// <summary>
	/// Decompresses one raw block into <paramref name="destination"/>. The block carries no
	/// frame header and no content size, so the caller has to know the output length, which
	/// the DSAR chunk table always provides.
	/// </summary>
	public static bool TryDecompress( ReadOnlySpan<byte> source, Span<byte> destination, out int written )
	{
		written = 0;

		var sourceOffset = 0;
		var destinationOffset = 0;

		while ( sourceOffset < source.Length )
		{
			var token = source[sourceOffset++];

			// Literal run: high nibble is the length, 15 means "add 255-terminated extension bytes".
			var literalLength = (int)( token >> 4 );
			if ( literalLength == 15 )
			{
				int extension;
				do
				{
					if ( sourceOffset >= source.Length ) return false;
					extension = source[sourceOffset++];
					literalLength += extension;
				}
				while ( extension == 255 );
			}

			if ( literalLength > 0 )
			{
				if ( sourceOffset + literalLength > source.Length ) return false;
				if ( destinationOffset + literalLength > destination.Length ) return false;

				source.Slice( sourceOffset, literalLength ).CopyTo( destination.Slice( destinationOffset ) );
				sourceOffset += literalLength;
				destinationOffset += literalLength;
			}

			// A block ends right after a literal run; there is no match to follow.
			if ( sourceOffset >= source.Length ) break;

			if ( sourceOffset + 2 > source.Length ) return false;

			var matchOffset = source[sourceOffset] | ( source[sourceOffset + 1] << 8 );
			sourceOffset += 2;

			if ( matchOffset == 0 || matchOffset > destinationOffset ) return false;

			var matchLength = token & 0x0F;
			if ( matchLength == 15 )
			{
				int extension;
				do
				{
					if ( sourceOffset >= source.Length ) return false;
					extension = source[sourceOffset++];
					matchLength += extension;
				}
				while ( extension == 255 );
			}

			matchLength += 4;

			if ( destinationOffset + matchLength > destination.Length ) return false;

			// Byte-wise on purpose: LZ4 matches may overlap the current write position.
			var match = destinationOffset - matchOffset;
			for ( var i = 0; i < matchLength; i++ ) destination[destinationOffset + i] = destination[match + i];
			destinationOffset += matchLength;
		}

		written = destinationOffset;
		return true;
	}
}
