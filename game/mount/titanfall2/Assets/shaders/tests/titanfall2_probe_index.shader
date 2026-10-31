HEADER
{
	Description = "Titanfall 2 per-instance probe transport diagnostic";
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
	uint nT2InstanceIndex : SV_InstanceID;
};
struct PixelInput
{
	#include "common/pixelinput.hlsl"
	nointerpolation uint nT2ProbeInstance : TEXCOORD12;
};
VS
{
	#include "common/vertex.hlsl"
	PixelInput MainVs( VertexInput v )
	{
		PixelInput i = ProcessVertex( v );
		i.nT2ProbeInstance = v.nT2InstanceIndex;
		return FinalizeVertex( i );
	}
}
PS
{
	#include "common/pixel.hlsl"
	#include "titanfall2_probe_inputs.hlsl"
	RenderState( CullMode, NONE );
	float3 g_vT2ProbeNormal < Attribute( "T2ProbeNormal" ); Default3( 1, 0, 0 ); >;
	float4 MainPs( PixelInput i ) : SV_Target0
	{
		T2LightProbeData probe = T2ReadProbe( i.nT2ProbeInstance );
		return float4( T2ProbeIrradiance( probe, normalize( g_vT2ProbeNormal ), g_vT2ProbeSkyColor ), 1 );
	}
}
