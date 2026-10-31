using Sandbox.Rendering;

/// <summary>Retail 32-cubed RGB correction, sampled after the HDR display transform.</summary>
[Library, Hide]
public sealed class Titanfall2ColorCorrection : BasePostProcess<Titanfall2ColorCorrection>
{
	/// <summary>The mounted volume-texture resource that holds the 32-cubed table.</summary>
	[Property, Hide] public string GradingTexturePath { get; set; }
	[Property, Hide] public string SourcePath { get; set; }
	[Property, Range( 0, 1 )] public float Weight { get; set; } = 1;
	Texture _texture;
	bool _loadFailed;
	static Material _shader;

	internal void Configure( string gradingTexturePath, string sourcePath, float weight )
	{
		_texture = null;
		_loadFailed = false;
		GradingTexturePath = gradingTexturePath;
		SourcePath = sourcePath;
		Weight = Math.Clamp( weight, 0, 1 );
		Restore();
	}

	protected override Task OnLoad( LoadingContext context ) { Restore(); return Task.CompletedTask; }

	void Restore()
	{
		if ( _texture is not null || _loadFailed || string.IsNullOrEmpty( GradingTexturePath ) ) return;
		// The table belongs to the mount, not to this component, so a scene opened without
		// its mount renders uncorrected instead of failing. Warn once, not every frame.
		_texture = Texture.Load( GradingTexturePath, false );
		if ( _texture is null || !_texture.IsValid || _texture.IsError )
		{
			_texture = null;
			_loadFailed = true;
			Titanfall2Log.Warning( $"Unable to load the Titanfall 2 color-correction table '{GradingTexturePath}'." );
		}
	}

	public override void Render()
	{
		Restore();
		if ( _texture is null || !_texture.IsValid ) return;
		var weight = Math.Clamp( GetWeighted( value => value.Weight ), 0, 1 );
		if ( weight <= 0 ) return;
		_shader ??= Material.FromShader( "shaders/titanfall2_color_correction.shader" );
		Attributes.Set( "T2ColorLut", _texture );
		Attributes.Set( "T2ColorLutWeight", weight );
		Blit( BlitMode.WithBackbuffer( _shader, Stage.AfterPostProcess, 3990, false ), "Titanfall2ColorCorrection" );
	}

	// The table is a shared mounted resource; releasing the reference is all this component owns.
	protected override void OnDestroy() { _texture = null; }
}
