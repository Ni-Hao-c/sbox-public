HEADER
{
	Description = "Titanfall 2 unlit additive 3D skybox shader";
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
		return FinalizeVertex( i );
	}
}

PS
{
	#include "common/pixel.hlsl"
	float4 g_vT2Uv1RotScale < Attribute( "g_vT2Uv1RotScale" ); Default4( 1.0, 0.0, 0.0, 1.0 ); >;
	float4 g_vT2Uv1Translate < Attribute( "g_vT2Uv1Translate" ); Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vT2AlbedoTint < Attribute( "g_vT2AlbedoTint" ); Default4( 1.0, 1.0, 1.0, 1.0 ); >;
	float g_flT2MaterialOpacity < Attribute( "g_flT2MaterialOpacity" ); Default( 1.0 ); >;
	float g_flT2AlphaTestReference < Attribute( "g_flT2AlphaTestReference" ); Default( 0.5 ); >;

	float2 T2Uv1( float2 uv )
	{
		float2 transformed = float2( dot( uv, g_vT2Uv1RotScale.xy ), dot( uv, g_vT2Uv1RotScale.zw ) );
		return transformed + g_vT2Uv1Translate.xy * lerp( 1.0, g_flTime, saturate( g_vT2Uv1Translate.z ) );
	}

	CreateInputTexture2D( Albedo, Linear, 8, "None", "_color", ",0/,0/0", Default4( 1.00, 1.00, 1.00, 1.00 ) );
	CreateInputTexture2D( Opacity, Linear, 8, "None", "_opacity", ",0/,0/0", Default4( 1.00, 1.00, 1.00, 1.00 ) );
	Texture2D g_tAlbedo < Channel( RGBA, Box( Albedo ), Linear ); OutputFormat( DXT5 ); SrgbRead( false ); >;
	Texture2D g_tOpacity < Channel( RGBA, Box( Opacity ), Linear ); OutputFormat( DXT5 ); SrgbRead( false ); >;

	float g_flAlbedoIsSrgb < Attribute( "g_flAlbedoIsSrgb" ); Default( 1.0 ); >;
	float g_flT2VistaIntensity < Attribute( "g_flT2VistaIntensity" ); Default( 1.0 ); >;
	float g_flT2VistaHdrLimit < Attribute( "g_flT2VistaHdrLimit" ); Default( 65504.0 ); >;

	float4 MainPs( PixelInput i ) : SV_Target0
	{
		float2 uv = T2Uv1( i.vTextureCoords.xy );
		float4 albedo = Tex2DS( g_tAlbedo, g_sAniso, uv );
		float opacityMap = Tex2DS( g_tOpacity, g_sAniso, uv ).r;
		float3 srgbColor = SrgbGammaToLinear( albedo.rgb );
		float3 color = lerp( albedo.rgb, srgbColor, saturate( g_flAlbedoIsSrgb ) )
			* g_vT2AlbedoTint.rgb * g_flT2VistaIntensity;
		float peak = max( color.r, max( color.g, color.b ) );
		color *= min( 1.0, max( 0.001, g_flT2VistaHdrLimit ) / max( 0.001, peak ) );
		float opacity = albedo.a * opacityMap * g_flOpacityScale * g_flT2MaterialOpacity;
		clip( opacity - 0.0001 );
		return float4( color, opacity );
	}
}
