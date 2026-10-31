#ifndef TITANFALL2_OPACITY_FADE_H
#define TITANFALL2_OPACITY_FADE_H

// Neutral defaults preserve materials whose original program does not use these
// variables. Values are read from the active CBufUberStatic RDEF layout.
float g_flT2AlphaDistanceFadeScale < Default( 0.0 ); >;
float g_flT2AlphaDistanceFadeBias < Default( 1.0 ); >;
float4 g_vT2AlphaEdgeFade < Default4( 1.0, 1.0, 1.0, 0.0 ); >;
float g_flT2AtmosphereOpacityScale < Default( 1.0 ); >;
float g_flT2AtmosphereDepthFeather < Default( 0.0 ); >;

float T2OpacityFade( float3 worldPosition, float3 geometricNormal )
{
	float3 toCamera = g_vCameraPositionWs - worldPosition;
	float distanceToCamera = length( toCamera );
	float distanceFade = saturate( distanceToCamera * g_flT2AlphaDistanceFadeScale + g_flT2AlphaDistanceFadeBias );
	float facing = abs( dot( geometricNormal, toCamera ) )
		* rsqrt( max( dot( geometricNormal, geometricNormal ) * dot( toCamera, toCamera ), 0.00000001 ) );
	float edge = g_vT2AlphaEdgeFade.x > 0.0
		? pow( saturate( 1.0 - facing ), g_vT2AlphaEdgeFade.x ) : 1.0;
	// Retail uses the face normal's absolute cosine, then max(0, lerp(...)).
	// Outer values can be negative: beacon_clouds uses -0.5 to hide grazing cards.
	float edgeFade = max( 0.0, lerp( g_vT2AlphaEdgeFade.y, g_vT2AlphaEdgeFade.z, edge ) );
	return distanceFade * edgeFade;
}

float T2AtmosphereFade( float3 worldPosition, float2 screenPosition )
{
	float fade = saturate( g_flT2AtmosphereOpacityScale );
	if ( g_flT2AtmosphereDepthFeather > 0.0 )
	{
		float3 scenePosition = Depth::GetWorldPosition( screenPosition );
		fade *= saturate( distance( scenePosition, worldPosition ) / g_flT2AtmosphereDepthFeather );
	}
	return fade;
}

#endif
