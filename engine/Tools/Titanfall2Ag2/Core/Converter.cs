using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Titanfall2.Formats;

namespace Titanfall2Ag2;

public sealed record ConvertOptions( string Model, string CacheRoot, string EngineRoot, string[] Sequences, int Body = 0, int Skin = 0 );
public sealed record OutputFile( string Path, string Sha256 );
public sealed record InputFile( string Path, string Origin, string Sha256 );
public sealed record ClipInfo( string Name, string Path, float FrameRate, int FrameCount, bool Looping );
public sealed record MaterialInfo( string Source, string Path );
public sealed record ConversionManifest( string CacheKey, string Converter, string Compiler, bool Compiled, string Model, string Skeleton,
	InputFile[] Inputs, OutputFile[] Outputs, ClipInfo[] Clips, MaterialInfo[] Materials, string[] Warnings, int Bones, int Vertices, int Triangles );
public sealed record ConvertResult( string Directory, bool CacheHit, ConversionManifest Manifest );

public static class Converter
{
	static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
	public static string Hash( string path )
	{
		using var stream = File.OpenRead( path );
		return Convert.ToHexString( SHA256.HashData( stream ) );
	}
	public static async Task<ConvertResult> Run( ModelSource source, ConvertOptions options, Action<string> progress = null, CancellationToken cancellationToken = default )
	{
		ArgumentOutOfRangeException.ThrowIfNegative( options.Body );
		ArgumentOutOfRangeException.ThrowIfNegative( options.Skin );
		var inputs = ModelData.ReadDependencies( source, options.Model );
		var compiler = options.EngineRoot is null ? null : new OfficialCompiler( options.EngineRoot );
		var compilerId = compiler?.Fingerprint() ?? "source-only";
		var converterId = Hash( typeof( Converter ).Assembly.Location );
		var inputFiles = inputs.Select( input => new InputFile( input.File.Path, input.File.Origin, input.File.Sha256 ) ).ToArray();
		var identity = JsonSerializer.Serialize( new
		{
			Schema = 1,
			Converter = converterId,
			Compiler = compilerId,
			Inputs = inputFiles.Select( input => new { input.Path, input.Sha256 } ),
			options.Body,
			options.Skin,
			Sequences = options.Sequences.Order( StringComparer.OrdinalIgnoreCase ).Select( name => name.ToLowerInvariant() )
		} );
		var key = Convert.ToHexString( SHA256.HashData( Encoding.UTF8.GetBytes( identity ) ) ).ToLowerInvariant();
		var cache = Path.GetFullPath( options.CacheRoot );
		Directory.CreateDirectory( cache );
		using var gate = await Lock( Path.Combine( cache, key + ".lock" ), cancellationToken );
		var destination = Path.Combine( cache, key );
		var manifest = ReadValid( destination, key );
		if ( manifest is not null ) return new( destination, true, manifest );
		// Never overwrite a damaged/user-edited entry: retain it for inspection and publish a fresh generation.
		if ( Directory.Exists( destination ) ) Directory.Move( destination, destination + ".invalid-" + Guid.NewGuid().ToString( "N" ) );
		var staging = Path.Combine( cache, ".work-" + Guid.NewGuid().ToString( "N" )[..12] );
		var assets = Path.Combine( staging, "Assets" );
		var stem = $"models/tf2ag2/{key[..16]}/model";
		void Write( string relative, string text )
		{
			var path = ModelSource.Under( assets, relative );
			Directory.CreateDirectory( Path.GetDirectoryName( path ) );
			File.WriteAllText( path, text, new UTF8Encoding( false ) );
		}
		progress?.Invoke( "Export model geometry and skeleton" );
		var parsed = inputs[0].Model;
		var bones = ModelData.Skeleton( parsed );
		var geometry = Titanfall2MeshExtractor.ExtractLod( parsed, body: options.Body, skin: options.Skin );
		if ( geometry.VertexCount == 0 ) throw new InvalidDataException( "The model has no exportable LOD0 geometry." );
		var warnings = new List<string> { "Materials are editable neutral placeholders; original RPAK textures/materials are not exported.",
			"Exports LOD0 and the selected packed body/skin. Collision, morphs, IK rules, events and original gameplay graphs are not reconstructed." };
		var materials = geometry.Meshes.Select( ( mesh, i ) => new MaterialInfo( mesh.MaterialName ?? "", $"{stem}_materials/slot_{i}.vmat" ) ).ToArray();
		foreach ( var material in materials ) Write( material.Path, SourceDocuments.Material );
		Write( stem + ".dmx", new DmxWriter().Write( bones, geometry, materials.Select( m => m.Path ).ToArray() ) );
		Write( stem + ".skel", SourceDocuments.Skeleton( stem + ".dmx" ) );
		Write( stem + ".vmdl", SourceDocuments.Model( stem + ".dmx", stem + ".skel" ) );
		Write( stem + ".data.json", JsonSerializer.Serialize( new
		{
			Source = inputs[0].File.Path,
			options.Body,
			options.Skin,
			Bones = bones.Select( ( bone, index ) => new
			{
				Index = index,
				bone.Name,
				bone.Parent,
				Position = new[] { bone.Reference.Position.X, bone.Reference.Position.Y, bone.Reference.Position.Z },
				Rotation = new[] { bone.Reference.Rotation.X, bone.Reference.Rotation.Y, bone.Reference.Rotation.Z, bone.Reference.Rotation.W },
				Scale = new[] { bone.Reference.Scale.X, bone.Reference.Scale.Y, bone.Reference.Scale.Z }
			} ),
			Meshes = geometry.Meshes.Select( ( mesh, index ) => new
			{
				mesh.BodyPartName,
				mesh.ModelName,
				mesh.MaterialName,
				Material = materials[index].Path,
				Vertices = mesh.Vertices.Length,
				mesh.TriangleCount
			} ),
			Includes = inputs.Select( input => new
			{
				input.File.Path,
				Bones = input.Model.Bones.Select( bone => bone.Name ),
				Sequences = input.Model.Sequences.Select( sequence => new { sequence.Label, sequence.AnimationIndices } )
			} )
		}, JsonOptions ) );
		var clips = new List<ClipInfo>();
		foreach ( var animation in ModelData.Animations( inputs, bones, options.Sequences, warnings ) )
		{
			cancellationToken.ThrowIfCancellationRequested();
			var clipPath = $"{stem}_animations/clip_{clips.Count:D4}";
			progress?.Invoke( $"Export animation: {animation.Name}" );
			Write( clipPath + ".dmx", new DmxWriter().Write( bones, animation: animation ) );
			Write( clipPath + ".anim", SourceDocuments.Clip( clipPath + ".dmx", stem + ".skel", animation.FrameRate ) );
			clips.Add( new( animation.Name, clipPath + ".anim", animation.FrameRate, animation.Frames.Length, animation.Looping ) );
		}
		if ( compiler is not null )
		{
			var logPath = Path.Combine( staging, "compile.log" );
			progress?.Invoke( "Compile skeleton and material slots" );
			await compiler.Compile( assets, [stem + ".skel", .. materials.Select( m => m.Path )], logPath, cancellationToken );
			progress?.Invoke( "Compile model and AG2 clips" );
			await compiler.Compile( assets, [stem + ".vmdl", .. clips.Select( clip => clip.Path )], logPath, cancellationToken );
			if ( compiler.Fingerprint() != compilerId ) throw new IOException( "Engine files changed during compilation; retry after the update finishes." );
		}
		var files = Directory.EnumerateFiles( assets, "*", SearchOption.AllDirectories ).Order( StringComparer.Ordinal )
			.Select( file => new OutputFile( Path.GetRelativePath( staging, file ).Replace( '\\', '/' ), Hash( file ) ) ).ToArray();
		manifest = new( key, converterId, compilerId, compiler is not null, stem + ".vmdl", stem + ".skel", inputFiles, files,
			clips.ToArray(), materials, warnings.ToArray(), bones.Length, geometry.VertexCount, geometry.TriangleCount );
		File.WriteAllText( Path.Combine( staging, "manifest.json" ), JsonSerializer.Serialize( manifest, JsonOptions ) );
		File.WriteAllText( Path.Combine( staging, "IMPORT.md" ), $"Copy the entire Assets directory into the next s&box project's Assets. Keep relative paths intact.\n\nModel: {manifest.Model}\nSkeleton: {manifest.Skeleton}\nAnimation paths and original names: manifest.json / Clips.\n\n" + string.Join( "\n", warnings ) );
		Directory.Move( staging, destination );
		return new( destination, false, manifest );
	}

	public static ConversionManifest ReadValid( string directory, string key )
	{
		try
		{
			var path = Path.Combine( directory, "manifest.json" );
			if ( !File.Exists( path ) ) return null;
			var manifest = JsonSerializer.Deserialize<ConversionManifest>( File.ReadAllText( path ) );
			if ( manifest?.CacheKey != key || manifest.Outputs is not { Length: > 0 } ) return null;
			foreach ( var file in manifest.Outputs )
			{
				var full = ModelSource.Under( directory, file.Path );
				if ( !File.Exists( full ) || Hash( full ) != file.Sha256 ) return null;
			}
			return manifest;
		}
		catch ( Exception e ) when ( e is IOException or JsonException or ArgumentException ) { return null; }
	}

	static async Task<FileStream> Lock( string path, CancellationToken cancellationToken )
	{
		var deadline = DateTime.UtcNow.AddSeconds( 60 );
		while ( true )
		{
			cancellationToken.ThrowIfCancellationRequested();
			try { return new FileStream( path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None ); }
			catch ( IOException ) when ( DateTime.UtcNow < deadline ) { await Task.Delay( 100, cancellationToken ); }
		}
	}

	public static void CopyPackage( ConvertResult result, string output )
	{
		output = Path.GetFullPath( output );
		var source = Path.TrimEndingDirectorySeparator( Path.GetFullPath( result.Directory ) );
		if ( output.Equals( source, StringComparison.OrdinalIgnoreCase ) || output.StartsWith( source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase ) )
			throw new ArgumentException( "Package output must be outside the cache entry being copied." );
		if ( Directory.Exists( output ) || File.Exists( output ) ) throw new IOException( $"Output already exists; use a new directory: {output}" );
		Directory.CreateDirectory( output );
		foreach ( var file in Directory.EnumerateFiles( result.Directory, "*", SearchOption.AllDirectories ) )
		{
			var destination = ModelSource.Under( output, Path.GetRelativePath( result.Directory, file ) );
			Directory.CreateDirectory( Path.GetDirectoryName( destination ) );
			File.Copy( file, destination );
		}
		if ( ReadValid( output, result.Manifest.CacheKey ) is null ) throw new IOException( "Copied package failed its output checksums." );
	}
}
