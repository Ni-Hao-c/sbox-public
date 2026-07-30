using System.Runtime.InteropServices;

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

sealed record RuntimeLightmapPage(
	Texture SkyA,
	Texture SkyB,
	Texture RealTimeA,
	Texture RealTimeB,
	Texture RealTimeC );

sealed class RuntimeLightmaps
{
	public IReadOnlyList<RuntimeLightmapPage> Pages { get; }
	public IReadOnlyList<IDisposable> OwnedResources { get; }

	public RuntimeLightmaps( IReadOnlyList<RuntimeLightmapPage> pages )
	{
		Pages = pages;
		OwnedResources = pages
			.SelectMany( static page => new[] { page.SkyA, page.SkyB, page.RealTimeA, page.RealTimeB, page.RealTimeC } )
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

sealed record WorldBuildResult(
	IReadOnlyList<WorldRenderChunk> WorldChunks,
	IReadOnlyList<WorldRenderChunk> DecalChunks,
	IReadOnlyList<WorldRenderChunk> GodrayChunks,
	Model CollisionModel,
	IReadOnlyList<IDisposable> OwnedResources );
