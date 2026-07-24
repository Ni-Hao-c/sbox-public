enum Titanfall2MaterialSourceKind
{
	Rpak,
	Vmt,
	Particle
}

enum Titanfall2TextureSemantic
{
	Unknown,
	Albedo,
	Normal,
	GlossOrRoughness,
	SpecularOrMetalness,
	AmbientOcclusion,
	Opacity,
	Emissive,
	Detail,
	Distortion,
	Environment
}

enum Titanfall2DepthMode
{
	ReadWrite,
	ReadOnly,
	Disabled
}

enum Titanfall2CullMode
{
	Back,
	None
}

readonly record struct Titanfall2TextureBinding(
	string TexturePath,
	ulong TextureGuid,
	int HandleIndex,
	int ShaderRegister,
	string ShaderResourceName,
	Titanfall2TextureSemantic Semantic,
	bool Reflected );

readonly record struct Titanfall2MaterialTextureReference(
	string MaterialName,
	Titanfall2TextureSemantic Semantic,
	int ShaderRegister,
	string ShaderResourceName );

readonly record struct Titanfall2MaterialConstant( string Name, Vector4 Value, int Components );

readonly record struct Titanfall2RuntimeExpression(
	string Type,
	string Target,
	string SourceA,
	string SourceB,
	Vector4 Parameters );

/// <summary>
/// Source-independent description of a Titanfall material. MATL, VMT and PCF
/// adapters populate this object; runtime material creation and dependency
/// tracking consume the same representation.
/// </summary>
sealed class Titanfall2MaterialDescriptor
{
	static readonly Vector4 WhiteVector = new( 1f, 1f, 1f, 1f );
	public string Name { get; init; }
	public Titanfall2MaterialSourceKind SourceKind { get; init; }
	public Titanfall2MaterialMetadata Metadata { get; init; }
	public ulong ShaderSetGuid { get; init; }
	public string ShaderSetName { get; init; }
	public bool IsGodray { get; init; }
	public bool IsVista { get; init; }
	public bool IsRefract { get; init; }
	public bool IsHologram { get; init; }
	public bool HasEnvironmentReflection { get; set; }
	public Titanfall2MaterialMode Blend => Metadata.Mode;
	public Titanfall2DepthMode Depth => Metadata.IsDecal || Metadata.IsTranslucent
		? Titanfall2DepthMode.ReadOnly
		: Titanfall2DepthMode.ReadWrite;
	public Titanfall2CullMode Cull => Metadata.DoubleSided ? Titanfall2CullMode.None : Titanfall2CullMode.Back;
	public Titanfall2MaterialParameters? Parameters { get; set; }
	public float GodrayFadeScale { get; set; }
	public float GodrayFadeBias { get; set; }
	public List<Titanfall2TextureBinding> TextureBindings { get; } = [];
	public List<Titanfall2MaterialConstant> Constants { get; } = [];
	public List<Titanfall2RuntimeExpression> RuntimeExpressions { get; } = [];

	public bool HasSemantic( Titanfall2TextureSemantic semantic ) =>
		TextureBindings.Any( binding => binding.Semantic == semantic );

	internal static Titanfall2MaterialDescriptor FromVmt( string materialName, Titanfall2VmtDefinition definition )
	{
		var metadata = definition.GetMetadata( materialName );
		var descriptor = new Titanfall2MaterialDescriptor
		{
			Name = materialName,
			SourceKind = Titanfall2MaterialSourceKind.Vmt,
			Metadata = metadata,
			IsGodray = MaterialLoader.IsGodrayMaterial( materialName ),
			IsVista = MaterialLoader.IsVistaMaterial( materialName ),
			IsRefract = definition.ShaderName.Contains( "Refract", StringComparison.OrdinalIgnoreCase ),
			IsHologram = materialName.Contains( "holo", StringComparison.OrdinalIgnoreCase )
				|| definition.ShaderName.Contains( "Holo", StringComparison.OrdinalIgnoreCase ),
			HasEnvironmentReflection = !string.IsNullOrWhiteSpace( definition.GetString( "$envmap" ) )
		};
		AddVmtTexture( descriptor, Titanfall2TextureSemantic.Albedo, definition.GetTexture( "$basetexture", "$texture1" ), "$basetexture" );
		AddVmtTexture( descriptor, Titanfall2TextureSemantic.Normal, definition.GetTexture( "$bumpmap", "$normalmap" ), "$bumpmap" );
		AddVmtTexture( descriptor, Titanfall2TextureSemantic.GlossOrRoughness,
			definition.GetTexture( "$phongexponenttexture", "$roughnessmap" ), "$phongexponenttexture" );
		AddVmtTexture( descriptor, Titanfall2TextureSemantic.SpecularOrMetalness,
			definition.GetTexture( "$specularmap", "$reflectiontint", "$specular", "$metalnessmap" ), "$specularmap" );
		AddVmtTexture( descriptor, Titanfall2TextureSemantic.AmbientOcclusion,
			definition.GetTexture( "$ambientocclusiontexture", "$aotexture" ), "$ambientocclusiontexture" );
		AddVmtTexture( descriptor, Titanfall2TextureSemantic.Opacity,
			definition.GetTexture( "$opacitytexture", "$alphamask" ), "$opacitytexture" );
		AddVmtTexture( descriptor, Titanfall2TextureSemantic.Emissive,
			definition.GetTexture( "$selfillummask", "$emissive", "$emissivemap" ), "$selfillummask" );
		AddVmtTexture( descriptor, Titanfall2TextureSemantic.Detail,
			definition.GetTexture( "$detail", "$detailtexture", "$blendmodulatetexture" ), "$detail" );
		AddVmtTexture( descriptor, Titanfall2TextureSemantic.Distortion,
			definition.GetTexture( "$flowmap", "$distortionmap", "$refracttexture" ), "$flowmap" );

		var tint = NormalizeColor( definition.GetVector( "$color", definition.GetVector( "$color2", WhiteVector ) ) );
		descriptor.Constants.Add( new Titanfall2MaterialConstant( "g_vT2AlbedoTint", tint, 4 ) );
		descriptor.Constants.Add( new Titanfall2MaterialConstant( "g_flT2MaterialOpacity",
			new Vector4( definition.GetFloat( "$alpha", 1f ), 0f, 0f, 0f ), 1 ) );
		descriptor.Constants.Add( new Titanfall2MaterialConstant( "g_flT2AlphaTestReference",
			new Vector4( definition.GetFloat( "$alphatestreference", 0.5f ), 0f, 0f, 0f ), 1 ) );
		if ( definition.TryGetTextureScroll( false, out var scroll ) )
		{
			descriptor.Constants.Add( new Titanfall2MaterialConstant( "g_vT2Uv1Translate", new Vector4( scroll.x, scroll.y, 1f, 0f ), 4 ) );
		}
		descriptor.RuntimeExpressions.AddRange( definition.GetRuntimeExpressions() );
		return descriptor;
	}

	public Material CreateMaterial( Titanfall2Mount mount, string resourcePath, Action<string> warn )
	{
		var material = IsGodray
			? GodrayFadeScale > 0f
				? MaterialLoader.CreateGodrayMaterial( resourcePath, GodrayFadeScale, GodrayFadeBias )
				: MaterialLoader.CreateGodrayMaterial( resourcePath, Name )
			: IsVista
				? MaterialLoader.CreateVistaMaterial( resourcePath, Name, Metadata )
				: IsHologram
					? MaterialLoader.CreateHologramMaterial( resourcePath, Metadata )
					: IsRefract
						? MaterialLoader.CreateDistortionMaterial( resourcePath, Metadata )
						: Metadata.IsDecal
							? MaterialLoader.CreateDecalMaterial( resourcePath, Metadata )
							: MaterialLoader.CreateRuntimeMaterial( resourcePath, Metadata );

		if ( Parameters.HasValue ) MaterialLoader.ApplyMaterialParameters( material, Parameters.Value );
		foreach ( var constant in Constants ) ApplyConstant( material, constant );

		var boundParameters = new HashSet<string>( StringComparer.Ordinal );
		var albedoBound = false;
		foreach ( var binding in TextureBindings.OrderBy( binding => binding.HandleIndex ) )
		{
			var parameter = GetRuntimeParameter( binding.Semantic );
			if ( parameter is null )
			{
				warn?.Invoke( $"texture '{binding.TexturePath}' at t{binding.ShaderRegister} ({binding.ShaderResourceName ?? "unreflected"}) has no runtime semantic" );
				continue;
			}
			// The stock approximation exposes one texture per semantic. Preserve the
			// first reflected slot and do not let a later auxiliary map replace it.
			if ( !boundParameters.Add( parameter ) ) continue;

			var texture = Texture.Load( $"mount://{mount.Ident}/{binding.TexturePath}.vtex", false );
			if ( texture is null || texture.IsError || !texture.IsValid )
			{
				warn?.Invoke( $"texture '{binding.TexturePath}' (GUID 0x{binding.TextureGuid:X16}) could not be loaded" );
				continue;
			}
			material.Set( parameter, texture );
			if ( binding.Semantic == Titanfall2TextureSemantic.Normal ) material.Set( "g_flHasNormalMap", 1f );
			if ( binding.Semantic == Titanfall2TextureSemantic.Detail ) material.Set( "g_flT2DetailBlend", 1f );
			if ( binding.Semantic == Titanfall2TextureSemantic.Distortion && !Parameters.HasValue )
				material.Set( "g_vT2UvDistortion", new Vector4( 0.02f, 0.02f, 0f, 0f ) );
			if ( binding.Semantic == Titanfall2TextureSemantic.Environment )
				material.Set( "g_flT2HasEnvironment", 1f );
			if ( binding.Semantic == Titanfall2TextureSemantic.Albedo )
			{
				albedoBound = true;
				if ( IsVista ) material.Set( "g_flAlbedoIsSrgb", texture.ImageFormat == ImageFormat.BC6H ? 0f : 1f );
			}
			if ( binding.Semantic == Titanfall2TextureSemantic.Environment ) HasEnvironmentReflection = true;
		}

		if ( !albedoBound )
		{
			material.Set( "g_tAlbedo", Metadata.Mode == Titanfall2MaterialMode.Opaque ? Texture.White : Texture.Transparent );
			warn?.Invoke( "no colour/albedo texture was bound" );
		}
		if ( RuntimeExpressions.Count > 0 )
			Titanfall2MaterialAnimationRegistry.Register( material, Name, RuntimeExpressions );
		return material;
	}

	internal static Titanfall2TextureSemantic InferTextureSemantic( string shaderResourceName, string texturePath, bool first )
	{
		var reflected = InferFromToken( shaderResourceName );
		if ( reflected != Titanfall2TextureSemantic.Unknown ) return reflected;

		var name = System.IO.Path.GetFileNameWithoutExtension( texturePath ?? string.Empty );
		var suffix = InferFromSuffix( name );
		return suffix != Titanfall2TextureSemantic.Unknown
			? suffix
			: first ? Titanfall2TextureSemantic.Albedo : Titanfall2TextureSemantic.Unknown;
	}

	static Titanfall2TextureSemantic InferFromToken( string value )
	{
		if ( string.IsNullOrWhiteSpace( value ) ) return Titanfall2TextureSemantic.Unknown;
		var token = value.Replace( "_", string.Empty ).Replace( "$", string.Empty ).ToLowerInvariant();
		if ( token.Contains( "detailnormal" ) || token.Contains( "detailmask" ) || token.Contains( "layerblend" ) ) return Titanfall2TextureSemantic.Detail;
		if ( token.Contains( "cockpitscreen" ) && !token.Contains( "mask" ) ) return Titanfall2TextureSemantic.Emissive;
		if ( token.Contains( "normal" ) || token.Contains( "bump" ) ) return Titanfall2TextureSemantic.Normal;
		if ( token.Contains( "rough" ) || token.Contains( "gloss" ) || token.Contains( "exponent" ) ) return Titanfall2TextureSemantic.GlossOrRoughness;
		if ( token.Contains( "spec" ) || token.Contains( "metal" ) ) return Titanfall2TextureSemantic.SpecularOrMetalness;
		if ( token.Contains( "ambientocclusion" ) || token.Contains( "occlusion" ) || token.Contains( "cavity" ) || token.Contains( "cavtexture" ) ) return Titanfall2TextureSemantic.AmbientOcclusion;
		if ( token.Contains( "emiss" ) || token.Contains( "selfillum" ) || token.Contains( "illumtexture" ) ) return Titanfall2TextureSemantic.Emissive;
		if ( token.Contains( "opacity" ) || token.Contains( "transluc" ) || token.Contains( "alphamask" ) ) return Titanfall2TextureSemantic.Opacity;
		if ( token.Contains( "cloudmask" ) || token.Contains( "cockpitscreenmask" ) ) return Titanfall2TextureSemantic.Opacity;
		if ( token.Contains( "distort" ) || token.Contains( "refract" ) || token.Contains( "flowmap" ) ) return Titanfall2TextureSemantic.Distortion;
		if ( token.Contains( "environment" ) || token.Contains( "envmap" ) || token.Contains( "cubemap" ) || token.Contains( "reflection" ) ) return Titanfall2TextureSemantic.Environment;
		if ( token.Contains( "detail" ) || token.Contains( "blendmodulate" ) ) return Titanfall2TextureSemantic.Detail;
		if ( token.Contains( "albedo" ) || token.Contains( "diffuse" ) || token.Contains( "basetexture" ) || token.Contains( "colortexture" ) ) return Titanfall2TextureSemantic.Albedo;
		if ( token is "ao" or "aotexture" or "cav" ) return Titanfall2TextureSemantic.AmbientOcclusion;
		return Titanfall2TextureSemantic.Unknown;
	}

	static Titanfall2TextureSemantic InferFromSuffix( string name )
	{
		if ( EndsWithAny( name, "_nml", "_nrm", "_nor", "_normal", "_nm", "_bm" ) ) return Titanfall2TextureSemantic.Normal;
		if ( EndsWithAny( name, "_gls", "_gloss", "_rough", "_roughness", "_rgh", "_exp" ) ) return Titanfall2TextureSemantic.GlossOrRoughness;
		if ( EndsWithAny( name, "_spc", "_spec", "_metal", "_metalness" ) ) return Titanfall2TextureSemantic.SpecularOrMetalness;
		if ( EndsWithAny( name, "_ao", "_cav", "_cavity" ) ) return Titanfall2TextureSemantic.AmbientOcclusion;
		if ( EndsWithAny( name, "_ilm", "_ems", "_emit", "_emissive" ) ) return Titanfall2TextureSemantic.Emissive;
		if ( EndsWithAny( name, "_opa", "_opacity", "_alpha", "_mask" ) ) return Titanfall2TextureSemantic.Opacity;
		if ( EndsWithAny( name, "_detail", "_dtl" ) ) return Titanfall2TextureSemantic.Detail;
		if ( EndsWithAny( name, "_distort", "_distortion", "_flow" ) ) return Titanfall2TextureSemantic.Distortion;
		if ( EndsWithAny( name, "_env", "_cube", "_cubemap" ) ) return Titanfall2TextureSemantic.Environment;
		if ( EndsWithAny( name, "_col", "_alb", "_albedo", "_diff", "_diffuse", "_color", "_clr", "_basecolor" ) ) return Titanfall2TextureSemantic.Albedo;
		return Titanfall2TextureSemantic.Unknown;
	}

	static bool EndsWithAny( string value, params string[] suffixes ) =>
		suffixes.Any( suffix => value.EndsWith( suffix, StringComparison.OrdinalIgnoreCase ) );

	static void AddVmtTexture( Titanfall2MaterialDescriptor descriptor, Titanfall2TextureSemantic semantic,
		string texturePath, string sourceName )
	{
		if ( string.IsNullOrWhiteSpace( texturePath ) ) return;
		var index = descriptor.TextureBindings.Count;
		descriptor.TextureBindings.Add( new Titanfall2TextureBinding(
			texturePath, 0, index, index, sourceName, semantic, true ) );
	}

	static Vector4 NormalizeColor( Vector4 value )
	{
		var peak = MathF.Max( MathF.Abs( value.x ), MathF.Max( MathF.Abs( value.y ), MathF.Abs( value.z ) ) );
		return peak > 8f ? new Vector4( value.x / 255f, value.y / 255f, value.z / 255f, value.w ) : value;
	}

	static string GetRuntimeParameter( Titanfall2TextureSemantic semantic ) => semantic switch
	{
		Titanfall2TextureSemantic.Albedo => "g_tAlbedo",
		Titanfall2TextureSemantic.Normal => "g_tNormal",
		Titanfall2TextureSemantic.GlossOrRoughness => "g_tGloss",
		Titanfall2TextureSemantic.SpecularOrMetalness => "g_tSpecular",
		Titanfall2TextureSemantic.AmbientOcclusion => "g_tAO",
		Titanfall2TextureSemantic.Opacity => "g_tOpacity",
		Titanfall2TextureSemantic.Emissive => "g_tEmissive",
		Titanfall2TextureSemantic.Detail => "g_tDetail",
		Titanfall2TextureSemantic.Distortion => "g_tDistortion",
		Titanfall2TextureSemantic.Environment => "g_tEnvironment",
		_ => null
	};

	static void ApplyConstant( Material material, Titanfall2MaterialConstant constant )
	{
		if ( constant.Components <= 1 ) material.Set( constant.Name, constant.Value.x );
		else material.Set( constant.Name, constant.Value );
	}
}
