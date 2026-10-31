/// <summary>Adapts the retail sky cubemap to the native s&amp;box sky renderer.</summary>
sealed class Titanfall2SkyMaterialLoader : ResourceLoader<Titanfall2Mount>
{
	Texture _skyTexture;

	protected override object Load()
	{
		var texture = _skyTexture = Texture.Load( $"mount://{Host.Ident}/skies/skycube.vtex", false );
		if ( texture is null || !texture.IsValid() || texture.IsError )
		{
			Titanfall2Log.Warning( $"Unable to load the retail Titanfall 2 sky cubemap ({Path})." );
			return null;
		}

		var material = Material.Create( Path, "shaders/sky.shader", false );
		if ( material is null || !material.IsValid ) return null;
		// Use the actual shader attribute. Legacy input-name aliases are only expanded
		// for older game API versions, so "SkyTexture" leaves this cubemap unbound.
		if ( !material.Set( "g_tSkyTexture", texture ) )
		{
			Titanfall2Log.Warning( $"Unable to bind the retail Titanfall 2 sky cubemap ({Path})." );
			return null;
		}
		material.Set( "SkyTint", Color.White );
		return material;
	}
}
