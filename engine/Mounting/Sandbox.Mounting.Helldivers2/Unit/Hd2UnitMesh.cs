namespace Sandbox.Mounting.Helldivers2;

/// <summary>
/// One renderable mesh of a unit: its header, the material slots its groups refer to, and the
/// groups themselves. Helldivers 2 stores each level of detail as its own mesh, so a unit
/// usually holds several meshes per object rather than one mesh with a LOD chain.
/// </summary>
internal sealed class Hd2UnitMesh
{
	public Hd2MeshHeader Header { get; }

	/// <summary>Thin hashes of the material slots; <see cref="Hd2MeshGroup.MaterialIndex"/> indexes this list.</summary>
	public Hd2ThinHash[] Materials { get; }

	public Hd2MeshGroup[] Groups { get; }

	public Hd2UnitMesh( Hd2MeshHeader header, Hd2ThinHash[] materials, Hd2MeshGroup[] groups )
	{
		Header = header;
		Materials = materials;
		Groups = groups;
	}

	/// <summary>Index into the unit's mesh layout list, or -1 when this mesh carries no geometry.</summary>
	public int LayoutIndex => Header.LayoutIndex;

	public long VertexCount
	{
		get
		{
			var total = 0L;
			foreach ( var group in Groups ) total += group.NumVertices;
			return total;
		}
	}

	public long IndexCount
	{
		get
		{
			var total = 0L;
			foreach ( var group in Groups ) total += group.NumIndices;
			return total;
		}
	}

	/// <summary>The material slot a group draws with, or the default hash when the slot is out of range.</summary>
	public Hd2ThinHash MaterialOf( int groupIndex )
	{
		if ( (uint)groupIndex >= (uint)Groups.Length ) return default;

		var slot = Groups[groupIndex].MaterialIndex;
		return slot < Materials.Length ? Materials[slot] : default;
	}

	public override string ToString() =>
		$"{Header.MeshName}: layout {LayoutIndex}, {Groups.Length} groups, {VertexCount} vertices, {IndexCount} indices";
}
