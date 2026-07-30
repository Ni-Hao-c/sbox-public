using System.Runtime.InteropServices;

/// <summary>
/// Loads the small MIT-licensed LZHAM bridge supplied with this mount. The bridge is
/// deliberately limited to the Titanfall 2 VPK profile (dictionary log2 = 20).
/// </summary>
static class LzhamDecoder
{
	[UnmanagedFunctionPointer( CallingConvention.Cdecl )]
	delegate int DecompressDelegate( byte[] source, uint sourceLength, byte[] destination, uint destinationLength, out uint written );

	static readonly object Sync = new();
	static DecompressDelegate _decompress;
	static string _loadError;

	public static bool TryDecompress( byte[] source, int destinationLength, out byte[] destination, out string error )
	{
		destination = null;
		error = null;
		if ( source is null || destinationLength <= 0 )
		{
			error = "Invalid LZHAM source or destination length.";
			return false;
		}

		if ( !TryGetDecoder( out var decoder, out error ) ) return false;
		var output = GC.AllocateUninitializedArray<byte>( destinationLength );
		try
		{
			if ( decoder( source, (uint)source.Length, output, (uint)output.Length, out var written ) == 0 )
			{
				error = "LZHAM rejected the VPK chunk.";
				return false;
			}

			if ( written != output.Length ) Array.Resize( ref output, checked((int)written) );
			destination = output;
			return true;
		}
		catch ( Exception e )
		{
			error = e.Message;
			return false;
		}
	}

	static bool TryGetDecoder( out DecompressDelegate decoder, out string error )
	{
		lock ( Sync )
		{
			if ( _decompress is not null )
			{
				decoder = _decompress;
				error = null;
				return true;
			}

			if ( _loadError is not null )
			{
				decoder = null;
				error = _loadError;
				return false;
			}

			var assemblyDirectory = Path.GetDirectoryName( typeof(LzhamDecoder).Assembly.Location );
			var bridgePath = Path.Combine( assemblyDirectory ?? string.Empty, "titanfall2_lzham.dll" );
			if ( !File.Exists( bridgePath ) )
			{
				decoder = null;
				return Fail( $"LZHAM bridge is missing: {bridgePath}", out error );
			}

			try
			{
				var library = NativeLibrary.Load( bridgePath );
				if ( !NativeLibrary.TryGetExport( library, "tf2_lzham_decompress", out var symbol ) )
				{
					decoder = null;
					return Fail( "LZHAM bridge does not export tf2_lzham_decompress.", out error );
				}

				_decompress = Marshal.GetDelegateForFunctionPointer<DecompressDelegate>( symbol );
				decoder = _decompress;
				error = null;
				return true;
			}
			catch ( Exception e )
			{
				decoder = null;
				return Fail( $"Unable to load LZHAM bridge: {e.Message}", out error );
			}
		}
	}

	static bool Fail( string message, out string error )
	{
		_loadError = message;
		error = message;
		return false;
	}
}
