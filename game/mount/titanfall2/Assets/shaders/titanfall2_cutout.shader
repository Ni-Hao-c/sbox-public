// T2_MATERIAL_DECLARATIONS: ordinary and GPU-table paths share the same shading functions.
HEADER
{
	Description = "Titanfall 2 alpha-tested PBR shader";
}

FEATURES
{
	#include "common/features.hlsl"
}

MODES
{
	Forward();
	Depth();
}

COMMON
{
	DynamicCombo( D_T2_MATERIAL_TABLE, 0..1, Sys( ALL ) );
	#define S_TRANSLUCENT 0
	#define S_ALPHA_TEST 1
	#define S_RENDER_BACKFACES 1

	#include "common/shared.hlsl"
	#include "procedural.hlsl"

	#define CUSTOM_MATERIAL_INPUTS
}

struct VertexInput
{
	#include "common/vertexinput.hlsl"
	float4 vColor : COLOR0 < Semantic( Color ); >;
	uint nT2InstanceIndex : SV_InstanceID;
#if D_T2_STATIC_SCENE
	uint nT2VisibleBase : TEXCOORD8;
	uint nT2MaterialId : TEXCOORD9;
#endif
};

struct PixelInput
{
	#include "common/pixelinput.hlsl"
	float4 vProbeTint : COLOR1;
	nointerpolation uint nT2ProbeInstance : TEXCOORD12;
	nointerpolation uint nT2MaterialId : TEXCOORD13;
};

VS
{
	DynamicCombo( D_T2_STATIC_SCENE, 0..1, Sys( ALL ) );
	#include "common/vertex.hlsl"
	#include "titanfall2_static_scene_vertex.hlsl"

	PixelInput MainVs( VertexInput v )
	{
#if D_T2_STATIC_SCENE
		uint sourceId;
		PixelInput i = T2ProcessStaticVertex( v, sourceId );
#else
		PixelInput i = ProcessVertex( v );
#endif
		i.vVertexColor *= float4( SrgbGammaToLinear( v.vColor.rgb ), v.vColor.a );
#if D_T2_STATIC_SCENE
		i.vProbeTint = float4( 1, 1, 1, 1 );
		i.nT2ProbeInstance = sourceId;
		i.nT2MaterialId = v.nT2MaterialId;
#else
		i.vProbeTint = GetExtraPerInstanceShaderData( v.nInstanceTransformID ).vTint;
		i.nT2ProbeInstance = v.nT2InstanceIndex;
		i.nT2MaterialId = 0;
#endif
		return FinalizeVertex( i );
	}
}

PS
{
	DynamicCombo( D_T2_SIMPLE_SURFACE, 0..1, Sys( ALL ) );
	#include "common/pixel.hlsl"
	#include "titanfall2_gpu_material_table.hlsl"

	CreateInputTexture2D( Albedo, Srgb, 8, "None", "_color", ",0/,0/0", Default4( 1.00, 1.00, 1.00, 1.00 ) );
	CreateInputTexture2D( Normal, Linear, 8, "None", "_normal", ",0/,0/0", Default4( 0.50, 0.50, 1.00, 1.00 ) );
	CreateInputTexture2D( Gloss, Linear, 8, "None", "_gloss", ",0/,0/0", Default4( 0.00, 0.00, 0.00, 1.00 ) );
	CreateInputTexture2D( Specular, Linear, 8, "None", "_specular", ",0/,0/0", Default4( 0.00, 0.00, 0.00, 1.00 ) );
	CreateInputTexture2D( AO, Linear, 8, "None", "_ao", ",0/,0/0", Default4( 1.00, 1.00, 1.00, 1.00 ) );
	CreateInputTexture2D( Opacity, Linear, 8, "None", "_opacity", ",0/,0/0", Default4( 1.00, 1.00, 1.00, 1.00 ) );
	CreateInputTexture2D( Emissive, Srgb, 8, "None", "_emissive", ",0/,0/0", Default4( 0.00, 0.00, 0.00, 1.00 ) );

#if !D_T2_MATERIAL_TABLE
Texture2D g_tAlbedo < Channel( RGBA, Box( Albedo ), Srgb ); OutputFormat( DXT5 ); SrgbRead( true ); >;
#endif

#if !D_T2_MATERIAL_TABLE
Texture2D g_tNormal < Channel( RGBA, Box( Normal ), Linear ); OutputFormat( DXT5 ); SrgbRead( false ); >;
#endif

#if !D_T2_MATERIAL_TABLE
Texture2D g_tGloss < Channel( RGBA, Box( Gloss ), Linear ); OutputFormat( DXT5 ); SrgbRead( false ); >;
#endif

#if !D_T2_MATERIAL_TABLE
Texture2D g_tSpecular < Channel( RGBA, Box( Specular ), Linear ); OutputFormat( DXT5 ); SrgbRead( false ); >;
#endif

#if !D_T2_MATERIAL_TABLE
Texture2D g_tAO < Channel( RGBA, Box( AO ), Linear ); OutputFormat( DXT5 ); SrgbRead( false ); >;
#endif

#if !D_T2_MATERIAL_TABLE
Texture2D g_tOpacity < Channel( RGBA, Box( Opacity ), Linear ); OutputFormat( DXT5 ); SrgbRead( false ); >;
#endif

#if !D_T2_MATERIAL_TABLE
Texture2D g_tEmissive < Channel( RGBA, Box( Emissive ), Srgb ); OutputFormat( DXT5 ); SrgbRead( true ); >;
#endif
	// Optional Titanfall 2 MATL layers. Kept inline because mount shader include roots are isolated.
	CreateInputTexture2D( Detail, Linear, 8, "None", "_detail", ",0/,0/0", Default4( 0.50, 0.50, 0.50, 1.00 ) );
	CreateInputTexture2D( Distortion, Linear, 8, "None", "_distortion", ",0/,0/0", Default4( 0.50, 0.50, 0.00, 1.00 ) );

#if !D_T2_MATERIAL_TABLE
Texture2D g_tDetail < Channel( RGBA, Box( Detail ), Linear ); OutputFormat( DXT5 ); SrgbRead( false ); >;
#endif

#if !D_T2_MATERIAL_TABLE
Texture2D g_tDistortion < Channel( RGBA, Box( Distortion ), Linear ); OutputFormat( DXT5 ); SrgbRead( false ); >;
#endif
	CreateInputTextureCube( Environment, Srgb, 8, "None", "_cube", "Reflection", Default4( 0, 0, 0, 1 ) );
	CreateTextureCube( g_tEnvironment ) < Channel( RGBA, Box( Environment ), Srgb ); OutputFormat( RGBA8888 ); SrgbRead( true ); >;

#if !D_T2_MATERIAL_TABLE
float g_flT2GlossScale < Default( 1.0 ); >;
#endif

#if !D_T2_MATERIAL_TABLE
float4 g_vT2SpecularTint < Default4( 1.0, 1.0, 1.0, 1.0 ); >;
#endif

#if !D_T2_MATERIAL_TABLE
float g_flT2EmissiveStrength < Default( 1.0 ); >;
#endif

#if !D_T2_MATERIAL_TABLE
float g_flT2DetailBlend < Default( 0.0 ); >;
#endif

#if !D_T2_MATERIAL_TABLE
float4 g_vT2UvDistortion < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
#endif

#if !D_T2_MATERIAL_TABLE
float g_flT2DistortionUvChannel < Default( -1.0 ); >;
#endif

#if !D_T2_MATERIAL_TABLE
float g_flT2HasEnvironment < Default( 0.0 ); >;
#endif

#if !D_T2_MATERIAL_TABLE
float g_flT2EnvironmentIntensity < Default( 1.0 ); >;
#endif

#if !D_T2_MATERIAL_TABLE
float g_flT2FresnelStrength < Default( 0.0 ); >;
#endif
	#include "titanfall2_optional_surface.hlsl"
	float T2Roughness( float gloss ) { return 1.0 - saturate( gloss * max( 0.0, g_flT2GlossScale ) ); }
	float T2Metalness( float3 specular ) { float3 tinted = specular * g_vT2SpecularTint.rgb; return saturate( max( tinted.r, max( tinted.g, tinted.b ) ) * g_vT2SpecularTint.a ); }
	float3 T2Environment( float3 worldPosition, float3 worldNormal, float roughness )
	{
		if ( g_flT2HasEnvironment < 0.5 ) return float3( 0.0, 0.0, 0.0 );
		float3 viewDirection = normalize( g_vCameraPositionWs - worldPosition );
		float3 reflectionDirection = reflect( -viewDirection, normalize( worldNormal ) );
		float fresnel = pow( 1.0 - saturate( dot( normalize( worldNormal ), viewDirection ) ), 5.0 );
		float strength = g_flT2EnvironmentIntensity * lerp( 0.04, 1.0, max( fresnel, saturate( g_flT2FresnelStrength ) ) );
		return g_tEnvironment.SampleLevel( g_sAniso, reflectionDirection, saturate( roughness ) * 5.0 ).rgb * strength;
	}

#if !D_T2_MATERIAL_TABLE
float4 g_vT2Uv1RotScale < Default4( 1.0, 0.0, 0.0, 1.0 ); >;
#endif

#if !D_T2_MATERIAL_TABLE
float4 g_vT2Uv1Translate < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
#endif

#if !D_T2_MATERIAL_TABLE
float4 g_vT2AlbedoTint < Default4( 1.0, 1.0, 1.0, 1.0 ); >;
#endif

#if !D_T2_MATERIAL_TABLE
float4 g_vT2EmissiveTint < Default4( 1.0, 1.0, 1.0, 1.0 ); >;
#endif

#if !D_T2_MATERIAL_TABLE
float g_flT2MaterialOpacity < Default( 1.0 ); >;
#endif

#if !D_T2_MATERIAL_TABLE
float g_flT2AlphaTestReference < Default( 0.5 ); >;
#endif
	float2 T2Uv1( float2 uv )
	{
		float2 transformed = float2( dot( uv, g_vT2Uv1RotScale.xy ), dot( uv, g_vT2Uv1RotScale.zw ) );
		return transformed + g_vT2Uv1Translate.xy * lerp( 1.0, g_flTime, saturate( g_vT2Uv1Translate.z ) );
	}

#if !D_T2_MATERIAL_TABLE
float g_flUseVertexColor < Default( 0.0 ); >;
#endif

#if !D_T2_MATERIAL_TABLE
float g_flUseVertexAlpha < Default( 0.0 ); >;
#endif
	#define T2_HAS_PROBE_INDEX
	#include "titanfall2_surface.hlsl"

	// Retail Uvd1 programs (models/foliage/grass_*_animated) read uvDistortionTexture
	// through its own UV selector, usually UV2 scrolled by c_uv2Translate * c_gameTime,
	// and add ( rg * 2 - 1 ) * c_uvDistortionIntensity * ( uv1RotScaleX.x, uv1RotScaleY.y )
	// to UV1. That scrolling warp is the grass sway; the vertex program does not move.
	float2 T2DistortedUv( float2 rawUv )
	{
		float2 uv = T2Uv1( rawUv );
		#if D_T2_SIMPLE_SURFACE
			return uv;
		#else
		[branch] if ( all( g_vT2UvDistortion.xy == float2( 0, 0 ) ) ) return uv;
		if ( g_flT2DistortionUvChannel < -0.5 ) return T2ApplyDistortion( uv );
		float2 distortionUv = T2SurfaceUv( rawUv, uv, g_flT2DistortionUvChannel );
		float2 vector = Tex2DS( g_tDistortion, g_sAniso, distortionUv ).rg * 2.0 - 1.0;
		return uv + vector * g_vT2UvDistortion.xy * g_vT2Uv1RotScale.xw;
		#endif
	}

	Material BuildTitanfall2Material( PixelInput i, out float opacity, out float3 specularColor )
	{
		float2 uv = T2DistortedUv( i.vTextureCoords.xy );
		float4 albedo; float gloss; float3 specular; float3 surfaceNormal;
		T2ReadSurface( i, uv, albedo, gloss, specular, surfaceNormal );
		specularColor = specular * g_vT2SpecularTint.rgb;
		float2 rawUv = i.vTextureCoords.xy;
		float ao = Tex2DS( g_tAO, g_sAniso, T2SurfaceUv( rawUv, uv, g_vT2AuxUvChannels.z ) ).r;
		float opacityMap = Tex2DS( g_tOpacity, g_sAniso, T2SurfaceUv( rawUv, uv, g_vT2AuxUvChannels.x ) ).r;
		float3 emissive = T2ReadEmissive( rawUv, uv );
		float3 vertexTint = lerp( float3( 1, 1, 1 ), i.vVertexColor.rgb, saturate( g_flUseVertexColor ) );
		float vertexAlpha = lerp( 1.0, i.vVertexColor.a, saturate( g_flUseVertexAlpha ) );
		opacity = albedo.a * opacityMap * vertexAlpha * g_flT2MaterialOpacity;

		Material m = Material::Init();
		m.Albedo = T2ApplyDetail( albedo.rgb, uv, g_flT2SpecularWorkflow ) * g_vT2AlbedoTint.rgb * vertexTint * i.vProbeTint.rgb;
		m.Normal = surfaceNormal;
		m.Roughness = T2Roughness( gloss );
		m.Metalness = T2Metalness( specular );
		m.AmbientOcclusion = ao;
		m.TintMask = 1.0;
		m.Opacity = opacity;
		m.Emission = emissive * g_vT2EmissiveTint.rgb * vertexTint * g_flT2EmissiveStrength * i.vProbeTint.rgb;
		m.Emission += T2Environment( i.vPositionWithOffsetWs + g_vHighPrecisionLightingOffsetWs.xyz, m.Normal, m.Roughness );
		m.Transmission = 0.0;
		m.TextureCoords = uv;
		return m;
	}

	float4 MainPs( PixelInput i ) : SV_Target0
	{
#if D_T2_MATERIAL_TABLE
		g_nT2GpuMaterialId = i.nT2MaterialId;
#endif
		if ( DepthNormals::WantsDepthNormals() )
		{
			float2 rawUv = i.vTextureCoords.xy;
			float2 uv = T2DistortedUv( rawUv );
			float gloss;
			float3 normal;
			// Keep derivatives before clip, including along cutout edges.
			T2ReadDepthSurface( i, uv, gloss, normal );
			float alpha = Tex2DS( g_tAlbedo, g_sAniso, T2SurfaceUv( rawUv, uv, g_vT2SurfaceUvChannels.x ) ).a;
			float opacity = Tex2DS( g_tOpacity, g_sAniso, T2SurfaceUv( rawUv, uv, g_vT2AuxUvChannels.x ) ).r;
			float vertexAlpha = lerp( 1.0, i.vVertexColor.a, saturate( g_flUseVertexAlpha ) );
			clip( alpha * opacity * vertexAlpha * g_flT2MaterialOpacity - g_flT2AlphaTestReference );
			Material depth = Material::Init();
			depth.Normal = normal;
			depth.Roughness = T2Roughness( gloss );
			depth.Opacity = 1.0;
			depth.TextureCoords = uv;
			return T2DepthNormals( i, depth );
		}
		float opacity;
		float3 specularColor;
		Material m = BuildTitanfall2Material( i, opacity, specularColor );
		clip( opacity - g_flT2AlphaTestReference );
		m.Opacity = 1.0;
		return T2ShadeSurface( i, m, specularColor );
	}
}
