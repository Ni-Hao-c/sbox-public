
HEADER
{
	Description = "Titanfall 2 default PBR shader";
}

FEATURES
{
	#include "common/features.hlsl"
}

MODES
{
	Forward();
	Depth();
}

COMMON
{
	#ifndef S_ALPHA_TEST
	#define S_ALPHA_TEST 0
	#endif
	#ifndef S_TRANSLUCENT
	#define S_TRANSLUCENT 0
	#endif
	
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
	float3 vPositionOs : TEXCOORD14;
	float3 vNormalOs : TEXCOORD15;
	float4 vTangentUOs_flTangentVSign : TANGENT	< Semantic( TangentU_SignV ); >;
	float4 vProbeTint : COLOR1;
};

VS
{
	#include "common/vertex.hlsl"

	PixelInput MainVs( VertexInput v )
	{
		PixelInput i = ProcessVertex( v );
		i.vPositionOs = v.vPositionOs.xyz;
		i.vProbeTint = GetExtraPerInstanceShaderData( v.nInstanceTransformID ).vTint;

		VS_DecodeObjectSpaceNormalAndTangent( v, i.vNormalOs, i.vTangentUOs_flTangentVSign );

		return FinalizeVertex( i );
	}
}

PS
{
	#include "common/pixel.hlsl"

	CreateInputTexture2D( Albedo, Srgb, 8, "None", "_color", ",0/,0/0", Default4( 1.00, 1.00, 1.00, 1.00 ) );
	CreateInputTexture2D( Normal, Linear, 8, "None", "_norm", ",0/,0/0", Default4( 0.50, 0.50, 1.00, 1.00 ) );
	CreateInputTexture2D( Gloss, Linear, 8, "None", "_gloss", ",0/,0/0", Default4( 0.00, 0.00, 0.00, 1.00 ) );
	CreateInputTexture2D( Specular, Linear, 8, "None", "_specular", ",0/,0/0", Default4( 0.00, 0.00, 0.00, 1.00 ) );
	CreateInputTexture2D( AO, Linear, 8, "None", "_ao", ",0/,0/0", Default4( 1.00, 1.00, 1.00, 1.00 ) );
	CreateInputTexture2D( Opacity, Linear, 8, "None", "_opacity", ",0/,0/0", Default4( 1.00, 1.00, 1.00, 1.00 ) );
	CreateInputTexture2D( Emissive, Srgb, 8, "None", "_emissive", ",0/,0/0", Default4( 0.00, 0.00, 0.00, 1.00 ) );

	Texture2D g_tAlbedo < Channel( RGBA, Box( Albedo ), Srgb ); OutputFormat( DXT5 ); SrgbRead( true ); >;
	Texture2D g_tNormal < Channel( RGBA, Box( Normal ), Linear ); OutputFormat( DXT5 ); SrgbRead( false ); >;
	Texture2D g_tGloss < Channel( RGBA, Box( Gloss ), Linear ); OutputFormat( DXT5 ); SrgbRead( false ); >;
	Texture2D g_tSpecular < Channel( RGBA, Box( Specular ), Linear ); OutputFormat( DXT5 ); SrgbRead( false ); >;
	Texture2D g_tAO < Channel( RGBA, Box( AO ), Linear ); OutputFormat( DXT5 ); SrgbRead( false ); >;
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
	float g_flT2AlphaTestReference < Attribute( "g_flT2AlphaTestReference" ); Default( 0.5 ); >;

	float2 T2Uv1( float2 uv )
	{
		float2 transformed = float2( dot( uv, g_vT2Uv1RotScale.xy ), dot( uv, g_vT2Uv1RotScale.zw ) );
		return transformed + g_vT2Uv1Translate.xy * lerp( 1.0, g_flTime, saturate( g_vT2Uv1Translate.z ) );
	}

	float4 MainPs( PixelInput i ) : SV_Target0
	{
		float2 uv = T2ApplyDistortion( T2Uv1( i.vTextureCoords.xy ) );
		Material m = Material::Init();
		m.Albedo = float3( 1, 1, 1 );
		m.Normal = float3( 0, 0, 1 );
		m.Roughness = 1;
		m.Metalness = 0;
		m.AmbientOcclusion = 1;
		m.TintMask = 1;
		m.Opacity = 1;
		m.Emission = float3( 0, 0, 0 );
		m.Transmission = 0;

		float4 albedo = Tex2DS( g_tAlbedo, g_sAniso, uv );
		float gloss = Tex2DS( g_tGloss, g_sAniso, uv ).r;
		float3 specular = Tex2DS( g_tSpecular, g_sAniso, uv ).rgb;
		float ao = Tex2DS( g_tAO, g_sAniso, uv ).r;
		float opacity = Tex2DS( g_tOpacity, g_sAniso, uv ).r;
		float4 emissive = Tex2DS( g_tEmissive, g_sAniso, uv );

		m.Albedo = T2ApplyDetail( albedo.xyz, uv ) * g_vT2AlbedoTint.rgb * i.vProbeTint.rgb;
		m.Roughness = T2Roughness( gloss );
		m.Metalness = T2Metalness( specular );
		m.AmbientOcclusion = ao;
		m.Opacity = albedo.a * opacity * g_flT2MaterialOpacity;
		m.Emission = emissive.xyz * g_vT2EmissiveTint.rgb * g_flT2EmissiveStrength * i.vProbeTint.rgb;
		m.Emission += T2Environment( i.vPositionWithOffsetWs + g_vHighPrecisionLightingOffsetWs.xyz, m.Normal, m.Roughness );

		// Titanfall's packed tangent stream is not reconstructed by ModelLoader yet.
		// Using a tangent-space normal map without that basis produces a zero normal
		// and turns the entire model black, so use the decoded vertex normal for now.
		m.Normal = i.vNormalWs;
		m.TextureCoords = uv;

		float4 shade = ShadingModelStandard::Shade( i, m );
		return shade;
	}
}
