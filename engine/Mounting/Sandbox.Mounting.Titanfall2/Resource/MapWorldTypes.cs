using System.Runtime.InteropServices;
using Sandbox.Mounting.PerfKit;
using Titanfall2;

/// <summary>Vertex layout used by imported Titanfall 2 world meshes.</summary>
[StructLayout( LayoutKind.Sequential )]
struct TitanfallMapVertex
{
	[VertexLayout.Position] public Vector3 Position;
	[VertexLayout.Normal] public Vector3 Normal;
	[VertexLayout.TexCoord] public Vector2 TexCoord;
	[VertexLayout.TexCoord( 1 )] public Vector2 LightmapTexCoord;
	[VertexLayout.Color] public Color32 Color;
}

/// <summary>Temporary CPU-side grouping used while building world meshes.</summary>
sealed class MapMeshGroup
{
	public List<TitanfallMapVertex> Vertices { get; } = new();
	public List<int> Indices { get; } = new();
	public BBox Bounds = new() { Mins = float.MaxValue, Maxs = float.MinValue };
}

readonly record struct WorldMaterialKey( string MaterialName, int LightmapPage );

// The authored RealTimeC page has no reader in any shader, so it is deliberately not uploaded:
// the format still carries the bytes (see Titanfall2BspReader.ReadLightmaps) but nothing creates,
// binds or owns a texture for it.
sealed record RuntimeLightmapPage(
	Texture SkyA,
	Texture SkyB,
	Texture RealTimeA,
	Texture RealTimeB,
	Texture SunChannels );

sealed class RuntimeLightmaps
{
	public IReadOnlyList<RuntimeLightmapPage> Pages { get; }
	public IReadOnlyList<IDisposable> OwnedResources { get; }
	public Color SkyColor { get; }

	public RuntimeLightmaps( IReadOnlyList<RuntimeLightmapPage> pages, Color skyColor )
	{
		Pages = pages;
		SkyColor = skyColor;
		OwnedResources = pages
			.SelectMany( static page => new[] { page.SkyA, page.SkyB, page.RealTimeA, page.RealTimeB, page.SunChannels } )
			.Where( static texture => texture is not null )
			.Cast<IDisposable>()
			.ToArray();
	}
}

readonly record struct WorldRenderCell( int X, int Y, int Z )
{
	public static WorldRenderCell FromTriangle( Vector3 first, Vector3 second, Vector3 third, float size )
	{
		var center = (first + second + third) / 3f;
		return new WorldRenderCell(
			(int)MathF.Floor( center.x / size ),
			(int)MathF.Floor( center.y / size ),
			(int)MathF.Floor( center.z / size ) );
	}

	public override string ToString() => $"{X}_{Y}_{Z}";
}

sealed record WorldRenderChunk( WorldRenderCell Cell, Model Model );

/// <summary>Render category of one world source mesh; it selects the renderer its groups end up in.</summary>
enum WorldGroupCategory
{
	Opaque = 0,
	Translucent = 1,
	Decal = 2,
	Godray = 3,
	Water = 4,
	ShadowOnly = 5,
}

/// <summary>One classified world source mesh, before its triangles are split by spatial cell.</summary>
sealed class WorldMeshSource
{
	public Titanfall2BspReader.WorldMesh Mesh { get; init; }
	public WorldMaterialKey MaterialKey { get; init; }
	public WorldGroupCategory Category { get; init; }
	public bool IsDecal { get; init; }
	public bool IncludeCollision { get; init; }
}

/// <summary>Triangle start indices (into the source mesh's index list) that fall into one spatial cell.</summary>
readonly record struct WorldCellTriangles( WorldRenderCell Cell, List<int> TriangleStarts );

/// <summary>A source mesh's triangles bucketed per spatial cell, plus its fallback collision triangles.</summary>
sealed class WorldMeshCellBuckets
{
	public List<WorldCellTriangles> Cells { get; } = new();
	public List<Vector3> CollisionPositions { get; init; }
	public List<int> CollisionIndices { get; init; }
}

/// <summary>One source mesh's share of one spatial cell.</summary>
readonly record struct WorldCellContribution( int SourceIndex, List<int> TriangleStarts );

/// <summary>Groups built for one spatial cell. Only the worker that owns the cell touches them.</summary>
sealed class WorldCellGroups
{
	// Indexed by WorldGroupCategory.
	readonly Dictionary<WorldMaterialKey, MapMeshGroup>[] _materials =
		new Dictionary<WorldMaterialKey, MapMeshGroup>[(int)WorldGroupCategory.ShadowOnly + 1];

	public IReadOnlyList<Dictionary<WorldMaterialKey, MapMeshGroup>> Materials => _materials;

	public MapMeshGroup GetGroup( WorldGroupCategory category, WorldMaterialKey key )
	{
		var materials = _materials[(int)category] ??= new Dictionary<WorldMaterialKey, MapMeshGroup>();
		if ( materials.TryGetValue( key, out var group ) ) return group;
		group = new MapMeshGroup();
		materials.Add( key, group );
		return group;
	}
}

/// <summary>
/// Built world geometry. Opaque world surfaces are aggregated (one model per material, one
/// fragment per spatial cell, see <see cref="MountedAggregateBuilder"/>); translucent, decal,
/// Godray, water and shadow-only surfaces keep one model per spatial cell. <see cref="WorldShadowChunks"/>
/// is the optional depth-only copy of the opaque world used instead of the aggregates for the shadow
/// pass, and <see cref="WorldAggregateCastShadows"/> says which aggregates still cast (alpha-tested
/// materials keep their own depth shader so their shadows stay cut out).
/// </summary>
sealed record WorldBuildResult(
	IReadOnlyList<WorldRenderChunk> TranslucentChunks,
	IReadOnlyList<WorldRenderChunk> DecalChunks,
	IReadOnlyList<WorldRenderChunk> GodrayChunks,
	IReadOnlyList<WorldRenderChunk> WaterChunks,
	IReadOnlyList<MountedAggregate> WorldAggregates,
	Model CollisionModel,
	IReadOnlyList<IDisposable> OwnedResources,
	IReadOnlyList<WorldRenderChunk> ShadowOnlyChunks = null,
	IReadOnlyList<WorldRenderChunk> WorldShadowChunks = null,
	IReadOnlyList<bool> WorldAggregateCastShadows = null ) : IDisposable
{
	bool _disposed;

	/// <summary>
	/// Releases the textures this build created (lightmap pages, probe atlases). Nothing else
	/// holds them, so a build that is discarded - because it failed half way, or because no
	/// scene shows the map any more - must call this or the pages stay in video memory for the
	/// mount's lifetime. Calling it twice does nothing.
	/// </summary>
	public void Dispose()
	{
		if ( _disposed ) return;
		_disposed = true;
		foreach ( var resource in OwnedResources ?? [] ) resource?.Dispose();
	}
}
