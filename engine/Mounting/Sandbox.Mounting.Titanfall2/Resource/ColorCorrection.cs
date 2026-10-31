using Sandbox.Rendering;

/// <summary>Retail 32-cubed RGB correction, sampled after the HDR display transform.</summary>
[Library]
public sealed class Titanfall2ColorCorrection : BasePostProcess<Titanfall2ColorCorrection>
{
	[Property, Hide] public string EncodedLut { get; set; }
	[Property, Hide] public string SourcePath { get; set; }
	[Property, Range( 0, 1 )] public float Weight { get; set; } = 1;
	Texture _texture;
	bool _restoreFailed;
	static Material _shader;

	internal void Configure( byte[] bytes, string sourcePath, float weight )
	{
		if ( bytes?.Length != 32 * 32 * 32 * 3 ) throw new InvalidDataException( "A retail RGB lookup must contain 32 cubed samples." );
		_texture?.Dispose();
		_texture = null;
		_restoreFailed = false;
		EncodedLut = Convert.ToBase64String( bytes );
		SourcePath = sourcePath;
		Weight = Math.Clamp( weight, 0, 1 );
		Restore();
	}

	protected override Task OnLoad( LoadingContext context ) { Restore(); return Task.CompletedTask; }

	void Restore()
	{
		if ( _texture is not null || string.IsNullOrEmpty( EncodedLut ) ) return;
		var rgb = Convert.FromBase64String( EncodedLut );
		if ( rgb.Length != 32 * 32 * 32 * 3 ) throw new InvalidDataException( "Invalid retail color lookup." );
		var rgba = new byte[32 * 32 * 32 * 4];
		for ( var sample = 0; sample < rgb.Length / 3; ++sample )
		{
			rgb.AsSpan( sample * 3, 3 ).CopyTo( rgba.AsSpan( sample * 4 ) );
			rgba[sample * 4 + 3] = 255;
		}
		_texture = Texture.CreateVolume( 32, 32, 32 ).WithFormat( ImageFormat.RGBA8888 ).WithData( rgba ).Finish();
	}

	public override void Render()
	{
		// Render runs every frame with no engine catch: a corrupt serialized lookup
		// must fail once here, not throw from every frame.
		if ( _texture is null && !_restoreFailed )
		{
			try { Restore(); }
			catch ( Exception ) { _restoreFailed = true; }
		}
		if ( _texture is null || !_texture.IsValid ) return;
		var weight = Math.Clamp( GetWeighted( value => value.Weight ), 0, 1 );
		if ( weight <= 0 ) return;
		_shader ??= Material.FromShader( "shaders/titanfall2_color_correction.shader" );
		Attributes.Set( "T2ColorLut", _texture );
		Attributes.Set( "T2ColorLutWeight", weight );
		Blit( BlitMode.WithBackbuffer( _shader, Stage.AfterPostProcess, 3990, false ), "Titanfall2ColorCorrection" );
	}

	protected override void OnDestroy() { _texture?.Dispose(); _texture = null; }
}
