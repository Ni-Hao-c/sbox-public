// T2_MATERIAL_DECLARATIONS: ordinary and GPU-table paths share the same shading functions.
// Shared material inputs for the imported lit surfaces. These are material
// parameters; per-object RenderAttributes cannot be populated by Material.Set.
#include "titanfall2_normals.hlsl"

#if !D_T2_MATERIAL_TABLE
float g_flT2Layered < Default( 0 ); >;
#endif

#if !D_T2_MATERIAL_TABLE
float g_flT2LayeredOpacity < Default( 0 ); >;
#endif

#if !D_T2_MATERIAL_TABLE
float g_flT2LayerBlendRamp < Default( 0 ); >;
#endif

#if !D_T2_MATERIAL_TABLE
float g_flT2Unlit < Default( 0 ); >;
#endif

#if !D_T2_MATERIAL_TABLE
float g_flT2SpecularWorkflow < Default( 0 ); >;
#endif

#if !D_T2_MATERIAL_TABLE
float g_flT2HasCavity < Default( 0 ); >;
#endif

#if !D_T2_MATERIAL_TABLE
float g_flT2CavityUvChannel < Default( -1 ); >;
#endif
#include "titanfall2_uv.hlsl"
#include "titanfall2_emission.hlsl"
// x: present, y: slope encoding, z: two-channel encoding, w: material UV selector.
#if !D_T2_MATERIAL_TABLE
float4 g_vT2DetailNormal < Default4( 0, 0, 0, -1 ); >;
#endif
CreateInputTexture2D( DetailNormal, Linear, 8, "None", "_normal", "Detail", Default4( 0.5, 0.5, 1, 1 ) );
#if !D_T2_MATERIAL_TABLE
Texture2D g_tDetailNormal < Channel( RGBA, Box( DetailNormal ), Linear ); OutputFormat( DXT5 ); SrgbRead( false ); >;
#endif

CreateInputTexture2D( Cavity, Linear, 8, "None", "_cavity", "Reflection", Default4( 1, 1, 1, 1 ) );

#if !D_T2_MATERIAL_TABLE
Texture2D g_tCavity < Channel( RGBA, Box( Cavity ), Linear ); OutputFormat( DXT5 ); SrgbRead( false ); >;
#endif
float T2ReadCavity( PixelInput i, Material m )
{
	if ( g_flT2HasCavity < 0.5 ) return 1;
	return saturate( Tex2DS( g_tCavity, g_sAniso, T2SurfaceUv( i.vTextureCoords.xy, m.TextureCoords, g_flT2CavityUvChannel ) ).r );
}

CreateInputTexture2D( Albedo2, Srgb, 8, "None", "_color", "Layer 2", Default4( 1, 1, 1, 1 ) );
CreateInputTexture2D( Normal2, Linear, 8, "None", "_normal", "Layer 2", Default4( 0.5, 0.5, 1, 1 ) );
CreateInputTexture2D( Gloss2, Linear, 8, "None", "_gloss", "Layer 2", Default4( 0, 0, 0, 1 ) );
CreateInputTexture2D( Specular2, Linear, 8, "None", "_specular", "Layer 2", Default4( 0, 0, 0, 1 ) );
CreateInputTexture2D( LayerBlend, Linear, 8, "None", "_mask", "Layer 2", Default4( 0, 0, 0, 1 ) );

#if !D_T2_MATERIAL_TABLE
Texture2D g_tAlbedo2 < Channel( RGBA, Box( Albedo2 ), Srgb ); OutputFormat( DXT5 ); SrgbRead( true ); >;
#endif

#if !D_T2_MATERIAL_TABLE
Texture2D g_tNormal2 < Channel( RGBA, Box( Normal2 ), Linear ); OutputFormat( DXT5 ); SrgbRead( false ); >;
#endif

#if !D_T2_MATERIAL_TABLE
Texture2D g_tGloss2 < Channel( RGBA, Box( Gloss2 ), Linear ); OutputFormat( DXT5 ); SrgbRead( false ); >;
#endif

#if !D_T2_MATERIAL_TABLE
Texture2D g_tSpecular2 < Channel( RGBA, Box( Specular2 ), Linear ); OutputFormat( DXT5 ); SrgbRead( false ); >;
#endif

#if !D_T2_MATERIAL_TABLE
Texture2D g_tLayerBlend < Channel( RGBA, Box( LayerBlend ), Linear ); OutputFormat( DXT5 ); SrgbRead( false ); >;
#endif
float T2LayerWeight( PixelInput i, float2 uv )
{
	float2 maskUv = T2SurfaceUv( i.vTextureCoords.xy, uv, g_flT2LayerBlendUvChannel );
	float mask = Tex2DS( g_tLayerBlend, g_sAniso, maskUv ).r;
	// Retail Vcolt/Ao/Lyr programs use vertex alpha even without Vcola.
	return saturate( (g_flT2LayerBlendRamp + 1.0) * i.vVertexColor.a
		+ g_flT2LayerBlendRamp * (mask - 1.0) );
}

float T2SurfaceCoverage( PixelInput i, float2 uv, float vertexAlpha )
{
	if ( g_flT2LayeredOpacity < 0.5 ) return vertexAlpha;
	return 1.0 - T2LayerWeight( i, uv );
}

// Shared by colour and depth-normal paths. No albedo, specular, AO, emission or
// environment samples here; both paths retain the same layer weight and UV rules.
void T2ReadSurfaceGeometry( PixelInput i, float2 uv, float layerWeight,
	out float gloss, out float3 normalWs )
{
	float2 rawUv = i.vTextureCoords.xy;
	float2 normalUv = T2SurfaceUv( rawUv, uv, g_vT2SurfaceUvChannels.y );
	gloss = Tex2DS( g_tGloss, g_sAniso, T2SurfaceUv( rawUv, uv, g_vT2SurfaceUvChannels.z ) ).r;
	float3 normal = float3( 0, 0, 1 );
	if ( g_flHasNormalMap > 0.5 )
		normal = T2DecodeSurfaceNormal( Tex2DS( g_tNormal, g_sAniso, normalUv ).rgb, g_flT2NormalSlope, g_flT2NormalTwoChannel );
	if ( g_flT2Layered > 0.5 )
	{
		gloss = lerp( gloss, Tex2DS( g_tGloss2, g_sAniso, T2SurfaceUv( rawUv, uv, g_vT2SurfaceUvChannels2.z ) ).r, layerWeight );
		if ( g_flHasNormalMap2 > 0.5 )
		{
			float3 normal2 = T2DecodeSurfaceNormal( Tex2DS( g_tNormal2, g_sAniso, T2SurfaceUv( rawUv, uv, g_vT2SurfaceUvChannels2.y ) ).rgb, g_flT2NormalSlope2, g_flT2NormalTwoChannel2 );
			normal = lerp( normal, normal2, layerWeight );
		}
	}
	if ( g_vT2DetailNormal.x > 0.5 )
	{
		float2 detailUv = T2SurfaceUv( rawUv, uv, g_vT2DetailNormal.w );
		float3 detail = T2DecodeSurfaceNormal( Tex2DS( g_tDetailNormal, g_sAniso, detailUv ).rgb,
			g_vT2DetailNormal.y, g_vT2DetailNormal.z );
		// Retail uberDetDtn adds decoded tangent slopes before building the world normal.
		normal.xy += detail.xy;
	}
	// Flat materials do not need the derivative-built tangent frame. Material-table
	// flags are constant over a primitive, as are the ordinary material parameters.
	[branch] if ( g_flHasNormalMap <= 0.5 && (g_flT2Layered <= 0.5 || g_flHasNormalMap2 <= 0.5) && g_vT2DetailNormal.x <= 0.5 )
		normalWs = T2GeometricNormalWs( i );
	else
		normalWs = T2SurfaceNormalWs( i, normalUv, normal );
}

void T2ReadDepthSurface( PixelInput i, float2 uv, out float gloss, out float3 normalWs )
{
	float weight = 0;
	if ( g_flT2Layered > 0.5 ) weight = T2LayerWeight( i, uv );
	T2ReadSurfaceGeometry( i, uv, weight, gloss, normalWs );
}

void T2ReadSurface( PixelInput i, float2 uv, out float4 albedo, out float gloss,
	out float3 specular, out float3 normalWs )
{
	float2 rawUv = i.vTextureCoords.xy;
	albedo = Tex2DS( g_tAlbedo, g_sAniso, T2SurfaceUv( rawUv, uv, g_vT2SurfaceUvChannels.x ) );
	specular = Tex2DS( g_tSpecular, g_sAniso, T2SurfaceUv( rawUv, uv, g_vT2SurfaceUvChannels.w ) ).rgb;
	float weight = 0;
	if ( g_flT2Layered > 0.5 )
	{
		weight = T2LayerWeight( i, uv );
		albedo.rgb = lerp( albedo.rgb, Tex2DS( g_tAlbedo2, g_sAniso, T2SurfaceUv( rawUv, uv, g_vT2SurfaceUvChannels2.x ) ).rgb, weight );
		specular = lerp( specular, Tex2DS( g_tSpecular2, g_sAniso, T2SurfaceUv( rawUv, uv, g_vT2SurfaceUvChannels2.w ) ).rgb, weight );
	}
	T2ReadSurfaceGeometry( i, uv, weight, gloss, normalWs );
}

#include "titanfall2_probe_lighting.hlsl"
#include "common/imported_height_fog.hlsl"

float4 T2DepthNormals( PixelInput i, Material m )
{
	m.WorldPositionWithOffset = i.vPositionWithOffsetWs;
	m.WorldPosition = i.vPositionWithOffsetWs + g_vHighPrecisionLightingOffsetWs.xyz;
	m.ScreenPosition = i.vPositionSs;
	m.GeometricNormal = T2GeometricNormalWs( i );
	Decals::Apply( m.WorldPosition, m );
	AdjustAlphaToCoverage( m );
	return DepthNormals::Output( m.Normal, m.Roughness, m.Opacity );
}

float4 T2ShadeSurfaceWithoutFog( PixelInput i, Material m, float3 specularColor )
{
	if ( DepthNormals::WantsDepthNormals() ) return T2DepthNormals( i, m );
	if ( g_flT2Unlit > 0.5 ) return float4( m.Albedo + m.Emission, m.Opacity );
	if ( g_nT2ProbeMode != 0 || g_flT2SpecularWorkflow > 0.5 )
	{
		uint instanceIndex = 0;
		#ifdef T2_HAS_PROBE_INDEX
			instanceIndex = i.nT2ProbeInstance;
		#else
			// Mixed transparent models are individual objects, never the rigid
			// instancing path. Preserve native lighting if submitted otherwise.
			if ( g_nT2ProbeMode == 2 ) return ShadingModelStandard::Shade( i, m );
		#endif
		T2LightProbeData probe = T2ReadProbe( instanceIndex );
		if ( probe.metadata.x > 0.5 || g_flT2SpecularWorkflow > 0.5 ) return T2ShadeWithProbe( i, m, probe, specularColor );
	}
	return ShadingModelStandard::Shade( i, m );
}

float4 T2ShadeSurface( PixelInput i, Material m, float3 specularColor )
{
	// Fog, unlit emission and probe selection belong to the colour pass only.
	if ( DepthNormals::WantsDepthNormals() ) return T2DepthNormals( i, m );
	float4 shade = T2ShadeSurfaceWithoutFog( i, m, specularColor );
	#ifdef T2_ADDITIVE_FOG
		shade.rgb = ApplyImportedHeightFog( shade.rgb, i.vPositionWithOffsetWs + g_vHighPrecisionLightingOffsetWs.xyz, true );
	#else
		shade.rgb = ApplyImportedHeightFog( shade.rgb, i.vPositionWithOffsetWs + g_vHighPrecisionLightingOffsetWs.xyz );
	#endif
	return shade;
}
