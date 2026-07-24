using System.Runtime.InteropServices;
using System.Text;

/// <summary>Calls the local bridge, which in turn loads Titanfall 2's own Miles/Bink DLLs.</summary>
static class MilesBinkDecoder
{
	[UnmanagedFunctionPointer( CallingConvention.Cdecl, CharSet = CharSet.Ansi )]
	delegate int DecodeDelegate(
		[MarshalAs( UnmanagedType.LPWStr )] string gameRoot,
		byte[] source,
		uint sourceLength,
		out nint outputPcm,
		out uint outputFrames,
		out uint outputRate,
		out ushort outputChannels,
		StringBuilder error,
		uint errorCapacity );

	[UnmanagedFunctionPointer( CallingConvention.Cdecl )]
	delegate void FreeDelegate( nint memory );

	static readonly object Sync = new();
	static DecodeDelegate _decode;
	static FreeDelegate _free;
	static string _loadError;

	public static bool TryDecode( string gameRoot, byte[] source, out byte[] pcm, out uint rate, out ushort channels, out string error )
	{
		pcm = null;
		rate = 0;
		channels = 0;
		error = null;
		if ( source is null || source.Length < 24 )
		{
			error = "Invalid BCF source.";
			return false;
		}
		if ( !TryGetDecoder( out var decode, out var free, out error ) ) return false;

		var nativePcm = nint.Zero;
		try
		{
			var errorBuffer = new StringBuilder( 1024 );
			if ( decode( gameRoot, source, (uint)source.Length, out nativePcm, out var frames, out rate, out channels, errorBuffer, (uint)errorBuffer.Capacity ) == 0 )
			{
				error = errorBuffer.Length == 0 ? "Miles/Bink decode failed." : errorBuffer.ToString();
				return false;
			}
			var byteCount = checked((int)((ulong)frames * channels * sizeof(short)));
			pcm = GC.AllocateUninitializedArray<byte>( byteCount );
			Marshal.Copy( nativePcm, pcm, 0, byteCount );
			return true;
		}
		catch ( Exception e )
		{
			error = e.Message;
			return false;
		}
		finally
		{
			if ( nativePcm != nint.Zero ) free( nativePcm );
		}
	}

	static bool TryGetDecoder( out DecodeDelegate decode, out FreeDelegate free, out string error )
	{
		lock ( Sync )
		{
			if ( _decode is not null )
			{
				decode = _decode;
				free = _free;
				error = null;
				return true;
			}
			if ( _loadError is not null )
			{
				decode = null;
				free = null;
				error = _loadError;
				return false;
			}

			var directory = Path.GetDirectoryName( typeof(MilesBinkDecoder).Assembly.Location );
			var bridgePath = Path.Combine( directory ?? string.Empty, "titanfall2_miles.dll" );
			if ( !File.Exists( bridgePath ) ) return Fail( $"Miles bridge is missing: {bridgePath}", out decode, out free, out error );
			try
			{
				var library = NativeLibrary.Load( bridgePath );
				if ( !NativeLibrary.TryGetExport( library, "tf2_miles_decode", out var decodeSymbol ) ||
					!NativeLibrary.TryGetExport( library, "tf2_miles_free", out var freeSymbol ) )
					return Fail( "Miles bridge exports are incomplete.", out decode, out free, out error );
				_decode = Marshal.GetDelegateForFunctionPointer<DecodeDelegate>( decodeSymbol );
				_free = Marshal.GetDelegateForFunctionPointer<FreeDelegate>( freeSymbol );
				decode = _decode;
				free = _free;
				error = null;
				return true;
			}
			catch ( Exception e )
			{
				return Fail( $"Unable to load the Miles bridge: {e.Message}", out decode, out free, out error );
			}
		}
	}

	static bool Fail( string message, out DecodeDelegate decode, out FreeDelegate free, out string error )
	{
		_loadError = message;
		decode = null;
		free = null;
		error = message;
		return false;
	}
}
