HEADER
{
	Description = "Titanfall 2 particle ribbons";
}

FEATURES
{
	#include "vr_common_features.fxc"
}

MODES
{
	Forward();
}

COMMON
{
	#include "common/shared.hlsl"
}

struct VertexInput
{
	float3 WorldPosition : POSITION < Semantic( None ); >;
	float3 Normal : NORMAL < Semantic( None ); >;
	float3 Tangent : TANGENT < Semantic( None ); >;
	float4 Color : COLOR0 < Semantic( None ); >;
	float2 TextureCoords : TEXCOORD0 < Semantic( None ); >;
};

struct PixelInput
{
	#include "common/pixelinput.hlsl"
};

VS
{
	PixelInput MainVs( VertexInput v )
	{
		PixelInput i;
		i.vPositionWs = v.WorldPosition - g_vHighPrecisionLightingOffsetWs.xyz;
		i.vPositionPs = Position3WsToPs( v.WorldPosition );
		i.vNormalWs = v.Normal;
		i.vTangentUWs = v.Tangent;
		i.vTangentVWs = cross( v.Normal, v.Tangent );
		i.vVertexColor = v.Color;
		i.vTextureCoords = float4( v.TextureCoords, v.TextureCoords );
		return i;
	}
}

PS
{
	#include "common/pixel.hlsl"

	CreateInputTexture2D( TextureColor, Srgb, 8, "", "_color", "Material,10/10", Default4( 1, 1, 1, 1 ) );
	Texture2D g_tColor < Channel( RGBA, Box( TextureColor ), Srgb ); SrgbRead( true ); OutputFormat( BC7 ); >;
	SamplerState g_sBeam < Filter( ANISOTROPIC ); AddressU( CLAMP ); AddressV( WRAP ); >;
	float g_flParticleOverbright < Attribute( "g_flParticleOverbright" ); Default( 1 ); >;
	float g_flParticleAddSelf < Attribute( "g_flParticleAddSelf" ); Default( 0 ); >;
	float g_flParticleAdditive < Attribute( "g_flParticleAdditive" ); Default( 0 ); >;
	float g_DepthFeather < Attribute( "g_DepthFeather" ); Default( 0 ); >;

	RenderState( DepthWriteEnable, false );
	RenderState( CullMode, NONE );
	RenderState( BlendEnable, true );
	RenderState( SrcBlend, ONE );
	RenderState( DstBlend, INV_SRC_ALPHA );

	float4 MainPs( PixelInput i ) : SV_Target0
	{
		float4 texel = g_tColor.Sample( g_sBeam, i.vTextureCoords.xy );
		float opacity = saturate( texel.a * i.vVertexColor.a );
		float3 worldPosition = i.vPositionWithOffsetWs + g_vHighPrecisionLightingOffsetWs.xyz;
		if ( g_DepthFeather > 0 )
			opacity *= saturate( distance( Depth::GetWorldPosition( i.vPositionSs.xy ), worldPosition ) / g_DepthFeather );
		clip( opacity - 0.0001 );

		float3 color = texel.rgb * SrgbGammaToLinear( i.vVertexColor.rgb ) * g_flParticleOverbright;
		// Source SpriteCard's add-self term uses the current particle alpha,
		// independently of the destination's alpha blend.
		color *= 1.0 + g_flParticleOverbright * g_flParticleAddSelf * i.vVertexColor.a;
		bool additive = g_flParticleAdditive > 0.5;
		float4 fogged = DoAtmospherics( worldPosition, i.vPositionSs.xy,
			float4( color, opacity ), additive || g_flParticleAddSelf > 0 );
		return float4( fogged.rgb * fogged.a, additive ? 0.0 : fogged.a );
	}
}
