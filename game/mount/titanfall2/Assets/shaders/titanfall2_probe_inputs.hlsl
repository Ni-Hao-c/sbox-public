#ifndef TITANFALL2_PROBE_INPUTS_HLSL
#define TITANFALL2_PROBE_INPUTS_HLSL

#include "titanfall2_probe_data.hlsl"

int g_nT2ProbeMode < Attribute( "T2ProbeMode" ); Default( 0 ); >;
float4 g_vT2ProbeRed < Attribute( "T2ProbeRed" ); Default4( 0, 0, 0, 0 ); >;
float4 g_vT2ProbeGreen < Attribute( "T2ProbeGreen" ); Default4( 0, 0, 0, 0 ); >;
float4 g_vT2ProbeBlue < Attribute( "T2ProbeBlue" ); Default4( 0, 0, 0, 0 ); >;
float4 g_vT2ProbeSky < Attribute( "T2ProbeSky" ); Default4( 0, 0, 0, 0 ); >;
float3 g_vT2ProbeSkyColor < Attribute( "T2ProbeSkyColor" ); Default3( 0, 0, 0 ); >;
int g_nT2EnvironmentEnabled < Attribute( "T2EnvironmentEnabled" ); Default( 0 ); >;
float3 g_vT2EnvironmentSkyColor < Attribute( "T2EnvironmentSkyColor" ); Default3( 0, 0, 0 ); >;
float3 T2CurrentSkyColor( float3 fallback ) { return g_nT2EnvironmentEnabled != 0 ? g_vT2EnvironmentSkyColor : fallback; }
int g_nT2ProbeWeights < Attribute( "T2ProbeWeights" ); Default( 0 ); >;
int g_nT2ProbeLightIds01 < Attribute( "T2ProbeLightIds01" ); Default( 0 ); >;
int g_nT2ProbeLightIds23 < Attribute( "T2ProbeLightIds23" ); Default( 0 ); >;
StructuredBuffer<T2LightProbeData> g_T2LightProbes < Attribute( "T2LightProbes" ); >;
StructuredBuffer<int> g_T2ProbeIndices < Attribute( "T2ProbeIndices" ); >;

T2LightProbeData T2ReadProbe( uint instanceIndex )
{
	if ( g_nT2ProbeMode == 2 ) return g_T2LightProbes[g_T2ProbeIndices[instanceIndex]];
	T2LightProbeData probe;
	probe.red = g_vT2ProbeRed;
	probe.green = g_vT2ProbeGreen;
	probe.blue = g_vT2ProbeBlue;
	probe.skyDirectionSunVisibility = g_vT2ProbeSky;
	probe.metadata = float4( g_nT2ProbeMode == 1 ? 1 : 0,
		asfloat( g_nT2ProbeWeights ), asfloat( g_nT2ProbeLightIds01 ), asfloat( g_nT2ProbeLightIds23 ) );
	return probe;
}

#endif
