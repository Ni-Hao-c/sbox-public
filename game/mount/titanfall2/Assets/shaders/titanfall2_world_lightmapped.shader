HEADER
{
	Description = "Titanfall 2 baked-lightmapped opaque world shader";
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

	#define S_UV2 1
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
	float4 vTangentUOs_flTangentVSign : TANGENT < Semantic( TangentU_SignV ); >;
};

VS
{
	#include "common/vertex.hlsl"

	PixelInput MainVs( VertexInput v )
	{
		PixelInput i = ProcessVertex( v );
		i.vPositionOs = v.vPositionOs.xyz;
		VS_DecodeObjectSpaceNormalAndTangent( v, i.vNormalOs, i.vTangentUOs_flTangentVSign );
		return FinalizeVertex( i );
	}
}

PS
{
	#include "common/pixel.hlsl"

	CreateInputTexture2D( Albedo, Srgb, 8, "None", "_color", ",0/,0/0", Default4( 1.00, 1.00, 1.00, 1.00 ) );
	CreateInputTexture2D( AO, Linear, 8, "None", "_ao", ",0/,0/0", Default4( 1.00, 1.00, 1.00, 1.00 ) );
	CreateInputTexture2D( Emissive, Srgb, 8, "None", "_emissive", ",0/,0/0", Default4( 0.00, 0.00, 0.00, 1.00 ) );
	CreateInputTexture2D( Detail, Srgb, 8, "None", "_detail", ",0/,0/0", Default4( 0.50, 0.50, 0.50, 1.00 ) );
	CreateInputTexture2D( LightmapSkyA, Linear, 8, "None", "_lightmap", ",0/,0/0", Default4( 0.50, 0.50, 0.50, 1.00 ) );
	CreateInputTexture2D( LightmapSkyB, Linear, 8, "None", "_lightmap", ",0/,0/0", Default4( 0.50, 0.50, 1.00, 1.00 ) );
	CreateInputTexture2D( LightmapRealTimeA, Linear, 8, "None", "_lightmap", ",0/,0/0", Default4( 0.00, 0.00, 0.00, 1.00 ) );
	CreateInputTexture2D( LightmapRealTimeB, Linear, 8, "None", "_lightmap", ",0/,0/0", Default4( 0.00, 0.00, 0.00, 1.00 ) );
	CreateInputTexture2D( LightmapRealTimeC, Linear, 8, "None", "_lightmap", ",0/,0/0", Default4( 0.00, 0.00, 0.00, 1.00 ) );

	Texture2D g_tAlbedo < Channel( RGBA, Box( Albedo ), Srgb ); OutputFormat( DXT5 ); SrgbRead( true ); >;
	Texture2D g_tAO < Channel( RGBA, Box( AO ), Linear ); OutputFormat( DXT5 ); SrgbRead( false ); >;
	Texture2D g_tEmissive < Channel( RGBA, Box( Emissive ), Srgb ); OutputFormat( DXT5 ); SrgbRead( true ); >;
	Texture2D g_tDetail < Channel( RGBA, Box( Detail ), Srgb ); OutputFormat( DXT5 ); SrgbRead( true ); >;
	Texture2D g_tLightmapSkyA < Channel( RGBA, Box( LightmapSkyA ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;
	Texture2D g_tLightmapSkyB < Channel( RGBA, Box( LightmapSkyB ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;
	Texture2D g_tLightmapRealTimeA < Channel( RGBA, Box( LightmapRealTimeA ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;
	Texture2D g_tLightmapRealTimeB < Channel( RGBA, Box( LightmapRealTimeB ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;
	Texture2D g_tLightmapRealTimeC < Channel( RGBA, Box( LightmapRealTimeC ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;

	float4 g_vT2Uv1RotScale < Attribute( "g_vT2Uv1RotScale" ); Default4( 1.0, 0.0, 0.0, 1.0 ); >;
	float4 g_vT2Uv1Translate < Attribute( "g_vT2Uv1Translate" ); Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vT2AlbedoTint < Attribute( "g_vT2AlbedoTint" ); Default4( 1.0, 1.0, 1.0, 1.0 ); >;
	float4 g_vT2EmissiveTint < Attribute( "g_vT2EmissiveTint" ); Default4( 1.0, 1.0, 1.0, 1.0 ); >;
	float g_flT2EmissiveStrength < Attribute( "g_flT2EmissiveStrength" ); Default( 1.0 ); >;
	float g_flT2DetailBlend < Attribute( "g_flT2DetailBlend" ); Default( 0.0 ); >;
	float g_flT2LightmapIntensity < Attribute( "g_flT2LightmapIntensity" ); Default( 1.35 ); >;
	float g_flT2RealTimeLightContribution < Attribute( "g_flT2RealTimeLightContribution" ); Default( 0.0 ); >;

	float2 T2Uv1( float2 uv )
	{
		float2 transformed = float2( dot( uv, g_vT2Uv1RotScale.xy ), dot( uv, g_vT2Uv1RotScale.zw ) );
		return transformed + g_vT2Uv1Translate.xy * lerp( 1.0, g_flTime, saturate( g_vT2Uv1Translate.z ) );
	}

	float4 MainPs( PixelInput i ) : SV_Target0
	{
		float2 uv = T2Uv1( i.vTextureCoords.xy );
		float2 lightmapUv = i.vTextureCoords.zw;
		float4 albedo = Tex2DS( g_tAlbedo, g_sAniso, uv );
		float3 detail = Tex2DS( g_tDetail, g_sAniso, uv ).rgb * 2.0;
		float ao = Tex2DS( g_tAO, g_sAniso, uv ).r;
		float3 emissive = Tex2DS( g_tEmissive, g_sAniso, uv ).rgb
			* g_vT2EmissiveTint.rgb * g_flT2EmissiveStrength;

		// SKY A stores the stable baked irradiance atlas. SKY B and the RTL pages
		// are decoded and bound independently so their directional/dynamic terms
		// can be enabled without changing the BSP/material grouping path.
		float3 baked = Tex2DS( g_tLightmapSkyA, g_sAniso, lightmapUv ).rgb
			* max( 0.0, g_flT2LightmapIntensity );
		float3 realTime = Tex2DS( g_tLightmapRealTimeA, g_sAniso, lightmapUv ).rgb
			* max( 0.0, g_flT2RealTimeLightContribution );
		float3 baseColor = albedo.rgb * g_vT2AlbedoTint.rgb
			* lerp( float3( 1.0, 1.0, 1.0 ), detail, saturate( g_flT2DetailBlend ) );

		Material m = Material::Init();
		m.Albedo = float3( 0.0, 0.0, 0.0 );
		m.Normal = i.vNormalWs;
		m.Roughness = 1.0;
		m.Metalness = 0.0;
		m.AmbientOcclusion = 1.0;
		m.TintMask = 1.0;
		m.Opacity = 1.0;
		m.Emission = baseColor * max( baked + realTime, float3( 0.015, 0.015, 0.015 ) ) * ao + emissive;
		m.Transmission = 0.0;
		m.TextureCoords = uv;
		return ShadingModelStandard::Shade( i, m );
	}
}
