using System.Buffers.Binary;

/// <summary>
/// Managed decoder for the RTech PakFile bitstream used by Titanfall 2 RPAK v7 files.
/// It operates on a complete file buffer and leaves the original RPAK header intact.
/// </summary>
static class PakFileDecoder
{
	const int RpakHeaderSize = 0x58;

	// Fixed decode tables that describe the on-disk PakFile entropy code.
	// Only the 1,256 bytes addressed by this decoder are retained.
	static readonly byte[] DecodeTable = Convert.FromBase64String(
		"BP78CATvEfkE/fwHBAX/9AT+/BAE7xH2BP38+wQG/wsE/vwIBO8R+AT9/AwEBf/3BP78EATvEfUE/fz6BAb/8wT+/AgE7xH5BP38BwQF//QE/vwQBO8R9gT9/PsEBv8OBP78CATvEfgE/fwMBAX/CQT+/BAE7xH1BP38+gQG//EE/vwIBO8R+QT9/AcEBf/0BP78EATvEfYE/fz7BAb/DQT+/AgE7xH4BP38DAQF//cE/vwQBO8R9QT9/PoEBv/yBP78CATvEfkE/fwHBAX/9AT+/BAE7xH2BP38+wQG/w8E/vwIBO8R+AT9/AwEBf8KBP78EATvEfUE/fz6BAb/8AQFBAYEBQQHBAUEBgQFBBEEBQQGBAUECAQFBAYEBQQMBAUEBgQFBAcEBQQGBAUECQQFBAYEBQQIBAUEBgQFBA4EBQQGBAUEBwQFBAYEBQQRBAUEBgQFBAgEBQQGBAUECwQFBAYEBQQHBAUEBgQFBAoEBQQGBAUECAQFBAYEBQQQBAUEBgQFBAcEBQQGBAUEEQQFBAYEBQQIBAUEBgQFBAwEBQQGBAUEBwQFBAYEBQQJBAUEBgQFBAgEBQQGBAUEDwQFBAYEBQQHBAUEBgQFBBEEBQQGBAUECAQFBAYEBQQNBAUEBgQFBAcEBQQGBAUECgQFBAYEBQQIBAUEBgQFBP8CBAMFAgQEBgIEAwYCBQQGAgQDBQIEBAYCBAMGAgUECAIEAwUCBAQGAgQDBgIFBAcCBAMFAgQEBgIEAwYCBQQIAgQDBQIEBAYCBAMGAgUEBgIEAwUCBAQGAgQDBgIFBAgCBAMFAgQEBgIEAwYCBQQIAgQDBQIEBAYCBAMGAgUECAIEAwUCBAQGAgQDBgIFBAYCBAMFAgQEBgIEAwYCBQQIAgQDBQIEBAYCBAMGAgUEBwIEAwUCBAQGAgQDBgIFBAgCBAMFAgQEBgIEAwYCBQQGAgQDBQIEBAYCBAMGAgUECAIEAwUCBAQGAgQDBgIFBAgCBAMFAgQEBgIEAwYCBQQIAQIBAwECAQUBAgEDAQIBBgECAQMBAgEFAQIBAwECAQcBAgEDAQIBBQECAQMBAgEHAQIBAwECAQUBAgEDAQIBCAECAQMBAgEFAQIBAwECAQYBAgEDAQIBBQECAQMBAgEIAQIBAwECAQUBAgEDAQIBBwECAQMBAgEFAQIBAwECAQgBAgEDAQIBBQECAQMBAgEGAQIBAwECAQUBAgEDAQIBBwECAQMBAgEFAQIBAwECAQcBAgEDAQIBBQECAQMBAgEIAQIBAwECAQUBAgEDAQIBBgECAQMBAgEFAQIBAwECAQgBAgEDAQIBBQECAQMBAgEHAQIBAwECAQUBAgEDAQIBCAAIAAQACAAGAAgAAQAIAAsACAAMAAgACQAIAAMACAAOAAgABAAIAAcACAACAAgADQAIAAwACAAKAAgABQAIAA8BAgEFAQIBBgECAQYBAgEGAQIBBQECAQYBAgEGAQIBBgECAQUBAgEGAQIBBgECAQYBAgEFAQIBBgECAQYBAgEGSgAAAGoAAACKAAAAqgAAAMoAAADqAAAACgEAACoBAABKAQAAagEAAIoBAACqAQAAqgMAAKoFAACqJQAAqiUCAAUFBQUFBQUFBQUFCQkNERUAAAIEBggKKgABAQEBAQUFEQEAAAAAAAA=" );

	public static bool TryDecompress( ReadOnlySpan<byte> source, out byte[] output, out string error )
	{
		output = null;
		error = null;
		if ( source.Length < RpakHeaderSize )
		{
			error = "PakFile is smaller than the RPAK v7 header.";
			return false;
		}

		var declaredCompressedSize = BinaryPrimitives.ReadInt64LittleEndian( source.Slice( 0x18, 8 ) );
		var declaredDecompressedSize = BinaryPrimitives.ReadInt64LittleEndian( source.Slice( 0x28, 8 ) );
		if ( declaredCompressedSize < RpakHeaderSize || declaredCompressedSize > source.Length )
		{
			error = $"Invalid compressed size {declaredCompressedSize}.";
			return false;
		}
		if ( declaredDecompressedSize < RpakHeaderSize || declaredDecompressedSize > int.MaxValue )
		{
			error = $"Invalid decompressed size {declaredDecompressedSize}.";
			return false;
		}

		try
		{
			var input = new byte[checked((int)declaredCompressedSize + 16)];
			source[..(int)declaredCompressedSize].CopyTo( input );
			var state = Initialize( input, (ulong)declaredCompressedSize, RpakHeaderSize );
			if ( state.DecompressedSize != (ulong)declaredDecompressedSize )
			{
				error = $"PakFile stream declares {state.DecompressedSize} bytes, header declares {declaredDecompressedSize}.";
				return false;
			}

			var paddedOutput = new byte[checked((int)state.DecompressedSize + 16)];
			if ( !Decode( input, paddedOutput, state, (ulong)declaredCompressedSize, state.DecompressedSize, out error ) )
				return false;

			output = new byte[(int)state.DecompressedSize];
			Buffer.BlockCopy( input, 0, output, 0, RpakHeaderSize );
			Buffer.BlockCopy( paddedOutput, RpakHeaderSize, output, RpakHeaderSize, output.Length - RpakHeaderSize );
			return true;
		}
		catch ( Exception exception )
		{
			error = exception.Message;
			output = null;
			return false;
		}
	}

	static DecoderState Initialize( byte[] input, ulong dataSize, int headerSize )
	{
		var state = new DecoderState();
		ulong first = ReadUInt64( input, (ulong)headerSize );
		var sizeBitCount = (int)(first & 0x3F);
		first >>= 6;
		state.DecompressedSize = (first & LowBitMask( sizeBitCount )) | (1UL << sizeBitCount);

		var position = (ulong)headerSize + 8;
		var combined = (first >> sizeBitCount) | (ReadUInt64( input, position ) << (64 - (sizeBitCount + 6)));
		position += (ulong)(sizeBitCount + 6) >> 3;
		var currentBit = (sizeBitCount + 6) & 7;
		var fieldBitCount = currentBit + 13;
		var fields = (ulong.MaxValue >> currentBit) & combined;

		var inputWindowBits = (((byte)fields - 1) & 0x3F) + 1;
		state.InputWindowMask = LowBitMask( inputWindowBits );
		state.OutputWindowMask = LowBitMask( (int)(((fields >> 6) - 1) & 0x3F) + 1 );

		var nextPosition = position + (ulong)(fieldBitCount >> 3);
		combined = (fields >> 13) | (ReadUInt64( input, position ) << (64 - fieldBitCount));
		state.FilePosition = nextPosition;
		state.CurrentBit = (uint)(fieldBitCount & 7);
		state.Current = (ulong.MaxValue >> (int)state.CurrentBit) & combined;

		ulong compressedStreamSize;
		if ( state.InputWindowMask == ulong.MaxValue )
		{
			state.BlockHeaderSize = 0;
			compressedStreamSize = dataSize;
		}
		else
		{
			state.BlockHeaderSize = (uint)(inputWindowBits >> 3) + 1;
			compressedStreamSize = ReadUInt64( input, nextPosition ) & LowByteMask( state.BlockHeaderSize );
			state.FilePosition += state.BlockHeaderSize;
		}

		state.DecompressedPosition = (ulong)headerSize;
		state.BufferSizeNeeded = compressedStreamSize;
		state.CompressedStreamSize = compressedStreamSize;
		state.InputBoundary = unchecked(state.InputWindowMask - 6);
		state.DecompressedStreamSize = state.DecompressedSize;
		if ( state.DecompressedSize - 1 > state.OutputWindowMask )
		{
			state.DecompressedStreamSize = state.OutputWindowMask + 1;
			state.CompressedStreamSize = compressedStreamSize - state.BlockHeaderSize;
		}
		return state;
	}

	static bool Decode( byte[] input, byte[] output, DecoderState state, ulong inputLength, ulong outputLength, out string error )
	{
		error = null;
		var outputPosition = state.DecompressedPosition;
		var bitCount = state.CurrentBit;
		var current = state.Current;
		var inputPosition = state.FilePosition;
		var inputBoundary = Math.Min( state.InputBoundary, state.CompressedStreamSize );
		var entropyState = 0u;
		var nextEntropyState = entropyState;
		var normalize = bitCount != 0;
		var combined = normalize ? (ReadUInt64( input, inputPosition ) << (64 - (int)(byte)bitCount)) | current : current;

		while ( true )
		{
			if ( normalize )
			{
				var consumed = bitCount;
				bitCount &= 7;
				inputPosition += consumed >> 3;
				current = (ulong.MaxValue >> (int)bitCount) & combined;
				normalize = false;
			}

			var tableBase = entropyState << 8;
			var tableIndex = (int)(tableBase + (byte)current);
			var codeBitCount = DecodeTable[tableIndex + 512];
			var symbol = (sbyte)DecodeTable[tableIndex];
			bitCount += codeBitCount;
			current >>= codeBitCount;

			if ( symbol < 0 )
			{
				var literalCount = -symbol;
				nextEntropyState = 1;
				var isExtendedLiteral = literalCount == DecodeTable[entropyState + 1248];
				if ( isExtendedLiteral )
				{
					if ( (state.InputWindowMask & ~inputPosition) < 15
						|| (state.OutputWindowMask & ~outputPosition) < 15
						|| state.DecompressedSize - outputPosition < 16 )
					{
						literalCount = 1;
					}

					var low = (byte)current;
					var lengthBits = current >> 3;
					var selector = low & 7;
					uint baseLength;
					byte extraBitCount;
					if ( selector != 0 )
					{
						baseLength = DecodeTable[1232 + selector];
						extraBitCount = DecodeTable[1240 + selector];
					}
					else
					{
						var longSelector = (int)(lengthBits & 0xF);
						lengthBits >>= 4;
						bitCount += 4;
						baseLength = ReadTableUInt32( 1152 + longSelector * 4 );
						extraBitCount = DecodeTable[1216 + longSelector];
					}

					bitCount += (uint)extraBitCount + 3;
					current = lengthBits >> extraBitCount;
					literalCount += checked((int)(baseLength + (lengthBits & LowBitMask( extraBitCount ))));
				}

				if ( !CopyLiterals( input, output, inputPosition, outputPosition, literalCount, isExtendedLiteral, state.DecompressedSize, out error ) )
					return false;
				inputPosition += (uint)literalCount;
				outputPosition += (uint)literalCount;
			}
			else
			{
				nextEntropyState = 0;
				var lowNibble = (uint)current & 0xF;
				var wrapped = unchecked(lowNibble - 31);
				var prefixShift = (int)((wrapped >> 3) & 6);
				var distanceSelector = (int)((current >> prefixShift) & 0x3F);
				var longDistanceShift = (int)((24 * ((wrapped >> 3) & 2)) >> 4);
				var longDistanceBits = (int)((current >> 4) & (uint)longDistanceShift);
				var distancePower = 1u << ((int)lowNibble + longDistanceBits);
				var distanceExtraBits = prefixShift + DecodeTable[1088 + distanceSelector];
				var totalDistanceBits = distanceExtraBits + (int)lowNibble + longDistanceBits;
				bitCount += (uint)totalDistanceBits;
				var distance = 16UL * (distancePower + ((distancePower - 1) & (current >> distanceExtraBits)))
					+ DecodeTable[1024 + distanceSelector] - 16UL;
				current >>= totalDistanceBits;

				var isExtendedMatch = symbol == 17;
				int matchCount;
				if ( isExtendedMatch )
				{
					var low = (byte)current;
					var lengthBits = current >> 3;
					var selector = low & 7;
					ulong decodedLengthBits;
					uint baseLength;
					byte extraBitCount;
					if ( selector != 0 )
					{
						decodedLengthBits = lengthBits;
						baseLength = DecodeTable[1232 + selector];
						extraBitCount = DecodeTable[1240 + selector];
					}
					else
					{
						var longSelector = (int)(lengthBits & 0xF);
						decodedLengthBits = lengthBits >> 4;
						bitCount += 4;
						baseLength = ReadTableUInt32( 1152 + longSelector * 4 );
						extraBitCount = DecodeTable[1216 + longSelector];
						if ( bitCount + extraBitCount >= 61 )
						{
							decodedLengthBits |= (ulong)input[inputPosition++] << (61 - (int)(byte)bitCount);
							bitCount -= 8;
						}
					}

					bitCount += (uint)extraBitCount + 3;
					current = decodedLengthBits >> extraBitCount;
					matchCount = checked((int)(baseLength + (decodedLengthBits & LowBitMask( extraBitCount )) + 17));
					// The compact near-distance form shares thirteen bytes with the
					// decoder's look-ahead copy and therefore advances by length - 13.
					if ( distance < 8 ) matchCount -= 13;
				}
				else
				{
					matchCount = symbol;
				}

				if ( !CopyMatch( output, outputPosition, distance, matchCount, isExtendedMatch, state.DecompressedSize, out error ) )
					return false;
				outputPosition += (uint)matchCount;
			}

			entropyState = nextEntropyState;
			if ( inputPosition < inputBoundary )
			{
				combined = (ReadUInt64( input, inputPosition ) << (64 - (int)(byte)bitCount)) | current;
				normalize = true;
				continue;
			}

			if ( outputPosition == state.DecompressedStreamSize )
			{
				if ( outputPosition == state.DecompressedSize )
					return true;

				var bytesUntilInputWrap = state.InputWindowMask & unchecked(0UL - inputPosition);
				current >>= 1;
				bitCount++;
				if ( state.BlockHeaderSize > bytesUntilInputWrap )
				{
					inputPosition += bytesUntilInputWrap;
					if ( inputPosition > state.InputBoundary )
						state.InputBoundary += state.InputWindowMask + 1;
				}

				var blockHeaderPosition = inputPosition;
				inputPosition += state.BlockHeaderSize;
				var nextOutputBoundary = outputPosition + state.OutputWindowMask + 1;
				var compressedBlockSize = ReadUInt64( input, blockHeaderPosition ) & LowByteMask( state.BlockHeaderSize );
				state.BufferSizeNeeded += compressedBlockSize;
				state.CompressedStreamSize += compressedBlockSize;
				if ( nextOutputBoundary >= state.DecompressedSize )
				{
					nextOutputBoundary = state.DecompressedSize;
					state.CompressedStreamSize += state.BlockHeaderSize;
				}
				state.DecompressedStreamSize = nextOutputBoundary;
				if ( inputLength < state.BufferSizeNeeded || outputLength < nextOutputBoundary )
				{
					error = "PakFile ended before the next compressed block was available.";
					return false;
				}
			}

			if ( inputPosition >= state.InputBoundary )
			{
				inputPosition = (~state.InputWindowMask) & (inputPosition + 7);
				state.InputBoundary += state.InputWindowMask + 1;
			}
			inputBoundary = Math.Min( state.InputBoundary, state.CompressedStreamSize );
			combined = (ReadUInt64( input, inputPosition ) << (64 - (int)(byte)bitCount)) | current;
			normalize = true;
		}
	}

	static bool CopyLiterals( byte[] input, byte[] output, ulong inputPosition, ulong outputPosition, int count, bool isExtended, ulong outputLimit, out string error )
	{
		error = null;
		var physicalCount = isExtended ? count : 16;
		if ( count <= 0 || inputPosition + (uint)physicalCount > (ulong)input.Length
			|| outputPosition + (uint)count > outputLimit || outputPosition + (uint)physicalCount > (ulong)output.Length )
		{
			error = $"Invalid PakFile literal run ({count} bytes at input {inputPosition}, output {outputPosition}).";
			return false;
		}
		Buffer.BlockCopy( input, (int)inputPosition, output, (int)outputPosition, physicalCount );
		return true;
	}

	static bool CopyMatch( byte[] output, ulong outputPosition, ulong distance, int count, bool isExtended, ulong outputLimit, out string error )
	{
		error = null;
		var physicalCount = isExtended && distance >= 8 ? (count + 7) & ~7 : isExtended ? count : 16;
		if ( count <= 0 || distance > outputPosition || outputPosition + (uint)count > outputLimit
			|| outputPosition + (uint)physicalCount > (ulong)output.Length )
		{
			error = $"Invalid PakFile match ({count} bytes, distance {distance}, output {outputPosition}).";
			return false;
		}
		if ( distance == 0 ) return true;
		for ( var index = 0; index < count; index++ )
			output[(int)outputPosition + index] = output[(int)(outputPosition - distance) + index];
		for ( var index = count; index < physicalCount; index++ )
			output[(int)outputPosition + index] = output[(int)(outputPosition - distance) + index];
		return true;
	}

	static ulong ReadUInt64( byte[] buffer, ulong offset )
	{
		if ( offset > (ulong)buffer.Length - 8 ) throw new InvalidDataException( $"PakFile read exceeds input at {offset}." );
		return BinaryPrimitives.ReadUInt64LittleEndian( buffer.AsSpan( (int)offset, 8 ) );
	}

	static uint ReadTableUInt32( int offset ) => BinaryPrimitives.ReadUInt32LittleEndian( DecodeTable.AsSpan( offset, 4 ) );
	static ulong LowByteMask( uint byteCount ) => byteCount == 0 ? 0 : LowBitMask( checked((int)byteCount * 8) );
	static ulong LowBitMask( int bitCount ) => bitCount >= 64 ? ulong.MaxValue : bitCount <= 0 ? 0 : (1UL << bitCount) - 1;

	sealed class DecoderState
	{
		public ulong DecompressedSize;
		public ulong InputWindowMask;
		public ulong OutputWindowMask;
		public uint BlockHeaderSize;
		public ulong FilePosition;
		public ulong DecompressedPosition;
		public ulong BufferSizeNeeded;
		public ulong Current;
		public uint CurrentBit;
		public ulong InputBoundary;
		public ulong CompressedStreamSize;
		public ulong DecompressedStreamSize;
	}
}
