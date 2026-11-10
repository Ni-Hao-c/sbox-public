HEADER
{
	Description = "Titanfall 2 retail display curve";
}
MODES
{
	Default();
	Forward();
}
COMMON
{
	#include "postprocess/shared.hlsl"
	#include "titanfall2_display.hlsl"
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
	Texture2D g_tExposure < Attribute( "T2ExposureTexture" ); SrgbRead( false ); >;
	Texture2D g_tBloom < Attribute( "T2BloomTexture" ); SrgbRead( false ); >;
	Texture2D g_tWideBloom < Attribute( "T2WideBloomTexture" ); SrgbRead( false ); >;
	Texture2D g_tStreakBloom < Attribute( "T2StreakBloomTexture" ); SrgbRead( false ); >;
	float BloomStrength < Attribute( "T2BloomStrength" ); Default( 0 ); >;
	float4 BloomPostParameters < Attribute( "T2BloomPostParameters" ); Default4( 1, 0, 0.7, 0 ); >;
	float StreakPostExponent < Attribute( "T2StreakBloomPostExponent" ); Default( 1 ); >;
	float4 MainPs( PixelInput i ) : SV_Target0
	{
		float4 color = g_tColorBuffer.SampleLevel( g_sPointClamp, i.vTexCoord, 0 );
		// Retail engine_post applies the core's 0.5 scale before its post exponent,
		// then independently adds wide and streak branches before exposure/display.
		float3 bloom = float3( 0, 0, 0 );
		if ( BloomStrength > 0 )
		{
			bloom = pow( max( g_tBloom.SampleLevel( g_sBilinearClamp, i.vTexCoord, 0 ).rgb * 0.5, 0 ), BloomPostParameters.x );
			if ( BloomPostParameters.y > 0 ) bloom += BloomPostParameters.y * pow(
				max( g_tWideBloom.SampleLevel( g_sBilinearClamp, i.vTexCoord, 0 ).rgb, 0 ), BloomPostParameters.z );
			if ( BloomPostParameters.w > 0 ) bloom += BloomPostParameters.w * pow(
				max( g_tStreakBloom.SampleLevel( g_sBilinearClamp, i.vTexCoord, 0 ).rgb, 0 ), StreakPostExponent );
			bloom = min( bloom * BloomStrength, 65504 );
		}
		return float4( T2DisplayCurve( (color.rgb + bloom) * g_tExposure.Load( int3( 0, 0, 0 ) ).x ), color.a );
	}
}
