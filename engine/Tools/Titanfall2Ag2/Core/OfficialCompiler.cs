using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace Titanfall2Ag2;

public sealed class OfficialCompiler( string engineRoot )
{
	public string EngineRoot { get; } = Path.GetFullPath( engineRoot );
	public string Executable => Path.Combine( EngineRoot, "bin", "win64", "resourcecompiler.exe" );

	public string Fingerprint()
	{
		if ( !File.Exists( Executable ) ) throw new FileNotFoundException( "Official resourcecompiler.exe is required.", Executable );
		var names = new[] { ".version", "bin/assettypes.txt", "bin/win64/resourcecompiler.exe", "bin/win64/resourcecompiler.dll",
			"bin/win64/animationsystem.dll", "bin/win64/datamodel.dll", "bin/win64/modeldoc_utils.dll", "bin/win64/meshsystem.dll" };
		using var hash = IncrementalHash.CreateHash( HashAlgorithmName.SHA256 );
		foreach ( var name in names )
		{
			hash.AppendData( Encoding.UTF8.GetBytes( name ) );
			var path = Path.Combine( EngineRoot, name );
			hash.AppendData( File.Exists( path ) ? SHA256.HashData( File.ReadAllBytes( path ) ) : Encoding.UTF8.GetBytes( "missing" ) );
		}
		return Convert.ToHexString( hash.GetHashAndReset() );
	}

	public async Task Compile( string assets, string[] relativePaths, string logPath, CancellationToken cancellationToken = default )
	{
		var start = new ProcessStartInfo( Executable )
		{
			UseShellExecute = false,
			CreateNoWindow = true,
			WorkingDirectory = Path.GetDirectoryName( Executable ),
			RedirectStandardOutput = true,
			RedirectStandardError = true
		};
		foreach ( var arg in new[] { "-f", "-noassert", "-skiprendersystem", "-searchpaths", $"tf2ag2|{assets};core|{Path.Combine( EngineRoot, "core" )}" }
			.Concat( relativePaths.Select( relative => ModelSource.Under( assets, relative ) ) ) ) start.ArgumentList.Add( arg );
		using var process = Process.Start( start ) ?? throw new IOException( "Could not start resource compiler." );
		var output = process.StandardOutput.ReadToEndAsync();
		var error = process.StandardError.ReadToEndAsync();
		using var timeout = CancellationTokenSource.CreateLinkedTokenSource( cancellationToken );
		timeout.CancelAfter( TimeSpan.FromMinutes( 5 ) );
		try { await process.WaitForExitAsync( timeout.Token ); }
		catch ( OperationCanceledException )
		{
			process.Kill( entireProcessTree: true );
			await process.WaitForExitAsync( CancellationToken.None );
			File.AppendAllText( logPath, await output + await error );
			throw;
		}
		var log = await output + await error;
		File.AppendAllText( logPath, log );
		// Some native compile failures still exit with 0. Verify every requested output as well.
		if ( process.ExitCode != 0 || log.Contains( "[FAIL]", StringComparison.Ordinal ) || relativePaths.Any( relative => !File.Exists( ModelSource.Under( assets, relative + "_c" ) ) ) )
			throw new IOException( $"Resource compilation failed. See {logPath}\n{log}" );
	}
}
