// Lyrtrans uses the same authored ramp as Lyr, but reveals the underlying
// surface instead of sampling a second material. Vertex alpha is an input to
// that ramp, not another multiplier after it.
#include "titanfall2_uv.hlsl"
float g_flT2LayeredOpacity < Default( 0 ); >;
float g_flT2LayerBlendRamp < Default( 0 ); >;
CreateInputTexture2D( LayerBlend, Linear, 8, "None", "_mask", "Layer opacity", Default4( 0, 0, 0, 1 ) );
Texture2D g_tLayerBlend < Channel( RGBA, Box( LayerBlend ), Linear ); OutputFormat( DXT5 ); SrgbRead( false ); >;

float T2DecalCoverage( PixelInput i, float2 uv )
{
	if ( g_flT2LayeredOpacity < 0.5 ) return i.vVertexColor.a;
	float2 maskUv = T2SurfaceUv( i.vTextureCoords.xy, uv, g_flT2LayerBlendUvChannel );
	float mask = Tex2DS( g_tLayerBlend, g_sAniso, maskUv ).r;
	float weight = saturate( (g_flT2LayerBlendRamp + 1.0) * i.vVertexColor.a
		+ g_flT2LayerBlendRamp * (mask - 1.0) );
	return 1.0 - weight;
}
