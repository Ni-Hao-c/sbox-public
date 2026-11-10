/// <summary>Pure filesystem version helpers shared by container readers and discovery.</summary>
static class Titanfall2SourceIdentity
{
	internal static string Stamp( string path )
	{
		var info = new FileInfo( path );
		return $"{info.FullName}:{info.Length}:{info.LastWriteTimeUtc.Ticks}";
	}

	internal static void RejectReparse( string path )
	{
		for ( var current = Path.GetFullPath( path ); current is not null; current = Path.GetDirectoryName( current ) )
			if ( (File.GetAttributes( current ) & FileAttributes.ReparsePoint) != 0 ) throw new InvalidDataException( $"Reparse point in content: {current}" );
	}
}
