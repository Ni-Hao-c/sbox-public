using Sandbox;

/// <summary>
/// Keeps the mounted game's diagnostics useful without flooding the game log
/// during a large map import. Errors and the first few warnings remain visible;
/// detailed lifecycle/trace output can be enabled for a focused investigation.
/// </summary>
internal static class Titanfall2Log
{
	static readonly Sandbox.Diagnostics.Logger Log = new( "Titanfall2Mount" );
	static readonly bool Verbose = ReadBool( "SBOX_TITANFALL2_VERBOSE_LOG", false );
	static readonly int WarningLimit = ReadInt( "SBOX_TITANFALL2_WARNING_LIMIT", 32, 0, 1024 );
	static int _warningCount;
	static int _suppressionNotice;

	public static void Info( string message )
	{
		if ( Verbose ) Log.Info( message );
	}

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
			Log.Warning( $"Titanfall 2 mount warning limit reached ({WarningLimit}); additional mount warnings are suppressed. Set SBOX_TITANFALL2_VERBOSE_LOG=1 for full diagnostics." );
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
			Log.Warning( $"Titanfall 2 mount warning limit reached ({WarningLimit}); additional mount warnings are suppressed. Set SBOX_TITANFALL2_VERBOSE_LOG=1 for full diagnostics." );
	}

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
