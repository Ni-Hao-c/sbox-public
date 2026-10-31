namespace Sandbox.Mounting.Helldivers2;

/// <summary>
/// How a mesh is converted from the game's space into the engine's: the optional up-axis rotation,
/// the metres to inches scale, and an optional per-group material lookup.
///
/// A default-valued instance is already correct for this mount: no rotation (the game's unit meshes
/// measure as Z-up, like the engine) and <see cref="Hd2AxisConversion.MetresToInches"/> scale.
/// </summary>
internal struct Hd2MeshBuildOptions
{
	/// <summary>
	/// Uniform scale from the game's metres to the engine's inches. Zero or negative means "use
	/// <see cref="Hd2AxisConversion.MetresToInches"/>", so a default-valued options value is safe
	/// to pass; see <see cref="EffectiveUnitScale"/>.
	/// </summary>
	public float UnitScale { get; init; }

	/// <summary>
	/// Rotates positions and normals by ( x, y, z ) to ( x, z, -y ). Measured against the retail
	/// install, the game's meshes are already Z-up, so this lays models on their back and defaults
	/// to false; it exists for callers who find payloads that are Y-up after all. The rotation is
	/// handedness preserving, so no winding changes either way.
	/// </summary>
	public bool ConvertToZUp { get; init; }

	/// <summary>
	/// Optional per-group material lookup, called with each group's thin material hash (the slot
	/// <see cref="Hd2UnitMesh.MaterialOf"/> reports). When null, every draw call uses the single
	/// material passed to <see cref="Hd2MeshBuilder"/>. Returning null is allowed and draws
	/// that group with the engine's error material.
	/// </summary>
	public Func<Hd2ThinHash, Material> ResolveMaterial { get; init; }

	/// <summary>The mount's defaults: no rotation, metre to inch scale, one material for every group.</summary>
	public static Hd2MeshBuildOptions Default => new();

	/// <summary>
	/// The scale a build actually applies: <see cref="UnitScale"/> when it is positive, otherwise
	/// the metre to inch default. A negative scale would mirror the geometry and flip the winding,
	/// which this mount never wants, so it is treated as "unset" rather than honoured.
	/// </summary>
	public readonly float EffectiveUnitScale => UnitScale > 0f ? UnitScale : Hd2AxisConversion.MetresToInches;
}
