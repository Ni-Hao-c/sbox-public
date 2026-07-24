HEADER
{
	Description = "Titanfall 2 hologram approximation";
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
	#define S_TRANSLUCENT 1
	#define S_RENDER_BACKFACES 1
	#include "common/shared.hlsl"
	#include "procedural.hlsl"
	#define CUSTOM_MATERIAL_INPUTS
}

struct VertexInput
{
	#include "common/vertexinput.hlsl"
	float4 vColor : COLOR0 < Semantic( Color ); >;
};

struct PixelInput
{
	#include "common/pixelinput.hlsl"
};

VS
{
	#include "common/vertex.hlsl"
	PixelInput MainVs( VertexInput v )
	{
		PixelInput i = ProcessVertex( v );
		i.vVertexColor *= float4( SrgbGammaToLinear( v.vColor.rgb ), v.vColor.a );
		return FinalizeVertex( i );
	}
}

PS
{
	#include "common/pixel.hlsl"
	CreateInputTexture2D( Albedo, Srgb, 8, "None", "_color", ",0/,0/0", Default4( 0.15, 0.75, 1.00, 1.00 ) );
	CreateInputTexture2D( Opacity, Linear, 8, "None", "_opacity", ",0/,0/0", Default4( 1.00, 1.00, 1.00, 1.00 ) );
	CreateInputTexture2D( Emissive, Srgb, 8, "None", "_emissive", ",0/,0/0", Default4( 0.00, 0.00, 0.00, 1.00 ) );
	Texture2D g_tAlbedo < Channel( RGBA, Box( Albedo ), Srgb ); OutputFormat( DXT5 ); SrgbRead( true ); >;
	Texture2D g_tOpacity < Channel( RGBA, Box( Opacity ), Linear ); OutputFormat( DXT5 ); SrgbRead( false ); >;
	Texture2D g_tEmissive < Channel( RGBA, Box( Emissive ), Srgb ); OutputFormat( DXT5 ); SrgbRead( true ); >;
	// Optional Titanfall 2 MATL layers. Kept inline because mount shader include roots are isolated.
	CreateInputTexture2D( Detail, Srgb, 8, "None", "_detail", ",0/,0/0", Default4( 0.50, 0.50, 0.50, 1.00 ) );
	CreateInputTexture2D( Distortion, Linear, 8, "None", "_distortion", ",0/,0/0", Default4( 0.50, 0.50, 0.00, 1.00 ) );
	Texture2D g_tDetail < Channel( RGBA, Box( Detail ), Srgb ); OutputFormat( DXT5 ); SrgbRead( true ); >;
	Texture2D g_tDistortion < Channel( RGBA, Box( Distortion ), Linear ); OutputFormat( DXT5 ); SrgbRead( false ); >;
	TextureCube g_tEnvironment < Attribute( "g_tEnvironment" ); >;
	float g_flT2GlossScale < Attribute( "g_flT2GlossScale" ); Default( 1.0 ); >;
	float4 g_vT2SpecularTint < Attribute( "g_vT2SpecularTint" ); Default4( 1.0, 1.0, 1.0, 1.0 ); >;
	float g_flT2EmissiveStrength < Attribute( "g_flT2EmissiveStrength" ); Default( 1.0 ); >;
	float g_flT2DetailBlend < Attribute( "g_flT2DetailBlend" ); Default( 0.0 ); >;
	float4 g_vT2UvDistortion < Attribute( "g_vT2UvDistortion" ); Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float g_flT2HasEnvironment < Attribute( "g_flT2HasEnvironment" ); Default( 0.0 ); >;
	float g_flT2EnvironmentIntensity < Attribute( "g_flT2EnvironmentIntensity" ); Default( 1.0 ); >;
	float g_flT2FresnelStrength < Attribute( "g_flT2FresnelStrength" ); Default( 0.0 ); >;
	float2 T2ApplyDistortion( float2 uv ) { float2 vector = Tex2DS( g_tDistortion, g_sAniso, uv ).rg * 2.0 - 1.0; return uv + vector * g_vT2UvDistortion.xy; }
	float3 T2ApplyDetail( float3 albedo, float2 uv ) { float3 detail = Tex2DS( g_tDetail, g_sAniso, uv ).rgb * 2.0; return albedo * lerp( float3( 1.0, 1.0, 1.0 ), detail, saturate( g_flT2DetailBlend ) ); }
	float T2Roughness( float gloss ) { return 1.0 - saturate( gloss * max( 0.0, g_flT2GlossScale ) ); }
	float T2Metalness( float3 specular ) { float3 tinted = specular * g_vT2SpecularTint.rgb; return saturate( max( tinted.r, max( tinted.g, tinted.b ) ) * g_vT2SpecularTint.a ); }
	float3 T2Environment( float3 worldPosition, float3 worldNormal, float roughness )
	{
		if ( g_flT2HasEnvironment < 0.5 ) return float3( 0.0, 0.0, 0.0 );
		float3 viewDirection = normalize( g_vCameraPositionWs - worldPosition );
		float3 reflectionDirection = reflect( -viewDirection, normalize( worldNormal ) );
		float fresnel = pow( 1.0 - saturate( dot( normalize( worldNormal ), viewDirection ) ), 5.0 );
		float strength = g_flT2EnvironmentIntensity * lerp( 0.04, 1.0, max( fresnel, saturate( g_flT2FresnelStrength ) ) );
		return g_tEnvironment.SampleLevel( g_sAniso, reflectionDirection, saturate( roughness ) * 5.0 ).rgb * strength;
	}

	float4 g_vT2Uv1RotScale < Attribute( "g_vT2Uv1RotScale" ); Default4( 1.0, 0.0, 0.0, 1.0 ); >;
	float4 g_vT2Uv1Translate < Attribute( "g_vT2Uv1Translate" ); Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vT2AlbedoTint < Attribute( "g_vT2AlbedoTint" ); Default4( 1.0, 1.0, 1.0, 1.0 ); >;
	float4 g_vT2EmissiveTint < Attribute( "g_vT2EmissiveTint" ); Default4( 1.0, 1.0, 1.0, 1.0 ); >;
	float g_flT2MaterialOpacity < Attribute( "g_flT2MaterialOpacity" ); Default( 1.0 ); >;
	float g_flT2HologramScanlineStrength < Attribute( "g_flT2HologramScanlineStrength" ); Default( 0.28 ); >;
	float g_flUseVertexColor < Attribute( "g_flUseVertexColor" ); Default( 0.0 ); >;
	float g_flUseVertexAlpha < Attribute( "g_flUseVertexAlpha" ); Default( 0.0 ); >;

	RenderState( BlendEnable, true );
	RenderState( SrcBlend, ONE );
	RenderState( DstBlend, INV_SRC_ALPHA );
	RenderState( BlendOp, ADD );
	RenderState( DepthWriteEnable, false );

	float4 MainPs( PixelInput i ) : SV_Target0
	{
		float2 uv0 = i.vTextureCoords.xy;
		float2 uv = float2( dot( uv0, g_vT2Uv1RotScale.xy ), dot( uv0, g_vT2Uv1RotScale.zw ) );
		uv += g_vT2Uv1Translate.xy * lerp( 1.0, g_flTime, saturate( g_vT2Uv1Translate.z ) );
		uv = T2ApplyDistortion( uv );
		float4 albedo = Tex2DS( g_tAlbedo, g_sAniso, uv );
		float opacityMap = Tex2DS( g_tOpacity, g_sAniso, uv ).r;
		float3 emissive = Tex2DS( g_tEmissive, g_sAniso, uv ).rgb;
		float3 worldPosition = i.vPositionWithOffsetWs + g_vHighPrecisionLightingOffsetWs.xyz;
		float3 viewDirection = normalize( g_vCameraPositionWs - worldPosition );
		float fresnel = pow( 1.0 - saturate( dot( normalize( i.vNormalWs ), viewDirection ) ), 3.0 ) * g_flT2FresnelStrength;
		float scanline = 1.0 - saturate( g_flT2HologramScanlineStrength )
			* (0.5 + 0.5 * sin( worldPosition.z * 0.24 + g_flTime * 18.0 ));
		float3 vertexTint = lerp( float3( 1.0, 1.0, 1.0 ), i.vVertexColor.rgb, saturate( g_flUseVertexColor ) );
		float vertexAlpha = lerp( 1.0, i.vVertexColor.a, saturate( g_flUseVertexAlpha ) );
		float opacity = saturate( albedo.a * opacityMap * vertexAlpha * g_flT2MaterialOpacity * (0.55 + fresnel) );
		clip( opacity - 0.001 );
		float3 color = (T2ApplyDetail( albedo.rgb, uv ) * g_vT2AlbedoTint.rgb
			+ emissive * g_vT2EmissiveTint.rgb * g_flT2EmissiveStrength + fresnel) * vertexTint * scanline;
		return float4( color * opacity, opacity );
	}
}
