// The vista is authored in miniature (Angel City's cards are only a few units
// from sky_camera). The native 3D skybox inherits the playable camera's planes;
// its Scale changes camera translation, not those planes. Keep the authored XY/W
// projection and perspective-correct UVs, but use a reversed infinite projection
// with a small near plane in the isolated sky world. Depth still orders its cards.
float g_flT2SkyProjectionNear < Attribute( "T2SkyProjectionNear" ); Default( 0.0 ); >;

PixelInput T2FinalizeSkyVertex( PixelInput i )
{
	i = FinalizeVertex( i );
	if ( g_flT2SkyProjectionNear > 0.0 ) i.vPositionPs.z = g_flT2SkyProjectionNear;
	return i;
}
