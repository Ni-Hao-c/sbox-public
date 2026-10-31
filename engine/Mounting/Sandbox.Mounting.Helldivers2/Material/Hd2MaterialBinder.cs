namespace Sandbox.Mounting.Helldivers2;

/// <summary>
/// Builds runtime <see cref="Material"/>s from parsed Helldivers 2 materials and binds the game's
/// own textures onto the engine's complex shader.
///
/// Two static pools keep everything that is created alive: a runtime material or texture with no
/// managed strong reference can be collected while native geometry still points at it, which renders
/// as the engine's red error material. The pools also keep two units that reference the same game
/// material, or two materials that share a texture, from building it twice.
///
/// No texture parameter is ever left unset. An unbound sampler is what draws the magenta and black
/// checkerboard, and plenty of shipped materials carry no albedo slot at all, so every unbound
/// parameter is filled with a neutral 1x1 texture and the albedo tint is neutralised when nothing
/// else drives it.
/// </summary>
internal static class Hd2MaterialBinder
{
	/// <summary>The engine shader every Helldivers 2 material is translated onto.</summary>
	public const string ShaderPath = "shaders/complex.shader";

	static readonly Dictionary<ulong, Material> Materials = [];
	static readonly Dictionary<ulong, Texture> Textures = [];

	static Texture _neutralColor;
	static Texture _neutralNormal;
	static Texture _neutralRoughness;
	static Texture _neutralOcclusion;
	static Texture _neutralMetalness;

	/// <summary>How many distinct materials have been built and are still referenced.</summary>
	public static int MaterialCount => Materials.Count;

	/// <summary>How many distinct textures materials have bound and are still referenced.</summary>
	public static int TextureCount => Textures.Count;

	/// <summary>
	/// Builds (or reuses) the runtime material for a material resource: settings first, then every
	/// slot that mapped onto a shader parameter and resolves to a texture that exists, then neutral
	/// fills for whatever is still unbound.
	/// </summary>
	public static bool TryBind( Hd2DataDirectory directory, in Hd2Resource resource, out Material material, out string error )
	{
		material = null;
		error = null;

		if ( directory is null )
		{
			error = "no Helldivers 2 data directory is open";
			return false;
		}

		if ( Materials.TryGetValue( resource.Name.Value, out var cached ) && cached is not null && cached.IsValid() && !cached.IsError )
		{
			material = cached;
			return true;
		}

		if ( !Hd2MaterialReader.TryRead( directory, resource, out var parsed, out error ) ) return false;

		try
		{
			material = Material.Create( NameOf( resource ), ShaderPath );
		}
		catch ( Exception exception )
		{
			error = $"the engine could not create a material from '{ShaderPath}': {exception.Message}";
			material = null;
			return false;
		}

		if ( material is null || !material.IsValid() || material.IsError )
		{
			error = $"the engine returned no usable material for '{ShaderPath}'";
			material = null;
			return false;
		}

		var boundSettings = 0;
		var boundTextures = 0;
		var neutralFills = 0;

		try
		{
			boundSettings = BindSettings( parsed, material );
			boundTextures = BindTextures( directory, parsed, material );
			neutralFills = FillUnboundParameters( material );
			ApplyNeutralValues( parsed, material );
		}
		catch ( Exception exception )
		{
			// A material with partial bindings still renders better than none, so keep it.
			Hd2Log.Warning( exception, $"Binding the Helldivers 2 material {resource.Name} failed part way through." );
		}

		Hd2Log.Trace( $"Helldivers 2 material {resource.Name} built with {boundTextures} texture, {boundSettings} setting and "
			+ $"{neutralFills} neutral binding(s); {Hd2MaterialReader.Describe( parsed )}" );

		Materials[resource.Name.Value] = material;
		return true;
	}

	/// <summary>Drops the pools. Native resources are left to the engine's teardown.</summary>
	public static void Clear()
	{
		Materials.Clear();
		Textures.Clear();
	}

	static int BindSettings( Hd2Material parsed, Material material )
	{
		var bound = 0;

		foreach ( var setting in parsed.Settings )
		{
			if ( setting.Parameter is null || setting.Values.Length == 0 ) continue;

			switch ( setting.Parameter )
			{
				// These are value parameters: the Vector4 overload never rewrites the name, so the
				// plain shader names are safe whatever the package API version is.
				case Hd2MaterialFamilies.MetalnessScalarParameter:
				case Hd2MaterialFamilies.RoughnessScaleParameter:
					if ( material.Set( setting.Parameter, setting.Scalar ) ) bound++;
					break;

				case Hd2MaterialFamilies.ColorTintParameter:
					if ( material.Set( setting.Parameter, ToTint( setting ) ) ) bound++;
					break;
			}
		}

		return bound;
	}

	static int BindTextures( Hd2DataDirectory directory, Hd2Material parsed, Material material )
	{
		var bound = 0;

		foreach ( var slot in parsed.BindableSlots )
		{
			if ( !directory.TryGetResource( slot.Texture, Hd2ResourceType.Texture, out var resource ) ) continue;

			var texture = GetTexture( directory, resource, slot.Texture );
			if ( texture is null ) continue;

			foreach ( var parameter in slot.Parameters )
			{
				// First binding wins, so a packed map only fills a parameter a more specific slot
				// has not already claimed.
				if ( IsBound( material, parameter ) ) continue;

				if ( !material.Set( parameter, texture ) )
				{
					Hd2Log.Warning( $"The engine refused to bind slot '{slot.Name}' to '{parameter}' on a Helldivers 2 material." );
					continue;
				}

				// Material.Set reports only that the call was made, so read the parameter back: an
				// unbound sampler still hands back a 0x0 placeholder.
				if ( !IsBound( material, parameter ) )
				{
					Hd2Log.Warning( $"The complex shader has no usable texture parameter '{parameter}', so slot '{slot.Name}' could not be bound." );
					continue;
				}

				bound++;
			}
		}

		return bound;
	}

	/// <summary>Fills every texture parameter nothing bound with a neutral 1x1 texture.</summary>
	static int FillUnboundParameters( Material material )
	{
		var filled = 0;

		foreach ( var (parameter, texture) in NeutralTextures() )
		{
			if ( IsBound( material, parameter ) ) continue;
			if ( texture is null ) continue;

			material.Set( parameter, texture );

			if ( IsBound( material, parameter ) ) filled++;
			else Hd2Log.Trace( $"The complex shader has no usable texture parameter '{parameter}' to fall back on." );
		}

		return filled;
	}

	/// <summary>
	/// Puts a definite value on the value parameters the settings did not drive, so a texture-less
	/// material still renders as a lit surface rather than as black or magenta.
	/// </summary>
	static void ApplyNeutralValues( Hd2Material parsed, Material material )
	{
		if ( !parsed.HasMappedSetting( Hd2MaterialFamilies.ColorTintParameter ) ) material.Set( Hd2MaterialFamilies.ColorTintParameter, new Vector4( 1f, 1f, 1f, 1f ) );
		if ( !parsed.HasMappedSetting( Hd2MaterialFamilies.MetalnessScalarParameter ) ) material.Set( Hd2MaterialFamilies.MetalnessScalarParameter, 0f );
		if ( !parsed.HasMappedSetting( Hd2MaterialFamilies.RoughnessScaleParameter ) ) material.Set( Hd2MaterialFamilies.RoughnessScaleParameter, 1f );
	}

	/// <summary>
	/// True when the parameter really carries a texture. Material.Set always reports success and an
	/// unbound sampler reads back as a 0x0 placeholder, so this is the only trustworthy signal.
	/// </summary>
	static bool IsBound( Material material, string parameter )
	{
		var texture = material.GetTexture( parameter );

		return texture is not null && texture.Width > 0;
	}

	/// <summary>The neutral texture bound to each texture parameter that ended up unset.</summary>
	static IEnumerable<(string Parameter, Texture Texture)> NeutralTextures()
	{
		yield return ( Hd2MaterialFamilies.ColorParameter, GetNeutralColor() );
		yield return ( Hd2MaterialFamilies.NormalParameter, GetNeutralNormal() );
		yield return ( Hd2MaterialFamilies.RoughnessParameter, GetNeutralRoughness() );
		yield return ( Hd2MaterialFamilies.AmbientOcclusionParameter, GetNeutralOcclusion() );
		yield return ( Hd2MaterialFamilies.MetalnessTextureParameter, GetNeutralMetalness() );
	}

	static Texture GetNeutralColor() => _neutralColor ??= MakeSolid( "hd2_neutral_color", 255, 255, 255, 255 );

	static Texture GetNeutralNormal() => _neutralNormal ??= MakeSolid( "hd2_neutral_normal", 128, 128, 255, 255 );

	static Texture GetNeutralRoughness() => _neutralRoughness ??= MakeSolid( "hd2_neutral_roughness", 181, 181, 181, 255 );

	static Texture GetNeutralOcclusion() => _neutralOcclusion ??= MakeSolid( "hd2_neutral_occlusion", 255, 255, 255, 255 );

	static Texture GetNeutralMetalness() => _neutralMetalness ??= MakeSolid( "hd2_neutral_metalness", 0, 0, 0, 255 );

	static Texture MakeSolid( string name, byte red, byte green, byte blue, byte alpha )
	{
		try
		{
			return Texture.Create( 1, 1, ImageFormat.RGBA8888 )
				.WithName( name )
				.WithData( new byte[] { red, green, blue, alpha } )
				.WithStaticUsage()
				.Finish();
		}
		catch ( Exception exception )
		{
			Hd2Log.Warning( exception, $"Unable to create the neutral Helldivers 2 texture '{name}'." );
			return null;
		}
	}

	static Vector4 ToTint( Hd2MaterialSetting setting )
	{
		var values = setting.Values;
		var red = values.Length > 0 ? values[0] : 1f;
		var green = values.Length > 1 ? values[1] : red;
		var blue = values.Length > 2 ? values[2] : red;

		return new Vector4( red, green, blue, 1f );
	}

	static Texture GetTexture( Hd2DataDirectory directory, in Hd2Resource resource, Hd2Hash name )
	{
		if ( Textures.TryGetValue( name.Value, out var cached ) && cached is not null && cached.IsValid() ) return cached;

		if ( Hd2TextureReader.TryCreate( directory, resource, out var texture, out var error ) )
		{
			Textures[name.Value] = texture;
			return texture;
		}

		// A texture array is refused by the texture reader because the engine's DDS loader builds a
		// plain 2D texture. That loader reads the first array element's mip chain, so handing it the
		// assembled payload here still yields a usable single slice - better than dropping the slot,
		// and the usual case is a two element packed normal map.
		if ( Hd2TextureReader.TryRead( directory, resource, out var info, out _ ) && info.Dds.ArraySize > 1 && !info.Dds.IsCubemap && !info.Dds.IsVolume )
		{
			if ( TryCreateFirstSlice( directory, resource, out texture, out var sliceError ) )
			{
				Textures[name.Value] = texture;
				return texture;
			}

			Hd2Log.Trace( $"Unable to build the first slice of texture array {name} for a material: {sliceError}" );
			return null;
		}

		Hd2Log.Trace( $"Unable to build Helldivers 2 texture {name} for a material: {error}" );
		return null;
	}

	/// <summary>
	/// Builds a single slice out of an array texture by handing the engine's own DDS loader the
	/// stitched payload. It ignores the array size and reads element zero, which is exactly what is
	/// wanted here; this belongs in the texture reader once that file is open for changes.
	/// </summary>
	static bool TryCreateFirstSlice( Hd2DataDirectory directory, in Hd2Resource resource, out Texture texture, out string error )
	{
		texture = null;
		error = null;

		if ( !directory.TryRead( resource, Hd2DataType.Main, out var main ) || main is null )
		{
			error = "the main stream could not be read";
			return false;
		}

		var head = main.Length - Hd2TextureHeader.HeaderSize;
		if ( head <= 0 )
		{
			error = $"the main stream is {main.Length} bytes, too small for the streamable texture header";
			return false;
		}

		var stream = ReadOrEmpty( directory, resource, Hd2DataType.Stream );
		var gpu = ReadOrEmpty( directory, resource, Hd2DataType.Gpu );
		var payload = new byte[head + stream.Length + gpu.Length];

		main.AsSpan( Hd2TextureHeader.HeaderSize ).CopyTo( payload );
		stream.AsSpan().CopyTo( payload.AsSpan( head ) );
		gpu.AsSpan().CopyTo( payload.AsSpan( head + stream.Length ) );

		try
		{
			texture = TextureLoader.FromDds( payload );
		}
		catch ( Exception exception )
		{
			error = exception.Message;
			texture = null;
			return false;
		}

		if ( texture is null || !texture.IsValid() )
		{
			error = "the engine produced no usable texture from the first slice";
			texture = null;
			return false;
		}

		return true;
	}

	static byte[] ReadOrEmpty( Hd2DataDirectory directory, in Hd2Resource resource, Hd2DataType type )
		=> resource.File.Has( type ) && directory.TryRead( resource, type, out var data ) && data is not null ? data : [];

	/// <summary>A debug label for the material: its recovered name when the database knows it.</summary>
	static string NameOf( in Hd2Resource resource )
	{
		var dictionary = Hd2ResourceType.Dictionary;
		if ( dictionary is not null && dictionary.TryGetName( resource.Name, out var name ) ) return name.Replace( '\\', '/' );

		return $"material/{resource.Name}";
	}
}
