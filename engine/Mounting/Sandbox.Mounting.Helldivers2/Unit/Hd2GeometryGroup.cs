namespace Sandbox.Mounting.Helldivers2;

/// <summary>
/// A shared geometry container. A unit whose <see cref="Hd2UnitHeader.GeometryGroup"/> is set
/// keeps only per-mesh metadata (name hashes, bounds); the layouts, vertices, indices and groups
/// live here, addressed by the unit's own name hash, and are read from this resource's own GPU
/// stream rather than the unit's.
/// </summary>
internal sealed class Hd2GeometryGroup
{
	/// <summary>Resource type of a geometry group, as the containers spell it.</summary>
	public static readonly Hd2Hash TypeId = Hd2Hash.Sum( "geometry_group" );

	/// <summary>Unknown. The reference implementation reads it and never uses it.</summary>
	public Hd2Hash UnkHash { get; }

	/// <summary>Vertex layouts, referenced by the meshes below.</summary>
	public Hd2MeshLayout[] MeshLayouts { get; }

	/// <summary>Each unit that draws geometry from this container, keyed by the unit's name hash.</summary>
	public IReadOnlyDictionary<Hd2Hash, Hd2GeometryGroupUnit> Units { get; }

	public Hd2GeometryGroup( Hd2Hash unkHash, Hd2MeshLayout[] meshLayouts, IReadOnlyDictionary<Hd2Hash, Hd2GeometryGroupUnit> units )
	{
		UnkHash = unkHash;
		MeshLayouts = meshLayouts;
		Units = units;
	}

	public long TotalMeshCount
	{
		get
		{
			var total = 0L;
			foreach ( var unit in Units.Values ) total += unit.Meshes.Length;
			return total;
		}
	}

	public long TotalVertexCount
	{
		get
		{
			var total = 0L;
			foreach ( var unit in Units.Values )
				foreach ( var mesh in unit.Meshes )
					total += mesh.VertexCount;

			return total;
		}
	}
}

/// <summary>One unit's meshes inside a <see cref="Hd2GeometryGroup"/>.</summary>
internal sealed class Hd2GeometryGroupUnit
{
	/// <summary>Thin hash naming each mesh. The unit's own mesh info list carries the same names.</summary>
	public Hd2ThinHash[] MeshNames { get; }

	public Hd2UnitMesh[] Meshes { get; }

	public Hd2GeometryGroupUnit( Hd2ThinHash[] meshNames, Hd2UnitMesh[] meshes )
	{
		MeshNames = meshNames;
		Meshes = meshes;
	}
}
