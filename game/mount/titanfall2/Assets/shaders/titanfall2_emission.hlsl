#include "titanfall2_uv.hlsl"
CreateInputTexture2D( EmissiveMultiply, Srgb, 8, "None", "_emission_multiplier", "Emission modulation", Default4( 1, 1, 1, 1 ) );
Texture2D g_tEmissiveMultiply < Channel( RGBA, Box( EmissiveMultiply ), Srgb ); OutputFormat( DXT5 ); SrgbRead( true ); >;
float g_flT2HasEmissiveMultiply < Default( 0 ); >;
float g_flT2EmissiveMultiplyUvChannel < Default( -1 ); >;

float3 T2ReadEmissive( float2 rawUv, float2 fallbackUv )
{
	float2 uv = T2SurfaceUv( rawUv, fallbackUv, g_vT2AuxUvChannels.y );
	float3 emission = Tex2DS( g_tEmissive, g_sAniso, uv ).rgb;
	if ( g_flT2HasEmissiveMultiply > 0.5 )
	{
		float2 multiplyUv = T2SurfaceUv( rawUv, fallbackUv, g_flT2EmissiveMultiplyUvChannel );
		emission *= Tex2DS( g_tEmissiveMultiply, g_sAniso, multiplyUv ).rgb;
	}
	return emission;
}
