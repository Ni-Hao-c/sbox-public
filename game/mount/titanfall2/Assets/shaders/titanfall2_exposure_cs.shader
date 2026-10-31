HEADER
{
	Description = "Titanfall 2 logarithmic meter and retail exposure adaptation";
}
MODES
{
	Default();
}
COMMON
{
	#include "postprocess/shared.hlsl"
	#include "titanfall2_display.hlsl"
	DynamicCombo( D_STAGE, 0..2, Sys( ALL ) );
}
CS
{
	Texture2D<float4> MeterInput < Attribute( "T2MeterInput" ); SrgbRead( false ); >;
	RWTexture2D<float4> MeterOutput < Attribute( "T2MeterOutput" ); >;
	RWTexture2D<float4> ExposureHistory < Attribute( "T2ExposureHistory" ); >;
	float4 ExposureLimits < Attribute( "T2ExposureLimits" ); >; // min, max, EV, rate/frame
	float2 ExposureFrame < Attribute( "T2ExposureFrame" ); >; // build time, forced exposure

	[numthreads( 8, 8, 1 )]
	void MainCs( uint2 id : SV_DispatchThreadID )
	{
		#if ( D_STAGE == 2 )
			if ( any( id != 0 ) ) return;
			float2 meter = MeterInput.Load( int3( 0, 0, 0 ) ).xy;
			float meanLog = meter.x / max( meter.y, 1 );
			float target = T2ExposureTarget( meanLog, ExposureLimits.z );
			float4 old = ExposureHistory[uint2( 0, 0 )];
			float minimum = max( ExposureLimits.x, 0.000001 );
			float maximum = max( minimum, ExposureLimits.y );
			float exposure = old.w < 0 ? target : old.x;
			// One update per camera build, including repeated offscreen readbacks.
			if ( old.w != ExposureFrame.x ) exposure = old.w < 0 ? target : T2AdaptExposure( exposure, target, ExposureLimits.w );
			exposure = clamp( exposure, minimum, maximum );
			if ( ExposureFrame.y > 0 ) exposure = ExposureFrame.y;
			ExposureHistory[uint2( 0, 0 )] = float4( exposure, exp2( meanLog ), target, ExposureFrame.x );
		#else
			uint width, height, inputWidth, inputHeight;
			MeterOutput.GetDimensions( width, height );
			if ( id.x >= width || id.y >= height ) return;
			MeterInput.GetDimensions( inputWidth, inputHeight );
			// Partition rather than discard partial tiles on odd/non-square views.
			uint2 begin = id * uint2( inputWidth, inputHeight ) / uint2( width, height );
			uint2 end = (id + 1) * uint2( inputWidth, inputHeight ) / uint2( width, height );
			float2 sum = 0;
			for ( uint y = begin.y; y < end.y; ++y ) for ( uint x = begin.x; x < end.x; ++x )
			{
				float4 value = MeterInput.Load( int3( x, y, 0 ) );
				#if ( D_STAGE == 0 )
					float luminance = dot( max( value.rgb, 0 ), float3( 0.299, 0.587, 0.114 ) );
					luminance = isfinite( luminance ) ? clamp( luminance, 0.000001, 1048576 ) : 0.000001;
					sum += float2( log2( luminance ), 1 );
				#else
					sum += value.xy;
				#endif
			}
			MeterOutput[id] = float4( sum, 0, 0 );
		#endif
	}
}
