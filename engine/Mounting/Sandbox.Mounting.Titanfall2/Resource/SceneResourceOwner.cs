/// <summary>
/// Releases resources owned exclusively by this scene. Shared mount caches have
/// the mount's lifetime and must survive destruction of an editor or play clone.
/// </summary>
[Library]
public sealed class Titanfall2SceneResourceOwner : Component, Component.DontExecuteOnServer
{
	readonly List<IDisposable> _ownedResources = new();

	[Property, Hide]
	public string MountIdent { get; set; }

	internal void Configure( string mountIdent, IEnumerable<IDisposable> ownedResources = null )
	{
		MountIdent = mountIdent;
		if ( ownedResources is not null )
			_ownedResources.AddRange( ownedResources.Where( static resource => resource is not null ) );
	}

	protected override void OnDestroy()
	{
		foreach ( var resource in _ownedResources )
			resource.Dispose();
		_ownedResources.Clear();
	}
}
