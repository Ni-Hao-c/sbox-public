#ifndef T2_DETAIL_COLOUR_HLSL
#define T2_DETAIL_COLOUR_HLSL

float3 T2DetailMultiplier( float3 detail, float retail )
{
	// Retail modulation maps are linear data (RPAK BC1_UNORM), unlike the
	// sRGB base colour. The uberDet program subtracts half an 8-bit step,
	// then doubles, so byte 128 is the neutral multiplier of exactly one.
	if ( retail > 0.5 ) return saturate( detail - (0.5 / 255.0) ) * 2.0;
	// Preserve the legacy VMT path's existing colour-space behavior.
	return SrgbGammaToLinear( detail ) * 2.0;
}

#endif
