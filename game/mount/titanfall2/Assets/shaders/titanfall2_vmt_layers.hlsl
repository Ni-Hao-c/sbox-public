// Texture-layer composition is separate from the material's framebuffer blend.
// Keep one implementation for opaque, alpha-blended and additive 2D variants.
float g_flVmtMultiplyLayers < Default( 0 ); >;

float4 T2VmtCombineLayers( float4 first, float4 second )
{
	if ( g_flVmtMultiplyLayers > 0.5 )
	{
		return float4( first.rgb * second.rgb * g_vVmtColor1.rgb * g_vVmtColor2.rgb,
			first.a * second.a * g_flVmtLayerAlpha1 * g_flVmtLayerAlpha2 );
	}

	// Preserve the single-layer/missing-layer fallback and older explicit sum
	// materials. A cubemap second layer has its own shader and does not use this.
	return float4( first.rgb * g_vVmtColor1.rgb * g_flVmtLayerAlpha1 * saturate( g_flVmtHasTexture1 )
		+ second.rgb * g_vVmtColor2.rgb * g_flVmtLayerAlpha2 * saturate( g_flVmtHasTexture2 ),
		max( first.a * g_flVmtLayerAlpha1 * saturate( g_flVmtHasTexture1 ),
			second.a * g_flVmtLayerAlpha2 * saturate( g_flVmtHasTexture2 ) ) );
}
