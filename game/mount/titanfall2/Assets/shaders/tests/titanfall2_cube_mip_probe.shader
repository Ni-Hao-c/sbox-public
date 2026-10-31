HEADER
{
	Description = "GPU regression probe for cubemap faces and compressed mip uploads";
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
	#include "common/shared.hlsl"
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
	PixelInput MainVs( VertexInput v ) { return FinalizeVertex( ProcessVertex( v ) ); }
}
PS
{
	#include "common/pixel.hlsl"
	CreateInputTextureCube( Cube, Linear, 8, "None", "_cube", "Probe", Default4( 0, 0, 0, 1 ) );
	CreateTextureCube( g_tCube ) < Channel( RGBA, Box( Cube ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;
	float3 g_vDirection < Default3( 1, 0, 0 ); >;
	float g_flMipLevel < Default( 0 ); >;
	float4 MainPs( PixelInput i ) : SV_Target0
	{
		return float4( g_tCube.SampleLevel( g_sBilinearClamp, g_vDirection, g_flMipLevel ).rgb, 1 );
	}
}
