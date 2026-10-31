using Titanfall2;

/// <summary>
/// Restores the map's compiled-light resources when a mounted scene is cloned.
/// View attributes reach both normal renderers and aggregate indirect draws.
/// Material parameters cannot bind per-view structured buffers.
/// </summary>
[Library]
public sealed class Titanfall2LocalLightController : Component, Component.ExecuteInEditor, Component.DontExecuteOnServer
{
	[Property, Hide] public string EncodedLights { get; set; }
	[Property, Hide] public int[] SourceLightPages { get; set; } = [];
	Titanfall2LocalLightBuffer _lights;
	GpuBuffer<int> _pages;

	internal void Configure( IReadOnlyList<Titanfall2WorldLight> lights, IReadOnlyList<short> pages )
	{
		Release();
		EncodedLights = Titanfall2LocalLightBuffer.Encode( lights );
		var compactToSource = lights.Select( ( light, index ) => (light, index) )
			.Where( pair => pair.light.IsRealTime ).Select( pair => pair.index ).ToArray();
		SourceLightPages = pages.Select( index => index >= 0 && index < compactToSource.Length ? compactToSource[index] : -1 ).ToArray();
		Restore();
	}

	protected override Task OnLoad( LoadingContext context ) { Restore(); return Task.CompletedTask; }
	protected override void OnEnabled() => Restore();

	void Restore()
	{
		if ( _lights is not null || string.IsNullOrEmpty( EncodedLights ) ) return;
		try
		{
			_lights = Titanfall2LocalLightBuffer.Restore( EncodedLights );
			if ( SourceLightPages.Length > 0 )
			{
				_pages = new GpuBuffer<int>( SourceLightPages.Length );
				_pages.SetData( SourceLightPages.AsSpan() );
			}
			_lights.Apply( Scene.RenderAttributes );
			Scene.RenderAttributes.Set( "T2LightPageEntryCount", SourceLightPages.Length );
			if ( _pages is not null ) Scene.RenderAttributes.Set( "T2LightPages", _pages );
		}
		catch
		{
			Release();
			throw;
		}
	}

	/// <summary>
	/// Binds the compiled lights to one object. Instanced prop batches overwrite the shared
	/// render-context light count, so per-object probe users must carry their own binding.
	/// </summary>
	internal void ApplyTo( RenderAttributes attributes )
	{
		if ( _lights is not null ) _lights.Apply( attributes );
		else attributes.Set( "T2LocalLightCount", 0 );
	}

	protected override void OnDisabled() => Release();
	protected override void OnDestroy() => Release();

	void Release()
	{
		if ( _lights is null ) return;
		Scene.RenderAttributes.Set( "T2LocalLightCount", 0 );
		Scene.RenderAttributes.Set( "T2LightPageEntryCount", 0 );
		Scene.RenderAttributes.Set( "T2LocalLights", (GpuBuffer)null );
		Scene.RenderAttributes.Set( "T2LightPages", (GpuBuffer)null );
		_pages?.Dispose();
		_lights.Dispose();
		_pages = null;
		_lights = null;
	}
}
