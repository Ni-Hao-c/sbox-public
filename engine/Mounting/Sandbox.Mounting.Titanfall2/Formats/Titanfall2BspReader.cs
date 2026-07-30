using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using NumericsVector2 = System.Numerics.Vector2;
using NumericsVector3 = System.Numerics.Vector3;

namespace Titanfall2;

public static partial class Titanfall2BspReader
{
	private const int HeaderCount = 128;
	private const int GameLumpId = 0x0023;
	private const int PakFileLumpId = 0x0028;
	private const int CubemapsLumpId = 0x002A;
	private const int TextureDataLumpId = 0x0002;
	private const int VerticesLumpId = 0x0003;
	private const int ModelsLumpId = 0x000E;
	private const int VertexNormalsLumpId = 0x001E;
	private const int TextureDataStringDataLumpId = 0x002B;
	private const int TextureDataStringTableLumpId = 0x002C;
	private const int VertexUnlitLumpId = 0x0047;
	private const int VertexLitFlatLumpId = 0x0048;
	private const int VertexLitBumpLumpId = 0x0049;
	private const int VertexUnlitTsLumpId = 0x004A;
	private const int MeshIndicesLumpId = 0x004F;
	private const int MeshesLumpId = 0x0050;
	private const int MaterialSortsLumpId = 0x0052;
	private const int CellBspNodesLumpId = 0x006A;
	private const int CellsLumpId = 0x006B;
	private const int PortalsLumpId = 0x006C;
	private const int PortalVerticesLumpId = 0x006D;
	private const int PortalVertexReferencesLumpId = 0x0070;
	private const int CellAabbNodesLumpId = 0x0077;
	private const int ObjectReferencesLumpId = 0x0078;

	public static IReadOnlyList<int> RequiredVisibilityLumpIds { get; } =
	[
		PlanesLumpId,
		CellBspNodesLumpId,
		CellsLumpId,
		PortalsLumpId,
		PortalVerticesLumpId,
		PortalVertexReferencesLumpId,
		CellAabbNodesLumpId,
		ObjectReferencesLumpId
	];
	private const uint MeshVertexMask = 0x600;
	private const uint MeshVertexLitFlat = 0x000;
	private const uint MeshVertexLitBump = 0x200;
	private const uint MeshVertexUnlit = 0x400;
	private const uint MeshVertexUnlitTs = 0x600;

	public sealed class ParsedBsp
	{
		public string FilePath { get; init; }
		public string MapName { get; init; }
		public int Version { get; init; }
		public int Revision { get; init; }
		public BspWorld World { get; init; } = new();
		public BspWorldCollision WorldCollision { get; init; } = new();
		public IReadOnlyList<StaticPropInstance> StaticProps { get; init; } = Array.Empty<StaticPropInstance>();
		public BspVisibility Visibility { get; init; } = new();
		public IReadOnlyList<BspCubemap> Cubemaps { get; init; } = Array.Empty<BspCubemap>();
		public byte[] EmbeddedCubemapVtf { get; init; } = Array.Empty<byte>();
	}

	public sealed class BspWorld
	{
		public IReadOnlyList<WorldMesh> Meshes { get; init; } = Array.Empty<WorldMesh>();
		public int SourceMeshCount { get; init; }
		public NumericsVector3 Mins { get; init; }
		public NumericsVector3 Maxs { get; init; }
	}

	public sealed class WorldMesh
	{
		public string MaterialName { get; init; }
		public int VisibilityObjectIndex { get; init; } = -1;
		public int Cubemap { get; init; } = -1;
		public WorldVertex[] Vertices { get; init; } = Array.Empty<WorldVertex>();
		public int[] Indices { get; init; } = Array.Empty<int>();
	}

	public readonly record struct BspCubemap( NumericsVector3 Origin, uint Flags );

	public readonly record struct WorldVertex(
		NumericsVector3 Position,
		NumericsVector3 Normal,
		NumericsVector2 TexCoord,
		WorldVertexColor Color );

	public readonly record struct WorldVertexColor( byte R, byte G, byte B, byte A );

	public readonly record struct VisibilityCellMask( ulong Low, ulong High )
	{
		public bool IsEmpty => Low == 0 && High == 0;

		public bool Contains( int cell ) => cell switch
		{
			>= 0 and < 64 => (Low & (1UL << cell)) != 0,
			>= 64 and < 128 => (High & (1UL << (cell - 64))) != 0,
			_ => false
		};

		public int FirstCell
		{
			get
			{
				if ( Low != 0 ) return System.Numerics.BitOperations.TrailingZeroCount( Low );
				return High != 0 ? 64 + System.Numerics.BitOperations.TrailingZeroCount( High ) : -1;
			}
		}
	}

	public sealed class BspVisibility
	{
		public int WorldMeshCount { get; init; }
		public IReadOnlyList<VisibilityPlane> Planes { get; init; } = Array.Empty<VisibilityPlane>();
		public IReadOnlyList<VisibilityBspNode> Nodes { get; init; } = Array.Empty<VisibilityBspNode>();
		public int CellCount { get; init; }
		public VisibilityCellMask AlwaysVisibleCells { get; init; }
		public IReadOnlyList<VisibilityPortal> Portals { get; init; } = Array.Empty<VisibilityPortal>();
		public IReadOnlyList<VisibilityCellMask> ObjectCellMasks { get; init; } = Array.Empty<VisibilityCellMask>();
		public bool IsValid => CellCount > 0 && CellCount <= 128 && Planes.Count > 0 && Nodes.Count > 0;

		public VisibilityCellMask GetObjectCellMask( int objectIndex ) =>
			objectIndex >= 0 && objectIndex < ObjectCellMasks.Count ? ObjectCellMasks[objectIndex] : default;
	}

	public readonly record struct VisibilityPlane( NumericsVector3 Normal, float Distance );
	public readonly record struct VisibilityBspNode( int Plane, int Child );
	public readonly record struct VisibilityPortal(
		int FromCell,
		int ToCell,
		NumericsVector3 Mins,
		NumericsVector3 Maxs,
		bool HasBounds );

	public sealed class StaticPropInstance
	{
		public int SourceIndex { get; init; } = -1;
		public string ModelPath { get; init; }
		public NumericsVector3 Origin { get; init; }
		public NumericsVector3 Angles { get; init; }
		public float Scale { get; init; } = 1f;
		public int Skin { get; init; }
		public byte SolidType { get; init; }
		public byte Flags { get; init; }
		public ushort Cubemap { get; init; }
		public float ForcedFadeScale { get; init; }
		public NumericsVector3 LightingOrigin { get; init; }
		public byte DiffuseModulationR { get; init; }
		public byte DiffuseModulationG { get; init; }
		public byte DiffuseModulationB { get; init; }
		public byte DiffuseModulationA { get; init; }
		public uint CollisionFlagsAdd { get; init; }
		public uint CollisionFlagsRemove { get; init; }
		// R2 can leave the legacy solid byte at zero and express the effective
		// collision contents through the add/remove masks that follow the prop.
		public bool IsCollidable => SolidType != 0 || CollisionFlagsAdd != 0;
	}

	private readonly record struct LumpHeader( int Offset, int Length, int Version, int FourCc );
	private readonly record struct ModelEntry( NumericsVector3 Mins, NumericsVector3 Maxs, int FirstMesh, int NumMeshes );
	private readonly record struct MeshEntry( int FirstMeshIndex, int NumTriangles, int MaterialSort, int Cubemap, uint Flags );
	private readonly record struct MaterialSortEntry( int TextureData, int VertexOffset );
	private readonly record struct TextureDataEntry( int NameIndex );
	private readonly record struct VisibilityCellEntry( int NumPortals, int FirstPortal, int Flags );
	private readonly record struct VisibilityPortalEntry( int Type, int NumEdges, int FirstReference, int Cell );
	private readonly record struct VisibilityAabbNode( int TotalObjectReferences, int FirstObjectReference );
	private readonly record struct VertexRef(
		int PositionIndex,
		int NormalIndex,
		NumericsVector2 AlbedoUv,
		WorldVertexColor Color );
	private readonly record struct GameLumpChildHeader( string Name, int Version, int Offset, int Length );

	public static bool TryRead( string filePath, out ParsedBsp parsedBsp, out string error )
	{
		parsedBsp = null;
		error = null;

		if ( string.IsNullOrWhiteSpace( filePath ) || !File.Exists( filePath ) )
		{
			error = $"BSP file not found:\n{filePath}";
			return false;
		}

		try
		{
			var overrideLumps = LoadExternalLumpOverrides( filePath );
			using var stream = File.OpenRead( filePath );
			return TryReadFromStream(
				stream,
				filePath,
				Path.GetFileNameWithoutExtension( filePath ) ?? "titanfall2_map",
				overrideLumps,
				parseWorldCollision: true,
				out parsedBsp,
				out error );
		}
		catch ( Exception exception )
		{
			error = $"Failed to read BSP:\n{exception.Message}";
			return false;
		}
	}

	public static bool TryRead( byte[] bspBytes, string mapName, out ParsedBsp parsedBsp, out string error )
	{
		return TryRead( bspBytes, mapName, new Dictionary<int, byte[]>(), out parsedBsp, out error );
	}

	public static bool TryRead( byte[] bspBytes, string mapName, IReadOnlyDictionary<int, byte[]> overrideLumps,
		out ParsedBsp parsedBsp, out string error )
	{
		return TryRead( bspBytes, mapName, overrideLumps, parseWorldCollision: true, out parsedBsp, out error );
	}

	public static bool TryRead( byte[] bspBytes, string mapName, IReadOnlyDictionary<int, byte[]> overrideLumps,
		bool parseWorldCollision, out ParsedBsp parsedBsp, out string error )
	{
		parsedBsp = null;
		error = null;
		if ( bspBytes == null || bspBytes.Length == 0 )
		{
			error = "BSP data is empty.";
			return false;
		}

		var resolvedMapName = string.IsNullOrWhiteSpace( mapName ) ? "titanfall2_map" : Path.GetFileNameWithoutExtension( mapName ) ?? "titanfall2_map";
		try
		{
			using var stream = new MemoryStream( bspBytes, writable: false );
			return TryReadFromStream(
				stream,
				resolvedMapName + ".bsp",
				resolvedMapName,
				overrideLumps ?? new Dictionary<int, byte[]>(),
				parseWorldCollision,
				out parsedBsp,
				out error );
		}
		catch ( Exception exception )
		{
			error = $"Failed to read BSP bytes:\n{exception.Message}";
			return false;
		}
	}

	private static bool TryReadFromStream(
		Stream stream,
		string filePath,
		string mapName,
		IReadOnlyDictionary<int, byte[]> overrideLumps,
		bool parseWorldCollision,
		out ParsedBsp parsedBsp,
		out string error )
	{
		parsedBsp = null;
		error = null;
		using var reader = new BinaryReader( stream, Encoding.ASCII, leaveOpen: true );
		var magic = Encoding.ASCII.GetString( reader.ReadBytes( 4 ) );
		if ( !string.Equals( magic, "rBSP", StringComparison.Ordinal ) )
		{
			error = $"Unsupported BSP magic: {magic}";
			return false;
		}

		int version = reader.ReadInt32();
		int revision = reader.ReadInt32();
		int lumpCount = reader.ReadInt32();
		if ( lumpCount != 127 )
		{
			error = $"Unsupported lump count: {lumpCount}";
			return false;
		}

		var headers = new LumpHeader[HeaderCount];
		for ( int i = 0; i < HeaderCount; i++ )
		{
			headers[i] = new LumpHeader(
				reader.ReadInt32(),
				reader.ReadInt32(),
				reader.ReadInt32(),
				reader.ReadInt32() );
		}

		var world = ParseWorld( stream, headers, overrideLumps );
		var worldCollision = parseWorldCollision
			? ParseWorldCollision( stream, headers, overrideLumps )
			: new BspWorldCollision();
		var staticProps = ParseStaticProps( stream, headers, overrideLumps );
		var cubemaps = ReadCubemaps( ReadLumpBytes( stream, headers, overrideLumps, CubemapsLumpId ) );
		var embeddedCubemapVtf = ReadEmbeddedCubemapVtf(
			ReadLumpBytes( stream, headers, overrideLumps, PakFileLumpId ), mapName );
		parsedBsp = new ParsedBsp
		{
			FilePath = filePath,
			MapName = mapName,
			Version = version,
			Revision = revision,
			World = world,
			WorldCollision = worldCollision,
			StaticProps = staticProps,
			Cubemaps = cubemaps,
			EmbeddedCubemapVtf = embeddedCubemapVtf
		};

		return true;
	}

	private static BspWorld ParseWorld( Stream stream, IReadOnlyList<LumpHeader> headers, IReadOnlyDictionary<int, byte[]> overrideLumps )
	{
		var models = ReadModels( ReadLumpBytes( stream, headers, overrideLumps, ModelsLumpId ) );
		if ( models.Length == 0 )
			return new BspWorld();

		var vertices = ReadVector3Array( ReadLumpBytes( stream, headers, overrideLumps, VerticesLumpId ) );
		var normals = ReadVector3Array( ReadLumpBytes( stream, headers, overrideLumps, VertexNormalsLumpId ) );
		var meshIndices = ReadUInt16Array( ReadLumpBytes( stream, headers, overrideLumps, MeshIndicesLumpId ) );
		var meshes = ReadMeshes( ReadLumpBytes( stream, headers, overrideLumps, MeshesLumpId ) );
		var materialSorts = ReadMaterialSorts( ReadLumpBytes( stream, headers, overrideLumps, MaterialSortsLumpId ) );
		var textureData = ReadTextureData( ReadLumpBytes( stream, headers, overrideLumps, TextureDataLumpId ) );
		var textureNames = ReadTextureNames(
			ReadLumpBytes( stream, headers, overrideLumps, TextureDataStringTableLumpId ),
			ReadLumpBytes( stream, headers, overrideLumps, TextureDataStringDataLumpId ) );

		var vertexUnlit = ReadVertexRefs( ReadLumpBytes( stream, headers, overrideLumps, VertexUnlitLumpId ), 20 );
		var vertexLitFlat = ReadVertexRefs( ReadLumpBytes( stream, headers, overrideLumps, VertexLitFlatLumpId ), 36 );
		var vertexLitBump = ReadVertexRefs( ReadLumpBytes( stream, headers, overrideLumps, VertexLitBumpLumpId ), 44 );
		var vertexUnlitTs = ReadVertexRefs( ReadLumpBytes( stream, headers, overrideLumps, VertexUnlitTsLumpId ), 28 );

		var worldModel = models[0];
		var worldMeshes = new List<WorldMesh>();
		for ( int meshIndex = 0; meshIndex < worldModel.NumMeshes; meshIndex++ )
		{
			int sourceMeshIndex = worldModel.FirstMesh + meshIndex;
			if ( sourceMeshIndex < 0 || sourceMeshIndex >= meshes.Length )
				continue;

			var worldMesh = BuildWorldMesh(
				meshes[sourceMeshIndex],
				meshIndex,
				meshIndices,
				vertices,
				normals,
				materialSorts,
				textureData,
				textureNames,
				vertexUnlit,
				vertexLitFlat,
				vertexLitBump,
				vertexUnlitTs );

			if ( worldMesh != null && worldMesh.Vertices.Length > 0 && worldMesh.Indices.Length > 0 )
				worldMeshes.Add( worldMesh );
		}

		return new BspWorld
		{
			Meshes = worldMeshes,
			SourceMeshCount = worldModel.NumMeshes,
			Mins = worldModel.Mins,
			Maxs = worldModel.Maxs
		};
	}

	private static BspVisibility ParseVisibility(
		Stream stream,
		IReadOnlyList<LumpHeader> headers,
		IReadOnlyDictionary<int, byte[]> overrideLumps,
		int worldMeshCount )
	{
		var planes = ReadVisibilityPlanes( ReadLumpBytes( stream, headers, overrideLumps, PlanesLumpId ) );
		var nodes = ReadVisibilityBspNodes( ReadLumpBytes( stream, headers, overrideLumps, CellBspNodesLumpId ) );
		var cells = ReadVisibilityCells( ReadLumpBytes( stream, headers, overrideLumps, CellsLumpId ) );
		var rawPortals = ReadVisibilityPortals( ReadLumpBytes( stream, headers, overrideLumps, PortalsLumpId ) );
		var portalVertices = ReadVector3Array( ReadLumpBytes( stream, headers, overrideLumps, PortalVerticesLumpId ) );
		var portalVertexReferences = ReadUInt16Array(
			ReadLumpBytes( stream, headers, overrideLumps, PortalVertexReferencesLumpId ) );
		var cellAabbRoots = ReadVisibilityAabbNodes(
			ReadLumpBytes( stream, headers, overrideLumps, CellAabbNodesLumpId ) );
		var objectReferences = ReadUInt16Array(
			ReadLumpBytes( stream, headers, overrideLumps, ObjectReferencesLumpId ) );

		if ( cells.Length == 0 || cells.Length > 128 || planes.Length == 0 || nodes.Length == 0 )
			return new BspVisibility { WorldMeshCount = worldMeshCount };

		var maximumObjectReference = objectReferences.Length > 0 ? objectReferences.Max( static value => (int)value ) : -1;
		var objectCount = Math.Max( Math.Max( worldMeshCount, maximumObjectReference + 1 ), 0 );
		var lowMasks = new ulong[objectCount];
		var highMasks = new ulong[objectCount];
		var rootCount = Math.Min( cells.Length, cellAabbRoots.Length );
		for ( var cellIndex = 0; cellIndex < rootCount; cellIndex++ )
		{
			var root = cellAabbRoots[cellIndex];
			var firstReference = Math.Max( 0, root.FirstObjectReference );
			var endReference = Math.Min( objectReferences.Length, firstReference + root.TotalObjectReferences );
			for ( var referenceIndex = firstReference; referenceIndex < endReference; referenceIndex++ )
			{
				var objectIndex = objectReferences[referenceIndex];
				if ( objectIndex >= objectCount ) continue;
				if ( cellIndex < 64 ) lowMasks[objectIndex] |= 1UL << cellIndex;
				else highMasks[objectIndex] |= 1UL << (cellIndex - 64);
			}
		}

		var objectMasks = new VisibilityCellMask[objectCount];
		for ( var objectIndex = 0; objectIndex < objectMasks.Length; objectIndex++ )
			objectMasks[objectIndex] = new VisibilityCellMask( lowMasks[objectIndex], highMasks[objectIndex] );

		var portals = new List<VisibilityPortal>();
		ulong alwaysVisibleLow = 0;
		ulong alwaysVisibleHigh = 0;
		for ( var fromCell = 0; fromCell < cells.Length; fromCell++ )
		{
			var cell = cells[fromCell];
			if ( cell.Flags is 3 or 5 )
			{
				if ( fromCell < 64 ) alwaysVisibleLow |= 1UL << fromCell;
				else alwaysVisibleHigh |= 1UL << (fromCell - 64);
			}
			var firstPortal = Math.Max( 0, cell.FirstPortal );
			var endPortal = Math.Min( rawPortals.Length, firstPortal + Math.Max( 0, cell.NumPortals ) );
			for ( var portalIndex = firstPortal; portalIndex < endPortal; portalIndex++ )
			{
				var portal = rawPortals[portalIndex];
				if ( portal.Type != 0 || portal.Cell < 0 || portal.Cell >= cells.Length || portal.Cell == fromCell ) continue;

				var mins = new NumericsVector3( float.MaxValue );
				var maxs = new NumericsVector3( float.MinValue );
				var validVertices = 0;
				var firstVertexReference = Math.Max( 0, portal.FirstReference );
				var endVertexReference = Math.Min(
					portalVertexReferences.Length,
					firstVertexReference + Math.Max( 0, portal.NumEdges ) );
				for ( var referenceIndex = firstVertexReference; referenceIndex < endVertexReference; referenceIndex++ )
				{
					var vertexIndex = portalVertexReferences[referenceIndex];
					if ( vertexIndex >= portalVertices.Length ) continue;
					mins = NumericsVector3.Min( mins, portalVertices[vertexIndex] );
					maxs = NumericsVector3.Max( maxs, portalVertices[vertexIndex] );
					validVertices++;
				}

				portals.Add( new VisibilityPortal(
					fromCell,
					portal.Cell,
					validVertices > 0 ? mins : default,
					validVertices > 0 ? maxs : default,
					validVertices > 0 ) );
			}
		}

		return new BspVisibility
		{
			WorldMeshCount = worldMeshCount,
			Planes = planes,
			Nodes = nodes,
			CellCount = cells.Length,
			AlwaysVisibleCells = new VisibilityCellMask( alwaysVisibleLow, alwaysVisibleHigh ),
			Portals = portals,
			ObjectCellMasks = objectMasks
		};
	}

	private static IReadOnlyList<StaticPropInstance> ParseStaticProps( Stream stream, IReadOnlyList<LumpHeader> headers, IReadOnlyDictionary<int, byte[]> overrideLumps )
	{
		var gameLumpHeader = headers[GameLumpId];
		var gameLumpBytes = ReadLumpBytes( stream, headers, overrideLumps, GameLumpId );
		if ( gameLumpBytes.Length == 0 )
			return Array.Empty<StaticPropInstance>();

		using var memoryStream = new MemoryStream( gameLumpBytes, writable: false );
		using var reader = new BinaryReader( memoryStream, Encoding.ASCII, leaveOpen: true );
		if ( memoryStream.Length < 4 )
			return Array.Empty<StaticPropInstance>();

		int childCount = reader.ReadInt32();
		var childHeaders = new List<GameLumpChildHeader>( childCount );
		for ( int i = 0; i < childCount; i++ )
		{
			var idBytes = reader.ReadBytes( 4 );
			var name = new string( idBytes.Reverse().Select( b => (char)b ).ToArray() );
			int flags = reader.ReadUInt16();
			int version = reader.ReadUInt16();
			int offset = reader.ReadInt32();
			int length = reader.ReadInt32();
			_ = flags;
			childHeaders.Add( new GameLumpChildHeader( name, version, offset, length ) );
		}

		var sprpHeader = childHeaders.FirstOrDefault( x => string.Equals( x.Name, "sprp", StringComparison.OrdinalIgnoreCase ) );
		if ( string.IsNullOrWhiteSpace( sprpHeader.Name ) || sprpHeader.Version != 13 || sprpHeader.Length <= 0 )
			return Array.Empty<StaticPropInstance>();

		int relativeOffset = sprpHeader.Offset - gameLumpHeader.Offset;
		if ( relativeOffset < 0 || relativeOffset + sprpHeader.Length > gameLumpBytes.Length )
			return Array.Empty<StaticPropInstance>();

		memoryStream.Position = relativeOffset;
		int modelNameCount = reader.ReadInt32();
		var modelNames = new string[modelNameCount];
		for ( int i = 0; i < modelNameCount; i++ )
		{
			modelNames[i] = ReadFixedString( reader.ReadBytes( 128 ) );
		}

		int propCount = reader.ReadInt32();
		int unknown1 = reader.ReadInt32();
		int unknown2 = reader.ReadInt32();
		_ = unknown1;
		_ = unknown2;

		var props = new List<StaticPropInstance>( Math.Max( propCount, 0 ) );
		for ( int i = 0; i < propCount; i++ )
		{
			var origin = ReadVector3( reader );
			float pitch = reader.ReadSingle();
			float yaw = reader.ReadSingle();
			float roll = reader.ReadSingle();
			float scale = reader.ReadSingle();
			int modelNameIndex = reader.ReadUInt16();
			byte solidType = reader.ReadByte();
			byte flags = reader.ReadByte();
			int skin = reader.ReadUInt16();
			ushort cubemap = reader.ReadUInt16();
			float forcedFadeScale = reader.ReadSingle();
			var lightingOrigin = ReadVector3( reader );
			byte diffuseModulationR = reader.ReadByte();
			byte diffuseModulationG = reader.ReadByte();
			byte diffuseModulationB = reader.ReadByte();
			byte diffuseModulationA = reader.ReadByte();
			uint collisionFlagsAdd = reader.ReadUInt32();
			uint collisionFlagsRemove = reader.ReadUInt32();

			if ( modelNameIndex < 0 || modelNameIndex >= modelNames.Length )
				continue;

			var modelPath = NormalizeAssetPath( modelNames[modelNameIndex] );
			if ( string.IsNullOrWhiteSpace( modelPath ) )
				continue;

			props.Add( new StaticPropInstance
			{
				SourceIndex = i,
				ModelPath = modelPath,
				Origin = origin,
				Angles = new NumericsVector3( pitch, yaw, roll ),
				Scale = scale <= 0 ? 1f : scale,
				Skin = skin,
				SolidType = solidType,
				Flags = flags,
				Cubemap = cubemap,
				ForcedFadeScale = forcedFadeScale,
				LightingOrigin = lightingOrigin,
				DiffuseModulationR = diffuseModulationR,
				DiffuseModulationG = diffuseModulationG,
				DiffuseModulationB = diffuseModulationB,
				DiffuseModulationA = diffuseModulationA,
				CollisionFlagsAdd = collisionFlagsAdd,
				CollisionFlagsRemove = collisionFlagsRemove
			} );
		}

		return props;
	}

	private static WorldMesh BuildWorldMesh(
		MeshEntry mesh,
		int visibilityObjectIndex,
		IReadOnlyList<ushort> meshIndices,
		IReadOnlyList<NumericsVector3> positions,
		IReadOnlyList<NumericsVector3> normals,
		IReadOnlyList<MaterialSortEntry> materialSorts,
		IReadOnlyList<TextureDataEntry> textureData,
		IReadOnlyList<string> textureNames,
		IReadOnlyList<VertexRef> vertexUnlit,
		IReadOnlyList<VertexRef> vertexLitFlat,
		IReadOnlyList<VertexRef> vertexLitBump,
		IReadOnlyList<VertexRef> vertexUnlitTs )
	{
		if ( mesh.MaterialSort < 0 || mesh.MaterialSort >= materialSorts.Count )
			return null;

		var materialSort = materialSorts[mesh.MaterialSort];
		string materialName = ResolveMaterialName( materialSort, textureData, textureNames );
		int triangleIndexCount = mesh.NumTriangles * 3;
		if ( triangleIndexCount <= 0 )
			return null;

		int startIndex = mesh.FirstMeshIndex;
		int endIndex = Math.Min( startIndex + triangleIndexCount, meshIndices.Count );
		if ( startIndex < 0 || startIndex >= endIndex )
			return null;

		var vertices = new List<WorldVertex>( endIndex - startIndex );
		var indices = new List<int>( endIndex - startIndex );
		for ( int index = startIndex; index + 2 < endIndex; index += 3 )
		{
			if ( !TryResolveWorldVertex( mesh.Flags, materialSort.VertexOffset + meshIndices[index], positions, normals, vertexUnlit, vertexLitFlat, vertexLitBump, vertexUnlitTs, out var vertex0 ) ||
				!TryResolveWorldVertex( mesh.Flags, materialSort.VertexOffset + meshIndices[index + 1], positions, normals, vertexUnlit, vertexLitFlat, vertexLitBump, vertexUnlitTs, out var vertex1 ) ||
				!TryResolveWorldVertex( mesh.Flags, materialSort.VertexOffset + meshIndices[index + 2], positions, normals, vertexUnlit, vertexLitFlat, vertexLitBump, vertexUnlitTs, out var vertex2 ) )
			{
				continue;
			}

			int baseIndex = vertices.Count;
			vertices.Add( vertex0 );
			vertices.Add( vertex1 );
			vertices.Add( vertex2 );
			indices.Add( baseIndex );
			indices.Add( baseIndex + 1 );
			indices.Add( baseIndex + 2 );
		}

		if ( vertices.Count == 0 )
			return null;

		return new WorldMesh
		{
			MaterialName = materialName,
			VisibilityObjectIndex = visibilityObjectIndex,
			Cubemap = mesh.Cubemap,
			Vertices = vertices.ToArray(),
			Indices = indices.ToArray()
		};
	}

	private static string ResolveMaterialName( MaterialSortEntry materialSort, IReadOnlyList<TextureDataEntry> textureData, IReadOnlyList<string> textureNames )
	{
		if ( materialSort.TextureData < 0 || materialSort.TextureData >= textureData.Count )
			return "world/default";

		int nameIndex = textureData[materialSort.TextureData].NameIndex;
		if ( nameIndex < 0 || nameIndex >= textureNames.Count )
			return "world/default";

		var materialName = NormalizeAssetPath( textureNames[nameIndex] );
		return string.IsNullOrWhiteSpace( materialName ) ? "world/default" : materialName;
	}

	private static bool TryResolveWorldVertex(
		uint meshFlags,
		int vertexIndex,
		IReadOnlyList<NumericsVector3> positions,
		IReadOnlyList<NumericsVector3> normals,
		IReadOnlyList<VertexRef> vertexUnlit,
		IReadOnlyList<VertexRef> vertexLitFlat,
		IReadOnlyList<VertexRef> vertexLitBump,
		IReadOnlyList<VertexRef> vertexUnlitTs,
		out WorldVertex worldVertex )
	{
		worldVertex = default;

		var vertexType = meshFlags & MeshVertexMask;
		VertexRef vertexRef;
		if ( vertexType == MeshVertexLitBump )
		{
			if ( vertexIndex < 0 || vertexIndex >= vertexLitBump.Count )
				return false;

			vertexRef = vertexLitBump[vertexIndex];
		}
		else if ( vertexType == MeshVertexUnlit )
		{
			if ( vertexIndex < 0 || vertexIndex >= vertexUnlit.Count )
				return false;

			vertexRef = vertexUnlit[vertexIndex];
		}
		else if ( vertexType == MeshVertexUnlitTs )
		{
			if ( vertexIndex < 0 || vertexIndex >= vertexUnlitTs.Count )
				return false;

			vertexRef = vertexUnlitTs[vertexIndex];
		}
		else
		{
			if ( vertexIndex < 0 || vertexIndex >= vertexLitFlat.Count )
				return false;

			vertexRef = vertexLitFlat[vertexIndex];
		}

		if ( vertexRef.PositionIndex < 0 || vertexRef.PositionIndex >= positions.Count )
			return false;

		var position = positions[vertexRef.PositionIndex];
		var normal = vertexRef.NormalIndex >= 0 && vertexRef.NormalIndex < normals.Count
			? normals[vertexRef.NormalIndex]
			: new NumericsVector3( 0f, 0f, 1f );

		worldVertex = new WorldVertex( position, normal, vertexRef.AlbedoUv, vertexRef.Color );
		return true;
	}

	private static IReadOnlyDictionary<int, byte[]> LoadExternalLumpOverrides( string bspFilePath )
	{
		var overrides = new Dictionary<int, byte[]>();
		var directoryPath = Path.GetDirectoryName( bspFilePath );
		var fileName = Path.GetFileName( bspFilePath );
		if ( string.IsNullOrWhiteSpace( directoryPath ) || string.IsNullOrWhiteSpace( fileName ) )
			return overrides;

		int[] renderLumps =
		[
			TextureDataLumpId,
			VerticesLumpId,
			ModelsLumpId,
			VertexNormalsLumpId,
			GameLumpId,
			TextureDataStringDataLumpId,
			TextureDataStringTableLumpId,
			VertexUnlitLumpId,
			VertexLitFlatLumpId,
			VertexLitBumpLumpId,
			VertexUnlitTsLumpId,
			MeshIndicesLumpId,
			MeshesLumpId,
			MaterialSortsLumpId,
			PlanesLumpId,
			CellBspNodesLumpId,
			CellsLumpId,
			PortalsLumpId,
			PortalVerticesLumpId,
			PortalVertexReferencesLumpId,
			CellAabbNodesLumpId,
			ObjectReferencesLumpId
		];

		foreach ( int lumpId in renderLumps.Concat( RequiredCollisionLumpIds ).Distinct() )
		{
			var clientOverridePath = Path.Combine( directoryPath, $"{fileName}.{lumpId:X4}.bsp_lump.client" );
			var overridePath = Path.Combine( directoryPath, $"{fileName}.{lumpId:X4}.bsp_lump" );
			if ( File.Exists( clientOverridePath ) )
			{
				overrides[lumpId] = File.ReadAllBytes( clientOverridePath );
			}
			else if ( File.Exists( overridePath ) )
			{
				overrides[lumpId] = File.ReadAllBytes( overridePath );
			}
		}

		return overrides;
	}

	private static byte[] ReadLumpBytes( Stream stream, IReadOnlyList<LumpHeader> headers, IReadOnlyDictionary<int, byte[]> overrideLumps, int lumpId )
	{
		if ( overrideLumps.TryGetValue( lumpId, out var overrideBytes ) )
			return overrideBytes ?? Array.Empty<byte>();

		if ( lumpId < 0 || lumpId >= headers.Count )
			return Array.Empty<byte>();

		var header = headers[lumpId];
		if ( header.Length <= 0 || header.Offset < 0 )
			return Array.Empty<byte>();

		var bytes = new byte[header.Length];
		stream.Position = header.Offset;
		int bytesRead = stream.Read( bytes, 0, bytes.Length );
		if ( bytesRead == bytes.Length )
			return bytes;

		if ( bytesRead <= 0 )
			return Array.Empty<byte>();

		Array.Resize( ref bytes, bytesRead );
		return bytes;
	}

	private static ModelEntry[] ReadModels( byte[] bytes )
	{
		const int stride = 32;
		if ( bytes.Length < stride )
			return Array.Empty<ModelEntry>();

		var models = new ModelEntry[bytes.Length / stride];
		using var stream = new MemoryStream( bytes, writable: false );
		using var reader = new BinaryReader( stream, Encoding.ASCII, leaveOpen: true );
		for ( int i = 0; i < models.Length; i++ )
		{
			models[i] = new ModelEntry(
				ReadVector3( reader ),
				ReadVector3( reader ),
				reader.ReadInt32(),
				reader.ReadInt32() );
		}

		return models;
	}

	private static VisibilityPlane[] ReadVisibilityPlanes( byte[] bytes )
	{
		const int stride = 16;
		if ( bytes.Length < stride ) return Array.Empty<VisibilityPlane>();
		var planes = new VisibilityPlane[bytes.Length / stride];
		using var stream = new MemoryStream( bytes, writable: false );
		using var reader = new BinaryReader( stream, Encoding.ASCII, leaveOpen: true );
		for ( var index = 0; index < planes.Length; index++ )
			planes[index] = new VisibilityPlane( ReadVector3( reader ), reader.ReadSingle() );
		return planes;
	}

	private static VisibilityBspNode[] ReadVisibilityBspNodes( byte[] bytes )
	{
		const int stride = 8;
		if ( bytes.Length < stride ) return Array.Empty<VisibilityBspNode>();
		var nodes = new VisibilityBspNode[bytes.Length / stride];
		using var stream = new MemoryStream( bytes, writable: false );
		using var reader = new BinaryReader( stream, Encoding.ASCII, leaveOpen: true );
		for ( var index = 0; index < nodes.Length; index++ )
			nodes[index] = new VisibilityBspNode( reader.ReadInt32(), reader.ReadInt32() );
		return nodes;
	}

	private static VisibilityCellEntry[] ReadVisibilityCells( byte[] bytes )
	{
		const int stride = 8;
		if ( bytes.Length < stride ) return Array.Empty<VisibilityCellEntry>();
		var cells = new VisibilityCellEntry[bytes.Length / stride];
		using var stream = new MemoryStream( bytes, writable: false );
		using var reader = new BinaryReader( stream, Encoding.ASCII, leaveOpen: true );
		for ( var index = 0; index < cells.Length; index++ )
		{
			var numPortals = reader.ReadInt16();
			var firstPortal = reader.ReadInt16();
			var flags = reader.ReadInt16();
			reader.ReadInt16();
			cells[index] = new VisibilityCellEntry( numPortals, firstPortal, flags );
		}
		return cells;
	}

	private static VisibilityPortalEntry[] ReadVisibilityPortals( byte[] bytes )
	{
		const int stride = 12;
		if ( bytes.Length < stride ) return Array.Empty<VisibilityPortalEntry>();
		var portals = new VisibilityPortalEntry[bytes.Length / stride];
		using var stream = new MemoryStream( bytes, writable: false );
		using var reader = new BinaryReader( stream, Encoding.ASCII, leaveOpen: true );
		for ( var index = 0; index < portals.Length; index++ )
		{
			reader.ReadByte();
			var type = reader.ReadByte();
			var numEdges = reader.ReadByte();
			reader.ReadByte();
			var firstReference = reader.ReadInt16();
			var cell = reader.ReadInt16();
			reader.ReadInt32();
			portals[index] = new VisibilityPortalEntry( type, numEdges, firstReference, cell );
		}
		return portals;
	}

	private static VisibilityAabbNode[] ReadVisibilityAabbNodes( byte[] bytes )
	{
		const int stride = 32;
		if ( bytes.Length < stride ) return Array.Empty<VisibilityAabbNode>();
		var nodes = new VisibilityAabbNode[bytes.Length / stride];
		using var stream = new MemoryStream( bytes, writable: false );
		using var reader = new BinaryReader( stream, Encoding.ASCII, leaveOpen: true );
		for ( var index = 0; index < nodes.Length; index++ )
		{
			reader.ReadBytes( 12 );
			reader.ReadByte();
			reader.ReadByte();
			var totalObjectReferences = reader.ReadUInt16();
			reader.ReadBytes( 12 );
			reader.ReadUInt16();
			var firstObjectReference = reader.ReadUInt16();
			nodes[index] = new VisibilityAabbNode( totalObjectReferences, firstObjectReference );
		}
		return nodes;
	}

	private static MeshEntry[] ReadMeshes( byte[] bytes )
	{
		const int stride = 28;
		if ( bytes.Length < stride )
			return Array.Empty<MeshEntry>();

		var meshes = new MeshEntry[bytes.Length / stride];
		using var stream = new MemoryStream( bytes, writable: false );
		using var reader = new BinaryReader( stream, Encoding.ASCII, leaveOpen: true );
		for ( int i = 0; i < meshes.Length; i++ )
		{
			int firstMeshIndex = reader.ReadInt32();
			int numTriangles = reader.ReadUInt16();
			reader.ReadUInt16();
			reader.ReadUInt16();
			reader.ReadSByte(); // Vertex type; mesh flags are authoritative for this loader.
			int cubemap = reader.ReadSByte();
			reader.ReadSByte();
			reader.ReadSByte();
			reader.ReadSByte();
			reader.ReadSByte();
			reader.ReadInt16();
			reader.ReadInt16();
			reader.ReadByte();
			reader.ReadByte();
			int materialSort = reader.ReadUInt16();
			uint flags = reader.ReadUInt32();

			meshes[i] = new MeshEntry( firstMeshIndex, numTriangles, materialSort, cubemap, flags );
		}

		return meshes;
	}

	private static BspCubemap[] ReadCubemaps( byte[] bytes )
	{
		const int stride = 16;
		if ( bytes.Length < stride ) return Array.Empty<BspCubemap>();
		var cubemaps = new BspCubemap[bytes.Length / stride];
		using var stream = new MemoryStream( bytes, writable: false );
		using var reader = new BinaryReader( stream, Encoding.ASCII, leaveOpen: true );
		for ( var index = 0; index < cubemaps.Length; index++ )
		{
			cubemaps[index] = new BspCubemap(
				new NumericsVector3( reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32() ),
				reader.ReadUInt32() );
		}
		return cubemaps;
	}

	private static byte[] ReadEmbeddedCubemapVtf( byte[] pakBytes, string mapName )
	{
		const int maximumCubemapBytes = 256 * 1024 * 1024;
		if ( pakBytes.Length < 22 ) return Array.Empty<byte>();
		try
		{
			using var stream = new MemoryStream( pakBytes, writable: false );
			using var archive = new ZipArchive( stream, ZipArchiveMode.Read, leaveOpen: false );
			var normalizedMap = (mapName ?? string.Empty).Replace( '\\', '/' ).Trim( '/' );
			var expected = $"materials/maps/{normalizedMap}/cubemaps.hdr.vtf";
			var entry = archive.Entries.FirstOrDefault( candidate =>
				candidate.FullName.Equals( expected, StringComparison.OrdinalIgnoreCase) )
				?? archive.Entries.FirstOrDefault( candidate =>
					candidate.FullName.EndsWith( "/cubemaps.hdr.vtf", StringComparison.OrdinalIgnoreCase ) );
			if ( entry is null || entry.Length <= 0 || entry.Length > maximumCubemapBytes ) return Array.Empty<byte>();
			using var input = entry.Open();
			using var output = new MemoryStream( checked((int)entry.Length) );
			input.CopyTo( output );
			return output.ToArray();
		}
		catch
		{
			return Array.Empty<byte>();
		}
	}

	private static MaterialSortEntry[] ReadMaterialSorts( byte[] bytes )
	{
		const int stride = 12;
		if ( bytes.Length < stride )
			return Array.Empty<MaterialSortEntry>();

		var materialSorts = new MaterialSortEntry[bytes.Length / stride];
		using var stream = new MemoryStream( bytes, writable: false );
		using var reader = new BinaryReader( stream, Encoding.ASCII, leaveOpen: true );
		for ( int i = 0; i < materialSorts.Length; i++ )
		{
			int textureData = reader.ReadInt16();
			reader.ReadInt16();
			reader.ReadInt16();
			reader.ReadInt16();
			int vertexOffset = reader.ReadInt32();
			materialSorts[i] = new MaterialSortEntry( textureData, vertexOffset );
		}

		return materialSorts;
	}

	private static TextureDataEntry[] ReadTextureData( byte[] bytes )
	{
		const int stride = 36;
		if ( bytes.Length < stride )
			return Array.Empty<TextureDataEntry>();

		var textureData = new TextureDataEntry[bytes.Length / stride];
		using var stream = new MemoryStream( bytes, writable: false );
		using var reader = new BinaryReader( stream, Encoding.ASCII, leaveOpen: true );
		for ( int i = 0; i < textureData.Length; i++ )
		{
			reader.ReadSingle();
			reader.ReadSingle();
			reader.ReadSingle();
			int nameIndex = reader.ReadInt32();
			reader.ReadInt32();
			reader.ReadInt32();
			reader.ReadInt32();
			reader.ReadInt32();
			reader.ReadInt32();
			textureData[i] = new TextureDataEntry( nameIndex );
		}

		return textureData;
	}

	private static VertexRef[] ReadVertexRefs( byte[] bytes, int stride )
	{
		if ( bytes.Length < stride || stride < 20 )
			return Array.Empty<VertexRef>();

		var vertexRefs = new VertexRef[bytes.Length / stride];
		using var stream = new MemoryStream( bytes, writable: false );
		using var reader = new BinaryReader( stream, Encoding.ASCII, leaveOpen: true );
		for ( int i = 0; i < vertexRefs.Length; i++ )
		{
			int positionIndex = reader.ReadInt32();
			int normalIndex = reader.ReadInt32();
			float u = reader.ReadSingle();
			float v = reader.ReadSingle();
			var color = new WorldVertexColor(
				reader.ReadByte(),
				reader.ReadByte(),
				reader.ReadByte(),
				reader.ReadByte() );
			if ( stride > 20 )
				reader.ReadBytes( stride - 20 );

			vertexRefs[i] = new VertexRef( positionIndex, normalIndex, new NumericsVector2( u, 1f - v ), color );
		}

		return vertexRefs;
	}

	private static NumericsVector3[] ReadVector3Array( byte[] bytes )
	{
		const int stride = 12;
		if ( bytes.Length < stride )
			return Array.Empty<NumericsVector3>();

		var vectors = new NumericsVector3[bytes.Length / stride];
		using var stream = new MemoryStream( bytes, writable: false );
		using var reader = new BinaryReader( stream, Encoding.ASCII, leaveOpen: true );
		for ( int i = 0; i < vectors.Length; i++ )
		{
			vectors[i] = ReadVector3( reader );
		}

		return vectors;
	}

	private static ushort[] ReadUInt16Array( byte[] bytes )
	{
		if ( bytes.Length < 2 )
			return Array.Empty<ushort>();

		var values = new ushort[bytes.Length / 2];
		using var stream = new MemoryStream( bytes, writable: false );
		using var reader = new BinaryReader( stream, Encoding.ASCII, leaveOpen: true );
		for ( int i = 0; i < values.Length; i++ )
		{
			values[i] = reader.ReadUInt16();
		}

		return values;
	}

	private static string[] ReadTextureNames( byte[] stringTableBytes, byte[] stringDataBytes )
	{
		if ( stringTableBytes.Length < 4 || stringDataBytes.Length == 0 )
			return Array.Empty<string>();

		var names = new List<string>( stringTableBytes.Length / 4 );
		using var stream = new MemoryStream( stringTableBytes, writable: false );
		using var reader = new BinaryReader( stream, Encoding.ASCII, leaveOpen: true );
		while ( stream.Position + 4 <= stream.Length )
		{
			int offset = reader.ReadInt32();
			if ( offset < 0 || offset >= stringDataBytes.Length )
			{
				names.Add( string.Empty );
				continue;
			}

			int end = offset;
			while ( end < stringDataBytes.Length && stringDataBytes[end] != 0 )
				end++;

			names.Add( Encoding.UTF8.GetString( stringDataBytes, offset, end - offset ) );
		}

		return names.ToArray();
	}

	private static NumericsVector3 ReadVector3( BinaryReader reader )
	{
		return new NumericsVector3( reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle() );
	}

	private static string ReadFixedString( byte[] bytes )
	{
		if ( bytes == null || bytes.Length == 0 )
			return string.Empty;

		int terminatorIndex = Array.IndexOf( bytes, (byte)0 );
		int length = terminatorIndex >= 0 ? terminatorIndex : bytes.Length;
		return Encoding.UTF8.GetString( bytes, 0, length );
	}

	private static string NormalizeAssetPath( string path )
	{
		return string.IsNullOrWhiteSpace( path )
			? string.Empty
			: path.Replace( '\\', '/' ).Trim().TrimStart( '/' );
	}
}
