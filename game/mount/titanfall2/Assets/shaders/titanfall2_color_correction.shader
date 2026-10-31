HEADER
{
	Description = "Titanfall 2 retail 32-cubed color correction";
}
MODES
{
	Default();
	Forward();
}
FEATURES
{
}
COMMON
{
	#include "postprocess/shared.hlsl"
	#include "math_general.fxc"
}
struct VertexInput
{
	float3 vPositionOs : POSITION < Semantic( PosXyz ); >;
	float2 vTexCoord : TEXCOORD0 < Semantic( LowPrecisionUv ); >;
};
struct PixelInput
{
	float2 vTexCoord : TEXCOORD0;
	#if ( PROGRAM == VFX_PROGRAM_VS )
	float4 vPositionPs : SV_Position;
	#endif
};
VS
{
	PixelInput MainVs( VertexInput i )
	{
		PixelInput o;
		o.vPositionPs = float4( i.vPositionOs.xy, 0, 1 );
		o.vTexCoord = i.vTexCoord;
		return o;
	}
}
PS
{
	#include "postprocess/common.hlsl"
	Texture2D g_tColorBuffer < Attribute( "ColorBuffer" ); SrgbRead( true ); >;
	Texture3D g_tT2ColorLut < Attribute( "T2ColorLut" ); SrgbRead( false ); >;
	float g_flT2ColorLutWeight < Attribute( "T2ColorLutWeight" ); Default( 0 ); >;
	float3 T2DisplayRgb( float3 linear )
	{
		linear = max( linear, 0 );
		return lerp( 1.055 * pow( linear, 1.0 / 2.4 ) - 0.055, 12.92 * linear, step( linear, 0.0031308 ) );
	}
	float3 T2LinearRgb( float3 display )
	{
		return lerp( pow( (display + 0.055) / 1.055, 2.4 ), display / 12.92, step( display, 0.04045 ) );
	}
	float4 MainPs( PixelInput i ) : SV_Target0
	{
		float4 original = g_tColorBuffer.SampleLevel( g_sBilinearClamp, i.vTexCoord, 0 );
		// The .raw table maps display RGB, with R changing fastest. Do not
		// reinterpret its bytes as a linear-light or engine 16-slice lookup.
		float3 display = saturate( T2DisplayRgb( original.rgb ) );
		float3 uvw = (display * 31 + 0.5) / 32;
		float3 corrected = g_tT2ColorLut.SampleLevel( g_sTrilinearClamp, uvw, 0 ).rgb;
		return float4( lerp( original.rgb, T2LinearRgb( corrected ), saturate( g_flT2ColorLutWeight ) ), original.a );
	}
}
