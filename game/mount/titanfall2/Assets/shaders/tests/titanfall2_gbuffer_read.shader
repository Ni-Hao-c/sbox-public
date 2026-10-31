HEADER
{
	Description = "Read the actual native normals and roughness prepass for mount verification";
}
MODES
{
	Default();
	Forward();
}
COMMON
{
	#include "postprocess/shared.hlsl"
	DynamicCombo( D_MSAA_NORMALS, 0..1, Sys( ALL ) );
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
	#if D_MSAA_NORMALS
		Texture2DMS<float4> Normals < Attribute( "NormalsGBuffer" ); SrgbRead( false ); >;
	#else
		Texture2D<float4> Normals < Attribute( "NormalsGBuffer" ); SrgbRead( false ); >;
	#endif
	float4 MainPs( PixelInput i ) : SV_Target0
	{
		int2 pixel = int2( i.vTexCoord * g_vViewportSize + g_vViewportOffset );
		#if D_MSAA_NORMALS
			return Normals.Load( pixel, 0 );
		#else
			return Normals.Load( int3( pixel, 0 ) );
		#endif
	}
}
