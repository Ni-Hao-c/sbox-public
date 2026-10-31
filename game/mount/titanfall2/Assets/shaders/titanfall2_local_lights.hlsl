#ifndef TITANFALL2_LOCAL_LIGHTS_HLSL
#define TITANFALL2_LOCAL_LIGHTS_HLSL

struct T2LocalLightData
{
	float4 positionRadius;
	float4 colorSpecular;
	float4 directionOuter;
	float4 coneFalloff; // inner cosine, polynomial linear/quadratic, flags (inverse=1, spot=2)
	float4 shape; // emitter radius, cone exponent selector
};

int g_nT2LocalLightCount < Attribute( "T2LocalLightCount" ); Default( 0 ); >;
float g_flT2LocalLightScale < Attribute( "T2LocalLightScale" ); Default( 1 ); >;
StructuredBuffer<T2LocalLightData> g_T2LocalLights < Attribute( "T2LocalLights" ); >;

float T2LocalAttenuation( T2LocalLightData light, float3 ray )
{
	float distanceSquared = dot( ray, ray );
	float radius = light.positionRadius.w;
	if ( radius <= 0 || distanceSquared >= radius * radius ) return 0;
	float attenuation;
	if ( (uint( light.coneFalloff.w ) & 1) != 0 )
	{
		float normalizedSquared = distanceSquared / (radius * radius);
		float window = max( 0, 1 - normalizedSquared * normalizedSquared );
		attenuation = window * window / (distanceSquared + 1);
	}
	else
	{
		// Compiled polynomial coefficients already include the authored range.
		// Preserve their signs; these are not reciprocal Source 1 falloff terms.
		float distance = sqrt( max( distanceSquared, 1 ) );
		attenuation = max( 0, 1 + light.coneFalloff.y * distance + light.coneFalloff.z * distance * distance );
	}
	if ( (uint( light.coneFalloff.w ) & 2) != 0 )
	{
		float3 direction = ray * rsqrt( max( distanceSquared, 0.000001 ) );
		float width = max( light.coneFalloff.x - light.directionOuter.w, 0.000001 );
		float cone = saturate( (dot( -light.directionOuter.xyz, direction ) - light.directionOuter.w) / width );
		attenuation *= lerp( cone, cone * cone, light.shape.y );
	}
	return attenuation;
}

void T2AddCompiledLight( inout LightingTerms_t lighting, CombinerInput combiner, uint index, float visibility )
{
	if ( g_nT2LocalLightCount <= 0 || g_flT2LocalLightScale <= 0 || visibility <= 0 || index >= uint( g_nT2LocalLightCount ) ) return;
	T2LocalLightData light = g_T2LocalLights[index];
	float3 ray = light.positionRadius.xyz - combiner.vPositionWs;
	float attenuation = T2LocalAttenuation( light, ray ) * saturate( visibility ) * g_flT2LocalLightScale;
	if ( attenuation <= 0 ) return;
	float3 direction = ray * rsqrt( max( dot( ray, ray ), 0.000001 ) );
	float3 view = CalculatePositionToCameraDirWs( combiner.vPositionWs );
	float3 diffuse, specular, transmissive;
	ComputeDiffuseAndSpecularTerms( diffuse, specular, transmissive, true, true, false,
		combiner, direction, view, (1 - combiner.vRoughness.xy) * 0.8 + 0.6,
		ClampToPositive( dot( combiner.vNormalWs, direction ) ) );
	float3 color = light.colorSpecular.rgb * attenuation;
	lighting.vDiffuse.rgb += diffuse * color * T2DiffuseFresnel( combiner, direction );
	lighting.vSpecular.rgb += specular * color * light.colorSpecular.w;
}

void T2AddProbeLocalLights( inout LightingTerms_t lighting, CombinerInput combiner, T2LightProbeData probe )
{
	if ( g_nT2LocalLightCount <= 0 || probe.metadata.x < 0.5 ) return;
	uint weights = asuint( probe.metadata.y );
	uint2 indices = asuint( probe.metadata.zw );
	[unroll] for ( uint channel = 0; channel < 4; ++channel )
	{
		uint index = (indices[channel / 2] >> ((channel % 2) * 16)) & 65535;
		float visibility = ((weights >> (channel * 8)) & 255) / 255.0;
		T2AddCompiledLight( lighting, combiner, index, visibility );
	}
}

#endif
