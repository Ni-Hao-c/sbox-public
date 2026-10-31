HEADER
{
	Description = "Titanfall 2 runtime water shader";
}

FEATURES
{
	#include "common/features.hlsl"
}

MODES
{
	Forward();
}

COMMON
{
	#define S_TRANSLUCENT 1
	#define S_RENDER_BACKFACES 1
	#define S_SPECULAR 1
	// Refraction is composited inside the water body. Premultiplied blending
	// handles authored shoreline coverage against the actual destination, so a
	// zero-alpha bank does not rewrite it through a filtered framebuffer copy.
	#define BLEND_MODE_ALREADY_SET 1

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
};

VS
{
	#include "common/vertex.hlsl"

	PixelInput MainVs( VertexInput v )
	{
		PixelInput i = ProcessVertex( v );
		i.vVertexColor *= float4( SrgbGammaToLinear( v.vColor.rgb ), v.vColor.a );
		return FinalizeVertex( i );
	}
}

PS
{
	#define DEPTH_STATE_ALREADY_SET 1
	#include "common/pixel.hlsl"
	#include "common/imported_height_fog.hlsl"
	BoolAttribute( bWantsFBCopyTexture, true );
	Texture2D g_tFrameBufferCopyTexture < Attribute( "FrameBufferCopyTexture" ); SrgbRead( false ); >;
	float4 g_vFrameBufferCopyRectangle < Attribute( "FrameBufferCopyRectangle" ); Default4( 0, 0, 1, 1 ); >;

	CreateInputTexture2D( Albedo, Srgb, 8, "None", "_color", ",0/,0/0", Default4( 0.18, 0.32, 0.34, 1.00 ) );
	CreateInputTexture2D( Normal, Linear, 8, "None", "_normal", ",0/,0/0", Default4( 0.50, 0.50, 1.00, 1.00 ) );
	CreateInputTexture2D( Gloss, Linear, 8, "None", "_gloss", ",0/,0/0", Default4( 0.70, 0.70, 0.70, 1.00 ) );
	CreateInputTexture2D( Specular, Linear, 8, "None", "_specular", ",0/,0/0", Default4( 0.25, 0.25, 0.25, 1.00 ) );
	CreateInputTexture2D( AO, Linear, 8, "None", "_ao", ",0/,0/0", Default4( 1.00, 1.00, 1.00, 1.00 ) );
	CreateInputTexture2D( Opacity, Linear, 8, "None", "_opacity", ",0/,0/0", Default4( 1.00, 1.00, 1.00, 1.00 ) );
	CreateInputTexture2D( Emissive, Srgb, 8, "None", "_emissive", ",0/,0/0", Default4( 0.00, 0.00, 0.00, 1.00 ) );
	CreateInputTexture2D( Detail, Srgb, 8, "None", "_detail", ",0/,0/0", Default4( 0.50, 0.50, 0.50, 1.00 ) );
	CreateInputTexture2D( Distortion, Linear, 8, "None", "_distortion", ",0/,0/0", Default4( 0.50, 0.50, 0.00, 1.00 ) );
	CreateInputTexture2D( Distortion2, Linear, 8, "None", "_distortion", ",0/,0/0", Default4( 0.50, 0.50, 0.00, 1.00 ) );

	Texture2D g_tAlbedo < Channel( RGBA, Box( Albedo ), Srgb ); OutputFormat( DXT5 ); SrgbRead( true ); >;
	Texture2D g_tNormal < Channel( RGBA, Box( Normal ), Linear ); OutputFormat( DXT5 ); SrgbRead( false ); >;
	Texture2D g_tGloss < Channel( RGBA, Box( Gloss ), Linear ); OutputFormat( DXT5 ); SrgbRead( false ); >;
	Texture2D g_tSpecular < Channel( RGBA, Box( Specular ), Linear ); OutputFormat( DXT5 ); SrgbRead( false ); >;
	Texture2D g_tAO < Channel( RGBA, Box( AO ), Linear ); OutputFormat( DXT5 ); SrgbRead( false ); >;
	Texture2D g_tOpacity < Channel( RGBA, Box( Opacity ), Linear ); OutputFormat( DXT5 ); SrgbRead( false ); >;
	Texture2D g_tEmissive < Channel( RGBA, Box( Emissive ), Srgb ); OutputFormat( DXT5 ); SrgbRead( true ); >;
	Texture2D g_tDetail < Channel( RGBA, Box( Detail ), Srgb ); OutputFormat( DXT5 ); SrgbRead( true ); >;
	Texture2D g_tDistortion < Channel( RGBA, Box( Distortion ), Linear ); OutputFormat( DXT5 ); SrgbRead( false ); >;
	Texture2D g_tDistortion2 < Channel( RGBA, Box( Distortion2 ), Linear ); OutputFormat( DXT5 ); SrgbRead( false ); >;

	// Material.Set cannot populate shader variables declared as RenderAttributes.
	#include "titanfall2_normals.hlsl"
	float g_flUseVertexColor < Default( 0.0 ); >;
	float g_flUseVertexAlpha < Default( 0.0 ); >;
	float g_flWaterOpacity < Default( 0.86 ); >;
	// Source albedo already contains the water colour. A second dark green tint
	// crushed red/brown water to near black, especially without baked lighting.
	float3 g_vT2WaterTint < Default3( 1.0, 1.0, 1.0 ); >;
	float4 g_vT2WaterFlow < Default4( 0.010, 0.004, -0.006, 0.008 ); >;
	float4 g_vT2Uv1RotScale < Default4( 1.0, 0.0, 0.0, 1.0 ); >;
	float4 g_vT2Uv2RotScale < Default4( 1.0, 0.0, 0.0, 1.0 ); >;
	float4 g_vT2Uv3RotScale < Default4( 1.0, 0.0, 0.0, 1.0 ); >;
	float4 g_vT2Uv1Translate < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vT2Uv2Translate < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vT2Uv3Translate < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vT2AlbedoTint < Default4( 1.0, 1.0, 1.0, 1.0 ); >;
	float4 g_vT2UvDistortion < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float g_flT2MaterialOpacity < Default( 1.0 ); >;
	float g_flT2GlossScale < Default( 1.0 ); >;
	float4 g_vT2SpecularTint < Default4( 1.0, 1.0, 1.0, 1.0 ); >;
	float g_flT2EmissiveStrength < Default( 1.0 ); >;
	float g_flT2DetailBlend < Default( 0.0 ); >;

	float2 T2TransformUv( float2 uv, float4 rotScale, float4 translation )
	{
		float2 transformed = float2( dot( uv, rotScale.xy ), dot( uv, rotScale.zw ) );
		return transformed + translation.xy * lerp( 1.0, g_flTime, saturate( translation.z ) );
	}

	float2 T2WaterUv( PixelInput i )
	{
		float2 uv1 = T2TransformUv( i.vTextureCoords.xy, g_vT2Uv1RotScale, g_vT2Uv1Translate )
			+ g_vT2WaterFlow.xy * g_flTime;
		float2 uv2 = T2TransformUv( i.vTextureCoords.xy, g_vT2Uv2RotScale, g_vT2Uv2Translate )
			+ g_vT2WaterFlow.zw * g_flTime;
		// Retail Uvd1/Uvd2: first distortion samples UV2, second samples UV1;
		// their sum offsets UV1 before all four surface textures are sampled.
		float2 distortion = (Tex2DS( g_tDistortion, g_sAniso, uv2 ).rg * 2.0 - 1.0) * g_vT2UvDistortion.xy
			+ (Tex2DS( g_tDistortion2, g_sAniso, uv1 ).rg * 2.0 - 1.0) * g_vT2UvDistortion.zw;
		return uv1 + distortion * g_vT2Uv1RotScale.xw;
	}

	RenderState( BlendEnable, true );
	RenderState( SrcBlend, ONE );
	RenderState( DstBlend, INV_SRC_ALPHA );
	RenderState( DepthEnable, true );
	RenderState( DepthFunc, GREATER_EQUAL );
	RenderState( DepthWriteEnable, false );

	float4 MainPs( PixelInput i ) : SV_Target0
	{
		float2 uv = T2WaterUv( i );
		float3 tangentNormal = float3( 0, 0, 1 );
		if ( g_flHasNormalMap > 0.5 )
			tangentNormal = T2DecodeSurfaceNormal( Tex2DS( g_tNormal, g_sAniso, uv ).rgb, g_flT2NormalSlope, g_flT2NormalTwoChannel );
		float3 waterNormal = T2SurfaceNormalWs( i, uv, tangentNormal );
		float4 albedo = Tex2DS( g_tAlbedo, g_sAniso, uv );
		float gloss = Tex2DS( g_tGloss, g_sAniso, uv ).r;
		float3 specular = Tex2DS( g_tSpecular, g_sAniso, uv ).rgb * g_vT2SpecularTint.rgb;
		float ao = Tex2DS( g_tAO, g_sAniso, uv ).r;
		float opacityMap = Tex2DS( g_tOpacity, g_sAniso, uv ).r;
		float3 detail = Tex2DS( g_tDetail, g_sAniso, uv ).rgb * 2.0;
		float3 emissive = Tex2DS( g_tEmissive, g_sAniso, uv ).rgb;
		float3 vertexTint = lerp( float3( 1, 1, 1 ), i.vVertexColor.rgb, saturate( g_flUseVertexColor ) );
		float vertexAlpha = lerp( 1.0, i.vVertexColor.a, saturate( g_flUseVertexAlpha ) );
		// Texture alpha can encode flow/foam, so retain the body floor. Vcola and
		// material opacity are authored coverage and must still fade the bank edge.
		float coverage = saturate( vertexAlpha * g_flT2MaterialOpacity );
		float bodyOpacity = saturate( max( albedo.a * opacityMap, g_flWaterOpacity ) );
		float opacity = bodyOpacity * coverage;

		Material m = Material::Init();
		m.Albedo = albedo.rgb * lerp( float3( 1.0, 1.0, 1.0 ), detail, saturate( g_flT2DetailBlend ) )
			* g_vT2AlbedoTint.rgb * vertexTint * g_vT2WaterTint;
		m.Normal = waterNormal;
		m.Roughness = 1.0 - saturate( gloss * max( 0.0, g_flT2GlossScale ) );
		m.Metalness = saturate( max( specular.r, max( specular.g, specular.b ) ) * g_vT2SpecularTint.a * 0.35 );
		m.AmbientOcclusion = ao;
		m.TintMask = 1.0;
		m.Opacity = 1.0;
		m.Emission = emissive * g_flT2EmissiveStrength;
		m.Transmission = 0.0;
		m.TextureCoords = uv;

		float4 shade = ShadingModelStandard::Shade( i, m );
		shade.rgb = ApplyImportedHeightFog( shade.rgb, i.vPositionWithOffsetWs + g_vHighPrecisionLightingOffsetWs.xyz, false );
		float3 worldPosition = i.vPositionWithOffsetWs + g_vHighPrecisionLightingOffsetWs.xyz;
		float3 toCamera = normalize( g_vCameraPositionWs - worldPosition );
		float fresnel = 0.02 + 0.98 * pow( 1.0 - saturate( abs( dot( waterNormal, toCamera ) ) ), 5.0 );
		float2 screenUv = CalculateViewportUv( i.vPositionSs.xy );
		float2 halfTexel = g_vInvViewportSize * 0.5;
		float2 refractedUv = clamp( screenUv + Vector3WsToVs( waterNormal ).xy * float2( 1, -1 ) * (0.008 * coverage),
			halfTexel, 1.0 - halfTexel );
		// Reject foreground samples so refraction does not pull a bank/object onto the water.
		float surfaceDepth = dot( worldPosition - g_vCameraPositionWs, g_vCameraDirWs );
		if ( Depth::GetLinear( refractedUv / g_vInvViewportSize ) < surfaceDepth - 1.0 )
			refractedUv = screenUv;
		float3 background = g_tFrameBufferCopyTexture.SampleLevel( g_sBilinearClamp,
			refractedUv * g_vFrameBufferCopyRectangle.zw, 0 ).rgb;
		float3 bodyColor = lerp( background * lerp( float3( 1, 1, 1 ), g_vT2WaterTint, opacity * 0.25 ),
			shade.rgb, bodyOpacity + (1.0 - bodyOpacity) * fresnel );
		return float4( bodyColor * coverage, coverage );
	}
}
