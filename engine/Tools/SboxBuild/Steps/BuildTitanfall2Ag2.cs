using static Facepunch.Constants;

namespace Facepunch.Steps;

/// <summary>Builds/tests the independent exporter without building or deploying engine assemblies.</summary>
internal class BuildTitanfall2Ag2( bool format = false, string output = null )
{
	internal ExitCode Run() => BuildDisplay.Run( "Build and test standalone Titanfall 2 AG2 converter", () =>
	{
		var root = Directory.GetCurrentDirectory();
		var project = Path.Combine( root, "engine", "Tools", "Titanfall2Ag2" );
		if ( !Utility.RunDotnetCommand( root, $"build \"{Path.Combine( project, "Titanfall2Ag2.slnx" )}\" -c Release --nologo" ) ) return ExitCode.Failure;
		if ( format && !Utility.RunDotnetCommand( root, $"format whitespace \"{Path.Combine( project, "Titanfall2Ag2.slnx" )}\" --no-restore --exclude \"{Path.Combine( root, "engine", "Mounting" )}\"" ) ) return ExitCode.Failure;
		if ( !Utility.RunDotnetCommand( root, $"test \"{Path.Combine( project, "Tests", "Titanfall2Ag2.Tests.csproj" )}\" -c Release --no-build --nologo" ) ) return ExitCode.Failure;
		if ( !Utility.RunDotnetCommand( root, $"format whitespace \"{Path.Combine( project, "Titanfall2Ag2.slnx" )}\" --verify-no-changes --no-restore --exclude \"{Path.Combine( root, "engine", "Mounting" )}\"" ) ) return ExitCode.Failure;
		if ( output is not null )
		{
			var destination = Path.GetFullPath( output );
			if ( destination.StartsWith( Path.GetFullPath( project ) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase ) )
			{
				Log.Error( "Package output must be outside the source project." );
				return ExitCode.Failure;
			}
			if ( Directory.Exists( destination ) || File.Exists( destination ) )
			{
				Log.Error( "Package output already exists; choose a new directory." );
				return ExitCode.Failure;
			}
			void Copy( string source, string relative )
			{
				var path = Path.Combine( destination, relative );
				Directory.CreateDirectory( Path.GetDirectoryName( path ) );
				File.Copy( source, path );
			}
			foreach ( var file in Directory.EnumerateFiles( project, "*", SearchOption.AllDirectories )
				.Where( file => !Path.GetRelativePath( project, file ).Split( Path.DirectorySeparatorChar ).Any( part => part is "bin" or "obj" or "TestResults" ) ) )
				Copy( file, Path.Combine( "Source", Path.GetRelativePath( project, file ) ) );
			var formats = Path.Combine( root, "engine", "Mounting", "Sandbox.Mounting.Titanfall2", "Formats" );
			foreach ( var file in Directory.EnumerateFiles( Path.Combine( formats, "Mdl" ), "*.cs" ).Where( file => !file.EndsWith( "PhysicsReader.cs" ) )
				.Concat( [Path.Combine( formats, "Vpk", "VpkArchive.cs" ), Path.Combine( formats, "Rpak", "LzhamDecoder.cs" )] ) )
				Copy( file, Path.Combine( "Source", "Decoders", Path.GetRelativePath( formats, file ) ) );
			Copy( Path.Combine( root, "game", "mount", "titanfall2", "titanfall2_lzham.dll" ), "Source/titanfall2_lzham.dll" );
			File.WriteAllText( Path.Combine( destination, "Source", "Directory.Build.props" ), """
				<Project><PropertyGroup>
				  <SboxTf2FormatsRoot>$(MSBuildThisFileDirectory)Decoders</SboxTf2FormatsRoot>
				  <SboxTf2LzhamBridge>$(MSBuildThisFileDirectory)titanfall2_lzham.dll</SboxTf2LzhamBridge>
				</PropertyGroup></Project>
				""" );
			foreach ( var file in Directory.EnumerateFiles( Path.Combine( project, "Cli", "bin", "Release", "net10.0" ) ) ) Copy( file, Path.Combine( "Tool", Path.GetFileName( file ) ) );
			if ( !Utility.RunDotnetCommand( destination, "build Source/Titanfall2Ag2.slnx -c Release --nologo" ) ) return ExitCode.Failure;
			File.WriteAllText( Path.Combine( destination, "Source", "BUILD.md" ), "Independent source snapshot, including the shared decoder sources. Build with: dotnet build Titanfall2Ag2.slnx -c Release\nRun tests with: dotnet test Tests/Titanfall2Ag2.Tests.csproj -c Release\n" );
			Log.Info( $"Portable tool and independent source: {destination}" );
		}
		return ExitCode.Success;
	} );
}
