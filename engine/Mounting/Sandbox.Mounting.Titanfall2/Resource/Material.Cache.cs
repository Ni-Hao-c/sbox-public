using static Titanfall2ProcessedCache;

public partial class Titanfall2Mount
{
	string MaterialCacheIdentity( string name ) =>
		$"{ContentFingerprint}|material-vmt-proxies-v5:{(_materialPaths.TryGetValue( name, out var path ) ? path : name).Replace( '\\', '/' ).ToLowerInvariant()}";

	internal bool TryGetCachedMaterialDescriptor( string name, out Titanfall2MaterialDescriptor descriptor )
	{
		if ( TryGetMaterialDescriptor( name, out descriptor ) ) return descriptor.SourceKind == Titanfall2MaterialSourceKind.Rpak;
		if ( !Titanfall2StreamingSettings.ProcessedCache || string.IsNullOrEmpty( name ) ) return false;
		Titanfall2MaterialDescriptor found = null;
		if ( !Titanfall2ProcessedCache.TryRead( "material", MaterialCacheIdentity( name ), reader =>
		{
			found = reader.Json<Titanfall2MaterialDescriptor>();
			if ( found.SourceKind != Titanfall2MaterialSourceKind.Rpak || string.IsNullOrEmpty( found.Name )
				|| found.TextureBindings is null || found.Constants is null || found.RuntimeExpressions is null )
				throw new InvalidDataException( "Invalid cached material descriptor." );
		} ) ) return false;
		RegisterMaterialDescriptor( found, persist: false );
		descriptor = found;
		return true;
	}

	void WriteMaterialCache( Titanfall2MaterialDescriptor descriptor ) =>
		Titanfall2ProcessedCache.Write( "material", MaterialCacheIdentity( descriptor.Name ), writer => WriteJson( writer, descriptor ) );
}
