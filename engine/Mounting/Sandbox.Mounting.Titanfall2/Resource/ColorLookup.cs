using Titanfall2;

/// <summary>
/// A retail 32-cubed RGB colour-correction table, mounted as a volume texture. Registering the
/// table as its own resource keeps it out of the serialized scene: a map's correction component
/// stores only this resource path, not the 98,304 bytes of samples.
/// </summary>
sealed class Titanfall2ColorLookupLoader( ITitanfall2AssetSource source ) : ResourceLoader<Titanfall2Mount>
{
	const int Size = 32;
	internal const int RgbLength = Size * Size * Size * 3;

	protected override object Load()
	{
		if ( !source.TryReadAllBytes( out var rgb, out var error ) )
			throw new FileNotFoundException( $"Cannot read the Titanfall 2 colour-correction table '{Path}': {error}" );

		return CreateTexture( Path, rgb );
	}

	/// <summary>
	/// Expands the packed RGB table into the RGBA layout the volume formats accept. The retail
	/// table is authored for a post-display lookup, so it is uploaded untouched rather than
	/// resampled into the engine's smaller native grading LUT.
	/// </summary>
	internal static Texture CreateTexture( string name, byte[] rgb )
	{
		if ( rgb?.Length != RgbLength )
			throw new InvalidDataException( $"A retail colour lookup must contain 32 cubed RGB samples ('{name}')." );

		var rgba = new byte[Size * Size * Size * 4];
		for ( var sample = 0; sample < rgb.Length / 3; ++sample )
		{
			rgb.AsSpan( sample * 3, 3 ).CopyTo( rgba.AsSpan( sample * 4 ) );
			rgba[sample * 4 + 3] = 255;
		}

		return Texture.CreateVolume( Size, Size, Size )
			.WithFormat( ImageFormat.RGBA8888 )
			.WithName( name )
			.WithStaticUsage()
			.WithData( rgba )
			.Finish();
	}
}
