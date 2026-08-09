/// <summary>Builds a runtime material from a legacy VMT when no RPAK MATL exists at the same path.</summary>
class LegacyMaterialLoader( string materialName ) : ResourceLoader<Titanfall2Mount>
{
	static int _warningCount;
	const int WarningLimit = 64;
	static readonly Vector4 WhiteVector = new( 1f, 1f, 1f, 1f );

	protected override object Load()
	{
		if ( !Host.TryGetLegacyMaterialDefinition( materialName, out var definition, out var error ) )
		{
			Warn( $"could not parse VMT: {error}" );
			return null;
		}

		var metadata = definition.GetMetadata( materialName );
		var descriptor = Titanfall2MaterialDescriptor.FromVmt( materialName, definition );
		Host.RegisterMaterialDescriptor( descriptor );
		if ( definition.IsUnlitTwoTexture )
			return CreateUnlitTwoTexture( Host, Path, definition, metadata, Warn );
		return descriptor.CreateMaterial( Host, Path, Warn );
	}

	internal static Material CreateUnlitTwoTexture( Titanfall2Mount host, string resourcePath,
		Titanfall2VmtDefinition definition, Titanfall2MaterialMetadata metadata, Action<string> warn )
	{
		var shader = metadata.Mode switch
		{
			Titanfall2MaterialMode.Additive => "shaders/titanfall2_vmt_unlit_two_texture_additive.shader",
			Titanfall2MaterialMode.Opaque => "shaders/titanfall2_vmt_unlit_two_texture_opaque.shader",
			_ => "shaders/titanfall2_vmt_unlit_two_texture.shader"
		};
		var material = Material.Create( resourcePath, shader );
		material.Set( "g_tTexture1", Texture.Transparent );
		material.Set( "g_tTexture2", Texture.Transparent );

		var texture1 = definition.GetTexture( "$basetexture" );
		var texture2 = definition.GetTexture( "$texture2" );
		var texture1Animated = definition.TryGetAnimatedTexture( false, out var texture1FrameRate );
		var texture2Animated = definition.TryGetAnimatedTexture( true, out var texture2FrameRate );
		var hasTexture1 = BindLayerTexture( host, warn, material, "g_tTexture1", texture1, texture1Animated, texture1FrameRate,
			"g_flVmtTexture1FrameCount", "g_flVmtTexture1FrameRate" );
		var hasTexture2 = BindLayerTexture( host, warn, material, "g_tTexture2", texture2, texture2Animated, texture2FrameRate,
			"g_flVmtTexture2FrameCount", "g_flVmtTexture2FrameRate" );
		material.Set( "g_flVmtHasTexture1", hasTexture1 ? 1f : 0f );
		material.Set( "g_flVmtHasTexture2", hasTexture2 ? 1f : 0f );

		var color1 = NormalizeColor( definition.GetVector( "$color", definition.GetVector( "$layercolor1", WhiteVector ) ) );
		var color2 = NormalizeColor( definition.GetVector( "$color2", definition.GetVector( "$layercolor2", WhiteVector ) ) );
		material.Set( "g_vVmtColor1", color1 );
		material.Set( "g_vVmtColor2", color2 );
		material.Set( "g_flVmtLayerAlpha1", definition.GetFloat( "$layeralpha1", 1f ) );
		material.Set( "g_flVmtLayerAlpha2", definition.GetFloat( "$layeralpha2", 1f ) );
		material.Set( "g_flVmtMaterialAlpha", definition.GetFloat( "$alpha", 1f ) );
		material.Set( "g_flVmtUseVertexColor", metadata.UsesVertexColor ? 1f : 0f );
		material.Set( "g_flVmtUseVertexAlpha", metadata.UsesVertexAlpha ? 1f : 0f );

		ApplyLayerTransform( material, definition, false, "1" );
		ApplyLayerTransform( material, definition, true, "2" );
		var noise = definition.GetNoiseRange( "$scroll" );
		material.Set( "g_vVmtNoiseMinimum", new Vector4( noise.Minimum.x, noise.Minimum.y, 0f, 0f ) );
		material.Set( "g_vVmtNoiseMaximum", new Vector4( noise.Maximum.x, noise.Maximum.y, 0f, 0f ) );
		material.Set( "g_flVmtNoiseEnabled", noise.IsDefined ? 1f : 0f );
		var noiseRate = MathF.Max( texture1FrameRate, texture2FrameRate );
		material.Set( "g_flVmtNoiseRate", noiseRate > 0f ? noiseRate : 30f );

		material.Set( "g_flVmtFresnelEnabled", definition.GetBool( "$fresnel" ) ? 1f : 0f );
		material.Set( "g_flVmtFresnelSharpness", MathF.Max( 0.001f, definition.GetFloat( "$fresnelsharpness1", 5f ) ) );
		material.Set( "g_flVmtFresnelInnerStrength", definition.GetFloat( "$fresnelinnerstrength1", 1f ) );
		material.Set( "g_flVmtFresnelOuterStrength", definition.GetFloat( "$fresnelouterstrength1", 1f ) );
		return material;
	}

	static void ApplyLayerTransform( Material material, Titanfall2VmtDefinition definition, bool texture2, string suffix )
	{
		var transform = definition.TryGetTextureTransform( texture2, out var parsed ) ? parsed : VmtTextureTransform.Identity;
		var scaleFallback = texture2 ? definition.GetFloat( "$t2scale", 1f ) : 1f;
		var rotationFallback = texture2 ? definition.GetFloat( "$t2rot", 0f ) : 0f;
		if ( !definition.TryGetTextureTransform( texture2, out _ ) )
			transform = new VmtTextureTransform( scaleFallback, rotationFallback, transform.Translation );
		var scroll = definition.TryGetTextureScroll( texture2, out var velocity ) ? velocity : Vector2.Zero;
		material.Set( $"g_vVmtTexture{suffix}Transform", new Vector4(
			transform.Scale,
			transform.RotationDegrees * MathF.PI / 180f,
			transform.Translation.x,
			transform.Translation.y ) );
		material.Set( $"g_vVmtTexture{suffix}Scroll", new Vector4( scroll.x, scroll.y, 0f, 0f ) );
	}

	static bool BindLayerTexture( Titanfall2Mount host, Action<string> warn, Material material, string parameter,
		string textureName, bool animated, float frameRate,
		string frameCountParameter, string frameRateParameter )
	{
		var texture = LoadTexture( host, textureName );
		var frameCount = 1;
		if ( animated && texture is not null && host.TryGetLegacyAnimatedTexturePath( textureName, out var animatedPath ) )
		{
			var atlas = Texture.Load( $"mount://{host.Ident}/{animatedPath}.vtex", false );
			if ( atlas is not null && !atlas.IsError && atlas.IsValid && texture.Height > 0 && atlas.Height >= texture.Height )
			{
				frameCount = Math.Max( 1, atlas.Height / texture.Height );
				texture = atlas;
			}
		}
		if ( texture is not null ) material.Set( parameter, texture );
		else if ( !string.IsNullOrWhiteSpace( textureName ) ) warn?.Invoke( $"texture '{textureName}' could not be loaded for {parameter}" );
		material.Set( frameCountParameter, (float)frameCount );
		material.Set( frameRateParameter, animated ? MathF.Max( 0f, frameRate ) : 0f );
		return texture is not null;
	}

	static Texture LoadTexture( Titanfall2Mount host, string textureName )
	{
		if ( string.IsNullOrWhiteSpace( textureName ) ) return null;
		var texture = Texture.Load( $"mount://{host.Ident}/{textureName}.vtex", false );
		return texture is not null && !texture.IsError && texture.IsValid ? texture : null;
	}

	static Vector4 NormalizeColor( Vector4 value )
	{
		var peak = MathF.Max( MathF.Abs( value.x ), MathF.Max( MathF.Abs( value.y ), MathF.Abs( value.z ) ) );
		return peak > 8f ? new Vector4( value.x / 255f, value.y / 255f, value.z / 255f, value.w ) : value;
	}

	void Warn( string reason )
	{
		if ( System.Threading.Interlocked.Increment( ref _warningCount ) > WarningLimit ) return;
		Titanfall2Log.Warning( $"Titanfall 2 legacy material binding failed for '{materialName}': {reason}." );
	}
}
