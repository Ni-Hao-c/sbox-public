namespace Sandbox.Mounting.Helldivers2;

/// <summary>
/// One of the fifteen 12-byte records that follow the twelve unknown bytes at the start of a
/// Stingray <c>texture</c> resource. Each record describes a run of image data in the resource's
/// pixel streams: where the run starts, how large it is, and the mip dimensions it carries.
/// </summary>
internal readonly struct Hd2TextureSection
{
	public const int StoredSize = 12;

	public readonly uint Offset;
	public readonly uint Size;
	public readonly ushort Width;
	public readonly ushort Height;

	public Hd2TextureSection( ReadOnlySpan<byte> data )
	{
		Offset = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian( data );
		Size = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian( data[4..] );
		Width = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian( data[8..] );
		Height = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian( data[10..] );
	}

	/// <summary>A record that carries no data; the table pads itself with these.</summary>
	public bool IsEmpty => Size == 0;

	public override string ToString() => $"{Width}x{Height} @0x{Offset:x8}+{Size}";
}
