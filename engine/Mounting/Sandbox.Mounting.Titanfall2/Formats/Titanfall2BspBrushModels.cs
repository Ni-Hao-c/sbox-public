using System.Text;

namespace Titanfall2;

public static partial class Titanfall2BspReader
{
	/// <summary>Lumps used by independent inline models, including their authored lightmap UVs.</summary>
	public static IReadOnlyList<int> RequiredBrushModelLumpIds => BrushLumps.Value;
	// Partial-file initialization order differs between SDK projects. Defer the
	// cross-part reference until every static field has finished initializing.
	static readonly Lazy<IReadOnlyList<int>> BrushLumps = new( () => RequiredCollisionLumpIds.Concat( new[]
	{
		TextureDataLumpId, VertexNormalsLumpId, TextureDataStringDataLumpId, TextureDataStringTableLumpId,
		VertexUnlitLumpId, VertexLitFlatLumpId, VertexLitBumpLumpId, VertexUnlitTsLumpId,
		MeshIndicesLumpId, MeshesLumpId, MaterialSortsLumpId, LightmapHeadersLumpId,
		LightmapDataSkyLumpId, LightmapDataRealTimeLightsLumpId, LightmapRealTimeLightPagesLumpId,
		WorldLightsLumpId, EntitiesLumpId
	} ).Distinct().ToArray() );

	public sealed class BspBrushModel
	{
		public int Index { get; init; }
		public BspWorld Geometry { get; init; } = new();
		public BspWorldCollision Collision { get; init; } = new();
	}

	internal static bool IsBrushLightingLump( int id ) => id is LightmapHeadersLumpId or LightmapDataSkyLumpId
		or LightmapDataRealTimeLightsLumpId or LightmapRealTimeLightPagesLumpId or WorldLightsLumpId;

	public sealed class BspBrushModels
	{
		public BspWorld WorldBounds { get; init; } = new();
		public IReadOnlyList<BspBrushModel> Models { get; init; } = Array.Empty<BspBrushModel>();
		public BspLightmaps Lightmaps { get; init; } = new();
		public IReadOnlyList<Titanfall2WorldLight> WorldLights { get; init; } = Array.Empty<Titanfall2WorldLight>();
		public IReadOnlyList<Dictionary<string, string>> Entities { get; init; } = Array.Empty<Dictionary<string, string>>();
	}

	public static int ReadBrushModelCount( byte[] modelLump )
	{
		if ( modelLump is null || modelLump.Length < 32 || modelLump.Length % 32 != 0 )
			throw new InvalidDataException( "Invalid BSP model table size." );
		return modelLump.Length / 32 - 1;
	}

	/// <summary>Returns the inline count without parsing geometry. Model 0 is the world.</summary>
	public static int ReadBrushModelCountFromBsp( byte[] bspBytes )
	{
		using var stream = new MemoryStream( bspBytes, writable: false );
		return ReadBrushModelCount( ReadLumpBytes( stream, ReadBrushHeaders( stream ), null, ModelsLumpId ) );
	}

	/// <summary>Reads model 1..N only. Retail render vertices, CM planes and tricoll vertices
	/// already use model-local coordinates: apply entity origin/angles in the game exactly once.
	/// Trigger and clip contents are retained; the caller owns collision/query policy.</summary>
	public static bool TryReadBrushModels( byte[] bspBytes, IReadOnlyDictionary<int, byte[]> overrideLumps,
		bool parseLightmaps, out BspBrushModels result, out string error )
	{
		result = null;
		error = null;
		try
		{
			using var stream = new MemoryStream( bspBytes, writable: false );
			var headers = ReadBrushHeaders( stream );
			// Read/copy each shared lump once, not once per door. Raw tables are released
			// when this parse finishes; the mount cache retains only inline geometry.
			var lumps = RequiredBrushModelLumpIds.Where( id => parseLightmaps || !IsBrushLightingLump( id ) ).ToDictionary( id => id,
				id => ReadLumpBytes( stream, headers, overrideLumps, id ) );
			var count = ReadBrushModelCount( lumps[ModelsLumpId] );
			var geometry = ReadModelGeometry( stream, headers, lumps );
			var models = new BspBrushModel[count];
			for ( var index = 1; index <= count; index++ )
			{
				var render = geometry( index );
				if ( render.Meshes.SelectMany( mesh => mesh.Vertices ).Any( vertex => !IsFinite( vertex.Position ) || !IsFinite( vertex.Normal ) ) )
					throw new InvalidDataException( $"Non-finite inline model {index} geometry." );
				var collision = ParseWorldCollision( stream, headers, lumps, index, playerOnly: false, includeParts: true );
				if ( collision.RejectedPrimitiveCount != 0 )
					throw new InvalidDataException( $"Incomplete inline model {index} collision: {collision.RejectedPrimitiveCount} rejected primitives." );
				models[index - 1] = new BspBrushModel { Index = index, Geometry = render, Collision = collision };
			}
			var lightmaps = parseLightmaps ? ReadLightmaps( lumps[LightmapHeadersLumpId], lumps[LightmapDataSkyLumpId],
				lumps[LightmapDataRealTimeLightsLumpId], lumps[LightmapRealTimeLightPagesLumpId] ) : new();
			// Keep page numbering, but do not retain the world's unused atlas images
			// for the lifetime of one independently loaded door.
			var usedPages = models.SelectMany( model => model.Geometry.Meshes ).Select( mesh => mesh.LightmapPage ).ToHashSet();
			lightmaps = new BspLightmaps
			{
				Pages = lightmaps.Pages.Select( ( page, index ) => usedPages.Contains( index ) ? page : new BspLightmapPage() ).ToArray(),
				RealTimeLightPages = lightmaps.RealTimeLightPages
			};
			result = new BspBrushModels
			{
				WorldBounds = new BspWorld { Mins = ReadVector3( lumps[ModelsLumpId], 0 ), Maxs = ReadVector3( lumps[ModelsLumpId], 12 ) },
				Models = models,
				Lightmaps = lightmaps,
				WorldLights = parseLightmaps ? Titanfall2WorldLight.Read( lumps[WorldLightsLumpId], headers[WorldLightsLumpId].Version ) : Array.Empty<Titanfall2WorldLight>(),
				Entities = Titanfall2EntityText.Parse( lumps[EntitiesLumpId] )
			};
			return true;
		}
		catch ( Exception exception )
		{
			error = $"Failed to read BSP inline models: {exception.Message}";
			return false;
		}
	}

	static LumpHeader[] ReadBrushHeaders( Stream stream )
	{
		using var reader = new BinaryReader( stream, Encoding.ASCII, leaveOpen: true );
		if ( stream.Length < 16 + HeaderCount * 16 || Encoding.ASCII.GetString( reader.ReadBytes( 4 ) ) != "rBSP" )
			throw new InvalidDataException( "Invalid BSP header." );
		reader.ReadInt32(); // version
		reader.ReadInt32(); // revision
		if ( reader.ReadInt32() != 127 ) throw new InvalidDataException( "Unsupported BSP lump count." );
		var headers = new LumpHeader[HeaderCount];
		for ( var index = 0; index < headers.Length; index++ )
			headers[index] = new LumpHeader( reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32() );
		return headers;
	}
}
