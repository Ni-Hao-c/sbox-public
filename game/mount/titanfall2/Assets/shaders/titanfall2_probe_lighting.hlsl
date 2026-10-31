#ifndef TITANFALL2_PROBE_LIGHTING_HLSL
#define TITANFALL2_PROBE_LIGHTING_HLSL

#include "titanfall2_probe_inputs.hlsl"

CombinerInput T2MaterialToCombinerInput( Material m, float3 specularColor )
{
	CombinerInput combiner = ShadingModelStandard::MaterialToCombinerInput( m );
	if ( g_flT2SpecularWorkflow > 0.5 )
	{
		combiner.vDiffuseColor = m.Albedo;
		combiner.vSpecularColor = saturate( specularColor );
	}
	return combiner;
}

float3 T2DiffuseFresnel( CombinerInput combiner, float3 directionToLight )
{
	if ( g_flT2SpecularWorkflow < 0.5 ) return float3( 1, 1, 1 );
	float3 view = CalculatePositionToCameraDirWs( combiner.vPositionWs );
	float3 sum = view + directionToLight;
	float3 halfDirection = sum * rsqrt( max( dot( sum, sum ), 0.000001 ) );
	float edge = pow( 1 - saturate( dot( view, halfDirection ) ), 5 );
	return (1 - combiner.vSpecularColor) * (1 - edge);
}

#include "titanfall2_local_lights.hlsl"

void T2ComputeDirectLighting( inout LightingTerms_t lighting, CombinerInput combiner, float sunVisibility )
{
	lighting.vDiffuse = float3( 0, 0, 0 );
	lighting.vSpecular = float3( 0, 0, 0 );
	lighting.vTransmissive = float3( 0, 0, 0 );
	float3 receiverNormal = ComputeShadowReceiverNormal( combiner.vPositionWs );
	if ( !LightmappedLight::UsesLightmaps() && !ProbeLight::UsesProbes() )
	{
		ComputeDirectionalLight( combiner, receiverNormal, lighting.vDiffuse.rgb, lighting.vSpecular.rgb, lighting.vTransmissive.rgb );
		lighting.vDiffuse.rgb *= sunVisibility * T2DiffuseFresnel( combiner, -g_DirectionalLightDirection.xyz );
		lighting.vSpecular.rgb *= sunVisibility;
		lighting.vTransmissive.rgb *= sunVisibility;
	}
	if ( DirectionalLightDebug > 0 && g_DirectionalLightCascadeCount > 0 )
		lighting.vDiffuse.rgb += DirectionalLightShadow::GetDebugColor( combiner.vPositionWs );
	ClusterRange range = Cluster::Query( ClusterItemType_Light, combiner.vPositionSs );
	[loop]
	for ( uint light = 0; light < range.Count; ++light )
	{
		BinnedLight data = DynamicLightConstantByIndex( Cluster::LoadItem( range, light ) );
		float3 diffuse = 0, specular = 0, transmissive = 0;
		ComputeDirectLightingForLight( combiner, receiverNormal, data, diffuse, specular, transmissive );
		float3 ray = data.GetPosition() - combiner.vPositionWs;
		float3 direction = ray * rsqrt( max( dot( ray, ray ), 0.000001 ) );
		lighting.vDiffuse.rgb += diffuse * T2DiffuseFresnel( combiner, direction );
		lighting.vSpecular.rgb += specular;
		lighting.vTransmissive.rgb += transmissive;
	}
}

float4 T2ShadeWithProbe( PixelInput i, Material m, T2LightProbeData probe, float3 specularColor )
{
	m.WorldPositionWithOffset = i.vPositionWithOffsetWs;
	m.WorldPosition = i.vPositionWithOffsetWs + g_vHighPrecisionLightingOffsetWs.xyz;
	m.ScreenPosition = i.vPositionSs;
	m.GeometricNormal = T2GeometricNormalWs( i );
	Decals::Apply( m.WorldPosition, m );
	AdjustAlphaToCoverage( m );
	CombinerInput combiner = T2MaterialToCombinerInput( m, specularColor );
	LightingTerms_t lighting = InitLightingTerms();
	T2ComputeDirectLighting( lighting, combiner, probe.metadata.x > 0.5 ? saturate( probe.skyDirectionSunVisibility.w ) : 1 );
	T2AddProbeLocalLights( lighting, combiner, probe );
	CalculateIndirectLighting( lighting, combiner );
	// Replace diffuse fill only. Albedo, emission and probe reflections keep
	// their own meanings. Native dynamic/local lighting is still evaluated.
	if ( probe.metadata.x > 0.5 ) lighting.vIndirectDiffuse.rgb = T2ProbeIrradiance( probe, m.Normal, T2CurrentSkyColor( g_vT2ProbeSkyColor ) );
	float3 diffuseAo = CalculateDiffuseAmbientOcclusion( combiner, lighting );
	float3 specularAo = CalculateSpecularAmbientOcclusion( combiner, lighting );
	float3 diffuse = (lighting.vDiffuse.rgb * lerp( 1.0, diffuseAo, combiner.flAmbientOcclusionDirectDiffuse )
		+ lighting.vIndirectDiffuse.rgb * diffuseAo) * combiner.vDiffuseColor.rgb + combiner.vEmissive.rgb;
	float3 specular = lighting.vSpecular.rgb * lerp( 1.0, specularAo, combiner.flAmbientOcclusionDirectSpecular )
		+ lighting.vIndirectSpecular.rgb * specularAo;
	// Retail uberAoCav multiplies both direct and environment specular by
	// cavityTexture.r, independently of diffuse AO and self illumination.
	float4 colour = float4( diffuse + specular * T2ReadCavity( i, m ), m.Opacity );
	if ( DepthNormals::WantsDepthNormals() ) return DepthNormals::Output( m.Normal, m.Roughness, colour.a );
	if ( g_bWireframeMode ) return g_vWireframeColor;
	if ( ToolsVis::WantsToolsVis() ) return ShadingModelStandard::DoToolsVis( colour, m, lighting );
	return DoAtmospherics( m.WorldPosition, m.ScreenPosition.xy, colour );
}

#endif
