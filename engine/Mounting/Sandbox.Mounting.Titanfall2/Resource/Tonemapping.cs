using Sandbox.Rendering;
using System.Runtime.InteropServices;

/// <summary>Retail display curve and bounded logarithmic auto exposure.</summary>
[Library, Hide]
public sealed class Titanfall2Tonemapping : BasePostProcess<Titanfall2Tonemapping>
{
	[Property] public float ExposureCompensation { get; set; }
	[Property] public float MinimumExposure { get; set; } = 0.5f;
	[Property] public float MaximumExposure { get; set; } = 2f;
	/// <summary>Retail mat_autoexposure_speed: amount per rendered frame.</summary>
	[Property] public float AdaptationRate { get; set; } = 0.1f;
	/// <summary>Positive values bypass adaptation, matching c_forceExposure.</summary>
	[Property] public float ForcedExposure { get; set; }
	/// <summary>Core HDR bloom. Wide and streak branches are not yet imported.</summary>
	// Conservative mount defaults until authored map/script bloom scales and
	// the source HDR material ranges are imported. Retail's unit gain washes
	// out the current bright specular surfaces and white simulation geometry.
	[Property] public float BloomStrength { get; set; } = 0.15f;
	[Property] public float BloomCutoff { get; set; } = 1f;
	[Property] public float BloomExponent { get; set; } = 1f;

	static readonly Material Shader = Material.FromShader( "shaders/titanfall2_tonemapping.shader" );
	static readonly ComputeShader ExposureShader = new( "shaders/titanfall2_exposure_cs.shader" );
	static readonly ComputeShader BloomShader = new( "shaders/titanfall2_bloom_cs.shader" );
	static readonly string[] MeterNames = ["T2Meter0", "T2Meter1", "T2Meter2", "T2Meter3", "T2Meter4", "T2Meter5", "T2Meter6"];
	static readonly ProfilingSampler ExposureProfile = new( "T2 GPU exposure meter and adaptation" );
	static readonly ProfilingSampler[] BloomProfiles = [new( "T2 GPU bloom extract" ), new( "T2 GPU bloom horizontal" ), new( "T2 GPU bloom vertical" )];
	static readonly ProfilingSampler DisplayProfile = new( "T2 GPU display transform" );
	readonly Dictionary<CameraComponent, CameraState> _cameras = [];
	readonly List<CameraComponent> _staleCameras = [];
	internal static Titanfall2Tonemapping CreateMapDisplay( GameObject owner, float exposureCompensation )
	{
		// Give the host an ordinary native baseline, as Source 1 imports do.
		// Sandbox's default camera tonemapping yields to a global native tone;
		// a different effect type alone does not participate in that selection.
		var native = owner.AddComponent<Tonemapping>();
		native.Mode = Tonemapping.TonemappingMode.Linear;
		native.AutoExposureEnabled = false;
		var display = owner.AddComponent<Titanfall2Tonemapping>();
		display.ExposureCompensation = exposureCompensation;
		return display;
	}

	sealed class CameraState : IDisposable
	{
		internal readonly CommandList Commands = new( "Titanfall2Display" );
		internal readonly Texture Exposure = Texture.Create( 1, 1, ImageFormat.RGBA32323232F ).WithUAVBinding()
			.WithData( MemoryMarshal.AsBytes( new float[] { 1, 0, 1, -1 }.AsSpan() ).ToArray() ).Finish();
		// The camera's auto exposure belongs to the host scene, not to this effect.
		internal bool AutoExposureCaptured;
		internal bool AutoExposureWasEnabled;
		public void Dispose() => Exposure.Dispose();
	}

	public override void Render()
	{
		// Exposure is applied exactly once by our display pass. A native meter
		// on top would multiply it again and use a different adaptation function.
		// Remember what the camera had so removing this effect restores the host.
		if ( !_cameras.TryGetValue( Camera, out var state ) )
		{
			state = new CameraState
			{
				AutoExposureCaptured = true,
				AutoExposureWasEnabled = Camera.AutoExposure.Enabled
			};
			_cameras.Add( Camera, state );
		}
		Camera.AutoExposure.Enabled = false;
		_staleCameras.Clear();
		foreach ( var camera in _cameras.Keys )
			if ( !camera.IsValid() ) _staleCameras.Add( camera );
		foreach ( var stale in _staleCameras )
		{
			_cameras[stale].Dispose();
			_cameras.Remove( stale );
		}
		var commands = state.Commands;
		commands.Reset();
		var color = commands.Attributes.GrabFrameTexture( "ColorBuffer" );
		var input = color.ColorTexture;
		using ( commands.ProfileScope( ExposureProfile ) )
		{
			for ( var level = 0; level < 7; ++level )
			{
				var output = commands.GetRenderTarget( MeterNames[level], 1 << (2 * (level + 1)), ImageFormat.RGBA32323232F, ImageFormat.None );
				commands.Attributes.SetCombo( "D_STAGE", level == 0 ? 0 : 1 );
				commands.Attributes.Set( "T2MeterInput", input );
				commands.Attributes.Set( "T2MeterOutput", output.ColorTexture );
				commands.DispatchCompute( ExposureShader, output.Size );
				commands.ResourceBarrierTransition( output, ResourceState.NonPixelShaderResource );
				input = output.ColorTexture;
			}
			commands.Attributes.SetCombo( "D_STAGE", 2 );
			commands.Attributes.Set( "T2MeterInput", input );
			commands.Attributes.Set( "T2ExposureLimits", new Vector4(
				Finite( GetWeighted( x => x.MinimumExposure, 0.5f ), 0.5f, 0.000001f, 64 ),
				Finite( GetWeighted( x => x.MaximumExposure, 2f ), 2f, 0.000001f, 64 ),
				Finite( GetWeighted( x => x.ExposureCompensation ), 0, -16, 16 ),
				Finite( GetWeighted( x => x.AdaptationRate, 0.1f ), 0.1f, 0, 64 ) ) );
			commands.Attributes.Set( "T2ExposureFrame", new Vector2( RealTime.Now, Finite( GetWeighted( x => x.ForcedExposure ), 0, 0, 64 ) ) );
			commands.Attributes.Set( "T2ExposureHistory", state.Exposure );
			commands.ResourceBarrierTransition( state.Exposure, ResourceState.UnorderedAccess );
			commands.DispatchCompute( ExposureShader, 1, 1, 1 );
			commands.ResourceBarrierTransition( state.Exposure, ResourceState.PixelShaderResource );
		}
		commands.Attributes.Set( "T2ExposureTexture", state.Exposure );
		// Meter the original scene, before bloom. Feeding the glow back into the
		// meter would incorrectly darken the image when bloom is enabled.
		var bloomStrength = Finite( GetWeighted( x => x.BloomStrength ), 0, 0, 16 );
		commands.Attributes.Set( "T2BloomStrength", bloomStrength );
		commands.Attributes.Set( "T2BloomTexture", Texture.Black );
		if ( bloomStrength > 0 )
		{
			var bloom = commands.GetRenderTarget( "T2Bloom", 4, ImageFormat.RGBA16161616F, ImageFormat.None );
			var horizontal = commands.GetRenderTarget( "T2BloomHorizontal", 4, ImageFormat.RGBA16161616F, ImageFormat.None );
			var vertical = commands.GetRenderTarget( "T2BloomVertical", 4, ImageFormat.RGBA16161616F, ImageFormat.None );
			commands.Attributes.Set( "T2BloomParameters", new Vector2(
				Finite( GetWeighted( x => x.BloomCutoff ), 0, 0, 65504 ),
				Finite( GetWeighted( x => x.BloomExponent, 1 ), 1, 0.01f, 8 ) ) );
			BloomPass( commands, color.ColorTexture, bloom, 0 );
			BloomPass( commands, bloom.ColorTexture, horizontal, 1 );
			BloomPass( commands, horizontal.ColorTexture, vertical, 2 );
			commands.ResourceBarrierTransition( vertical, ResourceState.PixelShaderResource );
			commands.Attributes.Set( "T2BloomTexture", vertical.ColorTexture );
		}
		using ( commands.ProfileScope( DisplayProfile ) ) commands.Blit( Shader );
		InsertCommandList( commands, Stage.Tonemapping, 0, "Titanfall2Display" );
	}

	static void BloomPass( CommandList commands, RenderTargetHandle.ColorTextureRef input, RenderTargetHandle output, int stage )
	{
		using var profile = commands.ProfileScope( BloomProfiles[stage] );
		commands.Attributes.SetCombo( "D_BLOOM_STAGE", stage );
		commands.Attributes.Set( "T2BloomInput", input );
		commands.Attributes.Set( "T2BloomOutput", output.ColorTexture );
		commands.DispatchCompute( BloomShader, output.Size );
		commands.ResourceBarrierTransition( output, ResourceState.NonPixelShaderResource );
	}

	static float Finite( float value, float fallback, float minimum, float maximum ) =>
		float.IsFinite( value ) ? Math.Clamp( value, minimum, maximum ) : fallback;

	/// <summary>Hands every camera we touched back to its previous auto-exposure state.</summary>
	void RestoreAutoExposure()
	{
		foreach ( var (camera, state) in _cameras )
		{
			if ( !state.AutoExposureCaptured ) continue;
			state.AutoExposureCaptured = false;
			if ( camera.IsValid() ) camera.AutoExposure.Enabled = state.AutoExposureWasEnabled;
		}
	}

	protected override void OnDisabled() => RestoreAutoExposure();

	protected override void OnDestroy()
	{
		RestoreAutoExposure();
		foreach ( var state in _cameras.Values ) state.Dispose();
		_cameras.Clear();
	}
}
