HEADER
{
	Description = "GPU regression probe for compressed texture mip uploads";
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
	CreateInputTexture2D( Albedo, Srgb, 8, "None", "_color", "Probe", Default4( 1, 1, 1, 1 ) );
	Texture2D g_tAlbedo < Channel( RGBA, Box( Albedo ), Srgb ); OutputFormat( DXT5 ); SrgbRead( true ); >;
	float g_flMipLevel < Default( 0 ); >;
	float4 MainPs( PixelInput i ) : SV_Target0
	{
		return float4( g_tAlbedo.SampleLevel( g_sAniso, i.vTextureCoords.xy, g_flMipLevel ).rgb, 1 );
	}
}
