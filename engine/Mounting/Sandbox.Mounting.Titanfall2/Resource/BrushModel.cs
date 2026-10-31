using Titanfall2;

/// <summary>Per-map, mount-owned inline geometry and lazily uploaded lightmap pages.</summary>
sealed class Titanfall2BrushModelSource( ITitanfall2AssetSource source, ITitanfall2AssetSource environmentSource ) : IDisposable
{
	readonly object _gate = new();
	Titanfall2BspReader.BspBrushModels _parsed;
	readonly Dictionary<int, RuntimeLightmapPage> _pages = new();
	readonly Dictionary<WorldMaterialKey, Material> _materials = new();
	Color _skyColor;
	bool _disposed;

	internal static int ReadCount( ITitanfall2AssetSource source )
	{
		if ( TryReadSidecar( source, 14, out var table ) ) return Titanfall2BspReader.ReadBrushModelCount( table );
		if ( !source.TryReadAllBytes( out var bytes, out var error ) ) throw new InvalidDataException( error );
		return Titanfall2BspReader.ReadBrushModelCountFromBsp( bytes );
	}

	static bool TryReadSidecar( ITitanfall2AssetSource source, int id, out byte[] bytes )
	{
		bytes = null;
		return source is ITitanfall2SiblingAssetSource siblings
			&& (siblings.TryReadSibling( $".{id:X4}.bsp_lump.client", out bytes, out _ )
				|| siblings.TryReadSibling( $".{id:X4}.bsp_lump", out bytes, out _ ));
	}

	void EnsureParsed()
	{
		if ( _disposed ) throw new ObjectDisposedException( nameof( Titanfall2BrushModelSource ) );
		if ( _parsed is not null ) return;
		if ( !source.TryReadAllBytes( out var bytes, out var error ) ) throw new InvalidDataException( error );
		var lightmaps = Titanfall2StreamingSettings.UseBspLightmaps;
		var overrides = new Dictionary<int, byte[]>();
		foreach ( var id in Titanfall2BspReader.RequiredBrushModelLumpIds )
			if ( (lightmaps || !Titanfall2BspReader.IsBrushLightingLump( id )) && TryReadSidecar( source, id, out var lump ) ) overrides[id] = lump;
		if ( !Titanfall2BspReader.TryReadBrushModels( bytes, overrides, lightmaps, out var parsed, out error ) )
			throw new InvalidDataException( error );
		var entities = environmentSource?.TryReadAllBytes( out var entityBytes, out _ ) == true
			? Titanfall2EntityText.Parse( entityBytes ) : parsed.Entities;
		_skyColor = MapLoader.ReadBakedSkyColor( new Titanfall2BspReader.ParsedBsp { WorldLights = parsed.WorldLights, World = parsed.WorldBounds }, entities );
		_parsed = parsed;
	}

	/// <summary>
	/// One collision-only model per original contents value. The render model mixes contents
	/// (191 of 1,053 retail inline models combine values such as solid and player clip), and
	/// collision tags live on GameObjects, so each value needs its own classified collider.
	/// </summary>
	internal (uint Contents, Model Model)[] BuildCollisionGroups( string path, int index )
	{
		lock ( _gate )
		{
			EnsureParsed();
			if ( index < 1 || index > _parsed.Models.Count ) throw new InvalidDataException( "Inline model index is out of bounds." );
			var collision = _parsed.Models[index - 1].Collision;
			var groups = new List<(uint, Model)>();
			foreach ( var group in collision.Parts.GroupBy( part => part.Contents ).OrderBy( group => group.Key ) )
			{
				var builder = Model.Builder.WithName( $"{path}/contents_{group.Key:x8}" );
				var vertices = new List<Vector3>();
				var indices = new List<int>();
				foreach ( var part in group )
				{
					var points = collision.Vertices.Skip( part.FirstVertex ).Take( part.VertexCount ).Select( ToSandbox ).ToList();
					var local = collision.Indices.Skip( part.FirstIndex ).Take( part.IndexCount ).Select( value => value - part.FirstVertex ).ToList();
					if ( part.IsConvex ) builder.AddCollisionHull( points );
					else builder.AddCollisionMesh( points, local );
					var first = vertices.Count;
					vertices.AddRange( points );
					indices.AddRange( local.Select( value => value + first ) );
				}
				if ( indices.Count >= 3 ) builder.AddTraceMesh( vertices, indices );
				builder.WithData( new Titanfall2BrushModelData
				{
					SourcePath = source.Description,
					ModelIndex = index,
					Parts = group.Select( part => new Titanfall2BrushCollisionPart { IsConvex = part.IsConvex, SourceIndex = part.SourceIndex, Contents = part.Contents } ).ToArray()
				} );
				groups.Add( (group.Key, builder.Create()) );
			}
			return groups.ToArray();
		}
	}

	internal Model Build( Titanfall2Mount host, string path, int index )
	{
		lock ( _gate )
		{
			EnsureParsed();
			if ( index < 1 || index > _parsed.Models.Count ) throw new InvalidDataException( "Inline model index is out of bounds." );
			var model = _parsed.Models[index - 1];
			var builder = Model.Builder.WithName( path );
			foreach ( var group in model.Geometry.Meshes.Where( mesh => !ShouldSkip( mesh.MaterialName ) )
				.GroupBy( mesh => new WorldMaterialKey( mesh.MaterialName, mesh.LightmapPage ) ) )
			{
				var vertices = new List<TitanfallMapVertex>();
				var indices = new List<int>();
				foreach ( var mesh in group )
				{
					var first = vertices.Count;
					vertices.AddRange( mesh.Vertices.Select( vertex => MapLoader.CreateWorldVertex( vertex, false ) ) );
					for ( var triangle = 0; triangle < mesh.Indices.Length; triangle += 3 )
					{
						indices.Add( first + mesh.Indices[triangle] );
						indices.Add( first + mesh.Indices[triangle + 2] );
						indices.Add( first + mesh.Indices[triangle + 1] );
					}
				}
				var renderMesh = new Mesh( path, GetMaterial( host, group.Key ) );
				renderMesh.CreateVertexBuffer( vertices.Count, vertices );
				renderMesh.CreateIndexBuffer( indices.Count, indices );
				renderMesh.Bounds = BBox.FromPoints( vertices.Select( vertex => vertex.Position ) );
				builder.AddMesh( renderMesh );
			}
			var collision = model.Collision;
			foreach ( var part in collision.Parts )
			{
				var points = collision.Vertices.Skip( part.FirstVertex ).Take( part.VertexCount ).Select( ToSandbox ).ToList();
				// Each source brush is convex. Combining all brushes into one hull would
				// close openings and create invisible solids between separate pieces.
				if ( part.IsConvex ) builder.AddCollisionHull( points );
				else builder.AddCollisionMesh( points, collision.Indices.Skip( part.FirstIndex ).Take( part.IndexCount )
					.Select( value => value - part.FirstVertex ).ToList() );
			}
			if ( !collision.IsEmpty ) builder.AddTraceMesh( collision.Vertices.Select( ToSandbox ).ToList(), collision.Indices.ToList() );
			builder.WithData( new Titanfall2BrushModelData
			{
				SourcePath = source.Description,
				ModelIndex = index,
				Parts = collision.Parts.Select( part => new Titanfall2BrushCollisionPart
				{
					IsConvex = part.IsConvex,
					SourceIndex = part.SourceIndex,
					Contents = part.Contents
				} ).ToArray()
			} );
			return builder.Create();
		}
	}

	Material GetMaterial( Titanfall2Mount host, WorldMaterialKey key )
	{
		if ( _materials.TryGetValue( key, out var material ) && material.IsValid ) return material;
		var metadata = host.TryGetMaterialMetadata( key.MaterialName, out var known ) ? known : MaterialLoader.InferMetadata( key.MaterialName );
		material = host.TryGetMaterialPath( key.MaterialName, out var registered ) ? Material.Load( $"mount://{host.Ident}/{registered}.vmat" ) : null;
		if ( material is null || !material.IsValid ) material = MaterialLoader.CreateRuntimeMaterial( $"{source.Description}/brush/{key.MaterialName}", metadata );
		if ( metadata.Mode == Titanfall2MaterialMode.Opaque && !metadata.IsUnlit && !metadata.IsWater
			&& key.LightmapPage >= 0 && key.LightmapPage < _parsed.Lightmaps.Pages.Count )
		{
			var page = _parsed.Lightmaps.Pages[key.LightmapPage];
			if ( page.SkyA.Length > 0 && page.SkyB.Length > 0 )
			{
				if ( !_pages.TryGetValue( key.LightmapPage, out var runtime ) )
				{
					runtime = MapLoader.CreateLightmapPage( page, _parsed.Lightmaps.RealTimeLightPages, _parsed.WorldLights );
					_pages.Add( key.LightmapPage, runtime );
				}
				material = MapLoader.CreateLightmappedMaterial( $"{source.Description}/brush/lightmap/{_materials.Count}", material, runtime, _skyColor );
			}
		}
		_materials[key] = material;
		return material;
	}

	static bool ShouldSkip( string name ) => name?.Contains( "toolsnodraw", StringComparison.OrdinalIgnoreCase ) == true
		|| Titanfall2CollisionFilter.IsNonSolidToolName( name )
		|| name?.Contains( "toolsinvisible", StringComparison.OrdinalIgnoreCase ) == true
		|| name?.Contains( "toolsskybox", StringComparison.OrdinalIgnoreCase ) == true;
	static Vector3 ToSandbox( System.Numerics.Vector3 value ) => new( value.X, value.Y, value.Z );

	public void Dispose()
	{
		lock ( _gate )
		{
			if ( _disposed ) return;
			_disposed = true;
			foreach ( var texture in new RuntimeLightmaps( _pages.Values.ToArray(), _skyColor ).OwnedResources ) texture.Dispose();
			_pages.Clear();
			_materials.Clear();
			_parsed = null;
		}
	}
}

sealed class Titanfall2BrushModelLoader( Titanfall2BrushModelSource source, int index ) : ResourceLoader<Titanfall2Mount>
{
	Material[] _materials;
	(uint Contents, Model Model)[] _collisionGroups;
	internal (uint Contents, Model Model)[] CollisionGroups => _collisionGroups ??= source.BuildCollisionGroups( Path, index );
	protected override object Load()
	{
		var model = source.Build( Host, Path, index );
		_materials = model.Materials.ToArray();
		return model;
	}
}

public partial class Titanfall2Mount
{
	/// <summary>
	/// Adds the inline model's collision under <paramref name="owner"/> as one child per original
	/// contents value, tagged for <see cref="Titanfall2CollisionQueries"/>. Use this instead of a
	/// ModelCollider on the brush model when contents matter (player clip, titan clip, no-grapple).
	/// Children follow the owner's transform; returns the created colliders.
	/// </summary>
	public ModelCollider[] CreateBrushCollision( GameObject owner, string path, bool isStatic = false )
	{
		ArgumentNullException.ThrowIfNull( owner );
		ArgumentException.ThrowIfNullOrWhiteSpace( path );
		var resourcePath = path.StartsWith( "mount://", StringComparison.OrdinalIgnoreCase ) ? path : $"mount://{Ident}/{path.TrimStart( '/' )}";
		if ( !resourcePath.EndsWith( ".vmdl", StringComparison.OrdinalIgnoreCase ) ) resourcePath += ".vmdl";
		if ( GetByPath( resourcePath ) is not Titanfall2BrushModelLoader loader ) return [];
		var colliders = new List<ModelCollider>();
		foreach ( var (contents, model) in loader.CollisionGroups )
		{
			var child = new GameObject( owner, true, $"titanfall2_brush_contents_{contents:x8}" );
			child.IsStatic = isStatic;
			var collider = child.AddComponent<ModelCollider>();
			collider.Model = model;
			collider.Static = isStatic;
			var source = child.AddComponent<Titanfall2CollisionSource>();
			source.MountIdent = Ident; source.ModelPath = path; source.GeometrySource = "bsp_inline_model";
			source.HasEffectiveContents = true; source.EffectiveContents = contents;
			Titanfall2CollisionQueries.Classify( child, collider, contents );
			colliders.Add( collider );
		}
		return colliders.ToArray();
	}
}

[Sandbox.ModelEditor.GameData( "titanfall2_brush" )]
public sealed class Titanfall2BrushModelData
{
	public string SourcePath { get; set; }
	public int ModelIndex { get; set; }
	public Titanfall2BrushCollisionPart[] Parts { get; set; } = Array.Empty<Titanfall2BrushCollisionPart>();
}

public sealed class Titanfall2BrushCollisionPart
{
	public bool IsConvex { get; set; }
	public int SourceIndex { get; set; }
	public uint Contents { get; set; }
}
