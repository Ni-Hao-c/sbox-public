/// <summary>Mutable preparation only; publishing freezes both candidates and winners.</summary>
static class Titanfall2ContentDiscovery
{
	internal static Titanfall2ContentSnapshot Build( string gameRoot, string profileRoot = null, string map = null )
	{
		map = string.IsNullOrWhiteSpace( map ) ? null : map.Trim().ToLowerInvariant();
		if ( map is not null && map.Any( character => !char.IsAsciiLetterOrDigit( character ) && character != '_' ) ) throw new InvalidDataException( "NORTHSTAR_MAP must be a map name without a path or extension." );
		var entries = new List<Titanfall2ContentEntry>();
		var identities = new List<string>();
		var diagnostics = new List<string>();
		var baseRpaks = Titanfall2NorthstarProfile.Files( Path.Combine( gameRoot, "r2/paks/Win64" ), recursive: false )
			.Where( path => path.EndsWith( ".rpak", StringComparison.OrdinalIgnoreCase ) )
			.OrderByDescending( Titanfall2Mount.GetPatchRank ).ThenBy( path => Path.GetFileName( path ).Equals( "common.rpak", StringComparison.OrdinalIgnoreCase ) ? 0 : 1 )
			.ThenBy( path => path, StringComparer.OrdinalIgnoreCase ).ToArray();
		var profile = new Titanfall2NorthstarProfile( profileRoot, map, baseRpaks );
		identities.AddRange( profile.Identities ); diagnostics.AddRange( profile.Diagnostics );
		foreach ( var mod in profile.Mods )
		{
			identities.Add( $"mod:{mod.Name}:{mod.Version}:{mod.Priority}:{mod.Directory}" );
			var root = Path.Combine( mod.Directory, "mod" );
			foreach ( var path in Titanfall2NorthstarProfile.Files( root ) )
			{
				if ( !Titanfall2ContentSnapshot.TryNormalize( Path.GetRelativePath( root, path ), out var virtualPath ) ) throw new InvalidDataException( $"Invalid Mod content path: {path}" );
				var source = new Titanfall2SnapshotFile( root, path );
				entries.Add( new( virtualPath, "file", $"northstar:{mod.Name}/loose", source.CacheIdentity, source ) );
			}
		}
		// Northstar's filesystem hook resolves loose Mod overrides before any VPK
		// read. Preserve that rule even when a lower-priority Mod owns the loose file.
		foreach ( var mod in profile.Mods )
		{
			foreach ( var path in mod.Rpaks ) AddRpak( path, $"northstar:{mod.Name}/rpak", required: true );
			foreach ( var path in mod.Vpks ) AddVpk( path, $"northstar:{mod.Name}/vpk", required: true );
			foreach ( var path in Titanfall2NorthstarProfile.Files( Path.Combine( mod.Directory, "paks" ) ).Where( path => path.EndsWith( ".starpak", StringComparison.OrdinalIgnoreCase ) ) ) identities.Add( Stamp( path ) );
			foreach ( var folder in new[] { "keyvalues", "media", "audio" } )
				if ( System.IO.Directory.Exists( Path.Combine( mod.Directory, folder ) ) ) diagnostics.Add( $"{mod.Name}: {folder} transforms are not applied; only indexed asset replacements are supported." );
		}
		// Aliases replace the requested retail package. Their targets may be retail
		// packages or mod packages; retaining the original would undo the redirect.
		foreach ( var path in baseRpaks.Where( path => !profile.Aliases.ContainsKey( Titanfall2NorthstarProfile.PackageBaseName( path ) ) ) ) AddRpak( path, "retail/rpak", required: false );
		foreach ( var path in Titanfall2NorthstarProfile.Files( Path.Combine( gameRoot, "vpk" ), recursive: false )
			.Where( path => path.EndsWith( "_dir.vpk", StringComparison.OrdinalIgnoreCase ) )
			.OrderBy( path => Path.GetFileName( path ).Equals( "englishclient_mp_common.bsp.pak000_dir.vpk", StringComparison.OrdinalIgnoreCase ) ? 0 : 1 )
			.ThenBy( path => path, StringComparer.OrdinalIgnoreCase ) ) AddVpk( path, "retail/vpk", required: false );
		foreach ( var folder in new[] { Path.Combine( gameRoot, "vpk" ), Path.Combine( gameRoot, "r2/paks/Win64" ) } )
			foreach ( var path in Titanfall2NorthstarProfile.Files( folder, recursive: false ).Where( path => path.EndsWith( ".vpk", StringComparison.OrdinalIgnoreCase ) || path.EndsWith( ".starpak", StringComparison.OrdinalIgnoreCase ) ) ) identities.Add( Stamp( path ) );
		var soundRoot = Path.Combine( gameRoot, "r2/sound" );
		foreach ( var path in Titanfall2NorthstarProfile.Files( soundRoot, recursive: false ).Where( path => path.EndsWith( ".mbnk", StringComparison.OrdinalIgnoreCase ) || path.EndsWith( ".mstr", StringComparison.OrdinalIgnoreCase ) ) ) identities.Add( Stamp( path ) );
		var bankPath = Path.Combine( soundRoot, "general.mbnk" );
		if ( File.Exists( bankPath ) )
		{
			var bankIdentity = Stamp( bankPath );
			if ( MilesBankArchive.TryOpen( gameRoot, bankPath, out var bank, out var error ) )
				foreach ( var sound in bank.Assets ) entries.Add( new( sound.ResourceName, "miles", "retail/miles", bankIdentity, Bank: bank, Sound: sound ) );
			else diagnostics.Add( $"Cannot index Miles bank {bankPath}: {error}" );
		}
		return new( entries, identities, diagnostics, map );

		void AddVpk( string path, string layer, bool required )
		{
			var identity = Stamp( path ); identities.Add( identity );
			if ( !VpkArchive.TryOpen( path, out var archive, out var error ) )
			{
				if ( required ) throw new InvalidDataException( $"Cannot index Mod VPK {path}: {error}" );
				diagnostics.Add( $"Cannot index retail VPK {path}: {error}" ); return;
			}
			var version = new Titanfall2VpkVersion( archive );
			identities.AddRange( version.Identities );
			foreach ( var entry in archive.Entries )
			{
				if ( !Titanfall2ContentSnapshot.TryNormalize( entry.Path, out var normalized ) ) throw new InvalidDataException( $"Invalid VPK member: {entry.Path}" );
				entries.Add( new( normalized, "file", $"{layer}:{Path.GetFileName( path )}", identity, new Titanfall2SnapshotVpkSource( archive, entry, version ) ) );
			}
		}

		void AddRpak( string path, string layer, bool required )
		{
			var identity = Stamp( path ); identities.Add( identity );
			if ( !RpakArchive.TryOpen( path, out var archive, out var error ) )
			{
				if ( required ) throw new InvalidDataException( $"Cannot index Mod RPAK {path}: {error}" );
				diagnostics.Add( $"Cannot index retail RPAK {path}: {error}" ); return;
			}
			archive.FreezeSources();
			if ( Stamp( path ) != identity ) throw new InvalidDataException( $"RPAK changed while indexing: {path}" );
			var unsupported = 0;
			foreach ( var asset in archive.Assets )
			{
				var name = string.IsNullOrWhiteSpace( asset.Name ) ? $"_guid/{asset.Guid:x16}" : asset.Name;
				if ( !Titanfall2ContentSnapshot.TryNormalize( name, out var normalized ) ) throw new InvalidDataException( $"Invalid RPAK name: {name}" );
				entries.Add( new( normalized, asset.Type, $"{layer}:{Path.GetFileName( path )}", identity, Archive: archive, Asset: asset ) );
				if ( asset.Type is not ("txtr" or "matl" or "shds" or "shdr") ) unsupported++;
			}
			if ( required && unsupported > 0 ) diagnostics.Add( $"{path}: {unsupported} logical assets indexed without an engine loader (RPAK models/UI/etc. are not converted)." );
		}
	}

	internal static string Stamp( string path ) => Titanfall2SourceIdentity.Stamp( path );
}
