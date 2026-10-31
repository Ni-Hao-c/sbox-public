namespace Sandbox.Mounting.Helldivers2;

/// <summary>
/// Stingray's 64-bit resource identity. Helldivers 2 keeps no plaintext resource names in
/// its containers: every resource and every resource type is addressed by one of these
/// (MurmurHash64A, seed 0, over the name as stored by the data compiler).
/// </summary>
public readonly struct Hd2Hash : IEquatable<Hd2Hash>, IComparable<Hd2Hash>
{
	const ulong Mix = 0xc6a4a7935bd1e995UL;
	const int Shift = 47;

	public readonly ulong Value;

	public Hd2Hash( ulong value )
	{
		Value = value;
	}

	/// <summary>Stingray's "thin" hash: the high 32 bits, used for short names inside payloads.</summary>
	public Hd2ThinHash Thin => new Hd2ThinHash( (uint)(Value >> 32) );

	/// <summary>
	/// Hashes a resource or type name. The data compiler hashes the name's UTF-8 bytes, so
	/// short ASCII names like "unit" and long paths like "content/units/foo" both work.
	/// </summary>
	public static Hd2Hash Sum( string text ) => new Hd2Hash( Murmur64A( System.Text.Encoding.UTF8.GetBytes( text ) ) );

	public static Hd2Hash Sum( ReadOnlySpan<byte> bytes ) => new Hd2Hash( Murmur64A( bytes ) );

	public static Hd2Hash Parse( string text )
	{
		var trimmed = text.Trim();
		if ( trimmed.StartsWith( "0x", StringComparison.OrdinalIgnoreCase ) ) trimmed = trimmed[2..];
		return ulong.TryParse( trimmed, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var value )
			? new Hd2Hash( value )
			: default;
	}

	public static bool TryParse( string text, out Hd2Hash hash )
	{
		var trimmed = text.Trim();
		if ( trimmed.StartsWith( "0x", StringComparison.OrdinalIgnoreCase ) ) trimmed = trimmed[2..];
		if ( trimmed.Length is 0 or > 16 )
		{
			hash = default;
			return false;
		}

		if ( ulong.TryParse( trimmed, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var value ) )
		{
			hash = new Hd2Hash( value );
			return true;
		}

		hash = default;
		return false;
	}

	/// <summary>
	/// MurmurHash64A. This matches the hash Stingray's <c>IDString64</c> uses, which is
	/// how the data compiler turned every resource name into what the containers store.
	/// </summary>
	static ulong Murmur64A( ReadOnlySpan<byte> bytes )
	{
		var hash = (ulong)bytes.Length * Mix;
		var offset = 0;

		while ( bytes.Length - offset >= 8 )
		{
			var key = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian( bytes.Slice( offset, 8 ) );
			offset += 8;

			key *= Mix;
			key ^= key >> Shift;
			key *= Mix;

			hash ^= key;
			hash *= Mix;
		}

		var tail = bytes.Length - offset;
		if ( tail > 0 )
		{
			for ( var i = tail - 1; i >= 0; i-- ) hash ^= (ulong)bytes[offset + i] << (8 * i);
			hash *= Mix;
		}

		hash ^= hash >> Shift;
		hash *= Mix;
		hash ^= hash >> Shift;

		return hash;
	}

	public bool Equals( Hd2Hash other ) => Value == other.Value;

	public override bool Equals( object obj ) => obj is Hd2Hash other && Equals( other );

	public override int GetHashCode() => Value.GetHashCode();

	public int CompareTo( Hd2Hash other ) => Value.CompareTo( other.Value );

	public static bool operator ==( Hd2Hash left, Hd2Hash right ) => left.Value == right.Value;

	public static bool operator !=( Hd2Hash left, Hd2Hash right ) => left.Value != right.Value;

	/// <summary>Rendered the way the data compiler writes it: big endian hex, 16 digits.</summary>
	public override string ToString() => Value.ToString( "x16" );
}

/// <summary>
/// Stingray's 32-bit "thin" identity: the high half of an <see cref="Hd2Hash"/>. Mesh names
/// and material references inside resource payloads use these instead of full hashes.
/// </summary>
public readonly struct Hd2ThinHash : IEquatable<Hd2ThinHash>
{
	public readonly uint Value;

	public Hd2ThinHash( uint value )
	{
		Value = value;
	}

	public bool Equals( Hd2ThinHash other ) => Value == other.Value;

	public override bool Equals( object obj ) => obj is Hd2ThinHash other && Equals( other );

	public override int GetHashCode() => Value.GetHashCode();

	public static bool operator ==( Hd2ThinHash left, Hd2ThinHash right ) => left.Value == right.Value;

	public static bool operator !=( Hd2ThinHash left, Hd2ThinHash right ) => left.Value != right.Value;

	/// <summary>Matches how the payloads store a thin hash: the numeric value as big endian hex.</summary>
	public override string ToString() => Value.ToString( "x8" );
}
