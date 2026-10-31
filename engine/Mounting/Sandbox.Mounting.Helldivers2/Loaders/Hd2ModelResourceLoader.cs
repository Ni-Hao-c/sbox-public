namespace Sandbox.Mounting.Helldivers2;

/// <summary>Loads one mounted Helldivers 2 <c>unit</c> resource as a <see cref="Model"/>.</summary>
internal sealed class Hd2ModelResourceLoader : ResourceLoader<Helldivers2Mount>
{
	/// <summary>
	/// Resources that already failed once. The editor's asset system retries a mounted asset every
	/// time something asks for it, so without this a unit that cannot be built (a terrain unit, for
	/// one) would log the same failure for every retry.
	/// </summary>
	static readonly HashSet<ulong> Failed = [];

	readonly Hd2Resource _resource;

	public Hd2ModelResourceLoader( Hd2Resource resource )
	{
		_resource = resource;
	}

	public Hd2Resource Resource => _resource;

	protected override object Load()
	{
		var directory = Host?.Directory;
		if ( directory is null ) return null;

		if ( Failed.Contains( _resource.Name.Value ) ) return null;

		if ( !Hd2ModelBuilder.TryBuild( directory, _resource, out var model, out var error ) )
		{
			Failed.Add( _resource.Name.Value );
			Hd2Log.Warning( $"Unable to load Helldivers 2 model {_resource.Name} from bundle {_resource.Table.Id}: {error}" );
			return null;
		}

		return model;
	}
}
