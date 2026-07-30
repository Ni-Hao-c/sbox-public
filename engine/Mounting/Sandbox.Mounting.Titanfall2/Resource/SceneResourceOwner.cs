/// <summary>
/// Releases scene-owned mount caches when a mounted map is torn down. Engine
/// resources may remain in the global streaming pool for reuse, but Titanfall's
/// decompressed package buffers and generated particle textures must not be kept
/// alive by the mount itself after returning to the menu.
/// </summary>
[Library]
public sealed class Titanfall2SceneResourceOwner : Component, Component.DontExecuteOnServer
{
	static readonly Sandbox.Diagnostics.Logger Log = new( "Titanfall2Lifetime" );
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
		if ( Sandbox.Mounting.Directory.Get( MountIdent ) is not Titanfall2Mount mount ) return;
		mount.ReleaseSceneResources();
		Log.Info( "Titanfall 2 map-owned package buffers and particle textures released." );
	}
}
