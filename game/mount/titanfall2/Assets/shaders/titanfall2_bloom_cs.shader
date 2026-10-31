HEADER
{
	Description = "Titanfall 2 core HDR bloom prefilter and separable retail blur";
}
MODES
{
	Default();
}
COMMON
{
	#include "postprocess/shared.hlsl"
	DynamicCombo( D_BLOOM_STAGE, 0..2, Sys( ALL ) );
}
CS
{
	SamplerState BilinearClamp < Filter( BILINEAR ); AddressU( CLAMP ); AddressV( CLAMP ); >;
	SamplerState BilinearMirror < Filter( BILINEAR ); AddressU( MIRROR ); AddressV( MIRROR ); >;
	Texture2D<float4> BloomInput < Attribute( "T2BloomInput" ); SrgbRead( false ); >;
	RWTexture2D<float4> BloomOutput < Attribute( "T2BloomOutput" ); >;
	float2 BloomParameters < Attribute( "T2BloomParameters" ); >; // cutoff, pre exponent

	// blurfilter kernel 4: original dev/blurfilter.vmt, 25 pixels wide.
	static const float2 Kernel[6] = {
		float2( 11.251852, 0.000534 ), float2( 9.289172, 0.003733 ),
		float2( 7.329586, 0.018004 ), float2( 5.372686, 0.059928 ),
		float2( 3.417910, 0.137740 ), float2( 1.464557, 0.218677 )
	};

	[numthreads( 8, 8, 1 )]
	void MainCs( uint2 id : SV_DispatchThreadID )
	{
		uint width, height, inputWidth, inputHeight;
		BloomOutput.GetDimensions( width, height );
		if ( id.x >= width || id.y >= height ) return;
		BloomInput.GetDimensions( inputWidth, inputHeight );
		float2 uv = (float2( id ) + 0.5) / float2( width, height );
		float2 texel = 1.0 / float2( inputWidth, inputHeight );
		float3 color;
		#if ( D_BLOOM_STAGE == 0 )
			// Four bilinear taps cover the full 4x4 footprint, then apply the
			// original per-channel cutoff/exponent, without a soft knee.
			color = (BloomInput.SampleLevel( BilinearClamp, uv + texel * float2( -1, -1 ), 0 ).rgb
				+ BloomInput.SampleLevel( BilinearClamp, uv + texel * float2( 1, -1 ), 0 ).rgb
				+ BloomInput.SampleLevel( BilinearClamp, uv + texel * float2( -1, 1 ), 0 ).rgb
				+ BloomInput.SampleLevel( BilinearClamp, uv + texel * float2( 1, 1 ), 0 ).rgb) * 0.25;
			color = pow( max( color - BloomParameters.x, 0 ), BloomParameters.y );
		#else
			float2 direction = D_BLOOM_STAGE == 1 ? float2( texel.x, 0 ) : float2( 0, texel.y );
			color = BloomInput.SampleLevel( BilinearMirror, uv, 0 ).rgb * 0.122765;
			[unroll] for ( int tap = 0; tap < 6; ++tap )
			{
				float2 offset = direction * Kernel[tap].x;
				color += (BloomInput.SampleLevel( BilinearMirror, uv - offset, 0 ).rgb
					+ BloomInput.SampleLevel( BilinearMirror, uv + offset, 0 ).rgb) * Kernel[tap].y;
			}
		#endif
		// Keep malformed HDR data from contaminating an entire blur row.
		color = float3( isfinite( color.r ) ? color.r : 0,
			isfinite( color.g ) ? color.g : 0, isfinite( color.b ) ? color.b : 0 );
		BloomOutput[id] = float4( clamp( color, 0, 65504 ), 0 );
	}
}
