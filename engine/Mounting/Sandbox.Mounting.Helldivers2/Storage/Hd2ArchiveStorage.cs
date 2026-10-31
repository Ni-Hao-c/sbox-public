namespace Sandbox.Mounting.Helldivers2;

/// <summary>A resource addressable in the mounted game: its table entry plus wherever its bytes live.</summary>
internal readonly struct Hd2Resource
{
	public readonly Hd2ArchiveTable Table;
	public readonly Hd2FileEntry File;
	public readonly Hd2ArchiveStorage Storage;

	public Hd2Resource( Hd2ArchiveTable table, Hd2FileEntry file, Hd2ArchiveStorage storage )
	{
		Table = table;
		File = file;
		Storage = storage;
	}

	public Hd2Hash Name => File.Name;

	public Hd2Hash Type => File.Type;

	public override string ToString() => $"{Hd2ResourceType.NameOf( Type )} {Name} in {Table.Id}";
}

/// <summary>Where one archive's three data streams live.</summary>
internal abstract class Hd2ArchiveStorage
{
	public abstract bool TryRead( Hd2DataType type, ulong offset, int count, out byte[] data );
}

/// <summary>An archive reconstructed out of the multi-gigabyte <c>bundles.NN.nxa</c> files.</summary>
internal sealed class Hd2NxaArchiveStorage : Hd2ArchiveStorage
{
	readonly Hd2Dsaa _index;
	readonly Hd2DsarFile[] _bundles;
	readonly int[] _itemIndices;

	public Hd2NxaArchiveStorage( Hd2Dsaa index, Hd2DsarFile[] bundles, int[] itemIndices )
	{
		_index = index;
		_bundles = bundles;
		_itemIndices = itemIndices;
	}

	public override bool TryRead( Hd2DataType type, ulong offset, int count, out byte[] data )
	{
		data = null;
		var itemIndex = _itemIndices[(int)type];
		if ( itemIndex < 0 ) return false;

		return Hd2NxaReader.TryRead( _index, _bundles, _index.Archives[itemIndex], offset, count, out data );
	}
}

/// <summary>
/// An archive that ships as its own DSAR file in <c>data/</c> (a Steam-patched or localized
/// bundle). Missing data streams fall back to the version inside the .nxa files, which is what
/// the game itself does.
/// </summary>
internal sealed class Hd2DsarArchiveStorage : Hd2ArchiveStorage
{
	readonly Hd2DsarFile[] _files;
	readonly Hd2ArchiveStorage _fallback;

	public Hd2DsarArchiveStorage( Hd2DsarFile[] files, Hd2ArchiveStorage fallback )
	{
		_files = files;
		_fallback = fallback;
	}

	public override bool TryRead( Hd2DataType type, ulong offset, int count, out byte[] data )
	{
		var file = _files[(int)type];
		if ( file is not null ) return file.TryRead( offset, count, out data );

		if ( _fallback is not null ) return _fallback.TryRead( type, offset, count, out data );

		data = null;
		return false;
	}
}

/// <summary>
/// A patch container (<c>&lt;bundle&gt;.patch_N</c>). These are not DSAR files: the container
/// itself holds the resource table, and its streamed data sits in raw sibling files.
/// </summary>
internal sealed class Hd2RawArchiveStorage : Hd2ArchiveStorage
{
	readonly string[] _paths;
	readonly FileStream[] _streams = new FileStream[3];
	readonly object _lock = new();

	public Hd2RawArchiveStorage( string mainPath, string streamPath, string gpuPath )
	{
		_paths = [mainPath, streamPath, gpuPath];
	}

	public override bool TryRead( Hd2DataType type, ulong offset, int count, out byte[] data )
	{
		data = null;
		var path = _paths[(int)type];
		if ( string.IsNullOrEmpty( path ) ) return false;

		try
		{
			var stream = GetStream( (int)type );
			if ( stream is null ) return false;

			var buffer = new byte[count];
			lock ( _lock )
			{
				if ( stream.Length < (long)offset + count ) return false;
				stream.Seek( (long)offset, SeekOrigin.Begin );
				if ( stream.ReadAtLeast( buffer, count, false ) != count ) return false;
			}

			data = buffer;
			return true;
		}
		catch ( Exception exception )
		{
			Hd2Log.Warning( exception, $"Unable to read patch data from '{path}'." );
			return false;
		}
	}

	FileStream GetStream( int type )
	{
		lock ( _lock )
		{
			if ( _streams[type] is not null ) return _streams[type];
			if ( !File.Exists( _paths[type] ) ) return null;

			_streams[type] = new FileStream( _paths[type], FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16, FileOptions.RandomAccess );
			return _streams[type];
		}
	}

	public void Dispose()
	{
		lock ( _lock )
		{
			for ( var i = 0; i < _streams.Length; i++ )
			{
				_streams[i]?.Dispose();
				_streams[i] = null;
			}
		}
	}
}

/// <summary>
/// Rebuilds a logical archive file out of the DSAA run list. A single archive can be split across
/// several runs and even several .nxa files, so reads walk runs until the requested range is met.
/// </summary>
internal static class Hd2NxaReader
{
	public static bool TryRead( Hd2Dsaa index, Hd2DsarFile[] bundles, Hd2DsaaArchiveItem item, ulong archiveOffset, int count, out byte[] data )
	{
		data = null;
		if ( count <= 0 ) return false;

		var buffer = new byte[count];
		if ( !TryRead( index, bundles, item, archiveOffset, buffer ) ) return false;

		data = buffer;
		return true;
	}

	public static bool TryRead( Hd2Dsaa index, Hd2DsarFile[] bundles, Hd2DsaaArchiveItem item, ulong archiveOffset, Span<byte> destination )
	{
		var entryIndex = FindEntry( item.Entries, archiveOffset );
		if ( entryIndex < 0 ) return false;

		var consumed = 0;
		var cursor = archiveOffset;

		while ( consumed < destination.Length )
		{
			if ( entryIndex >= item.Entries.Length ) return false;

			var entry = item.Entries[entryIndex];
			var entryEnd = entryIndex + 1 < item.Entries.Length ? item.Entries[entryIndex + 1].ArchiveOffset : item.Size;

			if ( cursor < entry.ArchiveOffset || cursor >= entryEnd ) return false;

			var insideRun = cursor - entry.ArchiveOffset;
			var target = (ulong)entry.UncompressedBundleOffset + insideRun;
			var available = (int)Math.Min( entryEnd - cursor, (ulong)( destination.Length - consumed ) );

			if ( entry.BundleIndex >= bundles.Length )
			{
				Hd2Log.Warning( $"DSAA entry points at bundle index {entry.BundleIndex}, but only {bundles.Length} .nxa files are open." );
				return false;
			}

			var bundle = bundles[entry.BundleIndex];
			if ( bundle is null || !bundle.TryRead( target, destination.Slice( consumed, available ) ) ) return false;

			consumed += available;
			cursor += (ulong)available;
			entryIndex++;
		}

		return true;
	}

	/// <summary>Largest run whose archive offset is at or before <paramref name="archiveOffset"/>.</summary>
	public static int FindEntry( Hd2DsaaEntry[] entries, ulong archiveOffset )
	{
		var low = 0;
		var high = entries.Length - 1;
		var result = -1;

		while ( low <= high )
		{
			var middle = ( low + high ) >> 1;
			if ( entries[middle].ArchiveOffset <= archiveOffset )
			{
				result = middle;
				low = middle + 1;
			}
			else
			{
				high = middle - 1;
			}
		}

		return result;
	}
}
