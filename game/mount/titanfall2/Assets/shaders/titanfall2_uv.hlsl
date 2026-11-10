// T2_MATERIAL_DECLARATIONS: ordinary and GPU-table paths share the same shading functions.
#ifndef TITANFALL2_SURFACE_UV
#define TITANFALL2_SURFACE_UV

#if !D_T2_MATERIAL_TABLE
float4 g_vT2SurfaceUvChannels < Default4( -1, -1, -1, -1 ); >;
#endif

#if !D_T2_MATERIAL_TABLE
float4 g_vT2SurfaceUvChannels2 < Default4( -1, -1, -1, -1 ); >;
#endif

#if !D_T2_MATERIAL_TABLE
float4 g_vT2AuxUvChannels < Default4( -1, -1, -1, -1 ); >;
#endif

#if !D_T2_MATERIAL_TABLE
float g_flT2LayerBlendUvChannel < Default( -1 ); >;
#endif

#if !D_T2_MATERIAL_TABLE
float4 g_vT2Uv2RotScale < Default4( 1, 0, 0, 1 ); >;
#endif

#if !D_T2_MATERIAL_TABLE
float4 g_vT2Uv2Translate < Default4( 0, 0, 0, 0 ); >;
#endif

#if !D_T2_MATERIAL_TABLE
float4 g_vT2Uv3RotScale < Default4( 1, 0, 0, 1 ); >;
#endif

#if !D_T2_MATERIAL_TABLE
float4 g_vT2Uv3Translate < Default4( 0, 0, 0, 0 ); >;
#endif
float2 T2TransformSurfaceUv( float2 uv, float4 rows, float4 translation )
{
	return float2( dot( uv, rows.xy ), dot( uv, rows.zw ) )
		+ translation.xy * lerp( 1.0, g_flTime, saturate( translation.z ) );
}

float2 T2SurfaceUv( float2 rawUv, float2 fallbackUv, float channel )
{
	if ( channel < -0.5 ) return fallbackUv;
	if ( channel < 0.5 ) return rawUv;
	if ( channel < 1.5 ) return T2TransformSurfaceUv( rawUv, g_vT2Uv1RotScale, g_vT2Uv1Translate );
	if ( channel < 2.5 ) return T2TransformSurfaceUv( rawUv, g_vT2Uv2RotScale, g_vT2Uv2Translate );
	return T2TransformSurfaceUv( rawUv, g_vT2Uv3RotScale, g_vT2Uv3Translate );
}
#endif
