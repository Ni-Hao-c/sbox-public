using Sandbox;

namespace Sandbox.Mounting.Helldivers2;

/// <summary>
/// Diagnostics for the Helldivers 2 mount. A full index of the game's containers walks
/// thousands of archives, so warnings are capped unless explicitly asked for in full.
/// </summary>
internal static class Hd2Log
{
	static readonly Sandbox.Diagnostics.Logger Log = new( "Helldivers2Mount" );
	static readonly bool Verbose = ReadBool( "SBOX_HELLDIVERS2_VERBOSE_LOG", false );
	static readonly int WarningLimit = ReadInt( "SBOX_HELLDIVERS2_WARNING_LIMIT", 32, 0, 4096 );
	static int _warningCount;
	static int _suppressionNotice;

	public static bool VerboseEnabled => Verbose;

	public static void Info( string message )
	{
		if ( Verbose ) Log.Info( message );
	}

	public static void Always( string message ) => Log.Info( message );

	public static void Trace( string message )
	{
		if ( Verbose ) Log.Trace( message );
	}

	public static void Warning( string message )
	{
		if ( Verbose )
		{
			Log.Warning( message );
			return;
		}

		var count = System.Threading.Interlocked.Increment( ref _warningCount );
		if ( count <= WarningLimit )
		{
			Log.Warning( message );
			return;
		}

		if ( System.Threading.Interlocked.Exchange( ref _suppressionNotice, 1 ) == 0 )
			Log.Warning( $"Helldivers 2 mount warning limit reached ({WarningLimit}); further mount warnings are suppressed. Set SBOX_HELLDIVERS2_VERBOSE_LOG=1 for full diagnostics." );
	}

	public static void Warning( Exception exception, string message )
	{
		if ( Verbose )
		{
			Log.Warning( exception, message );
			return;
		}

		var count = System.Threading.Interlocked.Increment( ref _warningCount );
		if ( count <= WarningLimit )
		{
			Log.Warning( exception, message );
			return;
		}

		if ( System.Threading.Interlocked.Exchange( ref _suppressionNotice, 1 ) == 0 )
			Log.Warning( $"Helldivers 2 mount warning limit reached ({WarningLimit}); further mount warnings are suppressed. Set SBOX_HELLDIVERS2_VERBOSE_LOG=1 for full diagnostics." );
	}

	public static void Error( Exception exception, string message ) => Log.Error( exception, message );

	public static void Error( string message ) => Log.Error( message );

	static bool ReadBool( string name, bool fallback )
	{
		var value = Environment.GetEnvironmentVariable( name )?.Trim();
		if ( string.IsNullOrWhiteSpace( value ) ) return fallback;

		return value == "1" || value.Equals( "true", StringComparison.OrdinalIgnoreCase )
			|| value.Equals( "yes", StringComparison.OrdinalIgnoreCase )
			|| value.Equals( "on", StringComparison.OrdinalIgnoreCase );
	}

	static int ReadInt( string name, int fallback, int minimum, int maximum )
	{
		return int.TryParse( Environment.GetEnvironmentVariable( name ), out var value )
			? Math.Clamp( value, minimum, maximum )
			: fallback;
	}
}
