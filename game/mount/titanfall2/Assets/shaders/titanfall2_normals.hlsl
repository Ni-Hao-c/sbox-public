// T2_MATERIAL_DECLARATIONS: ordinary and GPU-table paths share the same shading functions.

#if !D_T2_MATERIAL_TABLE
float g_flHasNormalMap < Default( 0 ); >;
#endif

#if !D_T2_MATERIAL_TABLE
float g_flHasNormalMap2 < Default( 0 ); >;
#endif

#if !D_T2_MATERIAL_TABLE
float g_flT2NormalSlope < Default( 0 ); >;
#endif

#if !D_T2_MATERIAL_TABLE
float g_flT2NormalSlope2 < Default( 0 ); >;
#endif

#if !D_T2_MATERIAL_TABLE
float g_flT2NormalTwoChannel < Default( 0 ); >;
#endif

#if !D_T2_MATERIAL_TABLE
float g_flT2NormalTwoChannel2 < Default( 0 ); >;
#endif
float3 T2DecodeSurfaceNormal( float3 encoded, float slope, float twoChannel )
{
	// Retail uberLyr shaders decode BC5 as tangent slopes, not a unit XY normal.
	if ( slope > 0.5 ) return float3( encoded.xy * 2.867661 - 1.439453, 1.0 );
	float3 normal = encoded * 2.0 - 1.0;
	if ( twoChannel > 0.5 ) normal.z = sqrt( saturate( 1.0 - dot( normal.xy, normal.xy ) ) );
	return normal;
}

// Shaders that draw both faces (cutout gratings, foliage, decals, water) must light the
// side the camera sees. Without the flip a back face keeps the front normal: N.V < 0
// saturates Fresnel into a white reflection, or the side away from the sun goes black,
// so one surface changed between white and black with the view angle. Same rule as the
// Source 1 mount's surface.shader (normalWs *= i.face ? 1 : -1).
float T2FaceSign( PixelInput i )
{
	#if ( S_RENDER_BACKFACES )
		return i.face ? 1.0 : -1.0;
	#else
		return 1.0;
	#endif
}

float3 T2GeometricNormalWs( PixelInput i )
{
	return normalize( i.vNormalWs ) * T2FaceSign( i );
}

float3 T2SurfaceNormalWs( PixelInput i, float2 uv, float3 normal )
{
	float3 n = normalize( i.vNormalWs );
	float side = T2FaceSign( i );
	float3 p = i.vPositionWithOffsetWs;
	float3 px = ddx( p ), py = ddy( p );
	float2 tx = ddx( uv ), ty = ddy( uv );
	float determinant = tx.x * ty.y - tx.y * ty.x;
	if ( abs( determinant ) < 0.00000001 ) return n * side;
	float3 t = (px * ty.y - py * tx.y) / determinant;
	float3 b = (py * tx.x - px * ty.x) / determinant;
	t -= n * dot( n, t );
	b -= n * dot( n, b );
	if ( min( dot( t, t ), dot( b, b ) ) < 0.00000001 ) return n * side;
	// The whole perturbed normal flips: a bump that rises out of the front sinks into the back.
	return normalize( normalize( t ) * normal.x + normalize( b ) * normal.y + n * normal.z ) * side;
}

