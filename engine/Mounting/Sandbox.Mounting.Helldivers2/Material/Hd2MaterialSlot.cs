namespace Sandbox.Mounting.Helldivers2;

/// <summary>
/// One entry of a material's texture slot table: the slot's thin hash key, the full hash of the
/// <c>texture</c> resource it names, and everything the reader managed to resolve about it.
/// </summary>
internal sealed class Hd2MaterialSlot
{
	/// <summary>The slot's identity inside the material; a thin hash, so its name is community data.</summary>
	public Hd2ThinHash Usage { get; }

	/// <summary>The name hash of the <c>texture</c> resource this slot points at.</summary>
	public Hd2Hash Texture { get; }

	/// <summary>The slot name recovered from the name database, or null while it stays opaque.</summary>
	public string UsageName { get; internal set; }

	/// <summary>
	/// Every engine material parameter this slot feeds. Channel packed names map to more than one -
	/// "normal_ao_roughness" is a normal map, a roughness map and an ambient occlusion map at the same
	/// time - so a slot is not limited to a single parameter.
	/// </summary>
	public string[] Parameters { get; internal set; } = [];

	/// <summary>True when the named texture resource is actually present in the mounted data.</summary>
	public bool TextureExists { get; internal set; }

	/// <summary>True when a binding was a fallback rather than a recognised slot name.</summary>
	public bool IsGuess { get; internal set; }

	/// <summary>True when the name reads as a family the engine shader has no slot for, such as emissive.</summary>
	public bool Unsupported { get; internal set; }

	/// <summary>True when the slot was merged in from a base material rather than this one's own table.</summary>
	public bool Inherited { get; internal set; }

	public Hd2MaterialSlot( Hd2ThinHash usage, Hd2Hash texture )
	{
		Usage = usage;
		Texture = texture;
	}

	/// <summary>The first mapped parameter, for single-parameter diagnostics.</summary>
	public string Parameter => Parameters.Length > 0 ? Parameters[0] : null;

	/// <summary>True when at least one family matched, so the slot has somewhere to bind.</summary>
	public bool IsMapped => Parameters.Length > 0;

	public bool HasParameter( string parameter ) => Array.IndexOf( Parameters, parameter ) >= 0;

	/// <summary>The recovered slot name, or the thin hash in hex while it stays opaque.</summary>
	public string Name => UsageName ?? Usage.ToString();

	public override string ToString() => $"{Name} -> 0x{Texture}"
		+ ( TextureExists ? "" : " (missing)" )
		+ ( IsMapped ? $" as {string.Join( "+", Parameters )}" : "" )
		+ ( IsGuess ? " (guess)" : "" )
		+ ( Inherited ? " (inherited)" : "" )
		+ ( Unsupported ? " (unsupported)" : "" );
}
