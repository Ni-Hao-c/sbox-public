namespace Sandbox.Mounting.Helldivers2;

/// <summary>
/// The resource types this mount understands. Helldivers 2 stores only 64-bit type ids, and
/// those ids are MurmurHash64A of the type name, so the constants below are recomputed from the
/// names rather than hard-coded; <see cref="Verify"/> checks them against ids observed in a real
/// install so a hash mismatch can never silently produce an empty mount.
/// </summary>
internal static class Hd2ResourceType
{
	public static readonly Hd2Hash Unit = Hd2Hash.Sum( "unit" );
	public static readonly Hd2Hash Material = Hd2Hash.Sum( "material" );
	public static readonly Hd2Hash Texture = Hd2Hash.Sum( "texture" );
	public static readonly Hd2Hash Animation = Hd2Hash.Sum( "animation" );
	public static readonly Hd2Hash Physics = Hd2Hash.Sum( "physics" );
	public static readonly Hd2Hash Level = Hd2Hash.Sum( "level" );
	public static readonly Hd2Hash StateMachine = Hd2Hash.Sum( "state_machine" );
	public static readonly Hd2Hash Entity = Hd2Hash.Sum( "entity" );
	public static readonly Hd2Hash Strings = Hd2Hash.Sum( "strings" );
	public static readonly Hd2Hash WwiseStream = Hd2Hash.Sum( "wwise_stream" );
	public static readonly Hd2Hash WwiseBank = Hd2Hash.Sum( "wwise_bank" );
	public static readonly Hd2Hash WwiseDep = Hd2Hash.Sum( "wwise_dep" );

	/// <summary>Type ids measured in a real install; used to validate the hash function at runtime.</summary>
	static readonly (string Name, Hd2Hash Hash, ulong Expected)[] Probe =
	[
		( "unit", Unit, 0xE0A48D0BE9A7453F ),
		( "material", Material, 0xEAC0B497876ADEDF ),
		( "texture", Texture, 0xCD4238C6A0C69E32 ),
		( "animation", Animation, 0x931E336D7646CC26 ),
		( "physics", Physics, 0x5F7203C8F280DAB8 ),
		( "level", Level, 0x2A690FD348FE9AC5 ),
		( "state_machine", StateMachine, 0xA486D4045106165C ),
		( "entity", Entity, 0x9831CA893B0D087D ),
		( "strings", Strings, 0x0D972BAB10B40FD3 ),
		( "wwise_stream", WwiseStream, 0x504B55235D21440E ),
		( "wwise_bank", WwiseBank, 0x535A7BD3E650D799 ),
		( "wwise_dep", WwiseDep, 0xAF32095C82F2B070 )
	];

	/// <summary>Set once when the mount loads, so ids outside the probe list still resolve to names.</summary>
	public static Hd2NameDictionary Dictionary { get; set; }

	/// <summary>Names for ids we recognise, so diagnostics read as names instead of hex.</summary>
	public static string NameOf( Hd2Hash type )
	{
		foreach ( var entry in Probe )
			if ( entry.Hash == type ) return entry.Name;

		if ( Dictionary is not null && Dictionary.TryGetName( type, out var known ) ) return known;

		return type.ToString();
	}

	/// <summary>
	/// Returns the number of probe ids that matched their expected value. A result below
	/// <see cref="Probe"/>.Length means the hash function or the stored name spelling is wrong.
	/// </summary>
	public static int Verify( out string failure )
	{
		failure = null;
		var matched = 0;

		foreach ( var (name, hash, expected) in Probe )
		{
			if ( hash.Value == expected )
			{
				matched++;
				continue;
			}

			failure ??= $"type hash mismatch for '{name}': computed 0x{hash}, expected 0x{expected:x16}";
		}

		return matched;
	}

	public static int ProbeCount => Probe.Length;
}
