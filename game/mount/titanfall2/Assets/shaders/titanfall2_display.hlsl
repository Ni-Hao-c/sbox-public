#ifndef TITANFALL2_DISPLAY_H
#define TITANFALL2_DISPLAY_H

// Retail engine_post_ps50, default (non-debug) branch. Input/output are linear.
float3 T2DisplayCurve( float3 radiance )
{
	float3 x = max( radiance, 0 );
	return x * (10 * x + 0.3) / (x * (10 * x + 0.5) + 1.5);
}

// exposure_adaptation_ps40. The input is mean log2 luminance, not mean RGB.
float T2ExposureTarget( float meanLogLuminance, float compensation )
{
	float luminance = exp2( clamp( meanLogLuminance, -20, 20 ) );
	float key = 1.03 - 2.0 / (log( luminance + 1.0002 ) + 2.0);
	return key / max( luminance, 0.000001 ) * exp2( compensation );
}

float T2AdaptExposure( float previous, float target, float rate )
{
	float delta = target - previous;
	float relative = abs( delta ) / max( max( previous, target ), 0.000001 );
	return previous + sign( delta ) * min( relative * max( rate, 0 ), abs( delta ) );
}
#endif
