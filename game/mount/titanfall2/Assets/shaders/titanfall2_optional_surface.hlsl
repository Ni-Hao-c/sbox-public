#include "titanfall2_detail.hlsl"
// Shared by default/cutout. Decisions use current material values so animated
// proxies remain live. GPU-table values are constant over each primitive.
float2 T2ApplyDistortion( float2 uv )
{
	#if D_T2_SIMPLE_SURFACE
		return uv;
	#else
	[branch] if ( all( g_vT2UvDistortion.xy == float2( 0, 0 ) ) ) return uv;
	float2 vector = Tex2DS( g_tDistortion, g_sAniso, uv ).rg * 2.0 - 1.0;
	return uv + vector * g_vT2UvDistortion.xy;
	#endif
}

float3 T2ApplyDetail( float3 albedo, float2 uv, float retail )
{
	#if D_T2_SIMPLE_SURFACE
		return albedo;
	#else
	[branch] if ( g_flT2DetailBlend <= 0 ) return albedo;
	float3 detail = T2DetailMultiplier( Tex2DS( g_tDetail, g_sAniso, uv ).rgb, retail );
	return albedo * lerp( float3( 1, 1, 1 ), detail, saturate( g_flT2DetailBlend ) );
	#endif
}
