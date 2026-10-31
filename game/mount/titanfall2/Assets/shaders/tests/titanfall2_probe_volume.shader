HEADER
{
	Description = "Titanfall 2 native light-probe format diagnostic";
}
FEATURES
{
	#include "common/features.hlsl"
}
MODES
{
	Forward();
}
COMMON
{
	#include "common/shared.hlsl"
	#include "procedural.hlsl"
	#define CUSTOM_MATERIAL_INPUTS
}
struct VertexInput
{
	#include "common/vertexinput.hlsl"
};
struct PixelInput
{
	#include "common/pixelinput.hlsl"
};
VS
{
	#include "common/vertex.hlsl"
	PixelInput MainVs( VertexInput v ) { return FinalizeVertex( ProcessVertex( v ) ); }
}
PS
{
	#include "common/pixel.hlsl"
	RenderState( CullMode, NONE );
	float3 g_vT2ProbeNormal < Attribute( "T2ProbeNormal" ); Default3( 1, 0, 0 ); >;
	float4 MainPs( PixelInput i ) : SV_Target0
	{
		float3 cube[6];
		SampleLightProbeVolume( cube, i.vPositionWithOffsetWs + g_vHighPrecisionLightingOffsetWs.xyz );
		return float4( SampleIrradiance( cube, normalize( g_vT2ProbeNormal ) ), 1 );
	}
}
