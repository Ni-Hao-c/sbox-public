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
	static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();
	static int _warningCount;
	static int _suppressionNotice;
	static int _spikeCount;
	static int _spikeSuppressionNotice;

	/// <summary>
	/// Monotonic seconds since this assembly was first touched. Retry backoff uses
	/// this rather than wall-clock time so a system clock change cannot disable it.
	/// </summary>
	public static double ElapsedSeconds => Clock.Elapsed.TotalSeconds;

	/// <summary>True when the verbose log switch is on; lets callers skip building detail strings.</summary>
	public static bool VerboseLogging => Verbose;

	/// <summary>
	/// Reports one unit of work that exceeded its budget, with its own model path and
	/// duration. Deliberately not gated behind verbose logging: for performance work
	/// these lines are the measurement itself. It has a separate, wider quota than
	/// <see cref="Warning(string)"/> because a sustained hitch would otherwise be reported
	/// once per frame.
	/// </summary>
	public static void Spike( string message )
	{
		var limit = Titanfall2StreamingSettings.SpikeLogLimit;
		if ( limit <= 0 ) return;

		var count = System.Threading.Interlocked.Increment( ref _spikeCount );
		if ( count <= limit )
		{
			Log.Warning( $"[spike] {message}" );
			return;
		}

		if ( System.Threading.Interlocked.Exchange( ref _spikeSuppressionNotice, 1 ) == 0 )
			Log.Warning( $"[spike] Titanfall 2 spike report limit reached ({limit}); further spikes are counted but not logged. Set SBOX_TITANFALL2_SPIKE_LOG_LIMIT to raise it." );
	}

	/// <summary>
	/// Logs one measured unit of work when it exceeded its budget. Cheap enough to call
	/// on every unit: the caller passes a shared stopwatch that it restarts in place, and
	/// the description is only formatted after the threshold check (the generic overloads
	/// take a format string plus arguments, so the fast path allocates nothing).
	/// </summary>
	public static void ReportSpike( System.Diagnostics.Stopwatch probe, string description )
	{
		var elapsed = probe.Elapsed.TotalMilliseconds;
		if ( elapsed < Titanfall2StreamingSettings.SpikeThresholdMilliseconds ) return;
		Spike( $"{elapsed:0.00} ms in {description}." );
	}

	public static void ReportSpike<T1>( System.Diagnostics.Stopwatch probe, string format, T1 value )
	{
		var elapsed = probe.Elapsed.TotalMilliseconds;
		if ( elapsed < Titanfall2StreamingSettings.SpikeThresholdMilliseconds ) return;
		Spike( $"{elapsed:0.00} ms in {Format( format, value )}." );
	}

	public static void ReportSpike<T1, T2>( System.Diagnostics.Stopwatch probe, string format, T1 first, T2 second )
	{
		var elapsed = probe.Elapsed.TotalMilliseconds;
		if ( elapsed < Titanfall2StreamingSettings.SpikeThresholdMilliseconds ) return;
		Spike( $"{elapsed:0.00} ms in {Format( format, first, second )}." );
	}

	public static void ReportSpike<T1, T2, T3>( System.Diagnostics.Stopwatch probe, string format, T1 first, T2 second, T3 third )
	{
		var elapsed = probe.Elapsed.TotalMilliseconds;
		if ( elapsed < Titanfall2StreamingSettings.SpikeThresholdMilliseconds ) return;
		Spike( $"{elapsed:0.00} ms in {Format( format, first, second, third )}." );
	}

	static string Format( string format, params object[] arguments ) =>
		string.Format( System.Globalization.CultureInfo.InvariantCulture, format, arguments );

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
