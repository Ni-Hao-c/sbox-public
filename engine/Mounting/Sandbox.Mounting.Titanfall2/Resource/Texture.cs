/// <summary>
/// Loads a texture from a Titanfall 2 loose file or RPak entry.
/// </summary>
class Titanfall2TextureLoader( ITitanfall2AssetSource source, bool animatedVtfAtlas = false ) : ResourceLoader<Titanfall2Mount>
{
	readonly ITitanfall2AssetSource _source = source;
	readonly bool _animatedVtfAtlas = animatedVtfAtlas;

	protected override object Load()
	{
		if ( _source is ITitanfall2TextureSource packedTexture )
		{
			if ( packedTexture.TryCreateTexture( out var runtimeTexture, out var packedError ) ) return runtimeTexture;
			Log.Warning( $"Failed to decode Titanfall 2 RPAK texture '{Path}': {packedError}" );
			return Texture.White;
		}

		if ( !_source.TryReadAllBytes( out var data, out var error ) )
		{
			Log.Warning( $"Failed to read Titanfall 2 texture '{Path}': {error}" );
			return Texture.White;
		}

		if ( VtfTextureDecoder.IsVtf( data ) )
		{
			if ( VtfTextureDecoder.TryCreate( data, _animatedVtfAtlas, out var vtfTexture, out error ) ) return vtfTexture;
			Log.Warning( $"Failed to decode Titanfall 2 VTF texture '{Path}': {error}" );
			return _animatedVtfAtlas ? Texture.Transparent : Texture.White;
		}

		if ( TryLoadDds( data, out var tex ) )
			return tex;

		if ( TryLoadTga( data, out tex ) )
			return tex;

		Log.Warning( $"Unsupported texture format: {Path}" );
		return Texture.White;
	}

	static bool TryLoadDds( byte[] data, out Texture tex )
	{
		tex = null;
		if ( data.Length < 4 || data[0] != 'D' || data[1] != 'D' || data[2] != 'S' || data[3] != ' ' )
			return false;

		try
		{
			tex = TextureLoader.FromDds( data );
			return tex.IsValid();
		}
		catch ( Exception )
		{
			tex = null;
			return false;
		}
	}

	static bool TryLoadTga( byte[] data, out Texture tex )
	{
		tex = null;
		if ( data.Length < 18 )
			return false;

		if ( data[2] != 2 && data[2] != 3 && data[2] != 10 )
			return false;

		return false;
	}
}
