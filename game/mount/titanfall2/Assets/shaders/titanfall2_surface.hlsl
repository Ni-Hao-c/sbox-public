// Shared material inputs for the imported lit surfaces. These are material
// parameters; per-object RenderAttributes cannot be populated by Material.Set.
#include "titanfall2_normals.hlsl"
float g_flT2Layered < Default( 0 ); >;
float g_flT2LayeredOpacity < Default( 0 ); >;
float g_flT2LayerBlendRamp < Default( 0 ); >;
float g_flT2Unlit < Default( 0 ); >;
float g_flT2SpecularWorkflow < Default( 0 ); >;
float g_flT2HasCavity < Default( 0 ); >;
float g_flT2CavityUvChannel < Default( -1 ); >;
#include "titanfall2_uv.hlsl"
#include "titanfall2_emission.hlsl"
CreateInputTexture2D( Cavity, Linear, 8, "None", "_cavity", "Reflection", Default4( 1, 1, 1, 1 ) );
Texture2D g_tCavity < Channel( RGBA, Box( Cavity ), Linear ); OutputFormat( DXT5 ); SrgbRead( false ); >;

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
Texture2D g_tAlbedo2 < Channel( RGBA, Box( Albedo2 ), Srgb ); OutputFormat( DXT5 ); SrgbRead( true ); >;
Texture2D g_tNormal2 < Channel( RGBA, Box( Normal2 ), Linear ); OutputFormat( DXT5 ); SrgbRead( false ); >;
Texture2D g_tGloss2 < Channel( RGBA, Box( Gloss2 ), Linear ); OutputFormat( DXT5 ); SrgbRead( false ); >;
Texture2D g_tSpecular2 < Channel( RGBA, Box( Specular2 ), Linear ); OutputFormat( DXT5 ); SrgbRead( false ); >;
Texture2D g_tLayerBlend < Channel( RGBA, Box( LayerBlend ), Linear ); OutputFormat( DXT5 ); SrgbRead( false ); >;

float T2SurfaceCoverage( PixelInput i, float2 uv, float vertexAlpha )
{
	if ( g_flT2LayeredOpacity < 0.5 ) return vertexAlpha;
	float2 maskUv = T2SurfaceUv( i.vTextureCoords.xy, uv, g_flT2LayerBlendUvChannel );
	float mask = Tex2DS( g_tLayerBlend, g_sAniso, maskUv ).r;
	float weight = saturate( (g_flT2LayerBlendRamp + 1.0) * i.vVertexColor.a
		+ g_flT2LayerBlendRamp * (mask - 1.0) );
	return 1.0 - weight;
}

void T2ReadSurface( PixelInput i, float2 uv, out float4 albedo, out float gloss,
	out float3 specular, out float3 normalWs )
{
	float2 rawUv = i.vTextureCoords.xy;
	float2 normalUv = T2SurfaceUv( rawUv, uv, g_vT2SurfaceUvChannels.y );
	albedo = Tex2DS( g_tAlbedo, g_sAniso, T2SurfaceUv( rawUv, uv, g_vT2SurfaceUvChannels.x ) );
	gloss = Tex2DS( g_tGloss, g_sAniso, T2SurfaceUv( rawUv, uv, g_vT2SurfaceUvChannels.z ) ).r;
	specular = Tex2DS( g_tSpecular, g_sAniso, T2SurfaceUv( rawUv, uv, g_vT2SurfaceUvChannels.w ) ).rgb;
	float3 normal = float3( 0, 0, 1 );
	if ( g_flHasNormalMap > 0.5 )
		normal = T2DecodeSurfaceNormal( Tex2DS( g_tNormal, g_sAniso, normalUv ).rgb, g_flT2NormalSlope, g_flT2NormalTwoChannel );
	if ( g_flT2Layered > 0.5 )
	{
		float mask = Tex2DS( g_tLayerBlend, g_sAniso, T2SurfaceUv( rawUv, uv, g_flT2LayerBlendUvChannel ) ).r;
		// Verified against the retail pixel programs for Vcolt, Ao and base Lyr:
		// mad r.y, ramp, vertex.a, vertex.a; mad r.x, ramp, mask, -ramp;
		// add_sat weight, r.x, r.y. The vertex alpha is used even without Vcola.
		float weight = saturate( (g_flT2LayerBlendRamp + 1.0) * i.vVertexColor.a
			+ g_flT2LayerBlendRamp * (mask - 1.0) );
		albedo.rgb = lerp( albedo.rgb, Tex2DS( g_tAlbedo2, g_sAniso, T2SurfaceUv( rawUv, uv, g_vT2SurfaceUvChannels2.x ) ).rgb, weight );
		gloss = lerp( gloss, Tex2DS( g_tGloss2, g_sAniso, T2SurfaceUv( rawUv, uv, g_vT2SurfaceUvChannels2.z ) ).r, weight );
		specular = lerp( specular, Tex2DS( g_tSpecular2, g_sAniso, T2SurfaceUv( rawUv, uv, g_vT2SurfaceUvChannels2.w ) ).rgb, weight );
		if ( g_flHasNormalMap2 > 0.5 )
		{
			float3 normal2 = T2DecodeSurfaceNormal( Tex2DS( g_tNormal2, g_sAniso, T2SurfaceUv( rawUv, uv, g_vT2SurfaceUvChannels2.y ) ).rgb, g_flT2NormalSlope2, g_flT2NormalTwoChannel2 );
			normal = lerp( normal, normal2, weight );
		}
	}
	normalWs = T2SurfaceNormalWs( i, normalUv, normal );
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
