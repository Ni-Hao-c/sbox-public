using System.Text.Json;
using Titanfall2Ag2;

if ( args.Length == 0 || args.Contains( "--help" ) )
{
	Console.WriteLine( """
		Titanfall2Ag2 — standalone model/skeleton/animation export with a verified disk cache
		  list    --root <extracted-content> [--vpk <directory.vpk>] [--match <substring>]
		  inspect --model <models/path.mdl> [--root <extracted-content>] [--vpk <directory.vpk> ...]
		  convert --model <models/path.mdl> [--root <extracted-content>] [--vpk <directory.vpk> ...]
		          --cache <directory> [--engine <sbox-install>] [--output <new-package-directory>]
		          [--sequence <exact-name> ...] [--body <packed-body>] [--skin <index>]
		Without --engine, writes editable sources only. With --engine, also compiles the package.
		Loose files win, followed by VPKs in the supplied order. Includes must be resolvable.
		Materials are neutral placeholders. Additive clips are reported as unsupported.
		""" );
	return 0;
}

try
{
	var command = args[0];
	if ( command is not ("list" or "inspect" or "convert") ) throw new ArgumentException( $"Unknown command: {command}" );
	var options = new Dictionary<string, List<string>>( StringComparer.Ordinal );
	var allowed = new[] { "--root", "--vpk", "--model", "--cache", "--engine", "--output", "--sequence", "--body", "--skin", "--match" };
	for ( var i = 1; i < args.Length; i += 2 )
	{
		if ( !allowed.Contains( args[i] ) || i + 1 >= args.Length ) throw new ArgumentException( $"Unknown option or missing value: {args[i]}" );
		if ( !options.TryGetValue( args[i], out var values ) ) options[args[i]] = values = [];
		values.Add( args[i + 1] );
	}
	string One( string name, bool required = false )
	{
		if ( !options.TryGetValue( name, out var values ) ) return required ? throw new ArgumentException( $"{name} is required." ) : null;
		return values.Count == 1 ? values[0] : throw new ArgumentException( $"{name} may only be specified once." );
	}
	string[] Many( string name ) => options.TryGetValue( name, out var values ) ? values.ToArray() : [];
	int Number( string name ) => One( name ) is { } value ? int.Parse( value, System.Globalization.CultureInfo.InvariantCulture ) : 0;
	var source = new ModelSource( One( "--root" ), Many( "--vpk" ) );
	if ( command == "list" )
	{
		foreach ( var path in source.ModelPaths.Where( path => One( "--match" ) is not { } match || path.Contains( match, StringComparison.OrdinalIgnoreCase ) ) ) Console.WriteLine( path );
		return 0;
	}
	var model = One( "--model", required: true );
	if ( command == "inspect" )
	{
		var inputs = ModelData.ReadDependencies( source, model );
		Console.WriteLine( JsonSerializer.Serialize( inputs.Select( input => new
		{
			input.File.Path,
			input.File.Origin,
			Bones = input.Model.Bones.Length,
			Sequences = input.Model.Sequences.Select( sequence => sequence.Label ),
			Animations = input.Model.Animations.Select( animation => new
			{
				animation.Name,
				animation.Animation.FrameCount,
				animation.Animation.FramesPerSecond,
				Additive = (animation.Animation.Flags & 4) != 0
			} )
		} ), new JsonSerializerOptions { WriteIndented = true } ) );
		return 0;
	}
	using var cancelled = new CancellationTokenSource();
	Console.CancelKeyPress += ( _, e ) => { e.Cancel = true; cancelled.Cancel(); };
	var result = await Converter.Run( source, new( model, One( "--cache", true ), One( "--engine" ), Many( "--sequence" ), Number( "--body" ), Number( "--skin" ) ), Console.WriteLine, cancelled.Token );
	if ( One( "--output" ) is { } output ) Converter.CopyPackage( result, output );
	Console.WriteLine( JsonSerializer.Serialize( new
	{
		result.CacheHit,
		result.Directory,
		result.Manifest.Compiled,
		result.Manifest.Model,
		result.Manifest.Skeleton,
		Bones = result.Manifest.Bones,
		Clips = result.Manifest.Clips.Length,
		result.Manifest.Warnings
	}, new JsonSerializerOptions { WriteIndented = true } ) );
	return 0;
}
catch ( Exception e )
{
	Console.Error.WriteLine( $"Conversion failed: {e.Message}" );
	return 1;
}
