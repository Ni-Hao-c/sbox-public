HEADER
{
	Description = "Titanfall 2 translucent additive Godray shader";
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
	#define MATERIAL_CONTROLLED_BACKFACES

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
		float4 vertexColor = float4( SrgbGammaToLinear( v.vColor.rgb ), v.vColor.a );
		i.vVertexColor *= vertexColor;
		return FinalizeVertex( i );
	}
}

PS
{
	#include "common/pixel.hlsl"
	#include "common/imported_height_fog.hlsl"
	// sbox_pixel.fxc sets a final fixed cull mode; select the authored
	// material feature after that include rather than drawing both sides.
	RenderState( CullMode, F_RENDER_BACKFACES ? NONE : BACK );

	CreateInputTexture2D( Albedo, Srgb, 8, "None", "_color", ",0/,0/0", Default4( 1.00, 1.00, 1.00, 1.00 ) );
	Texture2D g_tAlbedo < Channel( RGBA, Box( Albedo ), Srgb ); OutputFormat( BC7 ); SrgbRead( true ); >;

	#include "titanfall2_opacity_fade.hlsl"
	float4 g_vT2AlbedoTint < Default4( 1, 1, 1, 1 ); >;
	float g_flT2MaterialOpacity < Default( 1.0 ); >;
	float g_flGodrayDepthFeather < Default( 24.0 ); >;

	float4 MainPs( PixelInput i ) : SV_Target0
	{
		float4 texel = Tex2DS( g_tAlbedo, g_sAniso, i.vTextureCoords.xy );
		float3 worldPosition = i.vPositionWithOffsetWs + g_vHighPrecisionLightingOffsetWs.xyz;
		float materialFade = T2OpacityFade( worldPosition, i.vNormalWs );

		float depthFade = 1.0;
		if ( g_flGodrayDepthFeather > 0.0 )
		{
			float3 scenePosition = Depth::GetWorldPosition( i.vPositionSs.xy );
			depthFade = saturate( distance( scenePosition, worldPosition ) / g_flGodrayDepthFeather );
		}

		float opacity = texel.a * i.vVertexColor.a * materialFade * depthFade * g_flOpacityScale * g_flT2MaterialOpacity;
		clip( opacity - 0.0001 );
		return float4( ApplyImportedHeightFog( texel.rgb * i.vVertexColor.rgb * g_vT2AlbedoTint.rgb, worldPosition, true ), opacity );
	}
}
