namespace Sandbox.Mounting.Helldivers2;

// Every disposable handle this file opens is registered in _openedDsar (or _patchStorages) and
// released by Dispose, so ownership deliberately leaves the creating scope.
#pragma warning disable CA2000

/// <summary>
/// Indexes Helldivers 2's <c>data</c> folder. The install keeps almost everything inside 30
/// multi-gigabyte <c>bundles.NN.nxa</c> files, indexed by <c>bundles.nxa</c>; a handful of
/// extension-less 16-hex-name DSAR files and <c>.patch_N</c> containers override those (Steam
/// updates and mods), in that order of increasing priority.
/// </summary>
internal sealed class Hd2DataDirectory : IDisposable
{
	public const long AppId = 553850;
	public const string GameFolderName = "Helldivers 2";
	public const string DataFolderName = "data";

	readonly string _root;
	readonly List<Hd2DsarFile> _openedDsar = [];
	readonly List<Hd2RawArchiveStorage> _patchStorages = [];
	readonly Dictionary<Hd2Hash, Hd2ArchiveStorage> _storageByArchive = [];

	readonly List<(Hd2ArchiveTable Table, Hd2ArchiveStorage Storage)> _nxaArchives = [];
	readonly List<(Hd2ArchiveTable Table, Hd2ArchiveStorage Storage)> _looseArchives = [];
	readonly List<(Hd2ArchiveTable Table, Hd2ArchiveStorage Storage)> _patchArchives = [];

	readonly List<Hd2Resource> _resources = [];
	readonly Dictionary<ulong, List<int>> _resourceIndicesByType = [];
	readonly Dictionary<ulong, int> _resourceIndexByName = [];

	Hd2Dsaa _index;
	Hd2DsarFile[] _nxaFiles = [];

	public string Root => _root;

	public IReadOnlyList<Hd2Resource> Resources => _resources;

	public Hd2Dsaa Index => _index;

	public int NxaBundleCount => _nxaFiles.Length;

	public int ArchiveCount => _nxaArchives.Count + _looseArchives.Count + _patchArchives.Count;

	public int LooseArchiveCount => _looseArchives.Count;

	public int PatchCount => _patchArchives.Count;

	public Hd2Resource GetResource( int index ) => _resources[index];

	/// <summary>Finds a resource by name hash alone; used when following a hash out of a payload.</summary>
	public bool TryGetResource( Hd2Hash name, out Hd2Resource resource )
	{
		if ( _resourceIndexByName.TryGetValue( name.Value, out var index ) )
		{
			resource = _resources[index];
			return true;
		}

		resource = default;
		return false;
	}

	/// <summary>
	/// Finds a resource by name hash and type. Names are unique in practice, but a name that also
	/// exists under a different type falls back to a scan of that type's list.
	/// </summary>
	public bool TryGetResource( Hd2Hash name, Hd2Hash type, out Hd2Resource resource )
	{
		if ( TryGetResource( name, out resource ) && resource.Type == type ) return true;

		if ( _resourceIndicesByType.TryGetValue( type.Value, out var indices ) )
		{
			foreach ( var index in indices )
			{
				if ( _resources[index].Name != name ) continue;

				resource = _resources[index];
				return true;
			}
		}

		resource = default;
		return false;
	}

	public bool TryGetTypeIndices( Hd2Hash type, out IReadOnlyList<int> indices )
	{
		if ( _resourceIndicesByType.TryGetValue( type.Value, out var list ) )
		{
			indices = list;
			return true;
		}

		indices = null;
		return false;
	}

	/// <summary>Distinct resource types present, with how many resources carry each.</summary>
	public IReadOnlyList<(Hd2Hash Type, int Count)> TypeHistogram()
	{
		var result = new List<(Hd2Hash, int)>( _resourceIndicesByType.Count );
		foreach ( var pair in _resourceIndicesByType ) result.Add( ( new Hd2Hash( pair.Key ), pair.Value.Count ) );
		result.Sort( static ( left, right ) => right.Item2.CompareTo( left.Item2 ) );
		return result;
	}

	public long ChunksRead => _openedDsar.Sum( static file => file.ChunksRead );

	public long BytesDecompressed => _openedDsar.Sum( static file => file.BytesDecompressed );

	public static bool TryOpen( string dataDirectory, out Hd2DataDirectory directory, out string error )
	{
		directory = null;
		error = null;

		try
		{
			var result = new Hd2DataDirectory( dataDirectory );
			if ( !result.BuildIndex( out error ) )
			{
				result.Dispose();
				return false;
			}

			directory = result;
			return true;
		}
		catch ( Exception exception )
		{
			error = exception.Message;
			return false;
		}
	}

	Hd2DataDirectory( string root )
	{
		_root = root;
	}

	bool BuildIndex( out string error )
	{
		error = null;

		var bundlesIndexPath = Path.Combine( _root, "bundles.nxa" );
		if ( !File.Exists( bundlesIndexPath ) )
		{
			error = $"'{bundlesIndexPath}' is missing; this does not look like an installed copy.";
			return false;
		}

		// bundles.nxa is a DSAR container whose payload is the whole index (DSAA).
		if ( !Hd2DsarFile.TryOpen( bundlesIndexPath, out var bundlesIndex, out error ) ) return false;
		_openedDsar.Add( bundlesIndex );

		var indexPayload = ReadWholeDsar( bundlesIndex );
		if ( indexPayload is null )
		{
			error = "unable to read the bundles.nxa index payload.";
			return false;
		}

		if ( !Hd2Dsaa.TryParse( indexPayload, out var index, out error ) ) return false;
		_index = index;

		_nxaFiles = new Hd2DsarFile[index.NxaFilenames.Length];
		for ( var i = 0; i < _nxaFiles.Length; i++ )
		{
			var path = Path.Combine( _root, index.NxaFilenames[i] );
			if ( !File.Exists( path ) )
			{
				Hd2Log.Warning( $"Referenced archive '{index.NxaFilenames[i]}' is missing from '{_root}'." );
				continue;
			}

			if ( !Hd2DsarFile.TryOpen( path, out var nxa, out var nxaError ) )
			{
				Hd2Log.Warning( $"Unable to open '{index.NxaFilenames[i]}': {nxaError}" );
				continue;
			}

			_nxaFiles[i] = nxa;
			_openedDsar.Add( nxa );
		}

		BuildArchiveStorageMap();
		IndexNxaArchives();
		IndexLooseBundles();
		IndexPatches();
		Flatten();

		return true;
	}

	/// <summary>Maps each archive name to the .nxa runs that hold its three data streams.</summary>
	void BuildArchiveStorageMap()
	{
		var itemIndices = new Dictionary<Hd2Hash, int[]>();

		for ( var i = 0; i < _index.Archives.Length; i++ )
		{
			var item = _index.Archives[i];
			if ( !Hd2Hash.TryParse( item.BundleName, out var hash ) ) continue;

			if ( !itemIndices.TryGetValue( hash, out var slots ) )
			{
				slots = [-1, -1, -1];
				itemIndices[hash] = slots;
			}

			var slot = DataTypeSlot( item.Filename );
			if ( slot >= 0 ) slots[slot] = i;
		}

		foreach ( var pair in itemIndices )
			_storageByArchive[pair.Key] = new Hd2NxaArchiveStorage( _index, _nxaFiles, pair.Value );
	}

	static int DataTypeSlot( string filename )
	{
		if ( filename.EndsWith( ".stream", StringComparison.OrdinalIgnoreCase ) ) return (int)Hd2DataType.Stream;
		if ( filename.EndsWith( ".gpu_resources", StringComparison.OrdinalIgnoreCase ) ) return (int)Hd2DataType.Gpu;
		return System.IO.Path.GetExtension( filename ).Length == 0 ? (int)Hd2DataType.Main : -1;
	}

	void IndexNxaArchives()
	{
		var limit = ReadInt( "SBOX_HELLDIVERS2_ARCHIVE_LIMIT", 0, 0, int.MaxValue );
		var considered = 0;
		var failures = 0;

		foreach ( var pair in _storageByArchive )
		{
			if ( limit > 0 && considered >= limit ) break;

			var hash = pair.Key;
			var storage = pair.Value;
			considered++;

			if ( !TryReadTable( storage, hash, out var table, out var tableError ) )
			{
				failures++;
				if ( failures <= 8 ) Hd2Log.Warning( $"Bundle {hash} has no readable resource table: {tableError}" );
				continue;
			}

			_nxaArchives.Add( ( table, storage ) );

			if ( Hd2Log.VerboseEnabled && _nxaArchives.Count % 500 == 0 )
				Hd2Log.Info( $"Indexed {_nxaArchives.Count} bundles, {failures} unreadable so far." );
		}

		Hd2Log.Always( $"Helldivers 2 bundle index: {_nxaArchives.Count} bundles read from {_nxaFiles.Length} .nxa archives "
			+ $"({_index.Archives.Length} archived data streams, {_storageByArchive.Count} archive names, {failures} unreadable)." );
	}

	void IndexLooseBundles()
	{
		foreach ( var path in System.IO.Directory.EnumerateFiles( _root ).OrderBy( static path => path, StringComparer.OrdinalIgnoreCase ) )
		{
			var name = Path.GetFileName( path );
			if ( name.Length != 16 || !Hd2Hash.TryParse( name, out var hash ) ) continue;

			if ( !Hd2DsarFile.TryOpen( path, out var main, out var openError ) )
			{
				Hd2Log.Warning( $"Unable to open loose bundle '{name}': {openError}" );
				continue;
			}

			var files = new Hd2DsarFile[3];
			files[(int)Hd2DataType.Main] = main;
			_openedDsar.Add( main );

			var missing = 0;
			foreach ( var type in new[] { Hd2DataType.Stream, Hd2DataType.Gpu } )
			{
				var companionPath = path + ( type == Hd2DataType.Stream ? ".stream" : ".gpu_resources" );
				if ( !File.Exists( companionPath ) )
				{
					missing++;
					continue;
				}

				if ( Hd2DsarFile.TryOpen( companionPath, out var companion, out var companionError ) )
				{
					files[(int)type] = companion;
					_openedDsar.Add( companion );
				}
				else Hd2Log.Warning( $"Unable to open '{Path.GetFileName( companionPath )}': {companionError}" );
			}

			_storageByArchive.TryGetValue( hash, out var fallback );
			var storage = new Hd2DsarArchiveStorage( files, fallback );

			if ( !TryReadTable( storage, hash, out var table, out var tableError ) )
			{
				Hd2Log.Warning( $"Loose bundle '{name}' has no readable resource table: {tableError}" );
				continue;
			}

			_looseArchives.Add( ( table, storage ) );
			if ( missing > 0 ) Hd2Log.Info( $"Loose bundle {hash} is missing {missing} companion data stream(s); reads fall back to the .nxa copy." );
		}

		Hd2Log.Always( $"Helldivers 2 loose bundles: {_looseArchives.Count} extension-less DSAR archives overrode the .nxa index." );
	}

	void IndexPatches()
	{
		var patches = new List<(int Order, string Name, string Path)>();

		foreach ( var path in System.IO.Directory.EnumerateFiles( _root ) )
		{
			var name = Path.GetFileName( path );
			var match = PatchPattern.Match( name );
			if ( !match.Success ) continue;

			patches.Add( ( int.Parse( match.Groups["order"].Value ), name, path ) );
		}

		// Highest patch number wins, so apply them in descending order and let the first
		// write of a given resource win.
		patches.Sort( static ( left, right ) => right.Order.CompareTo( left.Order ) );

		foreach ( var (_, name, path) in patches )
		{
			var hashText = name[..16];
			if ( !Hd2Hash.TryParse( hashText, out var hash ) ) continue;

			var storage = new Hd2RawArchiveStorage( path, path + ".stream", path + ".gpu_resources" );
			_patchStorages.Add( storage );

			if ( !TryReadTable( storage, hash, out var table, out var tableError ) )
			{
				Hd2Log.Warning( $"Patch container '{name}' has no readable resource table: {tableError}" );
				continue;
			}

			_patchArchives.Add( ( table, storage ) );
		}

		Hd2Log.Always( $"Helldivers 2 patches: {_patchArchives.Count} patch containers found in '{_root}'." );
	}

	static readonly System.Text.RegularExpressions.Regex PatchPattern = new(
		@"^(?<hash>[0-9a-fA-F]{16})\.patch_(?<order>[0-9]+)$",
		System.Text.RegularExpressions.RegexOptions.Compiled );

	/// <summary>
	/// Publishes the resource list. A patch shadows the loose bundle it patches, which shadows
	/// the copy inside the .nxa archives; that is the order the game resolves them in.
	/// </summary>
	void Flatten()
	{
		var seen = new HashSet<(ulong Type, ulong Name)>();
		var duplicates = 0;

		foreach ( var group in new[] { _patchArchives, _looseArchives, _nxaArchives } )
		{
			foreach ( var (table, storage) in group )
			{
				foreach ( var file in table.Files )
				{
					if ( !seen.Add( ( file.Type.Value, file.Name.Value ) ) )
					{
						duplicates++;
						continue;
					}

					var index = _resources.Count;
					_resources.Add( new Hd2Resource( table, file, storage ) );
					_resourceIndexByName.TryAdd( file.Name.Value, index );

					if ( !_resourceIndicesByType.TryGetValue( file.Type.Value, out var list ) )
					{
						list = [];
						_resourceIndicesByType[file.Type.Value] = list;
					}

					list.Add( index );
				}
			}
		}

		Hd2Log.Always( $"Helldivers 2 resources: {_resources.Count} entries across {ArchiveCount} archives "
			+ $"({_patchArchives.Count} patch, {_looseArchives.Count} loose, {_nxaArchives.Count} archived; {duplicates} shadowed duplicates), "
			+ $"{ChunksRead} DSAR chunks decompressed to {BytesDecompressed / (1024.0 * 1024.0):0.0} MB." );
	}

	static bool TryReadTable( Hd2ArchiveStorage storage, Hd2Hash id, out Hd2ArchiveTable table, out string error )
	{
		table = null;
		error = null;

		if ( !storage.TryRead( Hd2DataType.Main, 0, Hd2ArchiveTable.HeaderSize, out var header ) )
		{
			error = "unable to read the archive header";
			return false;
		}

		if ( !Hd2ArchiveTable.TryReadHeader( header, out var numTypes, out var numFiles ) )
		{
			error = "missing archive magic 0x110000F0";
			return false;
		}

		var size = Hd2ArchiveTable.TableSize( numTypes, numFiles );
		if ( size < Hd2ArchiveTable.HeaderSize || size > 32_000_000 )
		{
			error = $"implausible table size {size} for {numTypes} types and {numFiles} files";
			return false;
		}

		if ( !storage.TryRead( Hd2DataType.Main, 0, size, out var data ) )
		{
			error = $"unable to read the {size} byte resource table";
			return false;
		}

		return Hd2ArchiveTable.TryParse( data, id, out table, out error );
	}

	public bool TryRead( in Hd2Resource resource, Hd2DataType type, out byte[] data )
	{
		var size = resource.File.SizeOf( type );
		if ( size == 0 )
		{
			data = null;
			return false;
		}

		return resource.Storage.TryRead( type, resource.File.OffsetOf( type ), (int)size, out data );
	}

	byte[] ReadWholeDsar( Hd2DsarFile file )
	{
		var total = 0L;
		foreach ( var chunk in file.Chunks ) total += chunk.UncompressedSize;
		if ( total <= 0 || total > int.MaxValue ) return null;

		var buffer = new byte[total];
		var offset = 0;
		for ( var i = 0; i < file.Chunks.Length; i++ )
		{
			var data = file.ReadChunk( i );
			Buffer.BlockCopy( data, 0, buffer, offset, data.Length );
			offset += data.Length;
		}

		return buffer;
	}

	static int ReadInt( string name, int fallback, int minimum, int maximum )
	{
		return int.TryParse( Environment.GetEnvironmentVariable( name ), out var value )
			? Math.Clamp( value, minimum, maximum )
			: fallback;
	}

	public void Dispose()
	{
		foreach ( var file in _openedDsar ) file.Dispose();
		_openedDsar.Clear();

		foreach ( var storage in _patchStorages ) storage.Dispose();
		_patchStorages.Clear();

		_storageByArchive.Clear();
		_resources.Clear();
		_resourceIndicesByType.Clear();
		_resourceIndexByName.Clear();
	}
}
