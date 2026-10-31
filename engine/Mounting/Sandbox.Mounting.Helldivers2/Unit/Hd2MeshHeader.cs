using System.Buffers.Binary;

namespace Sandbox.Mounting.Helldivers2;

/// <summary>An axis-aligned bounding box, stored as the unit format stores it.</summary>
internal readonly struct Hd2Aabb
{
	public readonly Vector3 Min;
	public readonly Vector3 Max;

	public Hd2Aabb( Vector3 min, Vector3 max )
	{
		Min = min;
		Max = max;
	}

	public BBox Bounds => new( Min, Max );

	/// <summary>
	/// The same box expressed the way <see cref="Hd2MeshBuilder"/> writes geometry: optionally
	/// rotated out of the game's up axis and scaled from the game's metres to the engine's inches,
	/// using whatever options that build was given. Both corners are transformed and the result is
	/// re-sorted, because the rotation reorders the corners' components.
	/// </summary>
	public Hd2Aabb Transform( Hd2MeshBuildOptions options )
	{
		var scale = options.EffectiveUnitScale;
		var a = Hd2AxisConversion.ToEngine( Min, options.ConvertToZUp, scale );
		var b = Hd2AxisConversion.ToEngine( Max, options.ConvertToZUp, scale );
		return new Hd2Aabb( Vector3.Min( a, b ), Vector3.Max( a, b ) );
	}

	public override string ToString() => $"min {Min:0.###}, max {Max:0.###}";
}

/// <summary>
/// The mesh flavours the format labels. The names are the reference implementation's own
/// guesses; the values are what a real payload carries.
/// </summary>
internal enum Hd2MeshHeaderType : uint
{
	/// <summary>0x000 - the reference calls this "bounding box, maybe?"; such meshes have no drawable geometry.</summary>
	Unknown0 = 0x000,

	/// <summary>0x10A - "low detail?" per the reference. Meshes synthesised from a geometry group are stamped with it.</summary>
	Unknown1 = 0x10A,

	/// <summary>0x189 - "full mesh?" per the reference.</summary>
	Unknown2 = 0x189
}

/// <summary>
/// One 128-byte mesh header: the mesh's name hash, bounds, and where its material list and
/// group list live inside the mesh info block, plus the layout it is drawn with.
/// </summary>
internal readonly struct Hd2MeshHeader
{
	public const int StoredSize = 128;

	public readonly Hd2Aabb Aabb;

	/// <summary>Unknown. The reference implementation reads it and never uses it.</summary>
	public readonly float UnkFloat00;

	public readonly Hd2MeshHeaderType Type;

	/// <summary>Thin hash of the mesh's name. Naming a mesh needs an external hash lookup.</summary>
	public readonly Hd2ThinHash MeshName;

	/// <summary>Index into the unit's bone list naming the transform the AABB is expressed in.</summary>
	public readonly uint AabbTransformIndex;

	public readonly uint TransformIndex;

	public readonly uint UnkInt03;

	/// <summary>Index into the skeleton map list, or -1.</summary>
	public readonly int SkeletonMapIndex;

	/// <summary>Index into the mesh layout list, or -1 when the mesh carries no geometry.</summary>
	public readonly int LayoutIndex;

	public readonly uint NumMaterials;

	/// <summary>Offset of the material list, relative to the start of this mesh header.</summary>
	public readonly uint MaterialOffset;

	public readonly uint NumGroups;

	/// <summary>Offset of the group list, relative to the start of this mesh header.</summary>
	public readonly uint GroupOffset;

	public Hd2MeshHeader( ReadOnlySpan<byte> data )
	{
		Aabb = new Hd2Aabb(
			new Vector3(
				BinaryPrimitives.ReadSingleLittleEndian( data.Slice( 8, 4 ) ),
				BinaryPrimitives.ReadSingleLittleEndian( data.Slice( 12, 4 ) ),
				BinaryPrimitives.ReadSingleLittleEndian( data.Slice( 16, 4 ) ) ),
			new Vector3(
				BinaryPrimitives.ReadSingleLittleEndian( data.Slice( 20, 4 ) ),
				BinaryPrimitives.ReadSingleLittleEndian( data.Slice( 24, 4 ) ),
				BinaryPrimitives.ReadSingleLittleEndian( data.Slice( 28, 4 ) ) ) );

		UnkFloat00 = BinaryPrimitives.ReadSingleLittleEndian( data.Slice( 32, 4 ) );
		Type = (Hd2MeshHeaderType)BinaryPrimitives.ReadUInt32LittleEndian( data.Slice( 36, 4 ) );
		MeshName = new Hd2ThinHash( BinaryPrimitives.ReadUInt32LittleEndian( data.Slice( 40, 4 ) ) );
		AabbTransformIndex = BinaryPrimitives.ReadUInt32LittleEndian( data.Slice( 44, 4 ) );
		TransformIndex = BinaryPrimitives.ReadUInt32LittleEndian( data.Slice( 48, 4 ) );
		UnkInt03 = BinaryPrimitives.ReadUInt32LittleEndian( data.Slice( 52, 4 ) );
		SkeletonMapIndex = BinaryPrimitives.ReadInt32LittleEndian( data.Slice( 56, 4 ) );
		LayoutIndex = BinaryPrimitives.ReadInt32LittleEndian( data.Slice( 60, 4 ) );
		NumMaterials = BinaryPrimitives.ReadUInt32LittleEndian( data.Slice( 104, 4 ) );
		MaterialOffset = BinaryPrimitives.ReadUInt32LittleEndian( data.Slice( 108, 4 ) );
		NumGroups = BinaryPrimitives.ReadUInt32LittleEndian( data.Slice( 120, 4 ) );
		GroupOffset = BinaryPrimitives.ReadUInt32LittleEndian( data.Slice( 124, 4 ) );
	}

	Hd2MeshHeader( Hd2Aabb aabb, Hd2MeshHeaderType type, Hd2ThinHash meshName, uint aabbTransformIndex, int layoutIndex )
	{
		Aabb = aabb;
		UnkFloat00 = 0f;
		Type = type;
		MeshName = meshName;
		AabbTransformIndex = aabbTransformIndex;
		TransformIndex = 0;
		UnkInt03 = 0;
		SkeletonMapIndex = -1;
		LayoutIndex = layoutIndex;
		NumMaterials = 0;
		MaterialOffset = 0;
		NumGroups = 0;
		GroupOffset = 0;
	}

	/// <summary>
	/// A header for a mesh whose geometry lives in a shared geometry group: the group supplies
	/// the layout, materials and groups, while bounds and their transform come from the unit's
	/// own mesh info list when it has an entry under the same mesh name.
	/// </summary>
	internal static Hd2MeshHeader Synthetic( Hd2ThinHash meshName, int layoutIndex, Hd2Aabb aabb, uint aabbTransformIndex, Hd2MeshHeaderType type ) =>
		new( aabb, type, meshName, aabbTransformIndex, layoutIndex );

	public override string ToString() =>
		$"{MeshName} type {(uint)Type:x} layout {LayoutIndex} {NumGroups} groups, {NumMaterials} materials, bounds {Aabb}";
}
