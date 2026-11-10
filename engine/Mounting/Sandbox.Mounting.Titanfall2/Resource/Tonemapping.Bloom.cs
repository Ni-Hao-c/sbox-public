using Sandbox.Rendering;

public sealed partial class Titanfall2Tonemapping
{
	// Keep the established mount-wide gain. Branch amounts and exponents below are
	// independent, matching the retail engine_post parameter layout.
	[Property] public float BloomStrength { get; set; } = 0.15f;
	[Property] public float BloomCutoff { get; set; } = 1f;
	[Property] public float BloomExponent { get; set; } = 1f;
	[Property] public float BloomPostExponent { get; set; } = 1f;
	[Property] public float WideBloomAmount { get; set; } = 0.2f;
	[Property] public float WideBloomPreExponent { get; set; } = 2f;
	[Property] public float WideBloomPostExponent { get; set; } = 0.7f;
	[Property] public float StreakBloomAmount { get; set; } = 0.5f;
	[Property] public float StreakBloomCutoff { get; set; } = 5f;
	[Property] public float StreakBloomPreExponent { get; set; } = 1f;
	[Property] public float StreakBloomPostExponent { get; set; } = 1f;

	static readonly ComputeShader BloomShader = new( "shaders/titanfall2_bloom_cs.shader" );
	static readonly ProfilingSampler[] BloomProfiles = [new( "T2 GPU bloom extract" ), new( "T2 GPU bloom horizontal" ), new( "T2 GPU bloom vertical" )];
	static readonly ProfilingSampler DownsampleProfile = new( "T2 GPU bloom downsample" );
	static readonly ProfilingSampler WideProfile = new( "T2 GPU wide bloom" );
	static readonly ProfilingSampler StreakProfile = new( "T2 GPU streak bloom" );

	void BuildBloom( CommandList commands, RenderTargetHandle.ColorTextureRef source )
	{
		var strength = Finite( GetWeighted( x => x.BloomStrength ), 0, 0, 16 );
		var wideAmount = Finite( GetWeighted( x => x.WideBloomAmount ), 0, 0, 16 );
		var streakAmount = Finite( GetWeighted( x => x.StreakBloomAmount ), 0, 0, 16 );
		commands.Attributes.Set( "T2BloomStrength", strength );
		commands.Attributes.Set( "T2BloomTexture", Texture.Black );
		commands.Attributes.Set( "T2WideBloomTexture", Texture.Black );
		commands.Attributes.Set( "T2StreakBloomTexture", Texture.Black );
		commands.Attributes.Set( "T2BloomPostParameters", new Vector4(
			Finite( GetWeighted( x => x.BloomPostExponent, 1 ), 1, 0.01f, 8 ), wideAmount,
			Finite( GetWeighted( x => x.WideBloomPostExponent, 0.7f ), 0.7f, 0.01f, 8 ), streakAmount ) );
		commands.Attributes.Set( "T2StreakBloomPostExponent", Finite( GetWeighted( x => x.StreakBloomPostExponent, 1 ), 1, 0.01f, 8 ) );
		if ( strength <= 0 ) return;
		var cutoff = Finite( GetWeighted( x => x.BloomCutoff, 1 ), 1, 0, 65504 );
		var core = commands.GetRenderTarget( "T2Bloom", 4, ImageFormat.RGBA16161616F, ImageFormat.None );
		var coreH = commands.GetRenderTarget( "T2BloomHorizontal", 4, ImageFormat.RGBA16161616F, ImageFormat.None );
		var coreV = commands.GetRenderTarget( "T2BloomVertical", 4, ImageFormat.RGBA16161616F, ImageFormat.None );
		commands.Attributes.Set( "T2BloomParameters", new Vector2( cutoff, Finite( GetWeighted( x => x.BloomExponent, 1 ), 1, 0.01f, 8 ) ) );
		BloomPass( commands, source, core, 0 );
		BloomPass( commands, core.ColorTexture, coreH, 1 );
		BloomPass( commands, coreH.ColorTexture, coreV, 2 );
		commands.ResourceBarrierTransition( coreV, ResourceState.PixelShaderResource );
		commands.Attributes.Set( "T2BloomTexture", coreV.ColorTexture );

		if ( wideAmount > 0 )
		{
			using var profile = commands.ProfileScope( WideProfile );
			// A separate prefilter preserves wide's exponent instead of exponentiating
			// the already blurred core. Two 4x downsample steps form the wide footprint.
			var pre = commands.GetRenderTarget( "T2WideBloomPre", 4, ImageFormat.RGBA16161616F, ImageFormat.None );
			var small = commands.GetRenderTarget( "T2WideBloomSmall", 16, ImageFormat.RGBA16161616F, ImageFormat.None );
			var horizontal = commands.GetRenderTarget( "T2WideBloomH", 16, ImageFormat.RGBA16161616F, ImageFormat.None );
			var vertical = commands.GetRenderTarget( "T2WideBloomV", 16, ImageFormat.RGBA16161616F, ImageFormat.None );
			commands.Attributes.Set( "T2BloomParameters", new Vector2( cutoff, Finite( GetWeighted( x => x.WideBloomPreExponent, 2 ), 2, 0.01f, 8 ) ) );
			BloomPass( commands, source, pre, 0 );
			BloomPass( commands, pre.ColorTexture, small, 3 );
			BloomPass( commands, small.ColorTexture, horizontal, 1 );
			BloomPass( commands, horizontal.ColorTexture, vertical, 2 );
			commands.ResourceBarrierTransition( vertical, ResourceState.PixelShaderResource );
			commands.Attributes.Set( "T2WideBloomTexture", vertical.ColorTexture );
		}
		if ( streakAmount > 0 )
		{
			using var profile = commands.ProfileScope( StreakProfile );
			var pre = commands.GetRenderTarget( "T2StreakBloomPre", 4, ImageFormat.RGBA16161616F, ImageFormat.None );
			var first = commands.GetRenderTarget( "T2StreakBloomA", 4, ImageFormat.RGBA16161616F, ImageFormat.None );
			var second = commands.GetRenderTarget( "T2StreakBloomB", 4, ImageFormat.RGBA16161616F, ImageFormat.None );
			commands.Attributes.Set( "T2BloomParameters", new Vector2(
				Finite( GetWeighted( x => x.StreakBloomCutoff, 5 ), 5, 0, 65504 ),
				Finite( GetWeighted( x => x.StreakBloomPreExponent, 1 ), 1, 0.01f, 8 ) ) );
			BloomPass( commands, source, pre, 0 );
			// Successive horizontal radii extend highlights without smearing vertically.
			BloomPass( commands, pre.ColorTexture, first, 1 );
			BloomPass( commands, first.ColorTexture, second, 1, 2 );
			BloomPass( commands, second.ColorTexture, first, 1, 4 );
			commands.ResourceBarrierTransition( first, ResourceState.PixelShaderResource );
			commands.Attributes.Set( "T2StreakBloomTexture", first.ColorTexture );
		}
	}

	static void BloomPass( CommandList commands, RenderTargetHandle.ColorTextureRef input, RenderTargetHandle output, int stage, float spacing = 1 )
	{
		using var profile = commands.ProfileScope( stage == 3 ? DownsampleProfile : BloomProfiles[stage] );
		commands.ResourceBarrierTransition( output, ResourceState.UnorderedAccess );
		commands.Attributes.SetCombo( "D_BLOOM_STAGE", stage );
		commands.Attributes.Set( "T2BloomBlurSpacing", spacing );
		commands.Attributes.Set( "T2BloomInput", input );
		commands.Attributes.Set( "T2BloomOutput", output.ColorTexture );
		commands.DispatchCompute( BloomShader, output.Size );
		commands.ResourceBarrierTransition( output, ResourceState.NonPixelShaderResource );
	}
}
