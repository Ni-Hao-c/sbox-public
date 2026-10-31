namespace Sandbox.Mounting.Helldivers2;

/// <summary>The shape of the float blob a material setting points at.</summary>
internal enum Hd2MaterialSettingType : uint
{
	Scalar = 0,
	Vector2 = 1,
	Vector3 = 2,
	Vector4 = 3,

	/// <summary>A count/stride sized blob whose meaning is not known; filediver leaves it raw too.</summary>
	Other = 12,

	Unknown = uint.MaxValue
}

/// <summary>
/// One entry of a material's settings table: a 20-byte definition (type, count, usage, offset,
/// stride) plus the float blob it points at. The usage is a thin hash, so the setting's name is
/// only known when the community database resolves it.
/// </summary>
internal sealed class Hd2MaterialSetting
{
	public Hd2MaterialSettingType Type { get; }

	/// <summary>Repeat count from the definition; only the <see cref="Hd2MaterialSettingType.Other"/> form reads it.</summary>
	public uint Count { get; }

	public Hd2ThinHash Usage { get; }

	/// <summary>The floats the definition points at; empty for unknown types and truncated blobs.</summary>
	public float[] Values { get; }

	/// <summary>The setting name recovered from the name database, or null while it stays opaque.</summary>
	public string UsageName { get; internal set; }

	/// <summary>The engine material parameter this setting maps onto, or null when nothing matched.</summary>
	public string Parameter { get; internal set; }

	/// <summary>True when the name reads as a family the engine shader has no parameter for, such as emissive.</summary>
	public bool Unsupported { get; internal set; }

	/// <summary>True when the setting was merged in from a base material rather than this one's own table.</summary>
	public bool Inherited { get; internal set; }

	/// <summary>True when the definition pointed outside the payload, so no values could be read.</summary>
	public bool Truncated { get; internal set; }

	public Hd2MaterialSetting( Hd2MaterialSettingType type, uint count, Hd2ThinHash usage, float[] values )
	{
		Type = type;
		Count = count;
		Usage = usage;
		Values = values;
	}

	/// <summary>The first value, or zero when the blob holds none.</summary>
	public float Scalar => Values.Length > 0 ? Values[0] : 0f;

	/// <summary>The recovered setting name, or the thin hash in hex while it stays opaque.</summary>
	public string Name => UsageName ?? Usage.ToString();

	public override string ToString() => $"{Name} ({Type}, {Values.Length} value(s))"
		+ ( Truncated ? " truncated" : "" )
		+ ( Parameter is null ? "" : $" as {Parameter}" )
		+ ( Inherited ? " (inherited)" : "" )
		+ ( Unsupported ? " (unsupported)" : "" );
}
