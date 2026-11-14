using System.Security.Cryptography;
using Titanfall2.Formats;

namespace Titanfall2Ag2;

public sealed record SourceFile( string Path, string Origin, byte[] Bytes )
{
	public string Sha256 => Convert.ToHexString( SHA256.HashData( Bytes ) );
}

/// <summary>Explicit ordered VPKs and/or an extracted content directory. Never modifies the input.</summary>
public sealed class ModelSource
{
	readonly string looseRoot;
	readonly List<VpkArchive> archives = [];

	public ModelSource( string looseRoot, IEnumerable<string> vpkDirectories )
	{
		this.looseRoot = looseRoot is null ? null : System.IO.Path.GetFullPath( looseRoot );
		foreach ( var path in vpkDirectories )
		{
			if ( !VpkArchive.TryOpen( System.IO.Path.GetFullPath( path ), out var archive, out var error ) )
				throw new InvalidDataException( $"{path}: {error}" );
			archives.Add( archive );
		}
	}

	public IEnumerable<string> ModelPaths => (looseRoot is null ? [] : Directory.EnumerateFiles( looseRoot, "*", SearchOption.AllDirectories )
		.Select( path => System.IO.Path.GetRelativePath( looseRoot, path ).Replace( '\\', '/' ) ))
		.Concat( archives.SelectMany( archive => archive.Entries.Select( entry => entry.Path ) ) )
		.Where( path => path.EndsWith( ".mdl", StringComparison.OrdinalIgnoreCase ) || path.EndsWith( ".rmdl", StringComparison.OrdinalIgnoreCase ) )
		.Distinct( StringComparer.OrdinalIgnoreCase ).Order( StringComparer.OrdinalIgnoreCase );

	public SourceFile Read( string path )
	{
		path = Normalize( path );
		if ( looseRoot is not null )
		{
			var full = Under( looseRoot, path );
			if ( File.Exists( full ) ) return new( path, full, File.ReadAllBytes( full ) );
		}
		foreach ( var archive in archives )
		{
			if ( !archive.TryGetEntry( path, out var entry ) ) continue;
			if ( !archive.TryReadAllBytes( entry, out var bytes, out var error ) )
				throw new IOException( $"{archive.DirectoryPath}: {path}: {error}" );
			return new( path, archive.DirectoryPath, bytes );
		}
		throw new FileNotFoundException( $"Model dependency not found: {path}. Supply its content root or additional --vpk directories." );
	}

	public static string Normalize( string path )
	{
		path = path.Replace( '\\', '/' );
		const string prefix = "mount://titanfall2/";
		if ( path.StartsWith( prefix, StringComparison.OrdinalIgnoreCase ) ) path = path[prefix.Length..];
		if ( path.EndsWith( ".vmdl", StringComparison.OrdinalIgnoreCase ) ) path = path[..^5];
		if ( string.IsNullOrWhiteSpace( path ) || path.StartsWith( '/' ) || path.Contains( ':' )
			|| path.Split( '/' ).Any( part => part is ".." or "." or "" ) )
			throw new ArgumentException( $"Expected a game-relative model path: {path}" );
		return path.ToLowerInvariant();
	}

	public static string Under( string root, string relative )
	{
		var full = System.IO.Path.GetFullPath( System.IO.Path.Combine( root, relative ) );
		if ( !full.StartsWith( System.IO.Path.TrimEndingDirectorySeparator( System.IO.Path.GetFullPath( root ) ) + System.IO.Path.DirectorySeparatorChar,
			StringComparison.OrdinalIgnoreCase ) ) throw new ArgumentException( "Path escapes the output directory." );
		return full;
	}
}
