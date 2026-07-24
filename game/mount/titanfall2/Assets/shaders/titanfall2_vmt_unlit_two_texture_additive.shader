HEADER
{
	Description = "Titanfall 2 legacy UnlitTwoTexture additive shader";
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
	#define S_ADDITIVE_BLEND 1
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

	CreateInputTexture2D( Texture1, Srgb, 8, "None", "_color", ",0/,0/0", Default4( 0.0, 0.0, 0.0, 1.0 ) );
	CreateInputTexture2D( Texture2, Srgb, 8, "None", "_color", ",0/,0/0", Default4( 0.0, 0.0, 0.0, 1.0 ) );
	Texture2D g_tTexture1 < Channel( RGBA, Box( Texture1 ), Srgb ); OutputFormat( DXT5 ); SrgbRead( true ); >;
	Texture2D g_tTexture2 < Channel( RGBA, Box( Texture2 ), Srgb ); OutputFormat( DXT5 ); SrgbRead( true ); >;

	float4 g_vVmtColor1 < Attribute( "g_vVmtColor1" ); Default4( 1.0, 1.0, 1.0, 1.0 ); >;
	float4 g_vVmtColor2 < Attribute( "g_vVmtColor2" ); Default4( 1.0, 1.0, 1.0, 1.0 ); >;
	float4 g_vVmtTexture1Transform < Attribute( "g_vVmtTexture1Transform" ); Default4( 1.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vVmtTexture2Transform < Attribute( "g_vVmtTexture2Transform" ); Default4( 1.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vVmtTexture1Scroll < Attribute( "g_vVmtTexture1Scroll" ); Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vVmtTexture2Scroll < Attribute( "g_vVmtTexture2Scroll" ); Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vVmtNoiseMinimum < Attribute( "g_vVmtNoiseMinimum" ); Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vVmtNoiseMaximum < Attribute( "g_vVmtNoiseMaximum" ); Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float g_flVmtLayerAlpha1 < Attribute( "g_flVmtLayerAlpha1" ); Default( 1.0 ); >;
	float g_flVmtLayerAlpha2 < Attribute( "g_flVmtLayerAlpha2" ); Default( 1.0 ); >;
	float g_flVmtHasTexture1 < Attribute( "g_flVmtHasTexture1" ); Default( 0.0 ); >;
	float g_flVmtHasTexture2 < Attribute( "g_flVmtHasTexture2" ); Default( 0.0 ); >;
	float g_flVmtMaterialAlpha < Attribute( "g_flVmtMaterialAlpha" ); Default( 1.0 ); >;
	float g_flVmtUseVertexColor < Attribute( "g_flVmtUseVertexColor" ); Default( 0.0 ); >;
	float g_flVmtUseVertexAlpha < Attribute( "g_flVmtUseVertexAlpha" ); Default( 0.0 ); >;
	float g_flVmtTexture1FrameCount < Attribute( "g_flVmtTexture1FrameCount" ); Default( 1.0 ); >;
	float g_flVmtTexture2FrameCount < Attribute( "g_flVmtTexture2FrameCount" ); Default( 1.0 ); >;
	float g_flVmtTexture1FrameRate < Attribute( "g_flVmtTexture1FrameRate" ); Default( 0.0 ); >;
	float g_flVmtTexture2FrameRate < Attribute( "g_flVmtTexture2FrameRate" ); Default( 0.0 ); >;
	float g_flVmtNoiseEnabled < Attribute( "g_flVmtNoiseEnabled" ); Default( 0.0 ); >;
	float g_flVmtNoiseRate < Attribute( "g_flVmtNoiseRate" ); Default( 30.0 ); >;
	float g_flVmtFresnelEnabled < Attribute( "g_flVmtFresnelEnabled" ); Default( 0.0 ); >;
	float g_flVmtFresnelSharpness < Attribute( "g_flVmtFresnelSharpness" ); Default( 5.0 ); >;
	float g_flVmtFresnelInnerStrength < Attribute( "g_flVmtFresnelInnerStrength" ); Default( 1.0 ); >;
	float g_flVmtFresnelOuterStrength < Attribute( "g_flVmtFresnelOuterStrength" ); Default( 1.0 ); >;

	float2 VmtRandom2( float seed )
	{
		return frac( sin( float2( seed * 12.9898 + 78.233, seed * 39.3468 + 11.135 ) ) * 43758.5453 );
	}

	float2 VmtTransformUv( float2 uv, float4 transform, float4 scroll, float2 noise )
	{
		float sine = sin( transform.y );
		float cosine = cos( transform.y );
		float2 centered = (uv - 0.5) * transform.x;
		float2 rotated = float2( centered.x * cosine - centered.y * sine, centered.x * sine + centered.y * cosine );
		return rotated + 0.5 + transform.zw + scroll.xy * g_flTime + noise;
	}

	float2 VmtAtlasUv( float2 uv, float frameCount, float frameRate )
	{
		float count = max( 1.0, floor( frameCount + 0.5 ) );
		float frame = frameRate > 0.0 ? fmod( floor( g_flTime * frameRate ), count ) : 0.0;
		return float2( frac( uv.x ), (frac( uv.y ) + frame) / count );
	}

	float4 MainPs( PixelInput i ) : SV_Target0
	{
		float noiseStep = floor( g_flTime * max( 0.001, g_flVmtNoiseRate ) );
		float2 noise = lerp( g_vVmtNoiseMinimum.xy, g_vVmtNoiseMaximum.xy, VmtRandom2( noiseStep ) )
			* saturate( g_flVmtNoiseEnabled );
		float2 uv1 = VmtTransformUv( i.vTextureCoords.xy, g_vVmtTexture1Transform, g_vVmtTexture1Scroll, 0.0 );
		float2 uv2 = VmtTransformUv( i.vTextureCoords.xy, g_vVmtTexture2Transform, g_vVmtTexture2Scroll, noise );
		float4 layer1 = Tex2DS( g_tTexture1, g_sAniso, VmtAtlasUv( uv1, g_flVmtTexture1FrameCount, g_flVmtTexture1FrameRate ) );
		float4 layer2 = Tex2DS( g_tTexture2, g_sAniso, VmtAtlasUv( uv2, g_flVmtTexture2FrameCount, g_flVmtTexture2FrameRate ) );
		float3 vertexTint = lerp( float3( 1.0, 1.0, 1.0 ), i.vVertexColor.rgb, saturate( g_flVmtUseVertexColor ) );
		float vertexAlpha = lerp( 1.0, i.vVertexColor.a, saturate( g_flVmtUseVertexAlpha ) );
		float hasTexture1 = saturate( g_flVmtHasTexture1 );
		float hasTexture2 = saturate( g_flVmtHasTexture2 );
		float3 color = (layer1.rgb * g_vVmtColor1.rgb * g_flVmtLayerAlpha1 * hasTexture1
			+ layer2.rgb * g_vVmtColor2.rgb * g_flVmtLayerAlpha2 * hasTexture2) * vertexTint;

		float3 worldPosition = i.vPositionWithOffsetWs + g_vHighPrecisionLightingOffsetWs.xyz;
		float3 viewDirection = normalize( g_vCameraPositionWs.xyz - worldPosition );
		float edge = pow( 1.0 - saturate( abs( dot( normalize( i.vNormalWs ), viewDirection ) ) ),
			max( 0.001, g_flVmtFresnelSharpness ) );
		float fresnelStrength = lerp( g_flVmtFresnelInnerStrength, g_flVmtFresnelOuterStrength, edge );
		color *= lerp( 1.0, fresnelStrength, saturate( g_flVmtFresnelEnabled ) );

		float opacity = max( layer1.a * g_flVmtLayerAlpha1 * hasTexture1,
			layer2.a * g_flVmtLayerAlpha2 * hasTexture2 )
			* vertexAlpha * g_flVmtMaterialAlpha * g_flOpacityScale;
		clip( opacity - 0.0001 );
		return float4( color * opacity, opacity );
	}

	RenderState( BlendEnable, true );
	RenderState( SrcBlend, ONE );
	RenderState( DstBlend, ONE );
	RenderState( BlendOp, ADD );
	RenderState( SrcBlendAlpha, ONE );
	RenderState( DstBlendAlpha, ONE );
	RenderState( BlendOpAlpha, ADD );
	RenderState( DepthWriteEnable, false );
}
