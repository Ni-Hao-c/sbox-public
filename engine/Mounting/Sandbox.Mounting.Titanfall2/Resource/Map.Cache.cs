using System.Buffers.Binary;
using System.Diagnostics;
using Titanfall2;
using NumericsVector3 = System.Numerics.Vector3;
using NumericsVector4 = System.Numerics.Vector4;

/// <summary>
/// Processed-cache identity of a map's BSP source and the on-disk cache of one rBSP parse.
/// The cached payload is plain managed data (no engine objects), so a hit skips the VPK read,
/// the LZHAM decompress and <c>Titanfall2BspReader.TryRead</c> entirely; a corrupt,
/// mismatched or unreadable entry is always treated as a miss and the map is parsed normally.
/// </summary>
static class Titanfall2MapCache
{
	/// <summary>Processed-cache identity shared by the per-map derived data (prop fade distances).</summary>
	internal static string Identity( Titanfall2Mount host, ITitanfall2AssetSource source ) =>
		$"{host.ContentFingerprint}|{source.CacheIdentity}|bsp-default-cm-index21-fog-v3";

	// The parse payload is its own format: its magic/version/tag are independent of the T2FC
	// container and of every other cache kind, so one kind can never be read as another.
	internal const uint PayloadMagic = 0x504D3254; // "T2MP"
	internal const int PayloadVersion = 1;
	internal const string PayloadTag = "map-parse-v1";
	const string Kind = "map";

	// Uncompressed payload ceiling. CacheReader can hand out at most 768 MiB per entry, and a map
	// this large would also evict most of the texture cache, so it is cheaper to parse it again.
	internal const long MaximumPayloadBytes = 384L * 1024 * 1024;

	// Lumps the parse does not keep but that other map builders need afterwards. Retail ships
	// them next to the .bsp entry, mirroring what ReadCollisionLumpOverrides already relies on.
	const int LightProbesLumpId = 0x0065;
	const int StaticPropLightProbeIndicesLumpId = 0x0066;
	const int LightProbeTreeLumpId = 0x0067;
	const int LightProbeReferencesLumpId = 0x0068;

	/// <summary>
	/// Key of one parse: the mount's content fingerprint (module id + game file size/mtime) already
	/// covers the converter and the game data, and the source identity covers the imported file.
	/// Both optional parse modes are part of the key because they change the payload.
	/// </summary>
	internal static string ParseIdentity( Titanfall2Mount host, ITitanfall2AssetSource source, bool lightmaps ) =>
		$"{host.ContentFingerprint}|{source?.CacheIdentity}|{PayloadTag}|collision:all-contents|lightmaps:{lightmaps}";

	/// <summary>Raw light probe lumps captured for the payload; null means the parse had no lookup.</summary>
	internal sealed record ProbeLumpData( byte[] Probes, byte[] Tree, byte[] References );

	/// <summary>One completed cache read, or the reason it was not usable.</summary>
	internal readonly record struct MapParseCacheRead(
		Titanfall2BspReader.ParsedBsp Parsed,
		string MissReason,
		long FileBytes,
		double Seconds );

	/// <summary>
	/// Starts the payload read and decode on a worker thread when an entry exists. Returns null when
	/// caching is disabled or there is no entry, so the common first load pays nothing for it.
	/// </summary>
	internal static Task<MapParseCacheRead> BeginRead( string identity )
	{
		if ( !Titanfall2StreamingSettings.ProcessedCache || string.IsNullOrEmpty( identity ) ) return null;
		try
		{
			if ( !File.Exists( Titanfall2ProcessedCache.GetPath( Kind, identity ) ) ) return null;
		}
		catch ( Exception )
		{
			return null;
		}

		return Task.Run( () =>
		{
			try
			{
				return Deserialize( identity );
			}
			catch ( Exception exception )
			{
				return new MapParseCacheRead( null, exception.Message, 0, 0 );
			}
		} );
	}

	/// <summary>Joins a pending read (never throws) and reports whether it produced a parse.</summary>
	internal static bool TryTake( Task<MapParseCacheRead> pending, out MapParseCacheRead read )
	{
		if ( pending is null )
		{
			read = new MapParseCacheRead( null, "no cache entry", 0, 0 );
			return false;
		}

		try
		{
			read = pending.GetAwaiter().GetResult();
		}
		catch ( Exception exception )
		{
			read = new MapParseCacheRead( null, exception.Message, 0, 0 );
			return false;
		}

		return read.Parsed is not null;
	}

	static MapParseCacheRead Deserialize( string identity )
	{
		var path = Titanfall2ProcessedCache.GetPath( Kind, identity );
		var fileBytes = 0L;
		try { fileBytes = new FileInfo( path ).Length; } catch ( Exception ) { }

		var timer = Stopwatch.StartNew();
		Titanfall2BspReader.ParsedBsp parsed = null;
		var hit = Titanfall2ProcessedCache.TryRead( Kind, identity, reader => parsed = Titanfall2MapCachePayload.Read( reader ) );
		timer.Stop();
		if ( !hit || parsed is null )
			return new MapParseCacheRead( null, "unreadable, corrupt or outdated", fileBytes, timer.Elapsed.TotalSeconds );
		return new MapParseCacheRead( parsed, null, fileBytes, timer.Elapsed.TotalSeconds );
	}

	/// <summary>
	/// Serializes the parse on a worker thread and publishes it. The caller keeps using the parse it
	/// already built, so a failed or skipped write can never change this load.
	/// </summary>
	internal static void BeginWrite( string identity, Titanfall2BspReader.ParsedBsp parsed,
		ITitanfall2AssetSource source, string mapPath, Sandbox.Diagnostics.Logger log )
	{
		if ( !Titanfall2StreamingSettings.ProcessedCache || parsed is null || string.IsNullOrEmpty( identity ) ) return;

		_ = Task.Run( () =>
		{
			try
			{
				if ( !TryCaptureProbeLookup( source, parsed, out var probes, out var probeError ) )
				{
					log.Info( $"Titanfall 2 map parse cache: skipped, {probeError} ({mapPath})." );
					return;
				}

				var bytes = Titanfall2MapCachePayload.EstimateBytes( parsed, probes );
				if ( bytes > MaximumPayloadBytes )
				{
					log.Info( $"Titanfall 2 map parse cache: skipped, {bytes / (1024.0 * 1024.0):0.0} MB exceeds the "
						+ $"{MaximumPayloadBytes / (1024.0 * 1024.0):0} MB payload limit ({mapPath})." );
					return;
				}

				var timer = Stopwatch.StartNew();
				if ( !Titanfall2ProcessedCache.Write( Kind, identity, writer => Titanfall2MapCachePayload.Write( writer, parsed, probes ) ) )
					return;
				log.Info( $"Titanfall 2 map parse cache: wrote {bytes / (1024.0 * 1024.0):0.0} MB in "
					+ $"{timer.Elapsed.TotalSeconds:0.00}s ({mapPath})." );
			}
			catch ( Exception exception )
			{
				Titanfall2Log.Warning( $"Titanfall 2 map parse cache write failed: {exception.Message}" );
			}
		} );
	}

	/// <summary>
	/// The parse builds its light probe lookup from lumps it does not keep, so the payload has to
	/// carry them. They are read from the sibling .bsp_lump entries retail ships next to the map and
	/// verified against the parsed lookup: any disagreement skips the cache rather than serving a
	/// payload whose prop lighting could differ. Both sibling flavours are tried, because the parse
	/// itself only substitutes the collision lumps and the lookup therefore comes from whichever
	/// copy the package ships inside the BSP.
	/// </summary>
	static bool TryCaptureProbeLookup( ITitanfall2AssetSource source, Titanfall2BspReader.ParsedBsp parsed,
		out ProbeLumpData data, out string error )
	{
		data = null;
		error = null;
		var lookup = parsed.LightProbes;
		if ( lookup is null ) return true;
		if ( source is not ITitanfall2SiblingAssetSource siblings )
		{
			error = "the source has no sibling lumps for the light probe lookup";
			return false;
		}

		if ( !TryCaptureFrom( siblings, parsed, lookup, clientVariant: true, out data )
			&& !TryCaptureFrom( siblings, parsed, lookup, clientVariant: false, out data ) )
		{
			error = "the sibling light probe lumps do not match the parsed lookup";
			return false;
		}

		return true;
	}

	static bool TryCaptureFrom( ITitanfall2SiblingAssetSource siblings, Titanfall2BspReader.ParsedBsp parsed,
		Titanfall2LightProbeLookup lookup, bool clientVariant, out ProbeLumpData data )
	{
		data = null;
		if ( !TryReadLump( siblings, LightProbesLumpId, clientVariant, out var probes )
			|| !TryReadLump( siblings, LightProbeTreeLumpId, clientVariant, out var tree )
			|| !TryReadLump( siblings, LightProbeReferencesLumpId, clientVariant, out var references ) )
			return false;

		var remapped = Titanfall2LightProbe.RemapLocalLightIndices( probes, parsed.WorldLights ?? Array.Empty<Titanfall2WorldLight>() );
		var rebuilt = Titanfall2LightProbeLookup.TryCreate( remapped, tree, references );
		if ( rebuilt is null || rebuilt.ReferenceCount != lookup.ReferenceCount ) return false;
		if ( !ProbeRecordsMatch( siblings, parsed, remapped, clientVariant ) ) return false;
		if ( !LookupsAgree( lookup, rebuilt, parsed ) ) return false;

		data = new ProbeLumpData( remapped, tree, references );
		return true;
	}

	static bool TryReadLump( ITitanfall2SiblingAssetSource siblings, int lumpId, bool clientVariant, out byte[] bytes )
	{
		// Same names Map.Collision.ReadCollisionLumpOverrides probes for the collision lumps.
		var suffix = clientVariant ? $".{lumpId:X4}.bsp_lump.client" : $".{lumpId:X4}.bsp_lump";
		return siblings.TryReadSibling( suffix, out bytes, out _ ) && bytes is { Length: > 0 };
	}

	/// <summary>Every static prop that carries a baked probe must decode to the same record.</summary>
	static bool ProbeRecordsMatch( ITitanfall2SiblingAssetSource siblings, Titanfall2BspReader.ParsedBsp parsed,
		byte[] probes, bool clientVariant )
	{
		if ( !TryReadLump( siblings, StaticPropLightProbeIndicesLumpId, clientVariant, out var indices ) ) return false;
		var props = parsed.StaticProps ?? Array.Empty<Titanfall2BspReader.StaticPropInstance>();
		foreach ( var prop in props )
		{
			if ( prop?.BakedLightProbe is not { } baked ) continue;
			var sourceIndex = prop.SourceIndex;
			if ( sourceIndex < 0 || (long)(sourceIndex + 1) * 4 > indices.Length ) continue;
			var probeIndex = BinaryPrimitives.ReadUInt32LittleEndian( indices.AsSpan( sourceIndex * 4, 4 ) );
			var offset = (long)probeIndex * Titanfall2LightProbe.RecordBytes;
			if ( offset + Titanfall2LightProbe.RecordBytes > probes.Length ) return false;
			if ( !Titanfall2LightProbe.TryRead( probes.AsSpan( (int)offset, Titanfall2LightProbe.RecordBytes ), out var probe ) ) return false;
			if ( !SameProbe( baked, probe ) ) return false;
		}

		// A map whose props carry no baked probe has nothing to cross-check here; the sampled
		// lookup comparison still covers the probe array for it.
		return true;
	}

	/// <summary>Both lookups were built from these bytes, so identical inputs give identical answers.</summary>
	static bool LookupsAgree( Titanfall2LightProbeLookup parsed, Titanfall2LightProbeLookup rebuilt, Titanfall2BspReader.ParsedBsp bsp )
	{
		const int sampleLimit = 32;
		var samples = 0;
		var mismatches = 0;

		void Sample( NumericsVector3 position )
		{
			if ( mismatches > 0 || samples >= sampleLimit ) return;
			samples++;
			var left = parsed.Find( position );
			var right = rebuilt.Find( position );
			if ( (left is null) != (right is null) )
			{
				mismatches++;
				return;
			}

			if ( left is not null && !SameProbe( left, right ) ) mismatches++;
		}

		var mins = bsp.World?.Mins ?? default;
		var maxs = bsp.World?.Maxs ?? default;
		Sample( (mins + maxs) * 0.5f );
		var props = bsp.StaticProps ?? Array.Empty<Titanfall2BspReader.StaticPropInstance>();
		var step = Math.Max( 1, props.Count / (sampleLimit - 1) );
		for ( var index = 0; index < props.Count && samples < sampleLimit; index += step )
		{
			var prop = props[index];
			if ( prop is null ) continue;
			Sample( prop.LightingOrigin );
		}

		return mismatches == 0 && samples > 0;
	}

	static bool SameProbe( Titanfall2LightProbe left, Titanfall2LightProbe right ) =>
		left is not null && right is not null
		&& left.Red == right.Red && left.Green == right.Green && left.Blue == right.Blue
		&& left.SkyDirectionSunVisibility == right.SkyDirectionSunVisibility
		&& left.PackedLightWeights == right.PackedLightWeights
		&& left.LightIndex0 == right.LightIndex0 && left.LightIndex1 == right.LightIndex1
		&& left.LightIndex2 == right.LightIndex2 && left.LightIndex3 == right.LightIndex3
		&& left.Reserved == right.Reserved;
}

/// <summary>
/// Binary payload of one cached rBSP parse. This is a private on-disk format: the writer and the
/// reader below are the only consumers, so both halves change together and
/// <see cref="Titanfall2MapCache.PayloadVersion"/> is bumped whenever they do. Every count is
/// bounded on read so a corrupt entry fails before it allocates.
/// </summary>
static class Titanfall2MapCachePayload
{
	const int MaximumMeshes = 200_000;
	const int MaximumVertices = 4_000_000;
	const int MaximumIndices = 12_000_000;
	const int MaximumCollisionParts = 4_000_000;
	const int MaximumCollisionVertices = 8_000_000;
	const int MaximumCollisionIndices = 24_000_000;
	const int MaximumPlanes = 2_000_000;
	const int MaximumNodes = 2_000_000;
	const int MaximumPortals = 2_000_000;
	const int MaximumObjectMasks = 2_000_000;
	const int MaximumCubemaps = 1_000_000;
	const int MaximumProps = 1_000_000;
	const int MaximumLights = 1_000_000;
	const int MaximumLightmapPages = 1_024;
	const int MaximumLightmapBytes = 64 * 1024 * 1024;
	const int MaximumEmbeddedCubemapBytes = 64 * 1024 * 1024;
	const int MaximumEntities = 200_000;
	const int MaximumEntityFields = 65_536;
	const int MaximumPartitions = 4_096;
	const int MaximumTextBytes = 1024 * 1024;
	const int MaximumProbeBytes = 64 * 1024 * 1024;

	/// <summary>Uncompressed size estimate; used to skip payloads that would crowd out everything else.</summary>
	internal static long EstimateBytes( Titanfall2BspReader.ParsedBsp bsp, Titanfall2MapCache.ProbeLumpData probes )
	{
		long bytes = 0;
		var world = bsp.World ?? new Titanfall2BspReader.BspWorld();
		foreach ( var mesh in world.Meshes ?? Array.Empty<Titanfall2BspReader.WorldMesh>() )
		{
			if ( mesh is null ) continue;
			bytes += 64 + 2L * (mesh.MaterialName?.Length ?? 0);
			bytes += 44L * (mesh.Vertices?.Length ?? 0) + 4L * (mesh.Indices?.Length ?? 0);
		}

		var collision = bsp.WorldCollision ?? new Titanfall2BspReader.BspWorldCollision();
		bytes += 32L * (collision.Parts?.Count ?? 0);
		bytes += 12L * (collision.Vertices?.Length ?? 0) + 4L * (collision.Indices?.Length ?? 0);

		var visibility = bsp.Visibility ?? new Titanfall2BspReader.BspVisibility();
		bytes += 16L * (visibility.Planes?.Count ?? 0) + 8L * (visibility.Nodes?.Count ?? 0)
			+ 32L * (visibility.Portals?.Count ?? 0) + 16L * (visibility.ObjectCellMasks?.Count ?? 0) + 64;

		bytes += 16L * (bsp.Cubemaps?.Count ?? 0);
		foreach ( var entity in bsp.Entities ?? Array.Empty<Dictionary<string, string>>() )
		{
			bytes += 64;
			if ( entity is null ) continue;
			foreach ( var pair in entity )
				bytes += 16 + 2L * ((pair.Key?.Length ?? 0) + (pair.Value?.Length ?? 0));
		}

		bytes += 64 + 2L * (bsp.EntityPartitions?.Count ?? 0);
		bytes += bsp.EmbeddedCubemapVtf?.Length ?? 0;
		var lightmaps = bsp.Lightmaps ?? new Titanfall2BspReader.BspLightmaps();
		foreach ( var page in lightmaps.Pages ?? Array.Empty<Titanfall2BspReader.BspLightmapPage>() )
		{
			if ( page is null ) continue;
			bytes += 32 + (page.SkyA?.Length ?? 0) + (page.SkyB?.Length ?? 0)
				+ (page.RealTimeA?.Length ?? 0) + (page.RealTimeB?.Length ?? 0) + (page.RealTimeC?.Length ?? 0);
		}

		bytes += 2L * (lightmaps.RealTimeLightPages?.Count ?? 0);
		bytes += 96L * (bsp.WorldLights?.Count ?? 0);
		foreach ( var prop in bsp.StaticProps ?? Array.Empty<Titanfall2BspReader.StaticPropInstance>() )
		{
			if ( prop is null ) continue;
			bytes += 160 + 2L * (prop.ModelPath?.Length ?? 0);
		}

		if ( probes is not null )
			bytes += (probes.Probes?.Length ?? 0) + (probes.Tree?.Length ?? 0) + (probes.References?.Length ?? 0);
		return bytes;
	}

	internal static void Write( BinaryWriter writer, Titanfall2BspReader.ParsedBsp bsp, Titanfall2MapCache.ProbeLumpData probes )
	{
		writer.Write( Titanfall2MapCache.PayloadMagic );
		writer.Write( Titanfall2MapCache.PayloadVersion );
		Titanfall2ProcessedCache.WriteString( writer, Titanfall2MapCache.PayloadTag );
		Titanfall2ProcessedCache.WriteString( writer, bsp.FilePath );
		Titanfall2ProcessedCache.WriteString( writer, bsp.MapName );
		writer.Write( bsp.Version );
		writer.Write( bsp.Revision );

		var world = bsp.World ?? new Titanfall2BspReader.BspWorld();
		var meshes = world.Meshes ?? Array.Empty<Titanfall2BspReader.WorldMesh>();
		writer.Write( meshes.Count );
		foreach ( var source in meshes )
		{
			var mesh = source ?? new Titanfall2BspReader.WorldMesh();
			Titanfall2ProcessedCache.WriteString( writer, mesh.MaterialName );
			writer.Write( mesh.VisibilityObjectIndex );
			writer.Write( mesh.Cubemap );
			writer.Write( mesh.LightmapPage );
			Titanfall2ProcessedCache.WriteArray( writer, mesh.Vertices ?? Array.Empty<Titanfall2BspReader.WorldVertex>() );
			Titanfall2ProcessedCache.WriteArray( writer, mesh.Indices ?? Array.Empty<int>() );
		}

		writer.Write( world.SourceMeshCount );
		WriteVector( writer, world.Mins );
		WriteVector( writer, world.Maxs );

		var collision = bsp.WorldCollision ?? new Titanfall2BspReader.BspWorldCollision();
		Titanfall2ProcessedCache.WriteArray( writer, collision.Parts?.ToArray() ?? Array.Empty<Titanfall2BspReader.BspCollisionPart>() );
		Titanfall2ProcessedCache.WriteArray( writer, collision.Vertices ?? Array.Empty<NumericsVector3>() );
		Titanfall2ProcessedCache.WriteArray( writer, collision.Indices ?? Array.Empty<int>() );
		writer.Write( collision.BrushCount );
		writer.Write( collision.TricollCount );
		writer.Write( collision.BrushTriangleCount );
		writer.Write( collision.TricollTriangleCount );
		writer.Write( collision.SourcePrimitiveCount );
		writer.Write( collision.NonBlockingPrimitiveCount );
		writer.Write( collision.WaterPrimitiveCount );
		writer.Write( collision.BlockingWaterPrimitiveCount );
		writer.Write( collision.RejectedPrimitiveCount );

		var visibility = bsp.Visibility ?? new Titanfall2BspReader.BspVisibility();
		writer.Write( visibility.WorldMeshCount );
		Titanfall2ProcessedCache.WriteArray( writer, visibility.Planes?.ToArray() ?? Array.Empty<Titanfall2BspReader.VisibilityPlane>() );
		Titanfall2ProcessedCache.WriteArray( writer, visibility.Nodes?.ToArray() ?? Array.Empty<Titanfall2BspReader.VisibilityBspNode>() );
		writer.Write( visibility.CellCount );
		writer.Write( visibility.AlwaysVisibleCells.Low );
		writer.Write( visibility.AlwaysVisibleCells.High );
		Titanfall2ProcessedCache.WriteArray( writer, visibility.Portals?.ToArray() ?? Array.Empty<Titanfall2BspReader.VisibilityPortal>() );
		Titanfall2ProcessedCache.WriteArray( writer, visibility.ObjectCellMasks?.ToArray() ?? Array.Empty<Titanfall2BspReader.VisibilityCellMask>() );

		Titanfall2ProcessedCache.WriteArray( writer, bsp.Cubemaps?.ToArray() ?? Array.Empty<Titanfall2BspReader.BspCubemap>() );

		var entities = bsp.Entities ?? Array.Empty<Dictionary<string, string>>();
		writer.Write( entities.Count );
		foreach ( var entity in entities )
		{
			if ( entity is null )
			{
				writer.Write( 0 );
				continue;
			}

			writer.Write( entity.Count );
			foreach ( var pair in entity )
			{
				Titanfall2ProcessedCache.WriteString( writer, pair.Key );
				Titanfall2ProcessedCache.WriteString( writer, pair.Value );
			}
		}

		var partitions = bsp.EntityPartitions ?? Array.Empty<string>();
		writer.Write( partitions.Count );
		foreach ( var partition in partitions )
			Titanfall2ProcessedCache.WriteString( writer, partition );

		Titanfall2ProcessedCache.WriteArray( writer, bsp.EmbeddedCubemapVtf ?? Array.Empty<byte>() );

		var lightmaps = bsp.Lightmaps ?? new Titanfall2BspReader.BspLightmaps();
		var pages = lightmaps.Pages ?? Array.Empty<Titanfall2BspReader.BspLightmapPage>();
		writer.Write( pages.Count );
		foreach ( var source in pages )
		{
			var page = source ?? new Titanfall2BspReader.BspLightmapPage();
			writer.Write( page.Type );
			writer.Write( page.Width );
			writer.Write( page.Height );
			Titanfall2ProcessedCache.WriteArray( writer, page.SkyA ?? Array.Empty<byte>() );
			Titanfall2ProcessedCache.WriteArray( writer, page.SkyB ?? Array.Empty<byte>() );
			Titanfall2ProcessedCache.WriteArray( writer, page.RealTimeA ?? Array.Empty<byte>() );
			Titanfall2ProcessedCache.WriteArray( writer, page.RealTimeB ?? Array.Empty<byte>() );
			Titanfall2ProcessedCache.WriteArray( writer, page.RealTimeC ?? Array.Empty<byte>() );
		}

		Titanfall2ProcessedCache.WriteArray( writer, lightmaps.RealTimeLightPages?.ToArray() ?? Array.Empty<short>() );

		var lights = bsp.WorldLights ?? Array.Empty<Titanfall2WorldLight>();
		writer.Write( lights.Count );
		foreach ( var source in lights )
		{
			var light = source ?? new Titanfall2WorldLight();
			WriteVector( writer, light.Origin );
			WriteVector( writer, light.Intensity );
			WriteVector( writer, light.Direction );
			writer.Write( light.Type );
			writer.Write( light.Flags );
			writer.Write( light.InnerConeCosine );
			writer.Write( light.OuterConeCosine );
			writer.Write( light.ConeExponent );
			writer.Write( light.Radius );
			writer.Write( light.ConstantAttenuation );
			writer.Write( light.LinearAttenuation );
			writer.Write( light.QuadraticAttenuation );
			writer.Write( light.EmitterRadius );
			writer.Write( light.HighlightSize );
			writer.Write( light.SpecularIntensity );
		}

		var props = bsp.StaticProps ?? Array.Empty<Titanfall2BspReader.StaticPropInstance>();
		writer.Write( props.Count );
		foreach ( var source in props )
		{
			var prop = source ?? new Titanfall2BspReader.StaticPropInstance();
			writer.Write( prop.SourceIndex );
			Titanfall2ProcessedCache.WriteString( writer, prop.ModelPath );
			WriteVector( writer, prop.Origin );
			WriteVector( writer, prop.Angles );
			writer.Write( prop.Scale );
			writer.Write( prop.Skin );
			writer.Write( prop.SolidType );
			writer.Write( prop.Flags );
			writer.Write( prop.Cubemap );
			writer.Write( prop.ForcedFadeScale );
			WriteVector( writer, prop.LightingOrigin );
			writer.Write( prop.DiffuseModulationR );
			writer.Write( prop.DiffuseModulationG );
			writer.Write( prop.DiffuseModulationB );
			writer.Write( prop.DiffuseModulationA );
			writer.Write( prop.ReservedCollisionData );
			writer.Write( prop.CollisionFlagsRemove );
			WriteProbe( writer, prop.BakedLightProbe );
			WriteVector( writer, prop.LightProbeColor );
		}

		if ( probes is null )
		{
			writer.Write( 0 );
			return;
		}

		writer.Write( 1 );
		Titanfall2ProcessedCache.WriteArray( writer, probes.Probes ?? Array.Empty<byte>() );
		Titanfall2ProcessedCache.WriteArray( writer, probes.Tree ?? Array.Empty<byte>() );
		Titanfall2ProcessedCache.WriteArray( writer, probes.References ?? Array.Empty<byte>() );
	}

	internal static Titanfall2BspReader.ParsedBsp Read( Titanfall2ProcessedCache.CacheReader reader )
	{
		if ( reader.ReadUInt32() != Titanfall2MapCache.PayloadMagic )
			throw new InvalidDataException( "Map cache payload magic mismatch." );
		if ( reader.ReadInt32() != Titanfall2MapCache.PayloadVersion )
			throw new InvalidDataException( "Map cache payload version mismatch." );
		if ( reader.Text() != Titanfall2MapCache.PayloadTag )
			throw new InvalidDataException( "Map cache payload tag mismatch." );

		var filePath = reader.Text();
		var mapName = reader.Text();
		var version = reader.ReadInt32();
		var revision = reader.ReadInt32();

		var meshCount = reader.Count( MaximumMeshes );
		var meshes = new Titanfall2BspReader.WorldMesh[meshCount];
		for ( var index = 0; index < meshes.Length; index++ )
		{
			meshes[index] = new Titanfall2BspReader.WorldMesh
			{
				MaterialName = reader.Text(),
				VisibilityObjectIndex = reader.ReadInt32(),
				Cubemap = reader.ReadInt32(),
				LightmapPage = reader.ReadInt32(),
				Vertices = reader.Blittable<Titanfall2BspReader.WorldVertex>( MaximumVertices ),
				Indices = reader.Blittable<int>( MaximumIndices )
			};
		}

		var world = new Titanfall2BspReader.BspWorld
		{
			Meshes = meshes,
			SourceMeshCount = reader.ReadInt32(),
			Mins = ReadVector( reader ),
			Maxs = ReadVector( reader )
		};

		var collision = new Titanfall2BspReader.BspWorldCollision
		{
			Parts = reader.Blittable<Titanfall2BspReader.BspCollisionPart>( MaximumCollisionParts ),
			Vertices = reader.Blittable<NumericsVector3>( MaximumCollisionVertices ),
			Indices = reader.Blittable<int>( MaximumCollisionIndices ),
			BrushCount = reader.ReadInt32(),
			TricollCount = reader.ReadInt32(),
			BrushTriangleCount = reader.ReadInt32(),
			TricollTriangleCount = reader.ReadInt32(),
			SourcePrimitiveCount = reader.ReadInt32(),
			NonBlockingPrimitiveCount = reader.ReadInt32(),
			WaterPrimitiveCount = reader.ReadInt32(),
			BlockingWaterPrimitiveCount = reader.ReadInt32(),
			RejectedPrimitiveCount = reader.ReadInt32()
		};

		var worldMeshCount = reader.ReadInt32();
		var planes = reader.Blittable<Titanfall2BspReader.VisibilityPlane>( MaximumPlanes );
		var nodes = reader.Blittable<Titanfall2BspReader.VisibilityBspNode>( MaximumNodes );
		var cellCount = reader.ReadInt32();
		var alwaysVisible = new Titanfall2BspReader.VisibilityCellMask( reader.ReadUInt64(), reader.ReadUInt64() );
		var portals = reader.Blittable<Titanfall2BspReader.VisibilityPortal>( MaximumPortals );
		var objectCellMasks = reader.Blittable<Titanfall2BspReader.VisibilityCellMask>( MaximumObjectMasks );
		var visibility = new Titanfall2BspReader.BspVisibility
		{
			WorldMeshCount = worldMeshCount,
			Planes = planes,
			Nodes = nodes,
			CellCount = cellCount,
			AlwaysVisibleCells = alwaysVisible,
			Portals = portals,
			ObjectCellMasks = objectCellMasks
		};

		var cubemaps = reader.Blittable<Titanfall2BspReader.BspCubemap>( MaximumCubemaps );

		var entityCount = reader.Count( MaximumEntities );
		var entities = new Dictionary<string, string>[entityCount];
		for ( var index = 0; index < entities.Length; index++ )
		{
			var fieldCount = reader.Count( MaximumEntityFields );
			var entity = new Dictionary<string, string>( fieldCount, StringComparer.OrdinalIgnoreCase );
			for ( var field = 0; field < fieldCount; field++ )
			{
				var key = reader.Text( MaximumTextBytes );
				var value = reader.Text( MaximumTextBytes );
				entity[key] = value;
			}
			entities[index] = entity;
		}

		var partitionCount = reader.Count( MaximumPartitions );
		var partitions = new string[partitionCount];
		for ( var index = 0; index < partitions.Length; index++ )
			partitions[index] = reader.Text( MaximumTextBytes );

		var embeddedCubemapVtf = reader.Blittable<byte>( MaximumEmbeddedCubemapBytes );

		var pageCount = reader.Count( MaximumLightmapPages );
		var pages = new Titanfall2BspReader.BspLightmapPage[pageCount];
		for ( var index = 0; index < pages.Length; index++ )
		{
			pages[index] = new Titanfall2BspReader.BspLightmapPage
			{
				Type = reader.ReadInt32(),
				Width = reader.ReadInt32(),
				Height = reader.ReadInt32(),
				SkyA = reader.Blittable<byte>( MaximumLightmapBytes ),
				SkyB = reader.Blittable<byte>( MaximumLightmapBytes ),
				RealTimeA = reader.Blittable<byte>( MaximumLightmapBytes ),
				RealTimeB = reader.Blittable<byte>( MaximumLightmapBytes ),
				RealTimeC = reader.Blittable<byte>( MaximumLightmapBytes )
			};
		}

		var lightmaps = new Titanfall2BspReader.BspLightmaps
		{
			Pages = pages,
			RealTimeLightPages = reader.Blittable<short>( MaximumLightmapBytes / sizeof( short ) )
		};

		var lightCount = reader.Count( MaximumLights );
		var lights = new Titanfall2WorldLight[lightCount];
		for ( var index = 0; index < lights.Length; index++ )
		{
			lights[index] = new Titanfall2WorldLight
			{
				Origin = ReadVector( reader ),
				Intensity = ReadVector( reader ),
				Direction = ReadVector( reader ),
				Type = reader.ReadInt32(),
				Flags = reader.ReadUInt32(),
				InnerConeCosine = reader.ReadSingle(),
				OuterConeCosine = reader.ReadSingle(),
				ConeExponent = reader.ReadSingle(),
				Radius = reader.ReadSingle(),
				ConstantAttenuation = reader.ReadSingle(),
				LinearAttenuation = reader.ReadSingle(),
				QuadraticAttenuation = reader.ReadSingle(),
				EmitterRadius = reader.ReadSingle(),
				HighlightSize = reader.ReadSingle(),
				SpecularIntensity = reader.ReadSingle()
			};
		}

		var propCount = reader.Count( MaximumProps );
		var props = new Titanfall2BspReader.StaticPropInstance[propCount];
		for ( var index = 0; index < props.Length; index++ )
		{
			var prop = new Titanfall2BspReader.StaticPropInstance
			{
				SourceIndex = reader.ReadInt32(),
				ModelPath = reader.Text(),
				Origin = ReadVector( reader ),
				Angles = ReadVector( reader ),
				Scale = reader.ReadSingle(),
				Skin = reader.ReadInt32(),
				SolidType = reader.ReadByte(),
				Flags = reader.ReadByte(),
				Cubemap = reader.ReadUInt16(),
				ForcedFadeScale = reader.ReadSingle(),
				LightingOrigin = ReadVector( reader ),
				DiffuseModulationR = reader.ReadByte(),
				DiffuseModulationG = reader.ReadByte(),
				DiffuseModulationB = reader.ReadByte(),
				DiffuseModulationA = reader.ReadByte(),
				ReservedCollisionData = reader.ReadUInt32(),
				CollisionFlagsRemove = reader.ReadUInt32(),
				BakedLightProbe = ReadProbe( reader ),
				LightProbeColor = ReadVector( reader )
			};
			props[index] = prop;
		}

		Titanfall2MapCache.ProbeLumpData probeData = null;
		if ( reader.ReadInt32() != 0 )
		{
			probeData = new Titanfall2MapCache.ProbeLumpData(
				reader.Blittable<byte>( MaximumProbeBytes ),
				reader.Blittable<byte>( MaximumProbeBytes ),
				reader.Blittable<byte>( MaximumProbeBytes ) );
		}

		Titanfall2LightProbeLookup lightProbes = null;
		if ( probeData is not null )
		{
			lightProbes = Titanfall2LightProbeLookup.TryCreate( probeData.Probes, probeData.Tree, probeData.References );
			if ( lightProbes is null ) throw new InvalidDataException( "Map cache light probe lookup is invalid." );
		}

		return new Titanfall2BspReader.ParsedBsp
		{
			FilePath = filePath,
			MapName = mapName,
			Version = version,
			Revision = revision,
			World = world,
			WorldCollision = collision,
			StaticProps = props,
			Visibility = visibility,
			Cubemaps = cubemaps,
			Entities = entities,
			EntityPartitions = partitions,
			EmbeddedCubemapVtf = embeddedCubemapVtf,
			Lightmaps = lightmaps,
			WorldLights = lights,
			LightProbes = lightProbes
		};
	}

	static void WriteVector( BinaryWriter writer, NumericsVector3 value )
	{
		writer.Write( value.X );
		writer.Write( value.Y );
		writer.Write( value.Z );
	}

	static NumericsVector3 ReadVector( Titanfall2ProcessedCache.CacheReader reader ) =>
		new( reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle() );

	static void WriteProbe( BinaryWriter writer, Titanfall2LightProbe probe )
	{
		writer.Write( probe is not null );
		if ( probe is null ) return;
		WriteVector4( writer, probe.Red );
		WriteVector4( writer, probe.Green );
		WriteVector4( writer, probe.Blue );
		WriteVector4( writer, probe.SkyDirectionSunVisibility );
		writer.Write( probe.PackedLightWeights );
		writer.Write( probe.LightIndex0 );
		writer.Write( probe.LightIndex1 );
		writer.Write( probe.LightIndex2 );
		writer.Write( probe.LightIndex3 );
		writer.Write( probe.Reserved );
	}

	static Titanfall2LightProbe ReadProbe( Titanfall2ProcessedCache.CacheReader reader )
	{
		if ( !reader.ReadBoolean() ) return null;
		var probe = new Titanfall2LightProbe
		{
			Red = ReadVector4( reader ),
			Green = ReadVector4( reader ),
			Blue = ReadVector4( reader ),
			SkyDirectionSunVisibility = ReadVector4( reader ),
			PackedLightWeights = reader.ReadUInt32(),
			LightIndex0 = reader.ReadUInt16(),
			LightIndex1 = reader.ReadUInt16(),
			LightIndex2 = reader.ReadUInt16(),
			LightIndex3 = reader.ReadUInt16(),
			Reserved = reader.ReadUInt32()
		};
		return probe;
	}

	static void WriteVector4( BinaryWriter writer, NumericsVector4 value )
	{
		writer.Write( value.X );
		writer.Write( value.Y );
		writer.Write( value.Z );
		writer.Write( value.W );
	}

	static NumericsVector4 ReadVector4( Titanfall2ProcessedCache.CacheReader reader ) =>
		new( reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle() );
}
