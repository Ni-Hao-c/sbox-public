/// <summary>
/// Titanfall 2 RPAK v7 container inspection. Asset decoding is intentionally kept
/// separate from mounting so container metadata can be validated without exporting assets.
/// </summary>
static class RpakReader
{
	const uint Magic = 0x6B615052; // "RPak"
	const ushort Version = 7;
	const ushort PakFileCompressed = 1 << 8;
	const ushort OodleCompressed = 1 << 9;
	const ushort ZstdCompressed = 1 << 15;

	public static RpakInspection InspectDirectory( string directory )
	{
		var result = new RpakInspection();
		if ( !System.IO.Directory.Exists( directory ) ) return result;

		foreach ( var path in System.IO.Directory.EnumerateFiles( directory, "*.rpak", SearchOption.TopDirectoryOnly ) )
		{
			result.TotalFiles++;
			if ( !TryReadHeader( path, out var header, out _ ) )
			{
				result.UnsupportedFiles++;
				continue;
			}

			if ( (header.Flags & PakFileCompressed) != 0 ) result.PakFileCompressedFiles++;
			else if ( (header.Flags & (OodleCompressed | ZstdCompressed)) != 0 ) result.UnsupportedFiles++;
			else result.UncompressedFiles++;
		}

		return result;
	}

	public static bool TryReadHeader( string path, out RpakHeader header, out string error )
	{
		header = default;
		error = null;
		try
		{
			using var stream = new FileStream( path, FileMode.Open, FileAccess.Read, FileShare.Read );
			using var reader = new BinaryReader( stream );
			if ( reader.ReadUInt32() != Magic )
			{
				error = "Invalid RPAK magic.";
				return false;
			}

			var version = reader.ReadUInt16();
			var flags = reader.ReadUInt16();
			if ( version != Version )
			{
				error = $"Unsupported RPAK version {version}.";
				return false;
			}

			stream.Position = 0x18;
			var compressedSize = reader.ReadInt64();
			stream.Position = 0x28;
			var decompressedSize = reader.ReadInt64();
			stream.Position = 0x38;
			var streamPathBytes = reader.ReadInt16();
			var segments = reader.ReadUInt16();
			var pages = reader.ReadUInt16();
			var patchCount = reader.ReadInt16();
			var pointers = reader.ReadInt32();
			var assets = reader.ReadInt32();
			var guidReferences = reader.ReadInt32();
			var dependencies = reader.ReadInt32();

			header = new RpakHeader( version, flags, compressedSize, decompressedSize, streamPathBytes, segments, pages, patchCount, pointers, assets, guidReferences, dependencies );
			return true;
		}
		catch ( Exception e )
		{
			error = e.Message;
			return false;
		}
	}
}

readonly record struct RpakHeader(
	ushort Version,
	ushort Flags,
	long CompressedSize,
	long DecompressedSize,
	short StreamPathBytes,
	ushort SegmentCount,
	ushort PageCount,
	short PatchCount,
	int PointerCount,
	int AssetCount,
	int GuidReferenceCount,
	int DependencyCount );

sealed class RpakInspection
{
	public int TotalFiles { get; internal set; }
	public int UncompressedFiles { get; internal set; }
	public int PakFileCompressedFiles { get; internal set; }
	public int UnsupportedFiles { get; internal set; }
}
