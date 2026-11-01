using Titanfall2;

/// <summary>
/// The shared rBSP parse itself, kept alive by its leases. A map is mounted twice
/// (<c>&lt;map&gt;.bsp.scene</c> and the pure-world <c>&lt;map&gt;.bsp.world.scene</c>) and the
/// editor can build both at once; without sharing, the same file - world meshes, collision,
/// visibility, lightmaps - is read and decoded twice.
/// </summary>
sealed class Titanfall2MapParse( string key, Func<(Titanfall2BspReader.ParsedBsp, string)> parse )
{
	// ExecutionAndPublication: two loaders building concurrently must not both parse.
	readonly Lazy<(Titanfall2BspReader.ParsedBsp Parsed, string Error)> _parse =
		new( parse, System.Threading.LazyThreadSafetyMode.ExecutionAndPublication );

	internal string Key { get; } = key;

	/// <summary>Number of live leases; the last one to go frees the parse.</summary>
	internal int Users { get; set; }

	/// <summary>Set when the parse failed or threw, so the mount never hands this entry out again.</summary>
	internal bool Faulted { get; private set; }

	internal (Titanfall2BspReader.ParsedBsp Parsed, string Error) Evaluate()
	{
		try
		{
			var result = _parse.Value;
			// Lazy caches its result forever, including a failure. Mark it so the next
			// attempt gets a fresh entry rather than the same error.
			if ( result.Error is not null ) Faulted = true;
			return result;
		}
		catch
		{
			Faulted = true;
			throw;
		}
	}
}

/// <summary>
/// One builder's lease on a shared parse (<see cref="Titanfall2MapParse"/>). Dispose it - or
/// use <c>using</c> - when the build is done; the last lease to go frees the parse.
/// </summary>
sealed class Titanfall2MapImport( Titanfall2Mount host, Titanfall2MapParse parse ) : IDisposable
{
	bool _disposed;

	internal Titanfall2MapParse Parse { get; } = parse;

	/// <summary>The parsed BSP, or null with <see cref="Error"/> set.</summary>
	public Titanfall2BspReader.ParsedBsp Parsed => Parse.Evaluate().Parsed;

	/// <summary>Why the parse failed, or null when it succeeded.</summary>
	public string Error => Parse.Evaluate().Error;

	public void Dispose()
	{
		if ( _disposed ) return;
		_disposed = true;
		host.EndMapImport( Parse );
	}
}

public partial class Titanfall2Mount
{
	readonly Dictionary<string, Titanfall2MapParse> _mapImports = new( StringComparer.OrdinalIgnoreCase );
	readonly object _mapImportLock = new();

	/// <summary>
	/// Leases the shared parse of <paramref name="key"/>, parsing on first use. The caller
	/// must dispose the returned lease; see <see cref="Titanfall2MapImport"/>.
	/// </summary>
	internal Titanfall2MapImport BeginMapImport( string key, Func<(Titanfall2BspReader.ParsedBsp, string)> parse )
	{
		lock ( _mapImportLock )
		{
			if ( _mapImports.TryGetValue( key, out var existing ) && existing.Faulted )
			{
				// Existing leases keep their entry; new ones get a chance to parse again.
				_mapImports.Remove( key );
				existing = null;
			}
			if ( existing is null )
			{
				existing = new Titanfall2MapParse( key, parse );
				_mapImports[key] = existing;
			}
			existing.Users++;
			return new Titanfall2MapImport( this, existing );
		}
	}

	internal void EndMapImport( Titanfall2MapParse parse )
	{
		lock ( _mapImportLock )
		{
			if ( --parse.Users > 0 && !parse.Faulted ) return;
			if ( _mapImports.TryGetValue( parse.Key, out var current ) && ReferenceEquals( current, parse ) )
				_mapImports.Remove( parse.Key );
		}
	}

	void ReleaseMapImports()
	{
		lock ( _mapImportLock ) _mapImports.Clear();
	}

	/// <summary>Number of shared map parses currently alive. Diagnostics and tests.</summary>
	internal int MapImportCount
	{
		get { lock ( _mapImportLock ) return _mapImports.Count; }
	}
}
