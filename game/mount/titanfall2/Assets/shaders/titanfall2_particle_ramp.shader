HEADER
{
	Description = "Mount-owned Titanfall SpriteCard color lookup";
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
	#include "common/shared.hlsl"
}
struct VertexInput
{
	float3 Position : POSITION < Semantic( None ); >;
	float3 Tint : NORMAL < Semantic( None ); >;
	float4 Overlay : TANGENT < Semantic( None ); >;
	float4 UvSequence : TEXCOORD0 < Semantic( None ); >;
	float4 Alpha : TEXCOORD1 < Semantic( None ); >;
};
struct PixelInput
{
	#include "common/pixelinput.hlsl"
	float4 Overlay : TEXCOORD14;
	float Blend : TEXCOORD15;
};
VS
{
	float4 SheetData < Attribute( "T2ParticleSheet" ); Default4( 0, 0, 0, 0 ); >;
	float Snap < Attribute( "T2ParticleSnap" ); Default( 0 ); >;
	PixelInput MainVs( VertexInput v )
	{
		PixelInput o = (PixelInput)0;
		o.vPositionPs = Position3WsToPs( v.Position );
		o.vPositionWs = v.Position - g_vHighPrecisionLightingOffsetWs.xyz;
		o.vNormalWs = -g_vCameraDirWs;
		o.vVertexColor = float4( v.Tint, v.Alpha.x );
		o.Overlay = v.Overlay;
		if ( Snap > 0.5 )
		{
			o.vTextureCoords.xy = Sheet::Single( SheetData, uint( v.UvSequence.z ), v.UvSequence.w, v.UvSequence.xy );
			o.vTextureCoords.zw = o.vTextureCoords.xy;
		}
		else Sheet::Blended( SheetData, uint( v.UvSequence.z ), v.UvSequence.w, v.UvSequence.xy, o.vTextureCoords.xy, o.vTextureCoords.zw, o.Blend );
		return o;
	}
}
PS
{
	#include "common/pixel.hlsl"
	#include "common/imported_height_fog.hlsl"
	Texture2D ParticleTexture < Attribute( "T2ParticleTexture" ); SrgbRead( true ); >;
	Texture2D RampTexture < Attribute( "T2ParticleRamp" ); SrgbRead( true ); >;
	SamplerState SampleClamp < Filter( BILINEAR ); AddressU( CLAMP ); AddressV( CLAMP ); >;
	float Additive < Attribute( "T2ParticleAdditive" ); Default( 0 ); >;
	float Lit < Attribute( "T2ParticleLit" ); Default( 0 ); >;
	float FogStrength < Attribute( "T2ParticleFog" ); Default( 1 ); >;
	float DepthFeather < Attribute( "T2ParticleDepthFeather" ); Default( 0 ); >;
	float2 CameraFade < Attribute( "T2ParticleCameraFade" ); Default2( 0, 0 ); >;
	RenderState( DepthWriteEnable, false );
	RenderState( CullMode, NONE );
	RenderState( BlendEnable, true );
	RenderState( SrcBlend, ONE );
	RenderState( DstBlend, INV_SRC_ALPHA );
	float4 MainPs( PixelInput i ) : SV_Target0
	{
		float4 texel = ParticleTexture.Sample( SampleClamp, i.vTextureCoords.xy );
		if ( i.Blend > 0 ) texel = lerp( texel, ParticleTexture.Sample( SampleClamp, i.vTextureCoords.zw ), i.Blend );
		clip( texel.a - 0.001 );
		// Lookup happens after the sheet frames blend; precolouring each frame
		// and blending the lookup results is not equivalent for nonlinear ramps.
		texel.rgb = RampTexture.SampleLevel( SampleClamp, texel.rg, 0 ).rgb;
		texel.rgb = lerp( texel.rgb, i.Overlay.rgb, saturate( i.Overlay.a ) );
		texel *= i.vVertexColor;
		float3 world = i.vPositionWithOffsetWs + g_vHighPrecisionLightingOffsetWs.xyz;
		if ( DepthFeather > 0 ) texel.a *= saturate( distance( Depth::GetWorldPosition( i.vPositionSs.xy ), world ) / DepthFeather );
		if ( CameraFade.y > CameraFade.x ) texel.a *= saturate( (distance( world, g_vCameraPositionWs ) - CameraFade.x) / (CameraFade.y - CameraFade.x) );
		if ( Lit > 0.5 )
		{
			FinalCombinerInput_t input = (FinalCombinerInput_t)0;
			input.vPositionWithOffsetWs = i.vPositionWithOffsetWs;
			input.vPositionWs = world; input.vPositionSs = i.vPositionSs;
			input.vNormalWs = i.vNormalWs; input.vRoughness = 1;
			LightingTerms_t lights = InitLightingTerms();
			ComputeDirectLighting( lights, input ); CalculateIndirectLighting( lights, input );
			texel.rgb *= lights.vDiffuse.rgb + lights.vIndirectDiffuse.rgb;
		}
		float4 fogged = DoAtmospherics( world, i.vPositionSs.xy, texel, Additive > 0.5 );
		texel.rgb = lerp( texel.rgb, fogged.rgb, saturate( FogStrength ) );
		texel.rgb = lerp( texel.rgb, ApplyImportedHeightFog( texel.rgb, world, Additive > 0.5 ), saturate( FogStrength ) );
		return float4( texel.rgb * texel.a, Additive > 0.5 ? 0 : texel.a );
	}
}
