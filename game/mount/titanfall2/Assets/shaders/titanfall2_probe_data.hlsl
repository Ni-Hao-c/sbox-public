#ifndef TITANFALL2_PROBE_DATA_HLSL
#define TITANFALL2_PROBE_DATA_HLSL

struct T2LightProbeData
{
	float4 red;
	float4 green;
	float4 blue;
	float4 skyDirectionSunVisibility;
	float4 metadata; // x = valid; yzw = bitcast packed weights and four ushort light IDs
};

float3 T2ProbeIrradiance( T2LightProbeData probe, float3 normal, float3 skyColor )
{
	float4 direction = float4( normal, 1 );
	float3 irradiance = max( float3( dot( probe.red, direction ), dot( probe.green, direction ),
		dot( probe.blue, direction ) ), 0 );
	float3 skyDirection = probe.skyDirectionSunVisibility.xyz;
	float lengthSquared = dot( skyDirection, skyDirection );
	float normalDot = dot( normal, skyDirection );
	float visibility = saturate( (max( lengthSquared, normalDot ) + normalDot) * sqrt( lengthSquared ) * 0.5 );
	return irradiance + skyColor * visibility;
}

#endif
