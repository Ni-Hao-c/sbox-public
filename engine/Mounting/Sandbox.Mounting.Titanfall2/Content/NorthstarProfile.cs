using System.Text.Json;
using System.Text.RegularExpressions;

sealed record Titanfall2NorthstarMod( string Name, string Version, int Priority, string Directory,
	IReadOnlyList<string> Vpks, IReadOnlyList<string> Rpaks );

/// <summary>
/// Read-only interpretation of NorthstarLauncher-main asset configuration. Script
/// dependency constants are deliberately not treated as package/version dependencies.
/// Conditional packages are resolved for a fixed optional map at session creation.
/// </summary>
sealed class Titanfall2NorthstarProfile
{
	internal IReadOnlyList<Titanfall2NorthstarMod> Mods { get; }
	internal IReadOnlyList<string> Identities { get; }
	internal IReadOnlyList<string> Diagnostics { get; }
	internal IReadOnlyDictionary<string, string> Aliases { get; }
	static readonly JsonDocumentOptions JsonOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true, MaxDepth = 64 };

	internal Titanfall2NorthstarProfile( string root, string map, IEnumerable<string> basePackages )
	{
		var diagnostics = new List<string>();
		var identities = new List<string> { $"profile:{root}", $"map:{map}" };
		var mods = new List<Titanfall2NorthstarMod>();
		var aliasMap = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase );
		Aliases = new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>( aliasMap );
		if ( string.IsNullOrWhiteSpace( root ) )
		{
			Mods = Array.Empty<Titanfall2NorthstarMod>(); Identities = identities.AsReadOnly(); Diagnostics = diagnostics.AsReadOnly(); return;
		}
		root = Path.GetFullPath( root );
		if ( !System.IO.Directory.Exists( root ) ) throw new DirectoryNotFoundException( $"Northstar profile not found: {root}" );
		using var enabled = ReadJson( Path.Combine( root, "enabledmods.json" ), identities );
		if ( enabled is not null && enabled.RootElement.TryGetProperty( "Version", out var format )
			&& (format.ValueKind != JsonValueKind.Number || !format.TryGetInt32( out var formatVersion ) || formatVersion is < 0 or > 1) ) throw new InvalidDataException( "Unsupported Northstar enabledmods.json format version." );
		var directories = new List<string>();
		directories.AddRange( Directories( Path.Combine( root, "mods" ) ) );
		// main also accepts unpacked ModWorkshop layouts directly under remote/mods.
		directories.AddRange( Directories( Path.Combine( root, "runtime/remote/mods" ) ).Where( path => File.Exists( Path.Combine( path, "mod.json" ) ) ) );
		foreach ( var packages in new[] { "packages", "runtime/remote/mods" } )
			foreach ( var package in Directories( Path.Combine( root, packages ) ) )
			{
				if ( directories.Contains( package, StringComparer.OrdinalIgnoreCase ) ) continue;
				if ( !Regex.IsMatch( Path.GetFileName( package ), @"^[a-zA-Z0-9_]+-[a-zA-Z0-9_]+-\d+\.\d+\.\d+$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds( 100 ) ) ) continue;
				directories.AddRange( Directories( Path.Combine( package, "mods" ) ) );
			}
		var manifests = new List<(string Directory, string Name, string Version, int Priority)>();
		foreach ( var directory in directories.Order( StringComparer.OrdinalIgnoreCase ) )
		{
			var manifestPath = Path.Combine( directory, "mod.json" );
			if ( !File.Exists( manifestPath ) )
			{
				identities.Add( $"missing:{manifestPath}" );
				diagnostics.Add( $"{directory}: skipped because mod.json is missing. Restore the Mod's manifest; enabledmods.json is optional." );
				continue;
			}
			try
			{
				using var manifest = ReadJson( manifestPath, identities );
				var json = manifest.RootElement;
				if ( !json.TryGetProperty( "Name", out var nameValue ) || nameValue.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace( nameValue.GetString() ) )
					throw new InvalidDataException( "mod.json requires a nonempty Name." );
				var name = nameValue.GetString();
				var priority = json.TryGetProperty( "LoadPriority", out var rank ) ? rank.GetInt32() : 0;
				var version = json.TryGetProperty( "Version", out var ver ) ? ver.GetString() : "0.0.0";
				// main's manifest v1 keys by both name and version; accept legacy bools
				// read-only as well. Remote mods are disabled on first discovery upstream.
				var remote = Path.GetRelativePath( Path.Combine( root, "runtime/remote/mods" ), directory );
				if ( !remote.StartsWith( "..", StringComparison.Ordinal ) && !Path.IsPathRooted( remote ) )
				{
					diagnostics.Add( $"{name}: downloaded remote content disabled, matching Northstar initial discovery." );
					continue;
				}
				if ( enabled is not null && enabled.RootElement.TryGetProperty( name, out var flag ) )
				{
					if ( flag.ValueKind == JsonValueKind.Object )
					{
						if ( flag.TryGetProperty( version, out var versionFlag ) && versionFlag.ValueKind != JsonValueKind.True ) continue;
					}
					else if ( flag.ValueKind != JsonValueKind.True ) continue;
				}
				manifests.Add( (directory, name, version, priority) );
				if ( json.TryGetProperty( "Scripts", out _ ) || json.TryGetProperty( "PluginDependencies", out _ ) || json.TryGetProperty( "Dependencies", out _ ) )
					diagnostics.Add( $"{name}: script/plugin declarations are metadata only; no Northstar code is executed." );
			}
			catch ( Exception exception ) { throw new InvalidDataException( $"Invalid Northstar manifest {manifestPath}: {exception.Message}", exception ); }
		}
		var duplicates = manifests.GroupBy( mod => mod.Name, StringComparer.Ordinal ).Where( group => group.Count() > 1 ).Select( group => group.Key ).ToHashSet( StringComparer.Ordinal );
		foreach ( var name in duplicates ) diagnostics.Add( $"{name}: multiple enabled versions; all disabled, matching Northstar-main." );
		// Northstar sorts ascending, then walks overrides backwards. Equal priorities
		// have no stable upstream rule; use a documented ordinal directory tie-break.
		foreach ( var mod in manifests.Where( mod => !duplicates.Contains( mod.Name ) ).OrderByDescending( mod => mod.Priority ).ThenBy( mod => mod.Directory, StringComparer.Ordinal ) )
		{
			var vpks = new List<string>();
			using var vpkConfig = ReadJson( Path.Combine( mod.Directory, "vpk", "vpk.json" ), identities );
			foreach ( var path in Files( Path.Combine( mod.Directory, "vpk" ), recursive: false ).Where( path => path.EndsWith( ".bsp.pak000_dir.vpk", StringComparison.OrdinalIgnoreCase ) ) )
			{
				var name = Path.GetFileName( path );
				if ( !name.StartsWith( "english", StringComparison.OrdinalIgnoreCase ) ) continue;
				name = name[7..^".bsp.pak000_dir.vpk".Length];
				if ( vpkConfig is null || IsTrue( vpkConfig.RootElement, "Preload", name ) || name.Equals( "client_" + map, StringComparison.OrdinalIgnoreCase ) ) vpks.Add( path );
				else diagnostics.Add( $"{mod.Name}: deferred VPK {name}; select NORTHSTAR_MAP for map-specific content." );
			}
			var rpaks = SelectRpaks( mod.Directory, map, basePackages, identities, diagnostics, aliasMap );
			mods.Add( new( mod.Name, mod.Version, mod.Priority, mod.Directory, vpks.AsReadOnly(), rpaks ) );
		}
		Mods = mods.AsReadOnly(); Identities = identities.AsReadOnly(); Diagnostics = diagnostics.AsReadOnly();
	}

	static IReadOnlyList<string> SelectRpaks( string directory, string map, IEnumerable<string> basePackages, List<string> identities, List<string> diagnostics, Dictionary<string, string> aliasMap )
	{
		var folder = Path.Combine( directory, "paks" );
		var files = Files( folder, recursive: false ).Where( path => path.EndsWith( ".rpak", StringComparison.OrdinalIgnoreCase ) ).ToDictionary( Path.GetFileName, StringComparer.OrdinalIgnoreCase );
		using var config = ReadJson( Path.Combine( folder, "rpak.json" ), identities );
		var selected = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
		var baseNames = basePackages.Select( PackageBaseName ).ToHashSet( StringComparer.OrdinalIgnoreCase );
		var loaded = new HashSet<string>( baseNames, StringComparer.OrdinalIgnoreCase );
		if ( !string.IsNullOrEmpty( map ) )
		{
			var mapPak = map + ".rpak";
			if ( !baseNames.Contains( mapPak ) && files.ContainsKey( mapPak ) ) selected.Add( mapPak );
			loaded.Add( mapPak );
		}
		if ( config is not null )
		{
			var json = config.RootElement;
			foreach ( var name in files.Keys )
			{
				if ( IsTrue( json, "Preload", name ) ) selected.Add( name );
				else if ( !string.IsNullOrEmpty( map ) && json.TryGetProperty( name, out var expression ) && expression.ValueKind == JsonValueKind.String
					&& Regex.IsMatch( map, "\\A(?:" + expression.GetString() + ")\\z", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds( 100 ) ) ) selected.Add( name );
			}
			if ( json.TryGetProperty( "Aliases", out var aliases ) && aliases.ValueKind == JsonValueKind.Object )
				foreach ( var alias in aliases.EnumerateObject() )
				{
					if ( alias.Value.ValueKind != JsonValueKind.String ) throw new InvalidDataException( "RPAK aliases must be strings." );
					var target = alias.Value.GetString();
					if ( !Titanfall2ContentSnapshot.TryNormalize( alias.Name, out var from ) || !Titanfall2ContentSnapshot.TryNormalize( target, out var to )
						|| from.Contains( '/' ) || to.Contains( '/' ) || !to.EndsWith( ".rpak", StringComparison.Ordinal ) ) throw new InvalidDataException( "RPAK aliases must name packages, not host paths." );
					if ( !aliasMap.TryAdd( from, to ) || !loaded.Contains( from ) ) continue;
					if ( files.ContainsKey( to ) ) selected.Add( to );
					else if ( !loaded.Contains( to ) ) throw new InvalidDataException( $"Northstar RPAK alias target not found in {directory}: {target}" );
					loaded.Remove( from ); loaded.Add( to );
				}
			bool changed;
			do
			{
				changed = false;
				loaded.UnionWith( selected );
				if ( !json.TryGetProperty( "Postload", out var postload ) || postload.ValueKind != JsonValueKind.Object ) break;
				foreach ( var name in files.Keys )
					if ( postload.TryGetProperty( name, out var dependency ) && dependency.ValueKind == JsonValueKind.String
						&& loaded.Contains( dependency.GetString() ) ) changed |= selected.Add( name );
			} while ( changed );
		}
		foreach ( var name in files.Keys.Where( name => !selected.Contains( name ) ) ) diagnostics.Add( $"{directory}: RPAK {name} has no active Preload/Postload/map rule; not indexed." );
		return Array.AsReadOnly( selected.OrderByDescending( Titanfall2Mount.GetPatchRank ).ThenBy( name => name, StringComparer.OrdinalIgnoreCase ).Select( name => files[name] ).ToArray() );
	}

	internal static string PackageBaseName( string path ) => Regex.Replace( Path.GetFileName( path ), @"\(\d\d\)(?=\.rpak$)", "", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds( 100 ) );

	static bool IsTrue( JsonElement json, string section, string name ) => json.TryGetProperty( section, out var values )
		&& values.ValueKind == JsonValueKind.Object && values.TryGetProperty( name, out var value ) && value.ValueKind == JsonValueKind.True;

	static JsonDocument ReadJson( string path, List<string> identities )
	{
		if ( !File.Exists( path ) ) { identities.Add( $"missing:{path}" ); return null; }
		if ( new FileInfo( path ).Length > 4 * 1024 * 1024 ) throw new InvalidDataException( $"Manifest too large: {path}" );
		var source = new Titanfall2SnapshotFile( Path.GetDirectoryName( path ), path );
		if ( !source.TryReadAllBytes( out var bytes, out var error ) ) throw new InvalidDataException( error );
		if ( bytes.Length > 4 * 1024 * 1024 ) throw new InvalidDataException( $"Manifest too large: {path}" );
		identities.Add( source.CacheIdentity );
		if ( bytes.AsSpan().StartsWith( new byte[] { 0xef, 0xbb, 0xbf } ) ) bytes = bytes[3..];
		var document = JsonDocument.Parse( bytes, JsonOptions );
		if ( document.RootElement.ValueKind == JsonValueKind.Object ) return document;
		document.Dispose(); throw new InvalidDataException( $"Expected JSON object: {path}" );
	}

	internal static IEnumerable<string> Directories( string root )
	{
		if ( !System.IO.Directory.Exists( root ) ) yield break;
		RejectReparse( root );
		foreach ( var path in System.IO.Directory.EnumerateDirectories( root ).Order( StringComparer.Ordinal ) )
		{
			RejectReparse( path ); yield return path;
		}
	}

	internal static IEnumerable<string> Files( string root, bool recursive = true )
	{
		if ( !System.IO.Directory.Exists( root ) ) yield break;
		RejectReparse( root );
		foreach ( var path in System.IO.Directory.EnumerateFiles( root ).Order( StringComparer.Ordinal ) )
		{
			RejectReparse( path ); yield return path;
		}
		if ( recursive ) foreach ( var directory in Directories( root ) ) foreach ( var path in Files( directory ) ) yield return path;
	}

	internal static void RejectReparse( string path ) => Titanfall2SourceIdentity.RejectReparse( path );
}
