using Titanfall2;
using static Titanfall2ProcessedCache;
using static Titanfall2.Titanfall2BspReader;

/// <summary>Persistent default-path BSP parse results, including PVS and prop metadata.</summary>
static class Titanfall2MapCache
{
	internal static string Identity( Titanfall2Mount host, ITitanfall2AssetSource source ) =>
		$"{host.ContentFingerprint}|{source.CacheIdentity}|bsp-default-cm-index21-fog-v3";

	internal static bool TryRead( string identity, out ParsedBsp parsed )
	{
		ParsedBsp result = null;
		var hit = Titanfall2ProcessedCache.TryRead( "map", identity, reader =>
		{
			var metadata = reader.Json<ParsedBsp>();
			var cubemaps = reader.Array<byte>( 64 * 1024 * 1024 );
			var meshes = new WorldMesh[reader.Count( 100000 )];
			for ( var i = 0; i < meshes.Length; i++ )
			{
				var name = reader.Text();
				var visibility = reader.ReadInt32(); var cubemap = reader.ReadInt32(); var lightmap = reader.ReadInt32();
				var vertices = reader.Array<WorldVertex>();
				var indices = reader.Array<int>();
				if ( indices.Length % 3 != 0 || indices.Any( index => (uint)index >= vertices.Length ) )
					throw new InvalidDataException( "Invalid cached BSP mesh indices." );
				meshes[i] = new WorldMesh
				{
					MaterialName = name,
					VisibilityObjectIndex = visibility,
					Cubemap = cubemap,
					LightmapPage = lightmap,
					Vertices = vertices,
					Indices = indices
				};
			}
			result = new ParsedBsp
			{
				FilePath = metadata.FilePath,
				MapName = metadata.MapName,
				Version = metadata.Version,
				Revision = metadata.Revision,
				World = new BspWorld
				{
					Meshes = meshes,
					SourceMeshCount = metadata.World.SourceMeshCount,
					Mins = metadata.World.Mins,
					Maxs = metadata.World.Maxs
				},
				StaticProps = metadata.StaticProps,
				Visibility = metadata.Visibility,
				Cubemaps = metadata.Cubemaps,
				Entities = metadata.Entities,
				EntityPartitions = metadata.EntityPartitions,
				EmbeddedCubemapVtf = cubemaps
			};
		} );
		parsed = hit ? result : null;
		return hit;
	}

	internal static void Write( string identity, ParsedBsp parsed )
	{
		// Large blittable geometry and cubemap bytes are binary; only the small,
		// extensible metadata uses JSON. Do not round-trip millions of vertices as text.
		var metadata = new ParsedBsp
		{
			FilePath = parsed.FilePath,
			MapName = parsed.MapName,
			Version = parsed.Version,
			Revision = parsed.Revision,
			World = new BspWorld { SourceMeshCount = parsed.World.SourceMeshCount, Mins = parsed.World.Mins, Maxs = parsed.World.Maxs },
			StaticProps = parsed.StaticProps,
			Visibility = parsed.Visibility,
			Cubemaps = parsed.Cubemaps,
			Entities = parsed.Entities,
			EntityPartitions = parsed.EntityPartitions
		};
		Titanfall2ProcessedCache.Write( "map", identity, writer =>
		{
			WriteJson( writer, metadata );
			WriteArray( writer, parsed.EmbeddedCubemapVtf );
			writer.Write( parsed.World.Meshes.Count );
			foreach ( var mesh in parsed.World.Meshes )
			{
				WriteString( writer, mesh.MaterialName );
				writer.Write( mesh.VisibilityObjectIndex ); writer.Write( mesh.Cubemap ); writer.Write( mesh.LightmapPage );
				WriteArray( writer, mesh.Vertices ); WriteArray( writer, mesh.Indices );
			}
		} );
	}
}
