using System.Security.Cryptography;
using System.Text.Json;
using static Facepunch.Constants;

namespace Facepunch.Steps;

internal class BuildTitanfall2( string engine, string output )
{
	internal ExitCode Run() => BuildDisplay.Run( "Build Titanfall 2 for official s&box", () =>
	{
		try
		{
			var root = Directory.GetCurrentDirectory();
			var engineRoot = Path.TrimEndingDirectorySeparator( Path.GetFullPath( engine ) );
			var package = Path.TrimEndingDirectorySeparator( Path.GetFullPath( output ) );
			var managed = Path.Combine( engineRoot, "bin", "managed" );
			var references = new[] { "Sandbox.Engine", "Sandbox.System", "Sandbox.Filesystem", "Sandbox.Mounting", "Sandbox.Reflection" };
			if ( !File.Exists( Path.Combine( engineRoot, "sbox.exe" ) ) )
				throw new DirectoryNotFoundException( $"Not an s&box installation: {engineRoot}" );
			// Build into a new package, never into either engine's live mount directory.
			if ( Directory.Exists( package ) || File.Exists( package ) )
				throw new IOException( $"Output already exists; choose a new package directory: {package}" );
			if ( IsUnder( package, engineRoot ) || IsUnder( package, Path.Combine( root, "game" ) ) )
				throw new IOException( "Package output must be outside the engine installation and repository game directory." );

			var engineFiles = references.ToDictionary( name => name + ".dll", name => Hash( Path.Combine( managed, name + ".dll" ) ) );
			var assets = Path.Combine( root, "game", "mount", "titanfall2" );
			var bridges = new[] { "titanfall2_lzham.dll", "titanfall2_miles.dll" };
			foreach ( var bridge in bridges )
				if ( !File.Exists( Path.Combine( assets, bridge ) ) )
					throw new FileNotFoundException( $"Build the native Titanfall 2 bridge first: {bridge}" );

			Directory.CreateDirectory( package );
			var project = Path.Combine( root, "engine", "Mounting", "Sandbox.Mounting.Titanfall2", "Build", "Official", "Official.csproj" );
			BuildDisplay.Status( "Compile against official engine assemblies" );
			if ( !Utility.RunDotnetCommand( root, $"build \"{project}\" -c Release --nologo -p:SboxEngineRoot=\"{engineRoot}\" -o \"{package}\"" ) )
				return ExitCode.Failure;

			foreach ( var reference in engineFiles )
				if ( Hash( Path.Combine( managed, reference.Key ) ) != reference.Value )
					throw new IOException( "The official engine changed during compilation. Build a fresh package after Steam finishes updating." );

			File.Move( Path.Combine( package, "Sandbox.Mounting.Titanfall2.dll" ), Path.Combine( package, "titanfall2.dll" ) );
			foreach ( var bridge in bridges )
				File.Copy( Path.Combine( assets, bridge ), Path.Combine( package, bridge ) );
			// Compiled shaders from the source engine may have a different format. Ship
			// source only; compile with the target engine before runtime acceptance.
			var shaders = Path.Combine( assets, "Assets", "shaders" );
			foreach ( var source in Directory.EnumerateFiles( shaders, "*", SearchOption.AllDirectories )
				.Where( path => Path.GetExtension( path ) is ".shader" or ".hlsl" )
				.Where( path => !Path.GetRelativePath( shaders, path ).StartsWith( $"tests{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase ) ) )
			{
				var destination = Path.Combine( package, "Assets", "shaders", Path.GetRelativePath( shaders, source ) );
				Directory.CreateDirectory( Path.GetDirectoryName( destination ) );
				File.Copy( source, destination );
			}

			var files = Directory.EnumerateFiles( package, "*", SearchOption.AllDirectories ).Order()
				.Select( path => new { path = Path.GetRelativePath( package, path ).Replace( '\\', '/' ), sha256 = Hash( path ) } ).ToArray();
			var manifest = new
			{
				builtAtUtc = DateTimeOffset.UtcNow,
				engineRoot,
				engineVersion = File.Exists( Path.Combine( engineRoot, ".version" ) ) ? File.ReadAllLines( Path.Combine( engineRoot, ".version" ) ) : [],
				engineAssemblies = engineFiles,
				compiledShaders = false,
				files
			};
			File.WriteAllText( Path.Combine( package, "compatibility.json" ), JsonSerializer.Serialize( manifest, new JsonSerializerOptions { WriteIndented = true } ) );
			Log.Info( $"Titanfall 2 package: {package}. Shader sources included; deployment and target-engine shader validation are separate." );
			return ExitCode.Success;
		}
		catch ( Exception exception )
		{
			Log.Error( $"Titanfall 2 package failed: {exception.Message}" );
			return ExitCode.Failure;
		}
	} );

	static string Hash( string path )
	{
		using var stream = File.OpenRead( path );
		return Convert.ToHexString( SHA256.HashData( stream ) );
	}

	static bool IsUnder( string path, string parent ) =>
		path.Equals( parent, StringComparison.OrdinalIgnoreCase )
		|| path.StartsWith( Path.TrimEndingDirectorySeparator( parent ) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase );
}
