/// <summary>
/// Loads a texture from a Titanfall 2 loose file or RPak entry.
/// </summary>
class Titanfall2TextureLoader( ITitanfall2AssetSource source, bool animatedVtfAtlas = false ) : Titanfall2ResourceLoader, ITitanfall2SceneResource
{
	readonly ITitanfall2AssetSource _source = source;
	readonly bool _animatedVtfAtlas = animatedVtfAtlas;
	readonly object _prepareLock = new();
	PreparedTexture _prepared;
	Action<long> _releaseBudget;
	bool _loaded;

	sealed record PreparedTexture( RpakTextureData? Packed, byte[] Bytes, string Error )
	{
		public long Length => Packed?.Data?.LongLength ?? Bytes?.LongLength ?? 0;
	}

	PreparedTexture ReadData()
	{
		if ( _source is RpakTextureAssetSource packed )
		{
			// The cache key and worker decode must use one snapshot even if the player
			// applies another quality level while this texture is being prepared.
			var maximumDimension = Titanfall2StreamingSettings.TextureMaxDimension;
			var identity = Titanfall2StreamingSettings.ProcessedCache
				? $"{Host.ContentFingerprint}|{Path}|texture:{maximumDimension}" : null;
			if ( identity is not null && Titanfall2TextureCache.TryRead( identity, out var cached ) ) return new( cached, null, null );
			if ( !packed.TryReadTexture( out var texture, out var error, maximumDimension ) ) return new( null, null, error );
			if ( identity is not null ) Titanfall2TextureCache.Write( identity, texture );
			return new( texture, null, null );
		}
		return _source.TryReadAllBytes( out var bytes, out var readError )
			? new( null, bytes, null ) : new( null, null, readError );
	}

	// Only CPU data may be prepared by a worker. Texture creation stays in Load().
	internal bool Prepare( Func<long, bool> reserve, Action<long> release )
	{
		lock ( _prepareLock )
		{
			if ( _loaded || _prepared is not null ) return true;
			var data = ReadData();
			var withinBudget = reserve( data.Length );
			_prepared = data;
			// Keep the payload that crosses the target, account for it, and tell the
			// caller to stop reading this material's remaining textures. Load can consume
			// it without decoding again; both consumption and cancellation return its bytes.
			_releaseBudget = release;
			return withinBudget;
		}
	}

	internal void ClearPreparation()
	{
		lock ( _prepareLock )
		{
			if ( _prepared is not null ) _releaseBudget?.Invoke( _prepared.Length );
			_prepared = null;
			_releaseBudget = null;
		}
	}

	public bool ReleaseSceneResource()
	{
		lock ( _prepareLock )
		{
			if ( !ReleaseLoaderResult() ) return false;
			// Let the next map's material prefetch decode this texture on a worker again.
			_loaded = false;
			ClearPreparation();
			return true;
		}
	}

	protected override object Load()
	{
		PreparedTexture prepared;
		lock ( _prepareLock )
		{
			prepared = _prepared ?? ReadData();
			ClearPreparation();
			_loaded = true;
		}
		if ( prepared.Error is not null )
		{
			Titanfall2Log.Warning( $"Failed to read Titanfall 2 texture '{Path}': {prepared.Error}" );
			return Texture.White;
		}
		if ( prepared.Packed is { } packedTexture )
		{
			if ( RpakTextureDecoder.TryCreate( packedTexture, out var runtimeTexture, out var packedError ) ) return runtimeTexture;
			Titanfall2Log.Warning( $"Failed to decode Titanfall 2 RPAK texture '{Path}': {packedError}" );
			return Texture.White;
		}

		var data = prepared.Bytes;
		string error;

		if ( VtfTextureDecoder.IsVtf( data ) )
		{
			if ( VtfTextureDecoder.TryCreate( data, _animatedVtfAtlas, out var vtfTexture, out error ) ) return vtfTexture;
			Titanfall2Log.Warning( $"Failed to decode Titanfall 2 VTF texture '{Path}': {error}" );
			return _animatedVtfAtlas ? Texture.Transparent : Texture.White;
		}

		if ( TryLoadDds( data, out var tex ) )
			return tex;

		Titanfall2Log.Warning( $"Unsupported texture format: {Path}" );
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
}
