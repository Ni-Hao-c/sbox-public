HEADER
{
	Description = "Titanfall 2 weighted 32-cubed LUT composition";
}
MODES
{
	Default();
}
COMMON
{
	#include "postprocess/shared.hlsl"
}
CS
{
	Texture3D<float4> Lut0 < Attribute( "T2LutBlend0" ); SrgbRead( false ); >;
	Texture3D<float4> Lut1 < Attribute( "T2LutBlend1" ); SrgbRead( false ); >;
	Texture3D<float4> Lut2 < Attribute( "T2LutBlend2" ); SrgbRead( false ); >;
	Texture3D<float4> Lut3 < Attribute( "T2LutBlend3" ); SrgbRead( false ); >;
	Texture3D<float4> Previous < Attribute( "T2LutBlendPrevious" ); SrgbRead( false ); >;
	RWTexture3D<float4> Output < Attribute( "T2LutBlendOutput" ); >;
	float4 Weights < Attribute( "T2LutBlendWeights" ); >;
	int First < Attribute( "T2LutBlendFirst" ); >;

	[numthreads( 4, 4, 4 )]
	void MainCs( uint3 id : SV_DispatchThreadID )
	{
		if ( any( id >= 32 ) ) return;
		float3 color = First != 0 ? float3( 0, 0, 0 ) : Previous.Load( int4( id, 0 ) ).rgb;
		if ( Weights.x > 0 ) color += Lut0.Load( int4( id, 0 ) ).rgb * Weights.x;
		if ( Weights.y > 0 ) color += Lut1.Load( int4( id, 0 ) ).rgb * Weights.y;
		if ( Weights.z > 0 ) color += Lut2.Load( int4( id, 0 ) ).rgb * Weights.z;
		if ( Weights.w > 0 ) color += Lut3.Load( int4( id, 0 ) ).rgb * Weights.w;
		Output[id] = float4( saturate( color ), 1 );
	}
}
