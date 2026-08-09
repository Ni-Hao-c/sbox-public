/// <summary>
/// Loads sound data from a Titanfall 2 loose file or RPak entry.
/// </summary>
class SoundLoader( ITitanfall2AssetSource source ) : ResourceLoader<Titanfall2Mount>
{
	readonly ITitanfall2AssetSource _source = source;

	protected override object Load()
	{
		if ( !_source.TryReadAllBytes( out var data, out var error ) )
		{
			Titanfall2Log.Warning( $"Failed to read Titanfall 2 sound '{Path}': {error}" );
			return null;
		}

		var sound = SoundFile.FromWav( Path, data );
		if ( sound is null )
			Titanfall2Log.Warning( $"Titanfall 2 sound creation returned null: {Path} ({data.Length} bytes, headless={Application.IsHeadless})" );
		return sound;
	}
}

/// <summary>Reconstructs one split MSTR BCF stream and decodes it through Titanfall 2's Miles/Bink runtime.</summary>
class MilesSoundLoader( MilesBankArchive archive, MilesSoundAsset asset ) : ResourceLoader<Titanfall2Mount>
{
	readonly MilesBankArchive _archive = archive;
	readonly MilesSoundAsset _asset = asset;

	protected override object Load()
	{
		if ( !_archive.TryReadBcf( _asset, out var bcf, out var readError ) )
		{
			Titanfall2Log.Warning( $"Failed to read Titanfall 2 BCF sound '{Path}': {readError}" );
			return null;
		}
		if ( !_archive.TryDecode( bcf, out var pcm, out var rate, out var channels, out var decodeError ) )
		{
			Titanfall2Log.Warning( $"Failed to decode Titanfall 2 BCF sound '{Path}': {decodeError}" );
			return null;
		}

		var sound = SoundFile.FromPcm( Path, pcm, new SoundFile.PcmOptions { Channels = channels, Rate = rate, Bits = 16 } );
		if ( sound is null )
			Titanfall2Log.Warning( $"Titanfall 2 Miles sound creation returned null: {Path} ({pcm.Length} PCM bytes, headless={Application.IsHeadless})" );
		return sound;
	}
}
