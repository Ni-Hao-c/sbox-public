HEADER
{
	Description = "Titanfall 2 baked-lightmapped opaque world shader";
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
	#ifndef S_ALPHA_TEST
	#define S_ALPHA_TEST 0
	#endif
	#ifndef S_TRANSLUCENT
	#define S_TRANSLUCENT 0
	#endif

	#define S_UV2 1
	#include "common/shared.hlsl"
	#include "procedural.hlsl"

	#define CUSTOM_MATERIAL_INPUTS
}

struct VertexInput
{
	#include "common/vertexinput.hlsl"
	float4 vColor : COLOR0 < Semantic( Color ); >;
};

struct PixelInput
{
	#include "common/pixelinput.hlsl"
	float3 vPositionOs : TEXCOORD14;
	float3 vNormalOs : TEXCOORD15;
	float4 vTangentUOs_flTangentVSign : TANGENT < Semantic( TangentU_SignV ); >;
};

VS
{
	#include "common/vertex.hlsl"

	PixelInput MainVs( VertexInput v )
	{
		PixelInput i = ProcessVertex( v );
		i.vTextureCoords.zw = v.vTexCoord2;
		i.vVertexColor *= float4( SrgbGammaToLinear( v.vColor.rgb ), v.vColor.a );
		i.vPositionOs = v.vPositionOs.xyz;
		VS_DecodeObjectSpaceNormalAndTangent( v, i.vNormalOs, i.vTangentUOs_flTangentVSign );
		return FinalizeVertex( i );
	}
}

PS
{
	#include "common/pixel.hlsl"

	CreateInputTexture2D( Albedo, Srgb, 8, "None", "_color", ",0/,0/0", Default4( 1.00, 1.00, 1.00, 1.00 ) );
	CreateInputTexture2D( Normal, Linear, 8, "None", "_normal", ",0/,0/0", Default4( 0.5, 0.5, 1, 1 ) );
	CreateInputTexture2D( Gloss, Linear, 8, "None", "_gloss", ",0/,0/0", Default4( 0, 0, 0, 1 ) );
	CreateInputTexture2D( Specular, Linear, 8, "None", "_specular", ",0/,0/0", Default4( 0, 0, 0, 1 ) );
	CreateInputTexture2D( AO, Linear, 8, "None", "_ao", ",0/,0/0", Default4( 1.00, 1.00, 1.00, 1.00 ) );
	CreateInputTexture2D( Emissive, Srgb, 8, "None", "_emissive", ",0/,0/0", Default4( 0.00, 0.00, 0.00, 1.00 ) );
	CreateInputTexture2D( Detail, Srgb, 8, "None", "_detail", ",0/,0/0", Default4( 0.50, 0.50, 0.50, 1.00 ) );
	CreateInputTexture2D( LightmapSkyA, Linear, 8, "None", "_lightmap", ",0/,0/0", Default4( 0.50, 0.50, 0.50, 1.00 ) );
	CreateInputTexture2D( LightmapSkyB, Linear, 8, "None", "_lightmap", ",0/,0/0", Default4( 0.50, 0.50, 1.00, 1.00 ) );
	CreateInputTexture2D( LightmapRealTimeA, Linear, 8, "None", "_lightmap", ",0/,0/0", Default4( 0.00, 0.00, 0.00, 1.00 ) );
	CreateInputTexture2D( LightmapRealTimeB, Linear, 8, "None", "_lightmap", ",0/,0/0", Default4( 0.00, 0.00, 0.00, 1.00 ) );
	CreateInputTexture2D( LightmapRealTimeC, Linear, 8, "None", "_lightmap", ",0/,0/0", Default4( 0.00, 0.00, 0.00, 1.00 ) );
	CreateInputTexture2D( LightmapSunChannels, Linear, 8, "None", "_lightmap", ",0/,0/0", Default4( 0, 0, 0, 0 ) );

	Texture2D g_tAlbedo < Channel( RGBA, Box( Albedo ), Srgb ); OutputFormat( DXT5 ); SrgbRead( true ); >;
	Texture2D g_tNormal < Channel( RGBA, Box( Normal ), Linear ); OutputFormat( DXT5 ); SrgbRead( false ); >;
	Texture2D g_tGloss < Channel( RGBA, Box( Gloss ), Linear ); OutputFormat( DXT5 ); SrgbRead( false ); >;
	Texture2D g_tSpecular < Channel( RGBA, Box( Specular ), Linear ); OutputFormat( DXT5 ); SrgbRead( false ); >;
	Texture2D g_tAO < Channel( RGBA, Box( AO ), Linear ); OutputFormat( DXT5 ); SrgbRead( false ); >;
	Texture2D g_tEmissive < Channel( RGBA, Box( Emissive ), Srgb ); OutputFormat( DXT5 ); SrgbRead( true ); >;
	Texture2D g_tDetail < Channel( RGBA, Box( Detail ), Srgb ); OutputFormat( DXT5 ); SrgbRead( true ); >;
	Texture2D g_tLightmapSkyA < Channel( RGBA, Box( LightmapSkyA ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;
	Texture2D g_tLightmapSkyB < Channel( RGBA, Box( LightmapSkyB ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;
	Texture2D g_tLightmapRealTimeA < Channel( RGBA, Box( LightmapRealTimeA ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;
	Texture2D g_tLightmapRealTimeB < Channel( RGBA, Box( LightmapRealTimeB ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;
	Texture2D g_tLightmapRealTimeC < Channel( RGBA, Box( LightmapRealTimeC ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;
	Texture2D g_tLightmapSunChannels < Channel( R, Box( LightmapSunChannels ), Linear ); OutputFormat( I8 ); SrgbRead( false ); >;

	float4 g_vT2Uv1RotScale < Default4( 1.0, 0.0, 0.0, 1.0 ); >;
	float4 g_vT2Uv1Translate < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vT2AlbedoTint < Default4( 1.0, 1.0, 1.0, 1.0 ); >;
	float4 g_vT2EmissiveTint < Default4( 1.0, 1.0, 1.0, 1.0 ); >;
	float g_flT2EmissiveStrength < Default( 1.0 ); >;
	float g_flT2DetailBlend < Default( 0.0 ); >;
	float g_flT2LightmapIntensity < Default( 1.0 ); >;
	float3 g_vT2LightmapSkyColor < Default3( 0, 0, 0 ); >;
	float g_flT2RealTimeLightContribution < Default( 0.0 ); >;
	float g_flT2HasSunLightmap < Default( 0 ); >;
	float g_flUseVertexColor < Default( 0 ); >;
	int g_nT2LightPageEntryCount < Attribute( "T2LightPageEntryCount" ); Default( 0 ); >;
	StructuredBuffer<int> g_T2LightPages < Attribute( "T2LightPages" ); >;
	float g_flT2GlossScale < Default( 1 ); >;
	float4 g_vT2SpecularTint < Default4( 1, 1, 1, 1 ); >;
	#include "titanfall2_surface.hlsl"

	float2 T2Uv1( float2 uv )
	{
		float2 transformed = float2( dot( uv, g_vT2Uv1RotScale.xy ), dot( uv, g_vT2Uv1RotScale.zw ) );
		return transformed + g_vT2Uv1Translate.xy * lerp( 1.0, g_flTime, saturate( g_vT2Uv1Translate.z ) );
	}

	float4 MainPs( PixelInput i ) : SV_Target0
	{
		float2 uv = T2Uv1( i.vTextureCoords.xy );
		float2 lightmapUv = i.vTextureCoords.zw;
		float4 albedo; float gloss; float3 specular; float3 normal;
		T2ReadSurface( i, uv, albedo, gloss, specular, normal );
		if ( DepthNormals::WantsDepthNormals() )
		{
			Material depth = Material::Init();
			depth.Normal = normal;
			depth.Roughness = 1.0 - saturate( gloss * max( 0.0, g_flT2GlossScale ) );
			depth.Opacity = 1;
			depth.TextureCoords = uv;
			return T2DepthNormals( i, depth );
		}
		float3 detail = Tex2DS( g_tDetail, g_sAniso, uv ).rgb * 2.0;
		float ao = Tex2DS( g_tAO, g_sAniso, T2SurfaceUv( i.vTextureCoords.xy, uv, g_vT2AuxUvChannels.z ) ).r;
		float3 emissive = T2ReadEmissive( i.vTextureCoords.xy, uv )
			* g_vT2EmissiveTint.rgb * g_flT2EmissiveStrength;

		// Verified against retail uber pixel programs: SKY A is RGB with an
		// alpha multiplier, SKY B stores the unnormalised directional sky term.
		float4 skyA = Tex2DS( g_tLightmapSkyA, g_sAniso, lightmapUv );
		float3 skyDirection = Tex2DS( g_tLightmapSkyB, g_sAniso, lightmapUv ).rgb * 2.007874 - 1.0;
		float directionLengthSquared = dot( skyDirection, skyDirection );
		float directionalDot = dot( normal, skyDirection );
		float skyVisibility = saturate( (max( directionLengthSquared, directionalDot ) + directionalDot)
			* (sqrt( directionLengthSquared ) * 0.5) );
		float3 baked = max( skyA.rgb * (skyA.a * 3.984375 + 0.015625)
			* dot( normal, normalize( i.vNormalWs ) ) + T2CurrentSkyColor( g_vT2LightmapSkyColor ) * skyVisibility, 0.0 )
			* max( 0.0, g_flT2LightmapIntensity );
		float3 baseColor = albedo.rgb * g_vT2AlbedoTint.rgb
			* lerp( float3( 1.0, 1.0, 1.0 ), detail, saturate( g_flT2DetailBlend ) )
			* lerp( float3( 1, 1, 1 ), i.vVertexColor.rgb, g_flUseVertexColor );

		Material m = Material::Init();
		m.Albedo = baseColor;
		m.Normal = normal;
		m.Roughness = 1.0 - saturate( gloss * max( 0.0, g_flT2GlossScale ) );
		float3 tintedSpecular = specular * g_vT2SpecularTint.rgb;
		m.Metalness = saturate( max( tintedSpecular.r, max( tintedSpecular.g, tintedSpecular.b ) ) * g_vT2SpecularTint.a );
		m.AmbientOcclusion = ao;
		m.TintMask = 1.0;
		m.Opacity = 1.0;
		m.Emission = emissive;
		m.Transmission = 0.0;
		m.TextureCoords = uv;
		m.WorldPositionWithOffset = i.vPositionWithOffsetWs;
		m.WorldPosition = i.vPositionWithOffsetWs + g_vHighPrecisionLightingOffsetWs.xyz;
		m.ScreenPosition = i.vPositionSs;
		m.GeometricNormal = i.vNormalWs;
		CombinerInput combiner = T2MaterialToCombinerInput( m, tintedSpecular );
		LightingTerms_t lighting = InitLightingTerms();
		float sunVisibility = 1;
		if ( g_flT2HasSunLightmap > 0.5 )
		{
			uint width, height;
			g_tLightmapSunChannels.GetDimensions( width, height );
			int2 pixel = clamp( int2( lightmapUv * float2( width, height ) ), 0, int2( width, height ) - 1 );
			uint channels = uint( g_tLightmapSunChannels.Load( int3( pixel, 0 ) ).r * 255 + 0.5 );
			float4 selector = float4( channels & 1, (channels >> 1) & 1, (channels >> 2) & 1, (channels >> 3) & 1 );
			sunVisibility = saturate( dot( selector, Tex2DS( g_tLightmapRealTimeB, g_sBilinearClamp, lightmapUv ) ) );
		}
		T2ComputeDirectLighting( lighting, combiner, sunVisibility );
		if ( g_nT2LocalLightCount > 0 && g_nT2LightPageEntryCount > 0 )
		{
			uint width, height;
			g_tLightmapRealTimeA.GetDimensions( width, height );
			int2 pixel = clamp( int2( lightmapUv * float2( width, height ) ), 0, int2( width, height ) - 1 );
			uint4 bytes = uint4( g_tLightmapRealTimeA.Load( int3( pixel, 0 ) ) * 255 + 0.5 );
			uint packed = bytes.x | (bytes.y << 8) | (bytes.z << 16) | (bytes.w << 24);
			uint page = (packed >> 24) * 63;
			float4 visibility = Tex2DS( g_tLightmapRealTimeB, g_sBilinearClamp, lightmapUv );
			[unroll] for ( uint channel = 0; channel < 4; ++channel )
			{
				uint local = (packed >> (channel * 6)) & 63;
				if ( local == 63 || page + local >= uint( g_nT2LightPageEntryCount ) ) continue;
				int sourceIndex = g_T2LightPages[page + local];
				if ( sourceIndex >= 0 ) T2AddCompiledLight( lighting, combiner, uint( sourceIndex ), visibility[channel] );
			}
		}
		CalculateIndirectLighting( lighting, combiner );
		// Replace only diffuse fill. Keep native sunlight, local lights and probe
		// specular, rather than adding baked colour on top of native diffuse fill.
		lighting.vIndirectDiffuse.rgb = baked;
		float3 diffuseAo = CalculateDiffuseAmbientOcclusion( combiner, lighting );
		float3 specularAo = CalculateSpecularAmbientOcclusion( combiner, lighting );
		float3 diffuse = (lighting.vDiffuse.rgb * lerp( 1.0, diffuseAo, combiner.flAmbientOcclusionDirectDiffuse )
			+ baked * diffuseAo) * combiner.vDiffuseColor.rgb;
		float3 reflection = lighting.vSpecular.rgb * lerp( 1.0, specularAo, combiner.flAmbientOcclusionDirectSpecular )
			+ lighting.vIndirectSpecular.rgb * specularAo;
		float4 shade = DoAtmospherics( m.WorldPosition, m.ScreenPosition.xy, float4( diffuse + reflection * T2ReadCavity( i, m ) + emissive, 1 ) );
		shade.rgb = ApplyImportedHeightFog( shade.rgb, m.WorldPosition );
		return shade;
	}
}
