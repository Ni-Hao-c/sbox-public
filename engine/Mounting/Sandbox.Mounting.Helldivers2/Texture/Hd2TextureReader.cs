namespace Sandbox.Mounting.Helldivers2;

/// <summary>
/// Turns a Stingray <c>texture</c> resource into a <see cref="Sandbox.Texture"/>.
///
/// A texture resource is an ordinary DDS file wrapped in a 0xC0-byte streamable header, and the
/// DDS bytes are spread across the resource's three data streams: the image starts at offset 0xC0
/// of the main stream and, for mip chains too large to sit there, keeps going through the
/// <c>.stream</c> and <c>.gpu_resources</c> companions. Stitching the three streams back together
/// in main/stream/gpu order and handing the result to <c>TextureLoader.FromDds</c> yields a
/// texture holding the game's original BCn blocks: nothing is transcoded.
/// </summary>
internal static class Hd2TextureReader
{
	/// <summary>
	/// Reads the headers of a texture whose three data streams have already been fetched. The
	/// <c>stream</c> and <c>gpu</c> spans may be empty; the main span must hold the whole 0xC0-byte
	/// streamable header. Returns false with a readable reason instead of throwing.
	/// </summary>
	public static bool TryRead( ReadOnlySpan<byte> main, ReadOnlySpan<byte> stream, ReadOnlySpan<byte> gpu, out Hd2TextureInfo info, out string error )
	{
		info = null;
		error = null;

		if ( !Hd2TextureHeader.TryParse( main, out var stingray, out error ) ) return false;

		if ( !Hd2DdsHeader.TryParse( main[Hd2TextureHeader.HeaderSize..], out var dds, out var ddsError ) )
		{
			error = $"the DDS payload at 0x{Hd2TextureHeader.HeaderSize:x} of the main stream is malformed: {ddsError}";
			return false;
		}

		info = new Hd2TextureInfo( stingray, dds, main.Length, stream.Length, gpu.Length );
		return true;
	}

	/// <summary>Reads the headers of a texture straight out of the mounted game data.</summary>
	public static bool TryRead( Hd2DataDirectory directory, in Hd2Resource resource, out Hd2TextureInfo info, out string error )
	{
		info = null;
		if ( !TryReadStreams( directory, resource, out var main, out var stream, out var gpu, out error ) ) return false;

		return TryRead( main, stream, gpu, out info, out error );
	}

	/// <summary>
	/// Builds a runtime texture from the three data streams of a texture resource. The engine's DDS
	/// loader only ever builds a plain 2D texture, so a cubemap, volume texture or texture array is
	/// refused with an explicit reason rather than silently handed back as its first slice.
	/// </summary>
	public static bool TryCreate( ReadOnlySpan<byte> main, ReadOnlySpan<byte> stream, ReadOnlySpan<byte> gpu, out Texture texture, out string error )
	{
		texture = null;
		if ( !TryRead( main, stream, gpu, out var info, out error ) ) return false;

		var shape = DescribeShape( info.Dds );
		if ( shape is not null )
		{
			error = $"the {info.Dds.FormatName} image is {shape}, which the engine's DDS loader cannot build as a single texture";
			texture = null;
			return false;
		}

		var payload = Assemble( main, stream, gpu );

		try
		{
			texture = TextureLoader.FromDds( payload );
		}
		catch ( Exception exception )
		{
			error = $"the engine could not build a {info.Dds.FormatName} texture ({info.Dds}) from {payload.Length:n0} bytes: {exception.Message}";
			texture = null;
			return false;
		}

		if ( texture is null || !texture.IsValid() )
		{
			error = $"the engine returned no usable texture for the {info.Dds.FormatName} image ({info.Dds})";
			texture = null;
			return false;
		}

		return true;
	}

	/// <summary>Builds a runtime texture straight from the mounted game data.</summary>
	public static bool TryCreate( Hd2DataDirectory directory, in Hd2Resource resource, out Texture texture, out string error )
	{
		texture = null;
		if ( !TryReadStreams( directory, resource, out var main, out var stream, out var gpu, out error ) ) return false;

		return TryCreate( main, stream, gpu, out texture, out error );
	}

	/// <summary>One line describing what a texture payload holds, for diagnostics.</summary>
	public static string Describe( ReadOnlySpan<byte> main, ReadOnlySpan<byte> stream, ReadOnlySpan<byte> gpu )
		=> TryRead( main, stream, gpu, out var info, out var error ) ? info.ToString() : $"unreadable texture: {error}";

	/// <summary>One line describing a texture resource of the mounted game, for diagnostics.</summary>
	public static string Describe( Hd2DataDirectory directory, in Hd2Resource resource )
	{
		var label = $"texture {resource.Name}";

		return TryRead( directory, resource, out var info, out var error ) ? $"{label}: {info}" : $"{label}: {error}";
	}

	/// <summary>Null for a plain 2D image, otherwise what the engine's DDS loader cannot express.</summary>
	static string DescribeShape( Hd2DdsHeader dds )
	{
		if ( dds.IsCubemap ) return $"a cubemap of {dds.ArraySize} cube(s)";
		if ( dds.IsVolume ) return $"a volume texture {dds.Width}x{dds.Height}x{dds.Depth}";
		if ( dds.ArraySize > 1 ) return $"a {dds.ArraySize} element texture array";

		return null;
	}

	/// <summary>
	/// Rebuilds the DDS file: everything after the streamable header of the main stream, followed
	/// by the whole stream and gpu companions, which is the order the data compiler laid them out in.
	/// </summary>
	static byte[] Assemble( ReadOnlySpan<byte> main, ReadOnlySpan<byte> stream, ReadOnlySpan<byte> gpu )
	{
		// UNVERIFIED: this follows filediver's texture extractor, which concatenates main, then
		// .stream, then .gpu_resources and hands the result to a DDS decoder as one file. The head of
		// the payload (the 0xC0 streamable header and the DDS header behind it) has been confirmed
		// against the game's own files, but whether a mip chain that spills into .stream or
		// .gpu_resources stays byte-contiguous with the end of the main stream could not be checked
		// here, because no readable Helldivers 2 install was available to this session.
		var head = main.Length - Hd2TextureHeader.HeaderSize;
		var payload = new byte[head + stream.Length + gpu.Length];

		main[Hd2TextureHeader.HeaderSize..].CopyTo( payload );
		stream.CopyTo( payload.AsSpan( head ) );
		gpu.CopyTo( payload.AsSpan( head + stream.Length ) );

		return payload;
	}

	static bool TryReadStreams( Hd2DataDirectory directory, in Hd2Resource resource, out byte[] main, out byte[] stream, out byte[] gpu, out string error )
	{
		main = null;
		stream = null;
		gpu = null;
		error = null;

		if ( directory is null )
		{
			error = "no Helldivers 2 data directory is open";
			return false;
		}

		if ( !resource.File.Has( Hd2DataType.Main ) )
		{
			error = $"{resource} has no main data stream";
			return false;
		}

		if ( !directory.TryRead( resource, Hd2DataType.Main, out main ) || main is null )
		{
			error = $"unable to read the {resource.File.SizeOf( Hd2DataType.Main )} byte main data stream of {resource}";
			return false;
		}

		if ( resource.File.Has( Hd2DataType.Stream ) && !directory.TryRead( resource, Hd2DataType.Stream, out stream ) )
			Hd2Log.Warning( $"Unable to read the {resource.File.SizeOf( Hd2DataType.Stream )} byte stream data of {resource}." );

		if ( resource.File.Has( Hd2DataType.Gpu ) && !directory.TryRead( resource, Hd2DataType.Gpu, out gpu ) )
			Hd2Log.Warning( $"Unable to read the {resource.File.SizeOf( Hd2DataType.Gpu )} byte gpu data of {resource}." );

		return true;
	}
}
