using System.Runtime.InteropServices;
using Sandbox.Mounting.PerfKit;

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
	Texture RealTimeC,
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
			.SelectMany( static page => new[] { page.SkyA, page.SkyB, page.RealTimeA, page.RealTimeB, page.RealTimeC, page.SunChannels } )
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

/// <summary>
/// Built world geometry. Opaque world surfaces are aggregated (one model per material, one
/// fragment per spatial cell, see <see cref="MountedAggregateBuilder"/>); translucent, decal,
/// Godray, water and shadow-only surfaces keep one model per spatial cell.
/// </summary>
sealed record WorldBuildResult(
	IReadOnlyList<WorldRenderChunk> TranslucentChunks,
	IReadOnlyList<WorldRenderChunk> DecalChunks,
	IReadOnlyList<WorldRenderChunk> GodrayChunks,
	IReadOnlyList<WorldRenderChunk> WaterChunks,
	IReadOnlyList<MountedAggregate> WorldAggregates,
	Model CollisionModel,
	IReadOnlyList<IDisposable> OwnedResources,
	IReadOnlyList<WorldRenderChunk> ShadowOnlyChunks = null ) : IDisposable
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
