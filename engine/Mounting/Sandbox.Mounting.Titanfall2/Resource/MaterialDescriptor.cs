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
	Environment,
	LayerBlend,
	DetailNormal,
	DetailMask,
	Distortion2,
	EmissiveMultiply,
	Cavity
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
	bool Reflected,
	int Layer = 0 );

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
	Vector4 Parameters,
	string SourceC = null,
	string SourceD = null );

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
	public List<Titanfall2TextureBinding> TextureBindings { get; init; } = [];
	public List<Titanfall2MaterialConstant> Constants { get; init; } = [];
	public List<Titanfall2RuntimeExpression> RuntimeExpressions { get; init; } = [];

	public bool HasSemantic( Titanfall2TextureSemantic semantic ) =>
		TextureBindings.Any( binding => binding.Semantic == semantic );

	// Retail uber shaders decode RG as tangent slopes, including normals stored
	// in BC1. Compression describes storage, not the normal's mathematical encoding.
	internal bool UsesSlopeNormals( ImageFormat format ) => SourceKind == Titanfall2MaterialSourceKind.Rpak
		&& (format == ImageFormat.ATI2N || ShaderSetName?.StartsWith( "uber", StringComparison.OrdinalIgnoreCase ) == true);
	public bool UsesLayeredSurface => Metadata.Mode == Titanfall2MaterialMode.Opaque && !Metadata.IsDecal
		&& !Metadata.IsWater && !IsGodray && !IsVista && !IsHologram && !IsRefract
		&& HasSemantic( Titanfall2TextureSemantic.LayerBlend )
		&& TextureBindings.Any( binding => binding.Semantic == Titanfall2TextureSemantic.Albedo && binding.Layer == 1 );
	public bool UsesLayeredOpacity => Metadata.IsTranslucent && !Metadata.IsWater
		&& ShaderSetName?.Contains( "Lyrtrans", StringComparison.Ordinal ) == true
		&& HasSemantic( Titanfall2TextureSemantic.LayerBlend );
	bool IsAtmosphereSheet => SourceKind == Titanfall2MaterialSourceKind.Rpak && Metadata.IsTranslucent
		&& !Metadata.IsDecal && !Metadata.IsWater && !IsGodray
		&& Name?.StartsWith( "world/atmosphere/", StringComparison.OrdinalIgnoreCase ) == true;

	// UV0 is the mesh coordinate. UV1..3 are independent material transforms;
	// the digit after UV is the number of coordinate sets, followed by one
	// selector per material texture in register order (not the sparse handle
	// number). For example, UV20000100000 transforms only
	// detailNormalTexture, not the albedo or the two-layer blend mask.
	internal int GetTextureUvChannel( Titanfall2TextureBinding binding )
	{
		var match = System.Text.RegularExpressions.Regex.Match( ShaderSetName ?? string.Empty,
			@"UV[1-4](?<channels>[0-3]+)(?=Samp|_)",
			System.Text.RegularExpressions.RegexOptions.CultureInvariant );
		if ( !match.Success ) return 0;
		var channels = match.Groups["channels"].Value;
		// Missing/unresolved handles cannot be assigned safely from the name alone.
		// Preserve the existing path until that descriptor has a complete layout.
		if ( channels.Length != TextureBindings.Count ) return -1;
		// Emul's selector follows the main emission map even though its D3D
		// register is t17. Retail tiffany_coffee_01 samples t4/DetailNormal on
		// UV0 and t17 on UV1; sorting t15 before t17 swaps those selectors.
		static int Order( Titanfall2TextureBinding texture ) =>
			texture.Semantic == Titanfall2TextureSemantic.EmissiveMultiply ? 9 : texture.HandleIndex * 2;
		var index = TextureBindings.Count( candidate => Order( candidate ) < Order( binding ) );
		return index < channels.Length ? channels[index] - '0' : -1;
	}

	void ApplySurfaceUvChannels( Material material )
	{
		// Ordinary signs also select UVs per texture. Their animated UV1 can
		// collapse one axis deliberately for a scanline, while albedo stays UV0.
		// Keep the existing warped-UV path for unhandled Uvd variants; selecting
		// their unwarped coordinates here would discard authored distortion.
		var usesUnwarpedSurfaceUvs = SourceKind == Titanfall2MaterialSourceKind.Rpak
			&& ShaderSetName?.Contains( "Uvd", StringComparison.Ordinal ) != true;
		if ( !UsesLayeredSurface && !UsesLayeredOpacity && !IsAtmosphereSheet && !usesUnwarpedSurfaceUvs ) return;
		var primary = new Vector4( -1f, -1f, -1f, -1f );
		var secondary = primary;
		var auxiliary = primary;
		foreach ( var binding in TextureBindings )
		{
			var channel = GetTextureUvChannel( binding );
			var channels = binding.Layer == 1 ? secondary : primary;
			switch ( binding.Semantic )
			{
				case Titanfall2TextureSemantic.Albedo: channels.x = channel; break;
				case Titanfall2TextureSemantic.Normal: channels.y = channel; break;
				case Titanfall2TextureSemantic.GlossOrRoughness: channels.z = channel; break;
				case Titanfall2TextureSemantic.SpecularOrMetalness: channels.w = channel; break;
				case Titanfall2TextureSemantic.Opacity: auxiliary.x = channel; break;
				case Titanfall2TextureSemantic.Emissive: auxiliary.y = channel; break;
				case Titanfall2TextureSemantic.EmissiveMultiply:
					material.Set( "g_flT2EmissiveMultiplyUvChannel", (float)channel );
					break;
				case Titanfall2TextureSemantic.AmbientOcclusion: auxiliary.z = channel; break;
				case Titanfall2TextureSemantic.Cavity:
					material.Set( "g_flT2CavityUvChannel", (float)channel );
					break;
				case Titanfall2TextureSemantic.Detail: auxiliary.w = channel; break;
				case Titanfall2TextureSemantic.LayerBlend:
					material.Set( "g_flT2LayerBlendUvChannel", (float)channel );
					break;
			}
			if ( binding.Layer == 1 ) secondary = channels;
			else primary = channels;
		}
		material.Set( "g_vT2SurfaceUvChannels", primary );
		material.Set( "g_vT2SurfaceUvChannels2", secondary );
		material.Set( "g_vT2AuxUvChannels", auxiliary );
	}

	internal bool UsesTexture( Titanfall2TextureBinding binding )
	{
		if ( binding.Layer > 0 && !UsesLayeredSurface ) return false;
		return binding.Semantic switch
		{
			Titanfall2TextureSemantic.Unknown or Titanfall2TextureSemantic.DetailNormal or Titanfall2TextureSemantic.DetailMask => false,
			Titanfall2TextureSemantic.LayerBlend => UsesLayeredSurface || UsesLayeredOpacity,
			Titanfall2TextureSemantic.Normal => !IsGodray && !IsVista && !IsHologram && !Metadata.IsUnlit,
			_ => true
		};
	}

	internal static int InferTextureLayer( string resourceName )
	{
		var token = resourceName?.Replace( "_", string.Empty ).ToLowerInvariant();
		return token is "albedo2texture" or "normal2texture" or "gloss2texture" or "spec2texture"
			or "albedotexture2" or "normaltexture2" or "glosstexture2" or "spectexture2" ? 1 : 0;
	}

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
			// A holo_screens directory also contains ordinary Basic and Unlit
			// materials. Their authored blend/UVs must not acquire the generic
			// hologram's scanlines, Fresnel or reduced opacity.
			IsHologram = definition.ShaderName.Contains( "Holo", StringComparison.OrdinalIgnoreCase ),
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
			definition.GetTexture( "$detail", "$detailtexture" ), "$detail" );
		AddVmtTexture( descriptor, Titanfall2TextureSemantic.LayerBlend,
			definition.GetTexture( "$blendmodulatetexture" ), "$blendmodulatetexture" );
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

	internal void ApplyLegacyRuntimeBehavior( Titanfall2VmtDefinition definition )
	{
		if ( definition is null ) return;
		if ( definition.TryGetTextureScroll( false, out var scroll ) )
		{
			Constants.RemoveAll( static constant =>
				constant.Name.Equals( "g_vT2Uv1Translate", StringComparison.Ordinal ) );
			Constants.Add( new Titanfall2MaterialConstant(
				"g_vT2Uv1Translate",
				new Vector4( scroll.x, scroll.y, 1f, 0f ),
				4 ) );
		}

		RuntimeExpressions.AddRange( definition.GetRuntimeExpressions() );
	}

	public Material CreateMaterial( Titanfall2Mount mount, string resourcePath, Action<string> warn )
	{
		// Dark atmosphere cards share the Godray geometry/fade path, but retail
		// uses ordinary premultiplied blending for them, not additive lighting.
		var additiveGodray = IsGodray && (SourceKind != Titanfall2MaterialSourceKind.Rpak
			|| Metadata.Mode == Titanfall2MaterialMode.Additive);
		var material = additiveGodray
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
		material.SetFeature( "F_RENDER_BACKFACES", Metadata.DoubleSided ? 1 : 0 );

		if ( Parameters.HasValue )
		{
			MaterialLoader.ApplyMaterialParameters( material, Parameters.Value );
			if ( Metadata.IsWater ) material.Set( "g_vT2WaterFlow", Vector4.Zero );
		}
		// The retail atmosphere program includes engine fog/exposure terms we do
		// not yet reproduce. Depth-feather only these overlapping unlit fog sheets;
		// lamps, glass, decals and additive shafts keep hard depth intersections.
		if ( IsAtmosphereSheet ) material.Set( "g_flT2AtmosphereDepthFeather", 96f );
		foreach ( var constant in Constants ) ApplyConstant( material, constant );
		material.Set( "g_flT2Layered", UsesLayeredSurface ? 1f : 0f );
		material.Set( "g_flT2SpecularWorkflow", SourceKind == Titanfall2MaterialSourceKind.Rpak ? 1f : 0f );
		material.Set( "g_flT2LayeredOpacity", UsesLayeredOpacity ? 1f : 0f );
		ApplySurfaceUvChannels( material );

		var boundParameters = new HashSet<string>( StringComparer.Ordinal );
		var albedoBound = false;
		foreach ( var binding in TextureBindings.OrderBy( binding => binding.HandleIndex ) )
		{
			if ( !UsesTexture( binding ) ) continue;
			var parameter = GetRuntimeParameter( binding.Semantic );
			if ( parameter is not null && binding.Layer == 1 ) parameter += "2";
			if ( parameter is null )
			{
				warn?.Invoke( $"texture '{binding.TexturePath}' at t{binding.ShaderRegister} ({binding.ShaderResourceName ?? "unreflected"}) has no runtime semantic" );
				continue;
			}
			// Each surface layer has a distinct runtime slot. Preserve the first
			// reflected binding for that slot rather than overwriting it with an auxiliary map.
			if ( !boundParameters.Add( parameter ) ) continue;

			var texture = Texture.Load( $"mount://{mount.Ident}/{binding.TexturePath}.vtex", false );
			if ( texture is null || texture.IsError || !texture.IsValid )
			{
				warn?.Invoke( $"texture '{binding.TexturePath}' (GUID 0x{binding.TextureGuid:X16}) could not be loaded" );
				continue;
			}
			if ( binding.Semantic == Titanfall2TextureSemantic.Environment && texture.Depth != 6 )
			{
				warn?.Invoke( $"environment '{binding.TexturePath}' is not a cubemap" );
				continue;
			}
			// A failed texture loader returns White. It is a colour fallback, not
			// a normal pointing diagonally away from the surface.
			if ( binding.Semantic == Titanfall2TextureSemantic.Normal && texture == Texture.White ) continue;
			material.Set( parameter, texture );
			if ( binding.Semantic == Titanfall2TextureSemantic.Normal )
			{
				material.Set( binding.Layer == 1 ? "g_flHasNormalMap2" : "g_flHasNormalMap", 1f );
				material.Set( binding.Layer == 1 ? "g_flT2NormalSlope2" : "g_flT2NormalSlope",
					UsesSlopeNormals( texture.ImageFormat ) ? 1f : 0f );
				material.Set( binding.Layer == 1 ? "g_flT2NormalTwoChannel2" : "g_flT2NormalTwoChannel",
					texture.ImageFormat == ImageFormat.ATI2N ? 1f : 0f );
			}
			if ( binding.Semantic == Titanfall2TextureSemantic.Detail ) material.Set( "g_flT2DetailBlend", 1f );
			if ( binding.Semantic == Titanfall2TextureSemantic.EmissiveMultiply ) material.Set( "g_flT2HasEmissiveMultiply", 1f );
			if ( binding.Semantic == Titanfall2TextureSemantic.Cavity ) material.Set( "g_flT2HasCavity", 1f );
			if ( binding.Semantic == Titanfall2TextureSemantic.Distortion && !Parameters.HasValue )
				material.Set( "g_vT2UvDistortion", new Vector4( 0.02f, 0.02f, 0f, 0f ) );
			if ( binding.Semantic == Titanfall2TextureSemantic.Environment )
				material.Set( "g_flT2HasEnvironment", 1f );
			if ( binding.Semantic == Titanfall2TextureSemantic.Albedo && binding.Layer == 0 )
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
		// These contain blend weights or normals, not colour. Multiplying them
		// into albedo made layered walls appear covered in black holes.
		if ( token.Contains( "layerblend" ) || token.Contains( "blendmodulate" ) ) return Titanfall2TextureSemantic.LayerBlend;
		if ( token.Contains( "detailnormal" ) ) return Titanfall2TextureSemantic.DetailNormal;
		if ( token.Contains( "detailmask" ) ) return Titanfall2TextureSemantic.DetailMask;
		if ( token.Contains( "cockpitscreen" ) && !token.Contains( "mask" ) ) return Titanfall2TextureSemantic.Emissive;
		if ( token.Contains( "normal" ) || token.Contains( "bump" ) ) return Titanfall2TextureSemantic.Normal;
		if ( token.Contains( "rough" ) || token.Contains( "gloss" ) || token.Contains( "exponent" ) ) return Titanfall2TextureSemantic.GlossOrRoughness;
		if ( token.Contains( "spec" ) || token.Contains( "metal" ) ) return Titanfall2TextureSemantic.SpecularOrMetalness;
		if ( token.Contains( "cavity" ) || token.Contains( "cavtexture" ) || token == "cav" ) return Titanfall2TextureSemantic.Cavity;
		if ( token.Contains( "ambientocclusion" ) || token.Contains( "occlusion" ) ) return Titanfall2TextureSemantic.AmbientOcclusion;
		if ( token.Contains( "emissivemultiply" ) ) return Titanfall2TextureSemantic.EmissiveMultiply;
		if ( token.Contains( "emiss" ) || token.Contains( "selfillum" ) || token.Contains( "illumtexture" ) ) return Titanfall2TextureSemantic.Emissive;
		if ( token.Contains( "opacity" ) || token.Contains( "transluc" ) || token.Contains( "alphamask" ) ) return Titanfall2TextureSemantic.Opacity;
		if ( token.Contains( "cloudmask" ) || token.Contains( "cockpitscreenmask" ) ) return Titanfall2TextureSemantic.Opacity;
		if ( token is "uvdistortion2texture" or "uvdistortiontexture2" ) return Titanfall2TextureSemantic.Distortion2;
		if ( token.Contains( "distort" ) || token.Contains( "refract" ) || token.Contains( "flowmap" ) ) return Titanfall2TextureSemantic.Distortion;
		if ( token.Contains( "environment" ) || token.Contains( "envmap" ) || token.Contains( "cubemap" ) || token.Contains( "reflection" ) ) return Titanfall2TextureSemantic.Environment;
		if ( token.Contains( "detail" ) ) return Titanfall2TextureSemantic.Detail;
		if ( token.Contains( "albedo" ) || token.Contains( "diffuse" ) || token.Contains( "basetexture" ) || token.Contains( "colortexture" ) ) return Titanfall2TextureSemantic.Albedo;
		if ( token is "ao" or "aotexture" ) return Titanfall2TextureSemantic.AmbientOcclusion;
		return Titanfall2TextureSemantic.Unknown;
	}

	static Titanfall2TextureSemantic InferFromSuffix( string name )
	{
		if ( EndsWithAny( name, "_nml", "_nrm", "_nor", "_normal", "_nm", "_bm" ) ) return Titanfall2TextureSemantic.Normal;
		if ( EndsWithAny( name, "_gls", "_gloss", "_rough", "_roughness", "_rgh", "_exp" ) ) return Titanfall2TextureSemantic.GlossOrRoughness;
		if ( EndsWithAny( name, "_spc", "_spec", "_metal", "_metalness" ) ) return Titanfall2TextureSemantic.SpecularOrMetalness;
		if ( EndsWithAny( name, "_cav", "_cavity" ) ) return Titanfall2TextureSemantic.Cavity;
		if ( EndsWithAny( name, "_ao" ) ) return Titanfall2TextureSemantic.AmbientOcclusion;
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
		Titanfall2TextureSemantic.Cavity => "g_tCavity",
		Titanfall2TextureSemantic.Opacity => "g_tOpacity",
		Titanfall2TextureSemantic.Emissive => "g_tEmissive",
		Titanfall2TextureSemantic.EmissiveMultiply => "g_tEmissiveMultiply",
		Titanfall2TextureSemantic.Detail => "g_tDetail",
		Titanfall2TextureSemantic.Distortion => "g_tDistortion",
		Titanfall2TextureSemantic.Distortion2 => "g_tDistortion2",
		Titanfall2TextureSemantic.Environment => "g_tEnvironment",
		Titanfall2TextureSemantic.LayerBlend => "g_tLayerBlend",
		_ => null
	};

	static void ApplyConstant( Material material, Titanfall2MaterialConstant constant )
	{
		if ( constant.Components <= 1 ) material.Set( constant.Name, constant.Value.x );
		else material.Set( constant.Name, constant.Value );
	}
}
