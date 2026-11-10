using Sandbox.Mounting.PerfKit;
using System.Runtime.InteropServices;

/// <summary>The optional table path alone pays for an extra vertex attribute.</summary>
[StructLayout( LayoutKind.Sequential )]
struct TitanfallWorldMaterialVertex
{
	[VertexLayout.Position] public Vector3 Position;
	[VertexLayout.Normal] public Vector3 Normal;
	[VertexLayout.TexCoord] public Vector2 TexCoord;
	[VertexLayout.TexCoord( 1 )] public Vector2 LightmapTexCoord;
	[VertexLayout.Color] public Color32 Color;
	[VertexLayout.TexCoord( 9 )] public float MaterialId;

	internal static TitanfallWorldMaterialVertex From( in TitanfallMapVertex source, int id )
	{
		if ( id < 0 || id > 16777216 ) throw new ArgumentOutOfRangeException( nameof( id ) );
		return new()
		{
			Position = source.Position,
			Normal = source.Normal,
			TexCoord = source.TexCoord,
			LightmapTexCoord = source.LightmapTexCoord,
			Color = source.Color,
			MaterialId = id
		};
	}
}

/// <summary>
/// Shares a submit material across compatible lightmapped surfaces. Spatial fragments, their
/// bounds and triangle ranges are retained; the engine still owns indirect drawing and culling.
/// </summary>
static class Titanfall2WorldMaterialBatch
{
	static readonly System.Collections.Concurrent.ConcurrentDictionary<string, WeakReference<Material[]>> SourceSets = new( StringComparer.Ordinal );
	internal static string Register( Material[] sources )
	{
		var key = Guid.NewGuid().ToString( "N" );
		SourceSets.TryAdd( key, new( sources ) );
		return key;
	}
	internal static Material[] Resolve( string key ) => key is not null && SourceSets.TryGetValue( key, out var entry )
		&& entry.TryGetTarget( out var materials ) ? materials : [];
	internal static void Release( string key ) { if ( key is not null ) SourceSets.TryRemove( key, out _ ); }
	internal sealed class Resources
	{
		internal Material[] Sources { get; set; } = [];
		internal Material[] Tables { get; set; } = [];
	}

	internal sealed record Result( IReadOnlyList<MountedAggregate> Aggregates,
		IReadOnlyList<WorldMaterialKey> Keys, Resources Owned );

	internal static Result Build(
		Dictionary<WorldRenderCell, Dictionary<WorldMaterialKey, MapMeshGroup>> cells,
		IReadOnlyDictionary<WorldMaterialKey, Material> materials,
		Func<WorldMaterialKey, bool> eligible,
		string resourceName )
	{
		var sources = new List<Material>();
		var tables = new Dictionary<int, MountedGeometryGroup<TitanfallWorldMaterialVertex>>();
		var keys = new Dictionary<int, WorldMaterialKey>();
		var aggregates = new List<MountedAggregate>();
		var aggregateKeys = new List<WorldMaterialKey>();
		foreach ( var (key, material) in materials.OrderBy( pair => pair.Key.MaterialName, StringComparer.OrdinalIgnoreCase )
			.ThenBy( pair => pair.Key.LightmapPage ) )
		{
			var table = eligible( key ) && material.ShaderName == "shaders/titanfall2_world_lightmapped.shader";
			if ( table )
			{
				var id = sources.Count;
				sources.Add( material );
				if ( !tables.TryGetValue( key.LightmapPage, out var batch ) )
				{
					var shared = material.CreateCopy( $"{resourceName}/world_material_table_{key.LightmapPage}" );
					shared.SetFeature( "F_T2_WORLD_MATERIAL_TABLE", 1 );
					batch = new( shared );
					tables.Add( key.LightmapPage, batch );
					keys.Add( key.LightmapPage, key );
				}
				foreach ( var (_, groups) in cells )
				{
					if ( !groups.TryGetValue( key, out var group ) || group.Vertices.Count == 0 || group.Indices.Count == 0 ) continue;
					batch.Add( group.Vertices.Select( vertex => TitanfallWorldMaterialVertex.From( vertex, id ) ).ToList(), group.Indices, group.Bounds );
				}
			}
			else
			{
				var batch = new MountedGeometryGroup<TitanfallMapVertex>( material );
				foreach ( var (_, groups) in cells )
					if ( groups.TryGetValue( key, out var group ) ) batch.Add( group.Vertices, group.Indices, group.Bounds );
				var built = MountedAggregateBuilder.Build( new[] { batch }, $"{resourceName}/ordinary_{aggregates.Count}" );
				aggregates.AddRange( built );
				aggregateKeys.AddRange( Enumerable.Repeat( key, built.Count ) );
			}
		}
		foreach ( var (page, batch) in tables.OrderBy( pair => pair.Key ) )
		{
			var built = MountedAggregateBuilder.Build( new[] { batch }, $"{resourceName}/table_batch_{aggregates.Count}" );
			aggregates.AddRange( built );
			aggregateKeys.AddRange( Enumerable.Repeat( keys[page], built.Count ) );
		}
		return new( aggregates, aggregateKeys, new Resources
		{
			Sources = sources.ToArray(),
			Tables = tables.Values.Select( batch => batch.Material ).ToArray()
		} );
	}
}

/// <summary>
/// Restores the world material buffer for each cloned scene, using the engine's view attributes.
/// No texture upload, geometry preparation or material capture runs in the frame loop.
/// </summary>
[Library, Hide]
public sealed class Titanfall2WorldMaterialTableController : Component, Component.ExecuteInEditor, Component.DontExecuteOnServer
{
	// Generated materials have no disk loader. The map build keeps their wrappers alive;
	// serializing Material[] would try to reload them through the mount's file loader.
	[Property, Hide] public string SourceKey { get; set; }
	[System.Text.Json.Serialization.JsonIgnore] public Material[] Sources => Titanfall2WorldMaterialBatch.Resolve( SourceKey );
	GpuBuffer<Titanfall2GpuMaterialData> _buffer;

	protected override Task OnLoad( LoadingContext context ) { Restore(); return Task.CompletedTask; }
	protected override void OnEnabled() => Restore();
	protected override void OnStart() => Restore();

	void Restore()
	{
		if ( _buffer is not null || string.IsNullOrEmpty( SourceKey ) ) return;
		var sources = Sources;
		if ( sources.Length == 0 || sources.Any( material => material is null || !material.IsValid ) )
			throw new InvalidOperationException( "World material table source lease is unavailable." );
		// Native aggregate buffers are view attributes. Like the BSP light-page controller,
		// this backend expects one world in a scene; reject overlap instead of binding wrong rows.
		if ( Scene.GetAllComponents<Titanfall2WorldMaterialTableController>().Any( other => other != this && other._buffer is not null ) )
			throw new NotSupportedException( "World material tables currently support one mounted world per scene." );
		_buffer = new( sources.Length );
		try
		{
			_buffer.SetData( sources.Select( Titanfall2GpuMaterialData.Capture ).ToArray() );
			Scene.RenderAttributes.Set( "T2WorldMaterials", _buffer );
		}
		catch { _buffer.Dispose(); _buffer = null; throw; }
	}

	protected override void OnDisabled() => Release();
	protected override void OnDestroy() => Release();
	void Release()
	{
		if ( _buffer is null ) return;
		Scene.RenderAttributes.Set( "T2WorldMaterials", (GpuBuffer)null );
		_buffer.Dispose();
		_buffer = null;
	}
}
