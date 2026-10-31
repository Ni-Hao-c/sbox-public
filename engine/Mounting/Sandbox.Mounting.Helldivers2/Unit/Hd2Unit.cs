namespace Sandbox.Mounting.Helldivers2;

/// <summary>
/// The parsed <c>unit</c> payload: what the unit is made of and where its geometry lives.
///
/// Geometry resolution follows the order the reference implementation uses:
/// <list type="number">
/// <item>when <see cref="UsesExternalGeometry"/> is false, the unit's own <see cref="Meshes"/>
/// drawn from its own GPU stream;</item>
/// <item>otherwise the shared <c>geometry_group</c> resource named by
/// <see cref="Hd2UnitHeader.GeometryGroup"/>, drawn from that resource's GPU stream;</item>
/// <item>terrain units, which carry heightmaps instead of meshes and are not built here.</item>
/// </list>
/// </summary>
internal sealed class Hd2Unit
{
	/// <summary>
	/// The "dev assets" geometry group. It appears in some builds but ships no geometry in the
	/// retail release, so the reference implementation treats it the same as a missing group.
	/// </summary>
	public const ulong DevGeometryGroup = 0xFCE38C71CA9C2166UL;

	public Hd2UnitHeader Header { get; }

	/// <summary>Vertex layouts, referenced by <see cref="Hd2UnitMesh.LayoutIndex"/>.</summary>
	public Hd2MeshLayout[] MeshLayouts { get; }

	/// <summary>The unit's own meshes. Empty for units whose geometry lives in a geometry group.</summary>
	public Hd2UnitMesh[] Meshes { get; }

	public Hd2LodGroup[] LodGroups { get; }

	/// <summary>Thin hash per mesh, naming the group of bones the mesh is bound to.</summary>
	public Hd2ThinHash[] GroupBones { get; }

	/// <summary>Maps a mesh's thin material hash to the full <c>material</c> resource hash.</summary>
	public IReadOnlyDictionary<Hd2ThinHash, Hd2Hash> MaterialLookup { get; }

	public Hd2Unit(
		Hd2UnitHeader header,
		Hd2MeshLayout[] meshLayouts,
		Hd2UnitMesh[] meshes,
		Hd2LodGroup[] lodGroups,
		Hd2ThinHash[] groupBones,
		IReadOnlyDictionary<Hd2ThinHash, Hd2Hash> materialLookup )
	{
		Header = header;
		MeshLayouts = meshLayouts;
		Meshes = meshes;
		LodGroups = lodGroups;
		GroupBones = groupBones;
		MaterialLookup = materialLookup;
	}

	/// <summary>True when the unit's geometry comes from the shared geometry group it names.</summary>
	public bool UsesExternalGeometry =>
		Header.GeometryGroup.Value != 0 && Header.GeometryGroup.Value != DevGeometryGroup;

	/// <summary>True when the unit is a terrain unit, which carries a heightmap instead of meshes.</summary>
	public bool HasTerrain => Header.TerrainInfoListOffset != 0;

	public long TotalVertexCount
	{
		get
		{
			var total = 0L;
			foreach ( var mesh in Meshes ) total += mesh.VertexCount;
			return total;
		}
	}

	public long TotalIndexCount
	{
		get
		{
			var total = 0L;
			foreach ( var mesh in Meshes ) total += mesh.IndexCount;
			return total;
		}
	}

	public int TotalGroupCount
	{
		get
		{
			var total = 0;
			foreach ( var mesh in Meshes ) total += mesh.Groups.Length;
			return total;
		}
	}
}
