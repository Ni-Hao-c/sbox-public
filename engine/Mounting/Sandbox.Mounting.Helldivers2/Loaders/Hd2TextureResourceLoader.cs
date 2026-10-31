namespace Sandbox.Mounting.Helldivers2;

/// <summary>
/// Loads one mounted Helldivers 2 <c>texture</c> resource as a <see cref="Texture"/>. The game's
/// BCn blocks are handed to the engine untouched, so this is a copy and an upload rather than a decode.
/// </summary>
internal sealed class Hd2TextureResourceLoader : ResourceLoader<Helldivers2Mount>
{
	readonly Hd2Resource _resource;

	public Hd2TextureResourceLoader( Hd2Resource resource )
	{
		_resource = resource;
	}

	public Hd2Resource Resource => _resource;

	protected override object Load()
	{
		var directory = Host?.Directory;
		if ( directory is null ) return null;

		if ( !Hd2TextureReader.TryCreate( directory, _resource, out var texture, out var error ) )
		{
			Hd2Log.Warning( $"Unable to load Helldivers 2 texture {_resource.Name} from bundle {_resource.Table.Id}: {error}" );
			return null;
		}

		return texture;
	}
}
