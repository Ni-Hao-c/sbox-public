using System.Buffers.Binary;

enum Titanfall2DecalMode
{
	None,
	Cutout,
	Premultiplied,
	AlphaBlend,
	Additive
}

enum Titanfall2MaterialMode
{
	Opaque,
	Cutout,
	Premultiplied,
	AlphaBlend,
	Additive
}

readonly record struct Titanfall2MaterialMetadata(
	Titanfall2MaterialMode Mode,
	bool IsDecal = false,
	bool DoubleSided = false,
	bool UsesVertexColor = false,
	bool UsesVertexAlpha = false,
	bool IsUnlit = false,
	bool IsWater = false )
{
	public Titanfall2DecalMode DecalMode => !IsDecal
		? Titanfall2DecalMode.None
		: Mode switch
		{
			Titanfall2MaterialMode.Additive => Titanfall2DecalMode.Additive,
			Titanfall2MaterialMode.AlphaBlend => Titanfall2DecalMode.AlphaBlend,
			Titanfall2MaterialMode.Premultiplied => Titanfall2DecalMode.Premultiplied,
			_ => Titanfall2DecalMode.Cutout
		};

	public bool IsTranslucent => Mode is Titanfall2MaterialMode.Premultiplied
		or Titanfall2MaterialMode.AlphaBlend or Titanfall2MaterialMode.Additive;
}

/// <summary>Builds a Titanfall 2 material and binds its RPAK texture GUIDs.</summary>
class MaterialLoader( RpakArchive archive, RpakAsset asset ) : ResourceLoader<Titanfall2Mount>
{
	static int _bindingWarningCount;
	static readonly object SharedTextureLock = new();
	static Texture _neutralEnvironment;
	const int BindingWarningLimit = 64;
	const int MaterialHeaderSize = 208;
	const int DxState0BlendOffset = 0x50;
	const int DxState1BlendOffset = 0x70;
	const int DxState0RasterizerOffset = 0x66;
	const int DxState1RasterizerOffset = 0x86;
	const int ShaderSetGuidOffset = 0x90;
	const int GlueFlagsOffset = 0xC0;
	const ushort RasterizerPresetMask = 0x70;
	const ushort RasterizerPresetDecal = 0x10;
	const ushort RasterizerCullMask = 0x06;
	const ushort RasterizerCullNone = 0x02;
	const uint GlueAlphaTest = 1u << 4;
	const uint BlendEnable = 1u << 1;
	const uint BlendFieldMask = 0x1F;
	const uint D3dBlendOne = 2;
	const uint D3dBlendSourceAlpha = 5;

	protected override object Load()
	{
		if ( asset.Version != 12 )
		{
			Titanfall2Log.Warning( $"Failed to read Titanfall 2 material '{Path}': unsupported version {asset.Version}" );
			return null;
		}
		if ( !archive.TryReadAsset( asset, out var data, out var error ) )
		{
			Titanfall2Log.Warning( $"Failed to read Titanfall 2 material '{Path}': {error}" );
			return null;
		}
		if ( data.Header.Length < MaterialHeaderSize )
		{
			Titanfall2Log.Warning( $"Titanfall 2 material header is truncated: {Path}" );
			return null;
		}

		// Titanfall 2 can retain legacy VMT behavior for materials whose texture data is
		// supplied by RPAK. Preserve the RPAK/TXTR resource priority, but use the VMT's
		// two-layer blend, animation and proxy parameters when the generic MATL path
		// cannot represent them.
		Host.TryGetLegacyMaterialDefinition( asset.Name, out var legacyDefinition, out _ );
		if ( legacyDefinition is not null && legacyDefinition.IsUnlitTwoTexture )
		{
			var legacyMetadata = legacyDefinition.GetMetadata( asset.Name );
			Host.RegisterMaterialDescriptor( Titanfall2MaterialDescriptor.FromVmt( asset.Name, legacyDefinition ) );
			return LegacyMaterialLoader.CreateUnlitTwoTexture(
				Host, Path, legacyDefinition, legacyMetadata, WarnBinding );
		}

		var handles = ReadPagePointer( data.Header, 152 );
		var streamingHandles = ReadPagePointer( data.Header, 160 );
		var handleCount = handles.Index == streamingHandles.Index && streamingHandles.Offset >= handles.Offset
			? (streamingHandles.Offset - handles.Offset) / 8
			: 0;
		if ( handleCount < 0 || handleCount > 64 ) handleCount = 0;

		var shaderSetGuid = GetShaderSetGuid( data.Header );
		Host.TryGetShaderSetName( shaderSetGuid, out var shaderSetName );
		var isGodray = IsGodrayMaterial( asset.Name ) || IsGodrayMaterial( Path );
		var vistaMode = GetVistaMaterialMode( IsVistaMaterial( asset.Name ) ? asset.Name : Path );
		var metadata = ReadMetadata( data.Header, asset.Name, shaderSetName );
		var descriptor = new Titanfall2MaterialDescriptor
		{
			Name = asset.Name,
			SourceKind = Titanfall2MaterialSourceKind.Rpak,
			Metadata = metadata,
			ShaderSetGuid = shaderSetGuid,
			ShaderSetName = shaderSetName,
			IsGodray = isGodray,
			IsVista = vistaMode != VistaMaterialMode.None,
			IsRefract = ContainsShaderFeature( shaderSetName, "Refract" ) || ContainsShaderFeature( shaderSetName, "Distort" ),
			IsHologram = ContainsShaderFeature( shaderSetName, "Hologram" ) || ContainsShaderFeature( shaderSetName, "Holo" )
		};
		// Titanfall resolves the RPAK MATL/TXTR first, but legacy VMT proxies still
		// provide runtime behaviour for a number of map materials. In particular,
		// Basic materials such as wargame_grid_pulse retain TextureScroll in VMT.
		// Merge only that behaviour here so native textures and render state remain
		// authoritative.
		if ( legacyDefinition is not null )
			descriptor.ApplyLegacyRuntimeBehavior( legacyDefinition );
		if ( isGodray && TryReadGodrayFade( archive, asset, out var fadeScale, out var fadeBias ) )
		{
			descriptor.GodrayFadeScale = fadeScale;
			descriptor.GodrayFadeBias = fadeBias;
		}
		if ( !isGodray )
		{
			if ( Host.TryReadMaterialParameters( archive, asset, shaderSetGuid, shaderSetName, out var parameters, out var parameterError ) )
				descriptor.Parameters = parameters;
			else if ( HasAnimatedUv( shaderSetName ) )
				WarnBinding( $"dynamic UV constants could not be read: {parameterError}" );
		}
		Host.TryGetShaderReflection( shaderSetGuid, out var reflection, out _ );
		if ( handleCount == 0 || !archive.TryReadPagePointer( handles, handleCount * 8, out var guidBytes, out error ) )
		{
			Host.RegisterMaterialDescriptor( descriptor );
			return descriptor.CreateMaterial( Host, Path, WarnBinding );
		}

		var first = true;
		for ( var index = 0; index < handleCount; index++ )
		{
			var guid = BinaryPrimitives.ReadUInt64LittleEndian( guidBytes.AsSpan( index * 8, 8 ) );
			if ( guid == 0 ) continue;
			if ( !Host.TryGetTexturePath( guid, out var texturePath ) )
			{
				WarnBinding( $"texture GUID 0x{guid:X16} is not registered" );
				continue;
			}
			DxbcResourceBinding resource = default;
			var reflected = reflection is not null && reflection.TryGetTexture( index, out resource );
			var resourceName = reflected ? resource.Name : null;
			var semantic = Titanfall2MaterialDescriptor.InferTextureSemantic( resourceName, texturePath, first );
			if ( semantic == Titanfall2TextureSemantic.Unknown
				&& index > 0
				&& descriptor.IsVista
				&& IsVistaOpacityMaterial( descriptor.Name ) )
			{
				// Vista smoke/cloud cards commonly store colour and opacity in
				// separate anonymous texture handles. Older shader reflection does
				// not name that second slot, so leaving it unknown turns the entire
				// low-poly card into an opaque grey/orange polygon.
				semantic = Titanfall2TextureSemantic.Opacity;
			}
			descriptor.TextureBindings.Add( new Titanfall2TextureBinding(
				texturePath, guid, index, reflected ? resource.BindPoint : index, resourceName, semantic, reflected ) );
			first = false;
		}

		Host.RegisterMaterialDescriptor( descriptor );
		return descriptor.CreateMaterial( Host, Path, WarnBinding );
	}

	enum VistaMaterialMode
	{
		None,
		Opaque,
		Translucent,
		Additive
	}

	void WarnBinding( string reason )
	{
		if ( System.Threading.Interlocked.Increment( ref _bindingWarningCount ) > BindingWarningLimit ) return;
		Titanfall2Log.Warning( $"Titanfall 2 material binding failed for '{Path}' from '{System.IO.Path.GetFileName( archive.FilePath )}': {reason}." );
	}

	/// <summary>
	/// The Titanfall shader samples these maps for every material. RPAK materials
	/// are allowed to omit any of them, so explicitly bind neutral textures rather
	/// than allowing Vulkan to substitute an unnamed error texture.
	/// </summary>
	internal static Material CreateRuntimeMaterial( string name ) => CreateRuntimeMaterial( name, default );

	internal static Material CreateRuntimeMaterial( string name, Titanfall2MaterialMetadata metadata )
	{
		if ( metadata.IsWater && !metadata.IsDecal ) return CreateWaterMaterial( name, metadata );
		var shader = metadata.Mode switch
		{
			Titanfall2MaterialMode.Cutout => "shaders/titanfall2_cutout.shader",
			Titanfall2MaterialMode.Premultiplied => "shaders/titanfall2_translucent.shader",
			Titanfall2MaterialMode.AlphaBlend => "shaders/titanfall2_alpha.shader",
			Titanfall2MaterialMode.Additive => "shaders/titanfall2_additive.shader",
			_ => "shaders/titanfall2_default.shader"
		};
		var material = Material.Create( name, shader );
		material.Set( "g_tAlbedo", Texture.White );
		material.Set( "g_tNormal", Texture.White );
		material.Set( "g_tGloss", Texture.Black );
		material.Set( "g_tSpecular", Texture.Black );
		material.Set( "g_tAO", Texture.White );
		material.Set( "g_tOpacity", Texture.White );
		material.Set( "g_tEmissive", Texture.Black );
		material.Set( "g_tDetail", Texture.White );
		material.Set( "g_tDistortion", Texture.White );
		BindNeutralEnvironment( material );
		SetExtendedDefaults( material );
		material.Set( "g_flUseVertexColor", metadata.UsesVertexColor ? 1f : 0f );
		material.Set( "g_flUseVertexAlpha", metadata.UsesVertexAlpha ? 1f : 0f );
		return material;
	}

	internal static Material CreateWaterMaterial( string name, Titanfall2MaterialMetadata metadata )
	{
		var material = Material.Create( name, "shaders/titanfall2_water.shader" );
		material.Set( "g_tAlbedo", Texture.White );
		material.Set( "g_tNormal", Texture.White );
		material.Set( "g_tGloss", Texture.Black );
		material.Set( "g_tSpecular", Texture.Black );
		material.Set( "g_tAO", Texture.White );
		material.Set( "g_tOpacity", Texture.White );
		material.Set( "g_tEmissive", Texture.Black );
		material.Set( "g_tDetail", Texture.White );
		material.Set( "g_tDistortion", Texture.White );
		SetExtendedDefaults( material );
		material.Set( "g_flHasNormalMap", 0f );
		material.Set( "g_flUseVertexColor", metadata.UsesVertexColor ? 1f : 0f );
		material.Set( "g_flUseVertexAlpha", metadata.UsesVertexAlpha ? 1f : 0f );
		// Titanfall water uses scene refraction and fog to retain body even when
		// its source alpha is low. The runtime shader cannot sample that original
		// buffer, so keep a conservative opacity floor instead.
		material.Set( "g_flWaterOpacity", metadata.IsTranslucent ? 0.84f : 0.90f );
		var normalized = name?.Replace( '\\', '/' ) ?? string.Empty;
		var flow = normalized.Contains( "waterfall", StringComparison.OrdinalIgnoreCase )
			? new Vector4( 0.018f, -0.18f, -0.012f, -0.09f )
			: normalized.Contains( "whtwsh", StringComparison.OrdinalIgnoreCase )
				? new Vector4( 0.035f, -0.055f, -0.022f, -0.032f )
				: normalized.Contains( "homestead_water", StringComparison.OrdinalIgnoreCase )
					? new Vector4( 0.025f, 0.012f, -0.014f, 0.019f )
					: new Vector4( 0.010f, 0.004f, -0.006f, 0.008f );
		material.Set( "g_vT2WaterFlow", flow );
		return material;
	}

	internal static Material CreateHologramMaterial( string name, Titanfall2MaterialMetadata metadata )
	{
		var material = Material.Create( name, "shaders/titanfall2_hologram.shader" );
		InitializeSpecialMaterial( material, metadata );
		material.Set( "g_flT2FresnelStrength", 1.5f );
		material.Set( "g_flT2HologramScanlineStrength", 0.28f );
		return material;
	}

	internal static Material CreateDistortionMaterial( string name, Titanfall2MaterialMetadata metadata )
	{
		var material = Material.Create( name, "shaders/titanfall2_distortion.shader" );
		InitializeSpecialMaterial( material, metadata );
		material.Set( "g_flT2MaterialOpacity", metadata.IsTranslucent ? 1f : 0.35f );
		material.Set( "g_vT2UvDistortion", new Vector4( 0.02f, 0.02f, 0f, 0f ) );
		return material;
	}

	static void InitializeSpecialMaterial( Material material, Titanfall2MaterialMetadata metadata )
	{
		material.Set( "g_tAlbedo", Texture.White );
		material.Set( "g_tNormal", Texture.White );
		material.Set( "g_tGloss", Texture.Black );
		material.Set( "g_tSpecular", Texture.Black );
		material.Set( "g_tAO", Texture.White );
		material.Set( "g_tOpacity", Texture.White );
		material.Set( "g_tEmissive", Texture.Black );
		material.Set( "g_tDetail", Texture.White );
		material.Set( "g_tDistortion", Texture.White );
		BindNeutralEnvironment( material );
		SetExtendedDefaults( material );
		material.Set( "g_flUseVertexColor", metadata.UsesVertexColor ? 1f : 0f );
		material.Set( "g_flUseVertexAlpha", metadata.UsesVertexAlpha ? 1f : 0f );
	}

	/// <summary>
	/// Reads Titanfall 2's native D3D rasterizer/blend state. RF_PRESET_DECAL is
	/// more reliable than asset naming and also catches decals embedded in models.
	/// </summary>
	internal static ulong GetShaderSetGuid( byte[] header )
	{
		return header is not null && header.Length >= ShaderSetGuidOffset + sizeof( ulong )
			? BinaryPrimitives.ReadUInt64LittleEndian( header.AsSpan( ShaderSetGuidOffset, sizeof( ulong ) ) )
			: 0;
	}

	internal static Titanfall2MaterialMetadata ReadMetadata( byte[] header, string sourceMaterialName, string shaderSetName = null )
	{
		if ( header is null || header.Length < MaterialHeaderSize ) return InferMetadata( sourceMaterialName );
		var rasterizer0 = BinaryPrimitives.ReadUInt16LittleEndian( header.AsSpan( DxState0RasterizerOffset, 2 ) );
		var rasterizer1 = BinaryPrimitives.ReadUInt16LittleEndian( header.AsSpan( DxState1RasterizerOffset, 2 ) );
		var blend0 = BinaryPrimitives.ReadUInt32LittleEndian( header.AsSpan( DxState0BlendOffset, 4 ) );
		var blend1 = BinaryPrimitives.ReadUInt32LittleEndian( header.AsSpan( DxState1BlendOffset, 4 ) );
		var blend = (blend0 & BlendEnable) != 0 ? blend0 : blend1;
		var glueFlags = BinaryPrimitives.ReadUInt32LittleEndian( header.AsSpan( GlueFlagsOffset, 4 ) );
		var isDecal = IsDecalPreset( rasterizer0 ) || IsDecalPreset( rasterizer1 );
		var isCutout = (glueFlags & GlueAlphaTest) != 0
			|| ContainsShaderFeature( shaderSetName, "Cut" );
		var mode = Titanfall2MaterialMode.Opaque;
		if ( (blend & BlendEnable) != 0 )
		{
			var sourceBlend = ((blend >> 2) & BlendFieldMask) + 1;
			var destinationBlend = ((blend >> 7) & BlendFieldMask) + 1;
			mode = destinationBlend == D3dBlendOne
				? Titanfall2MaterialMode.Additive
				: sourceBlend == D3dBlendSourceAlpha
					? Titanfall2MaterialMode.AlphaBlend
					: Titanfall2MaterialMode.Premultiplied;
		}
		else if ( isCutout || isDecal )
		{
			mode = Titanfall2MaterialMode.Cutout;
		}

		return new Titanfall2MaterialMetadata(
			mode,
			isDecal,
			IsCullNone( rasterizer0 ) || IsCullNone( rasterizer1 ),
			ContainsShaderFeature( shaderSetName, "Vcolt" ),
			ContainsShaderFeature( shaderSetName, "Vcola" ),
			ContainsShaderFeature( shaderSetName, "Unlit" ),
			IsWaterMaterial( sourceMaterialName ) );
	}

	internal static Titanfall2MaterialMetadata InferMetadata( string sourceMaterialName )
	{
		if ( string.IsNullOrWhiteSpace( sourceMaterialName ) ) return default;
		var normalized = sourceMaterialName.Replace( '\\', '/' ).Trim( '/' );
		var fileName = System.IO.Path.GetFileNameWithoutExtension( normalized );
		var looksLikeDecal = normalized.Contains( "/decal/", StringComparison.OrdinalIgnoreCase )
			|| normalized.Contains( "/decals/", StringComparison.OrdinalIgnoreCase )
			|| fileName.Contains( "decal", StringComparison.OrdinalIgnoreCase );
		var looksLikeFoliageCard = normalized.Contains( "/foliage/", StringComparison.OrdinalIgnoreCase )
			&& (fileName.Contains( "grass", StringComparison.OrdinalIgnoreCase )
				|| fileName.Contains( "leaf", StringComparison.OrdinalIgnoreCase )
				|| fileName.Contains( "leaves", StringComparison.OrdinalIgnoreCase )
				|| fileName.Contains( "plant", StringComparison.OrdinalIgnoreCase ));
		return new Titanfall2MaterialMetadata(
			looksLikeDecal || looksLikeFoliageCard ? Titanfall2MaterialMode.Cutout : Titanfall2MaterialMode.Opaque,
			looksLikeDecal,
			looksLikeFoliageCard,
			false,
			false,
			false,
			IsWaterMaterial( normalized ) );
	}

	internal static Material CreateDecalMaterial( string name, Titanfall2MaterialMetadata metadata )
	{
		var shader = metadata.DecalMode switch
		{
			Titanfall2DecalMode.Additive => "shaders/titanfall2_decal_additive.shader",
			Titanfall2DecalMode.AlphaBlend => "shaders/titanfall2_decal_alpha.shader",
			Titanfall2DecalMode.Premultiplied => "shaders/titanfall2_decal_translucent.shader",
			_ => "shaders/titanfall2_decal.shader"
		};
		var material = Material.Create( name, shader );
		material.Set( "g_tAlbedo", Texture.Transparent );
		material.Set( "g_tNormal", Texture.White );
		material.Set( "g_tGloss", Texture.Black );
		material.Set( "g_tSpecular", Texture.Black );
		material.Set( "g_tAO", Texture.White );
		material.Set( "g_tOpacity", Texture.White );
		material.Set( "g_tEmissive", Texture.Black );
		material.Set( "g_tDetail", Texture.White );
		material.Set( "g_tDistortion", Texture.White );
		BindNeutralEnvironment( material );
		SetExtendedDefaults( material );
		material.Set( "g_flHasNormalMap", 0f );
		material.Set( "g_flDecalOpacity", 1f );
		return material;
	}

	static void SetExtendedDefaults( Material material )
	{
		material.Set( "g_flT2GlossScale", 1f );
		material.Set( "g_vT2SpecularTint", new Vector4( 1f, 1f, 1f, 1f ) );
		material.Set( "g_flT2EmissiveStrength", 1f );
		material.Set( "g_flT2FresnelStrength", 0f );
		material.Set( "g_flT2DetailBlend", 0f );
		material.Set( "g_flT2NormalScale", 1f );
		material.Set( "g_flT2EnvironmentIntensity", 1f );
		material.Set( "g_flT2HasEnvironment", 0f );
	}

	static void BindNeutralEnvironment( Material material )
	{
		lock ( SharedTextureLock )
		{
			if ( _neutralEnvironment is null || !_neutralEnvironment.IsValid() )
			{
				var data = Enumerable.Repeat( (byte)255, 6 * 4 ).ToArray();
				_neutralEnvironment = Texture.CreateCube( 1, 1, ImageFormat.RGBA8888 )
					.WithMips( 1 ).WithStaticUsage().WithData( data ).Finish();
			}
			if ( _neutralEnvironment is not null && _neutralEnvironment.IsValid() )
				material.Set( "g_tEnvironment", _neutralEnvironment );
		}
	}

	internal static void ShutdownSharedResources()
	{
		lock ( SharedTextureLock )
		{
			_neutralEnvironment?.Dispose();
			_neutralEnvironment = null;
		}
	}

	static bool IsDecalPreset( ushort rasterizerFlags ) => (rasterizerFlags & RasterizerPresetMask) == RasterizerPresetDecal;
	static bool IsCullNone( ushort rasterizerFlags ) => (rasterizerFlags & RasterizerCullMask) == RasterizerCullNone;
	static bool ContainsShaderFeature( string shaderSetName, string feature ) => !string.IsNullOrWhiteSpace( shaderSetName )
		&& shaderSetName.Contains( feature, StringComparison.OrdinalIgnoreCase );

	static bool IsWaterMaterial( string sourceMaterialName )
	{
		if ( string.IsNullOrWhiteSpace( sourceMaterialName ) ) return false;
		var normalized = sourceMaterialName.Replace( '\\', '/' ).Trim( '/' );
		var fileName = System.IO.Path.GetFileNameWithoutExtension( normalized );
		return normalized.StartsWith( "world/water/", StringComparison.OrdinalIgnoreCase )
			|| normalized.StartsWith( "models/water/", StringComparison.OrdinalIgnoreCase )
			|| normalized.Contains( "/water/", StringComparison.OrdinalIgnoreCase )
			|| fileName.Contains( "water", StringComparison.OrdinalIgnoreCase );
	}

	/// <summary>
	/// Vista meshes form Titanfall's 3D skybox. They are self-lit and may contain
	/// BC6H HDR colour, translucent water/clouds, and additive fog/light overlays.
	/// Rendering them with the normal PBR material makes the sky black and turns
	/// opaque black pixels in overlay textures into solid geometry.
	/// </summary>
	internal static Material CreateVistaMaterial( string name, string sourceMaterialName ) =>
		CreateVistaMaterial( name, sourceMaterialName, InferMetadata( sourceMaterialName ) );

	internal static Material CreateVistaMaterial( string name, string sourceMaterialName, Titanfall2MaterialMetadata metadata )
	{
		var mode = GetVistaMaterialMode( sourceMaterialName );
		if ( mode == VistaMaterialMode.None ) mode = VistaMaterialMode.Opaque;
		var shader = metadata.Mode switch
		{
			Titanfall2MaterialMode.Cutout => "shaders/titanfall2_vista_cutout.shader",
			Titanfall2MaterialMode.Additive => "shaders/titanfall2_vista_additive.shader",
			Titanfall2MaterialMode.Premultiplied or Titanfall2MaterialMode.AlphaBlend => "shaders/titanfall2_vista_translucent.shader",
			_ => mode switch
			{
				VistaMaterialMode.Additive => "shaders/titanfall2_vista_additive.shader",
				VistaMaterialMode.Translucent => "shaders/titanfall2_vista_translucent.shader",
				_ => "shaders/titanfall2_vista.shader"
			}
		};
		var material = Material.Create( name, shader );
		material.Set( "g_tAlbedo", Texture.White );
		material.Set( "g_tOpacity", Texture.White );
		material.Set( "g_flAlbedoIsSrgb", 1f );
		material.Set( "g_flUseVertexColor", metadata.UsesVertexColor ? 1f : 0f );
		// Smoke/cloud vista cards are authored with vertex-alpha edge fades even
		// when an older shader-set name does not advertise the Vcola feature.
		material.Set( "g_flUseVertexAlpha",
			metadata.UsesVertexAlpha || IsVistaOpacityMaterial( sourceMaterialName ) ? 1f : 0f );
		if ( IsPlanetSunMaterial( sourceMaterialName ) )
		{
			// The source is BC6H HDR and Titanfall tone-maps it before bloom. Without
			// that stage s&box receives values high enough to fill the view with bloom.
			material.Set( "g_flT2VistaIntensity", 0.18f );
			material.Set( "g_flT2VistaHdrLimit", 1.5f );
		}
		return material;
	}

	internal static bool IsVistaMaterial( string name ) => GetVistaMaterialMode( name ) != VistaMaterialMode.None;

	internal static bool IsVistaOpacityMaterial( string name )
	{
		var mode = GetVistaMaterialMode( name );
		return mode is VistaMaterialMode.Translucent or VistaMaterialMode.Additive;
	}

	static VistaMaterialMode GetVistaMaterialMode( string name )
	{
		if ( string.IsNullOrWhiteSpace( name ) ) return VistaMaterialMode.None;
		var normalized = name.Replace( '\\', '/' );
		if ( !normalized.Contains( "/models/vistas/", StringComparison.OrdinalIgnoreCase )
			&& !normalized.StartsWith( "models/vistas/", StringComparison.OrdinalIgnoreCase ) )
			return VistaMaterialMode.None;

		var fileName = System.IO.Path.GetFileNameWithoutExtension( normalized );
		if ( fileName.Contains( "light", StringComparison.OrdinalIgnoreCase )
			|| fileName.Contains( "fog", StringComparison.OrdinalIgnoreCase )
			|| fileName.Contains( "gradate", StringComparison.OrdinalIgnoreCase )
			|| fileName.Contains( "glow", StringComparison.OrdinalIgnoreCase )
			|| fileName.Contains( "flare", StringComparison.OrdinalIgnoreCase ) )
			return VistaMaterialMode.Additive;

		if ( fileName.Contains( "water", StringComparison.OrdinalIgnoreCase )
			|| fileName.Contains( "cloud", StringComparison.OrdinalIgnoreCase )
			|| fileName.Contains( "smoke", StringComparison.OrdinalIgnoreCase )
			|| fileName.Contains( "mist", StringComparison.OrdinalIgnoreCase )
			// Crash Site and several other Titanfall maps place distant mountain
			// silhouettes on alpha-faded vista cards. Treating these materials as
			// opaque exposes the rectangular card/cylinder geometry around the map.
			|| fileName.Contains( "_mtn", StringComparison.OrdinalIgnoreCase )
			|| fileName.Contains( "mountain", StringComparison.OrdinalIgnoreCase ) )
			return VistaMaterialMode.Translucent;

		return VistaMaterialMode.Opaque;
	}

	/// <summary>
	/// Titanfall's Godray materials combine vertex RGBA, a translucent base texture,
	/// additive blending and a camera-distance fade. They must use a separate shader
	/// and model from opaque world geometry so they neither write depth nor collision.
	/// </summary>
	internal static Material CreateGodrayMaterial( string name, string sourceMaterialName )
	{
		GetGodrayFadeFromName( sourceMaterialName, out var scale, out var bias );
		return CreateGodrayMaterial( name, scale, bias );
	}

	internal static bool IsGodrayMaterial( string name )
	{
		if ( string.IsNullOrWhiteSpace( name ) ) return false;
		var normalized = name.Replace( '\\', '/' ).Trim( '/' );
		var fileName = System.IO.Path.GetFileNameWithoutExtension( normalized );
		return normalized.Contains( "/godray_", StringComparison.OrdinalIgnoreCase )
			|| normalized.StartsWith( "godray_", StringComparison.OrdinalIgnoreCase )
			|| (normalized.StartsWith( "world/atmosphere/", StringComparison.OrdinalIgnoreCase )
				&& fileName.StartsWith( "atmosphere_", StringComparison.OrdinalIgnoreCase )
				&& fileName.Contains( "_fade", StringComparison.OrdinalIgnoreCase ));
	}

	static Material CreateGodrayMaterial( string name, string sourceMaterialName, RpakArchive sourceArchive, RpakAsset sourceAsset )
	{
		if ( !TryReadGodrayFade( sourceArchive, sourceAsset, out var scale, out var bias ) )
			GetGodrayFadeFromName( sourceMaterialName, out scale, out bias );
		return CreateGodrayMaterial( name, scale, bias );
	}

	internal static Material CreateGodrayMaterial( string name, float fadeScale, float fadeBias )
	{
		var material = Material.Create( name, "shaders/titanfall2_godray.shader" );
		material.Set( "g_tAlbedo", Texture.White );
		material.Set( "g_flGodrayFadeScale", fadeScale );
		material.Set( "g_flGodrayFadeBias", fadeBias );
		material.Set( "g_flGodrayDepthFeather", 24f );
		return material;
	}

	static bool TryReadGodrayFade( RpakArchive sourceArchive, RpakAsset sourceAsset, out float scale, out float bias )
	{
		const int FadeScaleOffset = 0xA0;
		const int FadeBiasOffset = 0xA4;
		const int RequiredBytes = FadeBiasOffset + sizeof( float );
		scale = 0f;
		bias = 0f;
		if ( !sourceArchive.TryReadCpuData( sourceAsset, out var bytes, out _ ) || bytes.Length < RequiredBytes ) return false;

		scale = BitConverter.Int32BitsToSingle( BinaryPrimitives.ReadInt32LittleEndian( bytes.AsSpan( FadeScaleOffset, 4 ) ) );
		bias = BitConverter.Int32BitsToSingle( BinaryPrimitives.ReadInt32LittleEndian( bytes.AsSpan( FadeBiasOffset, 4 ) ) );
		if ( !float.IsFinite( scale ) || !float.IsFinite( bias ) || scale <= 0f || bias > 0f ) return false;

		var minimumDistance = -bias / scale;
		var maximumDistance = (1f - bias) / scale;
		return float.IsFinite( minimumDistance ) && float.IsFinite( maximumDistance )
			&& minimumDistance >= 0f && maximumDistance > minimumDistance && maximumDistance <= 100_000f;
	}

	static void GetGodrayFadeFromName( string sourceMaterialName, out float scale, out float bias )
	{
		const float minimumDistance = 30f;
		var maximumDistance = 500f;
		var fileName = System.IO.Path.GetFileNameWithoutExtension( sourceMaterialName ?? string.Empty );
		var match = System.Text.RegularExpressions.Regex.Match(
			fileName,
			@"^(?:godray|atmosphere)_(?<distance>[0-9]+(?:\.[0-9]+)?)_fade(?:_|$)",
			System.Text.RegularExpressions.RegexOptions.IgnoreCase );
		if ( match.Success )
		{
			if ( float.TryParse( match.Groups["distance"].Value, System.Globalization.NumberStyles.Float,
				System.Globalization.CultureInfo.InvariantCulture, out var parsed ) && parsed > minimumDistance )
				maximumDistance = parsed;
		}

		scale = 1f / (maximumDistance - minimumDistance);
		bias = -minimumDistance * scale;
	}

	internal static void ApplyMaterialParameters( Material material, Titanfall2MaterialParameters parameters )
	{
		ApplyUv( material, "1", parameters.Uv1 );
		ApplyUv( material, "2", parameters.Uv2 );
		ApplyUv( material, "3", parameters.Uv3 );
		material.Set( "g_vT2AlbedoTint", new Vector4(
			parameters.AlbedoTintR, parameters.AlbedoTintG, parameters.AlbedoTintB, 1f ) );
		material.Set( "g_vT2EmissiveTint", new Vector4(
			parameters.EmissiveTintR, parameters.EmissiveTintG, parameters.EmissiveTintB, 1f ) );
		material.Set( "g_flT2MaterialOpacity", parameters.Opacity );
		material.Set( "g_flT2AlphaTestReference", parameters.AlphaTestReference );
		material.Set( "g_vT2UvDistortion", new Vector4(
			parameters.DistortionX, parameters.DistortionY, parameters.Distortion2X, parameters.Distortion2Y ) );
		material.Set( "g_flT2GlossScale", parameters.GlossScale );
		material.Set( "g_vT2SpecularTint", new Vector4(
			parameters.SpecularTintR, parameters.SpecularTintG, parameters.SpecularTintB, parameters.MetalnessScale ) );
		material.Set( "g_flT2EmissiveStrength", parameters.EmissiveStrength );
		material.Set( "g_flT2FresnelStrength", parameters.FresnelStrength );
		material.Set( "g_flT2DetailBlend", parameters.DetailBlend );
		material.Set( "g_flT2NormalScale", parameters.NormalScale );
		material.Set( "g_flT2EnvironmentIntensity", parameters.EnvironmentIntensity );
	}

	static void ApplyUv( Material material, string index, Titanfall2UvTransform transform )
	{
		material.Set( $"g_vT2Uv{index}RotScale", new Vector4(
			transform.RowXx, transform.RowXy, transform.RowYx, transform.RowYy ) );
		material.Set( $"g_vT2Uv{index}Translate", new Vector4(
			transform.TranslateX, transform.TranslateY, transform.Animated ? 1f : 0f, 0f ) );
	}

	static bool HasAnimatedUv( string shaderSetName ) => ContainsShaderFeature( shaderSetName, "Uv1at" )
		|| ContainsShaderFeature( shaderSetName, "Uv2at" ) || ContainsShaderFeature( shaderSetName, "Uv3at" );

	static bool IsPlanetSunMaterial( string name )
	{
		if ( string.IsNullOrWhiteSpace( name ) ) return false;
		var normalized = name.Replace( '\\', '/' ).Trim( '/' );
		return normalized.StartsWith( "models/vistas/planet/planet_blue_sun", StringComparison.OrdinalIgnoreCase );
	}

	static RpakPagePointer ReadPagePointer( byte[] bytes, int offset ) => new(
		BinaryPrimitives.ReadInt32LittleEndian( bytes.AsSpan( offset, 4 ) ),
		BinaryPrimitives.ReadInt32LittleEndian( bytes.AsSpan( offset + 4, 4 ) ) );
}
