HEADER
{
	Description = "Titanfall 2 runtime water shader";
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
	#define S_SPECULAR 1

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

	CreateInputTexture2D( Albedo, Srgb, 8, "None", "_color", ",0/,0/0", Default4( 0.18, 0.32, 0.34, 1.00 ) );
	CreateInputTexture2D( Normal, Linear, 8, "None", "_normal", ",0/,0/0", Default4( 0.50, 0.50, 1.00, 1.00 ) );
	CreateInputTexture2D( Gloss, Linear, 8, "None", "_gloss", ",0/,0/0", Default4( 0.70, 0.70, 0.70, 1.00 ) );
	CreateInputTexture2D( Specular, Linear, 8, "None", "_specular", ",0/,0/0", Default4( 0.25, 0.25, 0.25, 1.00 ) );
	CreateInputTexture2D( AO, Linear, 8, "None", "_ao", ",0/,0/0", Default4( 1.00, 1.00, 1.00, 1.00 ) );
	CreateInputTexture2D( Opacity, Linear, 8, "None", "_opacity", ",0/,0/0", Default4( 1.00, 1.00, 1.00, 1.00 ) );
	CreateInputTexture2D( Emissive, Srgb, 8, "None", "_emissive", ",0/,0/0", Default4( 0.00, 0.00, 0.00, 1.00 ) );
	CreateInputTexture2D( Detail, Srgb, 8, "None", "_detail", ",0/,0/0", Default4( 0.50, 0.50, 0.50, 1.00 ) );
	CreateInputTexture2D( Distortion, Linear, 8, "None", "_distortion", ",0/,0/0", Default4( 0.50, 0.50, 0.00, 1.00 ) );

	Texture2D g_tAlbedo < Channel( RGBA, Box( Albedo ), Srgb ); OutputFormat( DXT5 ); SrgbRead( true ); >;
	Texture2D g_tNormal < Channel( RGBA, Box( Normal ), Linear ); OutputFormat( DXT5 ); SrgbRead( false ); >;
	Texture2D g_tGloss < Channel( RGBA, Box( Gloss ), Linear ); OutputFormat( DXT5 ); SrgbRead( false ); >;
	Texture2D g_tSpecular < Channel( RGBA, Box( Specular ), Linear ); OutputFormat( DXT5 ); SrgbRead( false ); >;
	Texture2D g_tAO < Channel( RGBA, Box( AO ), Linear ); OutputFormat( DXT5 ); SrgbRead( false ); >;
	Texture2D g_tOpacity < Channel( RGBA, Box( Opacity ), Linear ); OutputFormat( DXT5 ); SrgbRead( false ); >;
	Texture2D g_tEmissive < Channel( RGBA, Box( Emissive ), Srgb ); OutputFormat( DXT5 ); SrgbRead( true ); >;
	Texture2D g_tDetail < Channel( RGBA, Box( Detail ), Srgb ); OutputFormat( DXT5 ); SrgbRead( true ); >;
	Texture2D g_tDistortion < Channel( RGBA, Box( Distortion ), Linear ); OutputFormat( DXT5 ); SrgbRead( false ); >;

	float g_flHasNormalMap < Attribute( "g_flHasNormalMap" ); Default( 0.0 ); >;
	float g_flUseVertexColor < Attribute( "g_flUseVertexColor" ); Default( 0.0 ); >;
	float g_flUseVertexAlpha < Attribute( "g_flUseVertexAlpha" ); Default( 0.0 ); >;
	float g_flWaterOpacity < Attribute( "g_flWaterOpacity" ); Default( 0.78 ); >;
	float4 g_vT2Uv1RotScale < Attribute( "g_vT2Uv1RotScale" ); Default4( 1.0, 0.0, 0.0, 1.0 ); >;
	float4 g_vT2Uv2RotScale < Attribute( "g_vT2Uv2RotScale" ); Default4( 1.0, 0.0, 0.0, 1.0 ); >;
	float4 g_vT2Uv3RotScale < Attribute( "g_vT2Uv3RotScale" ); Default4( 1.0, 0.0, 0.0, 1.0 ); >;
	float4 g_vT2Uv1Translate < Attribute( "g_vT2Uv1Translate" ); Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vT2Uv2Translate < Attribute( "g_vT2Uv2Translate" ); Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vT2Uv3Translate < Attribute( "g_vT2Uv3Translate" ); Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vT2AlbedoTint < Attribute( "g_vT2AlbedoTint" ); Default4( 1.0, 1.0, 1.0, 1.0 ); >;
	float4 g_vT2UvDistortion < Attribute( "g_vT2UvDistortion" ); Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float g_flT2MaterialOpacity < Attribute( "g_flT2MaterialOpacity" ); Default( 1.0 ); >;
	float g_flT2GlossScale < Attribute( "g_flT2GlossScale" ); Default( 1.0 ); >;
	float4 g_vT2SpecularTint < Attribute( "g_vT2SpecularTint" ); Default4( 1.0, 1.0, 1.0, 1.0 ); >;
	float g_flT2EmissiveStrength < Attribute( "g_flT2EmissiveStrength" ); Default( 1.0 ); >;
	float g_flT2DetailBlend < Attribute( "g_flT2DetailBlend" ); Default( 0.0 ); >;

	float2 T2TransformUv( float2 uv, float4 rotScale, float4 translation )
	{
		float2 transformed = float2( dot( uv, rotScale.xy ), dot( uv, rotScale.zw ) );
		return transformed + translation.xy * lerp( 1.0, g_flTime, saturate( translation.z ) );
	}

	float3 DecodeWaterNormal( PixelInput i, out float2 distortion )
	{
		distortion = float2( 0.0, 0.0 );
		float3 surfaceNormal = normalize( i.vNormalWs );
		if ( g_flHasNormalMap < 0.5 ) return surfaceNormal;

		float2 uvA = T2TransformUv( i.vTextureCoords.xy, g_vT2Uv2RotScale, g_vT2Uv2Translate );
		float2 uvB = T2TransformUv( i.vTextureCoords.xy, g_vT2Uv3RotScale, g_vT2Uv3Translate );
		float3 normalA = Tex2DS( g_tNormal, g_sAniso, uvA ).xyz * 2.0 - 1.0;
		float3 normalB = Tex2DS( g_tNormal, g_sAniso, uvB ).xyz * 2.0 - 1.0;
		distortion = normalA.xy * g_vT2UvDistortion.xy + normalB.xy * g_vT2UvDistortion.zw;
		distortion += (Tex2DS( g_tDistortion, g_sAniso, uvA ).rg * 2.0 - 1.0) * g_vT2UvDistortion.xy;
		float3 tangentNormal = normalize( float3( normalA.xy + normalB.xy, max( 0.08, normalA.z * normalB.z ) ) );

		float3 worldPosition = i.vPositionWithOffsetWs + g_vHighPrecisionLightingOffsetWs.xyz;
		float3 positionDx = ddx( worldPosition );
		float3 positionDy = ddy( worldPosition );
		float2 uvDx = ddx( i.vTextureCoords.xy );
		float2 uvDy = ddy( i.vTextureCoords.xy );
		float3 tangent = positionDx * uvDy.y - positionDy * uvDx.y;
		float3 bitangent = positionDy * uvDx.x - positionDx * uvDy.x;
		float tangentLength = dot( tangent, tangent );
		float bitangentLength = dot( bitangent, bitangent );
		if ( tangentLength < 0.0000001 || bitangentLength < 0.0000001 ) return surfaceNormal;

		tangent *= rsqrt( tangentLength );
		bitangent *= rsqrt( bitangentLength );
		if ( dot( cross( tangent, bitangent ), surfaceNormal ) < 0.0 ) bitangent *= -1.0;
		return normalize( tangent * tangentNormal.x + bitangent * tangentNormal.y + surfaceNormal * tangentNormal.z );
	}

	RenderState( BlendEnable, true );
	RenderState( SrcBlend, ONE );
	RenderState( DstBlend, INV_SRC_ALPHA );
	RenderState( BlendOp, ADD );
	RenderState( SrcBlendAlpha, ONE );
	RenderState( DstBlendAlpha, INV_SRC_ALPHA );
	RenderState( BlendOpAlpha, ADD );
	RenderState( DepthWriteEnable, false );

	float4 MainPs( PixelInput i ) : SV_Target0
	{
		float2 distortion;
		float3 waterNormal = DecodeWaterNormal( i, distortion );
		float2 uv = T2TransformUv( i.vTextureCoords.xy, g_vT2Uv1RotScale, g_vT2Uv1Translate ) + distortion;
		float4 albedo = Tex2DS( g_tAlbedo, g_sAniso, uv );
		float gloss = Tex2DS( g_tGloss, g_sAniso, uv ).r;
		float3 specular = Tex2DS( g_tSpecular, g_sAniso, uv ).rgb * g_vT2SpecularTint.rgb;
		float ao = Tex2DS( g_tAO, g_sAniso, uv ).r;
		float opacityMap = Tex2DS( g_tOpacity, g_sAniso, uv ).r;
		float3 detail = Tex2DS( g_tDetail, g_sAniso, uv ).rgb * 2.0;
		float3 emissive = Tex2DS( g_tEmissive, g_sAniso, uv ).rgb;
		float3 vertexTint = lerp( float3( 1, 1, 1 ), i.vVertexColor.rgb, saturate( g_flUseVertexColor ) );
		float vertexAlpha = lerp( 1.0, i.vVertexColor.a, saturate( g_flUseVertexAlpha ) );
		float opacity = saturate( albedo.a * opacityMap * vertexAlpha * g_flWaterOpacity * g_flT2MaterialOpacity );
		clip( opacity - 0.0001 );

		Material m = Material::Init();
		m.Albedo = albedo.rgb * lerp( float3( 1.0, 1.0, 1.0 ), detail, saturate( g_flT2DetailBlend ) )
			* g_vT2AlbedoTint.rgb * vertexTint;
		m.Normal = waterNormal;
		m.Roughness = 1.0 - saturate( gloss * max( 0.0, g_flT2GlossScale ) );
		m.Metalness = saturate( max( specular.r, max( specular.g, specular.b ) ) * g_vT2SpecularTint.a * 0.35 );
		m.AmbientOcclusion = ao;
		m.TintMask = 1.0;
		m.Opacity = opacity;
		m.Emission = emissive * g_flT2EmissiveStrength;
		m.Transmission = 0.0;
		m.TextureCoords = uv;

		float4 shade = ShadingModelStandard::Shade( i, m );
		return float4( shade.rgb * opacity, opacity );
	}
}
