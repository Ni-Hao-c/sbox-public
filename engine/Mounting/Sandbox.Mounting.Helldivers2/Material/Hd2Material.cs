namespace Sandbox.Mounting.Helldivers2;

/// <summary>
/// A parsed <c>material</c> resource: the fixed header, the texture slot table and the settings
/// table. <see cref="Resolve"/> then fills in what the community name database and the mounted data
/// can tell us about each entry, <see cref="Inherit"/> merges a base material's tables in, and the
/// counters below are what the diagnostics report afterwards.
/// </summary>
internal sealed class Hd2Material
{
	public Hd2MaterialHeader Header { get; }

	public Hd2MaterialSlot[] Textures { get; private set; }

	public Hd2MaterialSetting[] Settings { get; private set; }

	/// <summary>Size of the main payload this material was parsed from.</summary>
	public int MainLength { get; }

	/// <summary>False until <see cref="Resolve"/> has looked at the mounted data.</summary>
	public bool Resolved { get; private set; }

	/// <summary>The base material's recovered name, or null while it stays opaque.</summary>
	public string BaseMaterialName { get; private set; }

	/// <summary>How many base materials were merged in, nearest first.</summary>
	public int BaseChainLength { get; internal set; }

	public Hd2Material( Hd2MaterialHeader header, Hd2MaterialSlot[] textures, Hd2MaterialSetting[] settings, int mainLength )
	{
		Header = header;
		Textures = textures;
		Settings = settings;
		MainLength = mainLength;
	}

	/// <summary>Slots whose thin hash key the name database resolved to a slot name.</summary>
	public int NamedSlots => Textures.Count( static slot => slot.UsageName is not null );

	/// <summary>Slots that feed at least one engine material parameter.</summary>
	public int MappedSlots => Textures.Count( static slot => slot.IsMapped );

	/// <summary>Slots whose texture hash names a <c>texture</c> resource present in the mounted data.</summary>
	public int ExistingTextures => Textures.Count( static slot => slot.TextureExists );

	/// <summary>Slots that could be built but have nowhere to bind: no slot name, so no parameter.</summary>
	public int ExistingUnmappedSlots => Textures.Count( static slot => slot.TextureExists && !slot.IsMapped );

	/// <summary>Named slots that belong to a family the engine shader has no parameter for.</summary>
	public int UnsupportedSlots => Textures.Count( static slot => slot.Unsupported );

	/// <summary>Slots merged in from a base material.</summary>
	public int InheritedSlots => Textures.Count( static slot => slot.Inherited );

	/// <summary>True when an albedo binding came from the colour fallback rather than a slot name.</summary>
	public bool GuessedColor => Textures.Any( static slot => slot.IsGuess );

	public int NamedSettings => Settings.Count( static setting => setting.UsageName is not null );

	public int MappedSettings => Settings.Count( static setting => setting.Parameter is not null );

	public int UnsupportedSettings => Settings.Count( static setting => setting.Unsupported );

	public int TruncatedSettings => Settings.Count( static setting => setting.Truncated );

	/// <summary>The slots the reader would bind, in table order.</summary>
	public IEnumerable<Hd2MaterialSlot> BindableSlots => Textures.Where( static slot => slot.IsMapped && slot.TextureExists );

	/// <summary>True when some slot feeds the parameter, whether by name or by the fallback rule.</summary>
	public bool HasParameter( string parameter ) => Textures.Any( slot => slot.HasParameter( parameter ) );

	/// <summary>True when a resolved setting drives the parameter.</summary>
	public bool HasMappedSetting( string parameter ) => Settings.Any( setting => setting.Parameter == parameter );

	/// <summary>
	/// Resolves slot and setting names through the name database, maps them onto engine parameters
	/// and checks which of the named textures are actually present. Passing a null dictionary or
	/// directory is allowed and simply leaves those fields unanswered.
	/// </summary>
	public void Resolve( Hd2DataDirectory directory, Hd2NameDictionary dictionary )
	{
		var parameters = new List<string>( 2 );

		foreach ( var slot in Textures )
		{
			if ( dictionary is not null && dictionary.TryGetName( slot.Usage, out var usageName ) ) slot.UsageName = usageName;

			if ( slot.UsageName is not null )
			{
				// Emissive and friends are checked first: a name such as "emissive_color" would
				// otherwise be read as an albedo slot and shine the wrong map into the colour channel.
				if ( Hd2MaterialFamilies.IsUnsupported( slot.UsageName ) )
				{
					slot.Unsupported = true;
				}
				else
				{
					parameters.Clear();
					Hd2MaterialFamilies.MatchTextureParameters( slot.UsageName, parameters );
					if ( parameters.Count > 0 ) slot.Parameters = parameters.ToArray();
				}
			}

			slot.TextureExists = directory is not null && directory.TryGetResource( slot.Texture, Hd2ResourceType.Texture, out _ );
		}

		foreach ( var setting in Settings )
		{
			if ( dictionary is not null && dictionary.TryGetName( setting.Usage, out var usageName ) ) setting.UsageName = usageName;

			if ( setting.UsageName is null ) continue;

			if ( !Hd2MaterialFamilies.TryMatchSettingParameter( setting.UsageName, out var parameter ) )
				setting.Unsupported = Hd2MaterialFamilies.IsUnsupported( setting.UsageName );
			else
				setting.Parameter = parameter;
		}

		if ( dictionary is not null && dictionary.TryGetName( Header.BaseMaterial, out var baseName ) ) BaseMaterialName = baseName;

		Resolved = true;
	}

	/// <summary>
	/// Merges a base material's tables into this one, nearest base first. The child always wins: an
	/// entry whose usage hash is already present is left alone, and everything merged in is marked as
	/// inherited so the diagnostics can tell the two apart.
	/// </summary>
	public void Inherit( Hd2Material parent )
	{
		if ( parent is null ) return;

		var textures = new List<Hd2MaterialSlot>( Textures );
		foreach ( var slot in parent.Textures )
		{
			if ( textures.Any( existing => existing.Usage == slot.Usage ) ) continue;

			textures.Add( new Hd2MaterialSlot( slot.Usage, slot.Texture )
			{
				UsageName = slot.UsageName,
				Parameters = slot.Parameters,
				TextureExists = slot.TextureExists,
				IsGuess = slot.IsGuess,
				Unsupported = slot.Unsupported,
				Inherited = true
			} );
		}

		Textures = textures.ToArray();

		var settings = new List<Hd2MaterialSetting>( Settings );
		foreach ( var setting in parent.Settings )
		{
			if ( settings.Any( existing => existing.Usage == setting.Usage ) ) continue;

			settings.Add( new Hd2MaterialSetting( setting.Type, setting.Count, setting.Usage, setting.Values )
			{
				UsageName = setting.UsageName,
				Parameter = setting.Parameter,
				Unsupported = setting.Unsupported,
				Truncated = setting.Truncated,
				Inherited = true
			} );
		}

		Settings = settings.ToArray();
	}

	/// <summary>
	/// Guarantees an albedo binding. When no slot feeds the colour parameter - which is the common
	/// case, because a material's other slot names often resolve while its albedo slot name does not -
	/// the first slot that carries a texture the mounted data actually has is bound to it and flagged
	/// as a guess. The flag is what keeps this visibly different from a recognised "albedo" slot.
	/// </summary>
	public bool ApplyColorFallback()
	{
		if ( HasParameter( Hd2MaterialFamilies.ColorParameter ) ) return false;

		foreach ( var slot in Textures )
		{
			if ( !slot.TextureExists || slot.IsMapped || slot.Unsupported ) continue;

			slot.Parameters = [Hd2MaterialFamilies.ColorParameter];
			slot.IsGuess = true;
			return true;
		}

		return false;
	}

	public override string ToString() => $"{Header.BaseMaterialText}, {Textures.Length} texture slot(s), {Settings.Length} setting(s)";
}
