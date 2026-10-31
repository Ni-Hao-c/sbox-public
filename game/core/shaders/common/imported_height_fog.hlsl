#ifndef IMPORTED_HEIGHT_FOG_H
#define IMPORTED_HEIGHT_FOG_H

// Piecewise height density and directional inscattering. Unbound views are
// neutral. The five vectors match the retail FogUnion shader layout.
cbuffer ImportedHeightFogConstants
{
	float4 g_vImportedFogDensity;
	float4 g_vImportedFogColor;
	float4 g_vImportedFogDirectional;
	float4 g_vImportedFogDirection;
	float4 g_vImportedFogHeight;
};

float4 ImportedHeightFog( float3 position, float3 cameraPosition )
{
	if ( g_vImportedFogDensity.w <= 0.0 ) return 0;
	float3 delta = position - cameraPosition;
	float distanceToCamera = length( delta );
	float3 direction = delta / max( distanceToCamera, 0.00001 );
	float cone = saturate( (dot( g_vImportedFogDirection.xyz, direction )
		- g_vImportedFogDirectional.w) * g_vImportedFogDirection.w );
	float3 color = g_vImportedFogColor.rgb + g_vImportedFogDirectional.rgb * (cone * cone);
	float distanceInFog = max( distanceToCamera + g_vImportedFogColor.w, 0.00001 );
	float low = min( position.z, cameraPosition.z );
	float heightDelta = abs( position.z - cameraPosition.z );
	float inverseDelta = rcp( max( heightDelta, 0.00001 ) );
	float slope = saturate( heightDelta * g_vImportedFogHeight.z ) * g_vImportedFogDensity.z / distanceInFog;
	float startDensity = g_vImportedFogDensity.x + g_vImportedFogDensity.z
		* saturate( (low - g_vImportedFogHeight.x) * g_vImportedFogHeight.z );
	float2 fractions = saturate( (g_vImportedFogHeight.xy - low) * inverseDelta );
	float bottomLength = distanceInFog * fractions.x;
	float transitionLength = distanceInFog * fractions.y - bottomLength;
	float topLength = distanceInFog * (1.0 - fractions.y);
	float opticalDepth = g_vImportedFogDensity.x * bottomLength + g_vImportedFogDensity.y * topLength
		+ (startDensity + slope * transitionLength * 0.5) * transitionLength;
	return float4( color, (1.0 - exp2( -max( opticalDepth, 0.0 ) )) * g_vImportedFogDensity.w );
}

float3 ApplyImportedHeightFog( float3 color, float3 position, bool additive = false )
{
	float4 fog = ImportedHeightFog( position, g_vCameraPositionWs );
	return lerp( color, additive ? float3( 0, 0, 0 ) : fog.rgb, fog.a );
}

#endif
