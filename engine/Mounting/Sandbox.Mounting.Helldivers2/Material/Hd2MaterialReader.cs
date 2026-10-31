namespace Sandbox.Mounting.Helldivers2;

/// <summary>
/// Reads Stingray <c>material</c> resources and turns them into runtime materials.
///
/// The main payload is a fixed 136-byte header, a texture slot table of thin-hash keys followed by
/// the full hashes of the <c>texture</c> resources they name, and a settings table of 20-byte
/// definitions followed by the float blobs they point at. The GPU stream holds compiled DXBC
/// shaders, which are deliberately ignored: the game's shaders cannot be translated, so materials
/// are built on the engine's own <c>shaders/complex.shader</c> and the game's textures are bound
/// onto it.
/// </summary>
internal static class Hd2MaterialReader
{
	/// <summary>Points a setting blob may not exceed, so a corrupt definition cannot ask for a huge allocation.</summary>
	const int MaxSettingValues = 1024;

	/// <summary>How far up the base material chain to walk before deciding it is malformed.</summary>
	const int MaxBaseChain = 8;

	/// <summary>
	/// Parses a material's main payload. Pure and self-contained: nameless slots stay nameless and
	/// no texture is looked up until <see cref="Hd2Material.Resolve"/> runs.
	/// </summary>
	public static bool TryRead( ReadOnlySpan<byte> main, out Hd2Material material, out string error )
	{
		material = null;
		error = null;

		if ( !Hd2MaterialHeader.TryParse( main, out var header, out error ) ) return false;

		var slotCount = (int)header.NumTextures;
		var settingCount = (int)header.NumSettings;
		var offset = Hd2MaterialHeader.StoredSize;

		var keys = new Hd2ThinHash[slotCount];
		for ( var i = 0; i < slotCount; i++ )
		{
			keys[i] = new Hd2ThinHash( ReadU32( main, offset ) );
			offset += 4;
		}

		var slots = new Hd2MaterialSlot[slotCount];
		for ( var i = 0; i < slotCount; i++ )
		{
			slots[i] = new Hd2MaterialSlot( keys[i], new Hd2Hash( ReadU64( main, offset ) ) );
			offset += 8;
		}

		var blobStart = offset;
		var settings = new Hd2MaterialSetting[settingCount];
		for ( var i = 0; i < settingCount; i++ )
		{
			var definition = offset;
			var type = ToSettingType( ReadU32( main, definition ) );
			var count = ReadU32( main, definition + 4 );
			var usage = new Hd2ThinHash( ReadU32( main, definition + 8 ) );
			var blobOffset = ReadU32( main, definition + 12 );
			var stride = ReadU32( main, definition + 16 );
			offset += 20;

			settings[i] = new Hd2MaterialSetting( type, count, usage, ReadValues( main, blobStart, blobOffset, type, count, stride, out var truncated ) )
			{
				Truncated = truncated
			};
		}

		material = new Hd2Material( header, slots, settings, main.Length );
		return true;
	}

	/// <summary>
	/// Parses a material and resolves it: slot names, parameters, texture lookups, the base material
	/// chain and, if everything else failed to give it an albedo, the colour fallback.
	/// </summary>
	public static bool TryRead( Hd2DataDirectory directory, ReadOnlySpan<byte> main, out Hd2Material material, out string error )
	{
		if ( !TryRead( main, out material, out error ) ) return false;

		Resolve( directory, material, 0 );
		return true;
	}

	/// <summary>Reads a material resource's main stream and resolves it against the mounted data.</summary>
	public static bool TryRead( Hd2DataDirectory directory, in Hd2Resource resource, out Hd2Material material, out string error )
	{
		material = null;

		if ( directory is null )
		{
			error = "no Helldivers 2 data directory is open";
			return false;
		}

		if ( !directory.TryRead( resource, Hd2DataType.Main, out var main ) )
		{
			error = "the material's main stream could not be read";
			return false;
		}

		return TryRead( directory, main, out material, out error );
	}

	/// <summary>
	/// Builds a runtime material from the game's material resource, with the game's own textures
	/// bound onto the engine's complex shader.
	/// </summary>
	public static bool TryCreate( Hd2DataDirectory directory, in Hd2Resource resource, out Material material, out string error )
		=> Hd2MaterialBinder.TryBind( directory, resource, out material, out error );

	/// <summary>
	/// One line describing what a material holds: the base material, how many of its texture slots
	/// recovered a name, mapped onto a parameter, named an existing texture, could be built but have
	/// nowhere to bind, came from a base material or came from the colour fallback, plus its settings.
	/// </summary>
	public static string Describe( Hd2Material material )
	{
		if ( material is null ) return "material: none";

		var line = $"material base {material.Header.BaseMaterialText}"
			+ ( material.BaseMaterialName is null ? "" : $" ({material.BaseMaterialName})" )
			+ $", {material.Textures.Length} texture slot(s) [{material.NamedSlots} named, {material.MappedSlots} mapped, "
			+ $"{material.ExistingTextures} name an existing texture, {material.ExistingUnmappedSlots} existing but unmapped"
			+ ( material.GuessedColor ? ", colour guessed" : "" )
			+ ( material.InheritedSlots > 0 ? $", {material.InheritedSlots} inherited" : "" )
			+ ( material.UnsupportedSlots > 0 ? $", {material.UnsupportedSlots} unsupported" : "" )
			+ $"], {material.Settings.Length} setting(s) [{material.NamedSettings} named, {material.MappedSettings} mapped"
			+ ( material.UnsupportedSettings > 0 ? $", {material.UnsupportedSettings} unsupported" : "" )
			+ ( material.TruncatedSettings > 0 ? $", {material.TruncatedSettings} truncated" : "" )
			+ $"]"
			+ ( material.BaseChainLength > 0 ? $", base chain {material.BaseChainLength}" : "" )
			+ $", main {material.MainLength:n0} bytes, header sizes main/gpu {material.Header.SectionMainSize:n0}/{material.Header.SectionGpuSize:n0}"
			+ ( material.Header.HasExpectedPrefix ? "" : $", prefix 0x{material.Header.Prefix:x} (not the 0x18 filediver records)" );

		return material.Resolved ? line : $"{line} (names and textures not resolved)";
	}

	/// <summary>Reads a material resource and describes it, for diagnostics.</summary>
	public static string Describe( Hd2DataDirectory directory, in Hd2Resource resource )
	{
		var label = $"material {resource.Name}";

		if ( directory is null ) return $"{label}: no Helldivers 2 data directory is open";
		if ( !TryRead( directory, resource, out var material, out var error ) ) return $"{label}: {error}";

		return $"{label}: {Describe( material )}";
	}

	/// <summary>The summary line plus the first <paramref name="limit"/> slots and settings, one per line.</summary>
	public static string DescribeSlots( Hd2Material material, int limit )
	{
		if ( material is null ) return "material: none";

		var text = new System.Text.StringBuilder();
		text.Append( Describe( material ) );

		foreach ( var slot in material.Textures.Take( limit ) ) text.Append( $"\n  slot {slot}" );
		foreach ( var setting in material.Settings.Take( limit ) ) text.Append( $"\n  setting {setting}" );

		if ( material.Textures.Length > limit || material.Settings.Length > limit )
			text.Append( $"\n  ... ({material.Textures.Length} texture slot(s), {material.Settings.Length} setting(s) in total)" );

		return text.ToString();
	}

	/// <summary>
	/// Measures the first <paramref name="count"/> materials against the mounted data: how many
	/// parse, how many slot names the community database recovers, how many map onto an engine
	/// parameter, how many name a <c>texture</c> resource that is actually present, and how many of
	/// those can be built but have nowhere to bind. The slot table is community data, so this is a
	/// coverage report rather than a pass/fail check.
	/// </summary>
	public static string Measure( Hd2DataDirectory directory, int count )
	{
		if ( directory is null ) return "materials: no Helldivers 2 data directory is open";
		if ( !directory.TryGetTypeIndices( Hd2ResourceType.Material, out var indices ) || indices.Count == 0 ) return "materials: none were indexed";

		var attempted = 0;
		var parsed = 0;
		var slots = 0;
		var namedSlots = 0;
		var mappedSlots = 0;
		var existingTextures = 0;
		var existingUnmapped = 0;
		var inheritedSlots = 0;
		var baseChained = 0;
		var baseChainLength = 0;
		var guessed = 0;
		var settings = 0;
		var mappedSettings = 0;
		var unsupportedSettings = 0;
		var namedUnmappedSlots = 0;
		var unsupportedSlots = 0;
		var byParameter = new Dictionary<string, int>( StringComparer.OrdinalIgnoreCase );
		var bySetting = new Dictionary<string, int>( StringComparer.OrdinalIgnoreCase );
		var unmappedNames = new List<string>();
		var namedSettings = new List<string>();
		var samples = new List<string>();
		var failures = new List<string>();
		var watch = System.Diagnostics.Stopwatch.StartNew();

		foreach ( var index in indices )
		{
			if ( attempted >= count ) break;

			var resource = directory.GetResource( index );
			attempted++;

			if ( !TryRead( directory, resource, out var material, out var error ) )
			{
				if ( failures.Count < 4 ) failures.Add( $"{resource.Name}: {error}" );
				continue;
			}

			parsed++;
			slots += material.Textures.Length;
			namedSlots += material.NamedSlots;
			mappedSlots += material.MappedSlots;
			existingTextures += material.ExistingTextures;
			existingUnmapped += material.ExistingUnmappedSlots;
			inheritedSlots += material.InheritedSlots;
			settings += material.Settings.Length;
			mappedSettings += material.MappedSettings;
			unsupportedSettings += material.UnsupportedSettings;
			if ( material.GuessedColor ) guessed++;
			if ( material.BaseChainLength > 0 )
			{
				baseChained++;
				baseChainLength += material.BaseChainLength;
			}

			foreach ( var slot in material.Textures )
			{
				if ( slot.IsMapped )
				{
					foreach ( var parameter in slot.Parameters )
						byParameter[parameter] = byParameter.GetValueOrDefault( parameter ) + 1;

					continue;
				}

				if ( slot.Unsupported )
				{
					unsupportedSlots++;
					continue;
				}

				if ( slot.UsageName is null ) continue;

				namedUnmappedSlots++;
				if ( unmappedNames.Count < 8 && !unmappedNames.Contains( slot.UsageName ) ) unmappedNames.Add( slot.UsageName );
			}

			foreach ( var setting in material.Settings )
			{
				if ( setting.Parameter is not null ) bySetting[setting.Parameter] = bySetting.GetValueOrDefault( setting.Parameter ) + 1;
				if ( setting.UsageName is null ) continue;
				if ( namedSettings.Count < 8 && !namedSettings.Contains( setting.UsageName ) ) namedSettings.Add( setting.UsageName );
			}

			if ( samples.Count < 3 ) samples.Add( DescribeSlots( material, 6 ).Replace( "\n", "\n    " ) );
		}

		watch.Stop();

		var text = new System.Text.StringBuilder();
		text.Append( $"materials: parsed {parsed}/{attempted} in {watch.Elapsed.TotalMilliseconds:0.0} ms; "
			+ $"{slots} texture slot(s) ({namedSlots} named, {mappedSlots} mapped, {existingTextures} name an existing texture, "
			+ $"{existingUnmapped} existing but unmapped, {inheritedSlots} inherited, {guessed} material(s) fell back to a guessed colour); "
			+ $"{settings} setting(s) ({mappedSettings} mapped, {unsupportedSettings} unsupported); "
			+ $"{baseChained} material(s) inherit from a base ({baseChainLength} base link(s))" );

		if ( byParameter.Count > 0 )
		{
			var breakdown = byParameter.OrderByDescending( static pair => pair.Value ).Select( static pair => $"{pair.Key}={pair.Value}" );
			text.Append( $"\n  slot mapping: {string.Join( ", ", breakdown )}; named but unmapped {namedUnmappedSlots}; unsupported family {unsupportedSlots}" );
		}

		if ( bySetting.Count > 0 )
		{
			var breakdown = bySetting.OrderByDescending( static pair => pair.Value ).Select( static pair => $"{pair.Key}={pair.Value}" );
			text.Append( $"\n  setting mapping: {string.Join( ", ", breakdown )}" );
		}

		if ( unmappedNames.Count > 0 ) text.Append( $"\n  named but unmapped slots: {string.Join( ", ", unmappedNames )}" );
		if ( namedSettings.Count > 0 ) text.Append( $"\n  named settings: {string.Join( ", ", namedSettings )}" );

		foreach ( var sample in samples ) text.Append( $"\n  sample {sample}" );
		foreach ( var failure in failures ) text.Append( $"\n  failure {failure}" );

		return text.ToString();
	}

	/// <summary>
	/// Resolves one parsed material, then walks its base material chain and merges what it finds so
	/// the caller sees the effective slot and settings tables. <paramref name="rootKey"/> is the
	/// hash of the material being loaded, when the caller knows it, and is what stops a material that
	/// bases itself on itself.
	/// </summary>
	static void Resolve( Hd2DataDirectory directory, Hd2Material material, ulong rootKey )
	{
		material.Resolve( directory, Hd2ResourceType.Dictionary );

		if ( directory is not null )
		{
			var seen = new HashSet<ulong> { rootKey };
			var current = material;

			for ( var depth = 0; depth < MaxBaseChain; depth++ )
			{
				if ( !current.Header.HasBaseMaterial ) break;

				if ( !directory.TryGetResource( current.Header.BaseMaterial, Hd2ResourceType.Material, out var parentResource ) )
				{
					Hd2Log.Trace( $"Helldivers 2 material base 0x{current.Header.BaseMaterial} is not present in the mounted data." );
					break;
				}

				if ( !seen.Add( parentResource.Name.Value ) )
				{
					Hd2Log.Trace( $"Helldivers 2 material base chain reached 0x{parentResource.Name} twice; stopping." );
					break;
				}

				if ( !directory.TryRead( parentResource, Hd2DataType.Main, out var parentMain ) ) break;
				if ( !TryRead( parentMain, out var parent, out var parentError ) )
				{
					Hd2Log.Trace( $"Helldivers 2 base material 0x{parentResource.Name} could not be parsed: {parentError}" );
					break;
				}

				parent.Resolve( directory, Hd2ResourceType.Dictionary );
				material.Inherit( parent );
				material.BaseChainLength++;
				current = parent;
			}
		}

		material.ApplyColorFallback();
	}

	static float[] ReadValues( ReadOnlySpan<byte> main, int blobStart, uint blobOffset, Hd2MaterialSettingType type, uint count, uint stride, out bool truncated )
	{
		truncated = false;

		var values = type switch
		{
			Hd2MaterialSettingType.Scalar => 1,
			Hd2MaterialSettingType.Vector2 => 2,
			Hd2MaterialSettingType.Vector3 => 3,
			Hd2MaterialSettingType.Vector4 => 4,
			Hd2MaterialSettingType.Other => (int)Math.Clamp( (long)count * stride / 4, 0, MaxSettingValues ),
			_ => 0
		};

		if ( values <= 0 ) return [];

		var start = (long)blobStart + blobOffset;
		if ( start < 0 || start > main.Length || main.Length - start < (long)values * 4 )
		{
			truncated = true;
			return [];
		}

		var result = new float[values];
		for ( var i = 0; i < values; i++ )
			result[i] = System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian( main.Slice( (int)start + i * 4, 4 ) );

		return result;
	}

	static Hd2MaterialSettingType ToSettingType( uint value ) => value switch
	{
		0 => Hd2MaterialSettingType.Scalar,
		1 => Hd2MaterialSettingType.Vector2,
		2 => Hd2MaterialSettingType.Vector3,
		3 => Hd2MaterialSettingType.Vector4,
		12 => Hd2MaterialSettingType.Other,
		_ => Hd2MaterialSettingType.Unknown
	};

	static uint ReadU32( ReadOnlySpan<byte> data, int offset ) => System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian( data[offset..] );

	static ulong ReadU64( ReadOnlySpan<byte> data, int offset ) => System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian( data[offset..] );
}
