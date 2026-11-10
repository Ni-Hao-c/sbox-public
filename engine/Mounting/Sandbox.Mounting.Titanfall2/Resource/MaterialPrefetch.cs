using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Threading;

/// <summary>
/// A windowed, map-scoped CPU pipeline. Workers read/decompress texture data; the
/// main thread still creates every Material/Texture. Slots advance as materials
/// are consumed, so a slow GPU cannot accumulate an entire map of decoded mips.
/// </summary>
sealed class Titanfall2MaterialPrefetch : IDisposable
{
	static readonly Sandbox.Diagnostics.Logger Log = new( "Titanfall2Prefetch" );
	const long BudgetBytes = 128L * 1024 * 1024;
	// Number of materials whose textures are prepared ahead of consumption.
	const int Window = 4;
	readonly Titanfall2Mount _host;
	readonly Queue<string> _pending;
	readonly Dictionary<string, Task> _jobs = new( StringComparer.OrdinalIgnoreCase );
	readonly ConcurrentBag<Titanfall2TextureLoader> _textures = new();
	readonly CancellationTokenSource _cancel = new();
	readonly Titanfall2TexturePreparationBudget _budget;
	long _waitTicks;
	readonly ConcurrentDictionary<int, byte> _workers = new();
	int _preparedMaterials;
	volatile bool _disposed;

	internal Titanfall2MaterialPrefetch( Titanfall2Mount host, IEnumerable<string> materials, long budgetBytes = BudgetBytes )
	{
		_host = host;
		_budget = new( budgetBytes );
		_pending = new Queue<string>( materials.Distinct( StringComparer.OrdinalIgnoreCase ) );
		for ( var i = 0; i < Window; i++ ) StartNext();
	}

	void StartNext()
	{
		if ( !_pending.TryDequeue( out var name ) ) return;
		_jobs[name] = Task.Run( () =>
		{
			try
			{
				_workers.TryAdd( Environment.CurrentManagedThreadId, 0 );
				_host.PrepareMaterialTextures( name, _cancel.Token, PrepareTexture );
				Interlocked.Increment( ref _preparedMaterials );
			}
			catch ( OperationCanceledException ) { }
			catch ( Exception ) { /* Ordinary loaders retain their synchronous error path. */ }
		} );
	}

	internal long RetainedBytes => _budget.RetainedBytes;

	internal bool PrepareTexture( Titanfall2TextureLoader loader )
	{
		if ( _disposed || !_budget.HasCapacity ) return false;
		_textures.Add( loader );
		return loader.Prepare( _budget.Reserve, _budget.Release );
	}

	internal void Wait( string name )
	{
		if ( _disposed ) return;
		if ( _jobs.TryGetValue( name, out var job ) )
		{
			var started = System.Diagnostics.Stopwatch.GetTimestamp();
			job.GetAwaiter().GetResult();
			_waitTicks += System.Diagnostics.Stopwatch.GetTimestamp() - started;
		}
	}

	internal void Consumed( string name )
	{
		if ( _disposed ) return;
		if ( _jobs.Remove( name ) ) StartNext();
	}

	public void Dispose()
	{
		if ( _disposed ) return;
		_disposed = true;
		_cancel.Cancel();
		Task.WhenAll( _jobs.Values ).GetAwaiter().GetResult();
		foreach ( var loader in _textures ) loader.ClearPreparation();
		_textures.Clear();
		_jobs.Clear();
		_pending.Clear();
		_cancel.Dispose();
		Log.Info( $"Titanfall 2 material prefetch: {_preparedMaterials} material jobs on {_workers.Count} worker threads; "
			+ $"retained peak {_budget.PeakBytes / (1024.0 * 1024.0):0.0} MiB, target {_budget.CapacityBytes / (1024.0 * 1024.0):0.0} MiB "
			+ $"(in-flight textures may exceed target), remaining {RetainedBytes} bytes; "
			+ $"main wait {_waitTicks / (double)System.Diagnostics.Stopwatch.Frequency:0.00}s; GPU creation on main thread." );
		_workers.Clear();
	}
}

public partial class Titanfall2Mount
{
	internal void PrepareMaterialTextures( string name, CancellationToken cancellation, Func<Titanfall2TextureLoader, bool> prepare )
	{
		cancellation.ThrowIfCancellationRequested();
		if ( TryGetCachedMaterialDescriptor( name, out var descriptor ) )
		{
			foreach ( var binding in descriptor.TextureBindings.OrderBy( binding => binding.HandleIndex ).DistinctBy( binding => (binding.Semantic, binding.Layer) ) )
			{
				cancellation.ThrowIfCancellationRequested();
				if ( !descriptor.UsesTexture( binding ) ) continue;
				if ( GetByPath( $"mount://{Ident}/{binding.TexturePath}.vtex" ) is Titanfall2TextureLoader prepared && !prepare( prepared ) ) return;
			}
			return;
		}
		if ( !_materialSources.TryGetValue( name, out var source ) || source.Asset.Version != 12 ) return;
		if ( !source.Archive.TryReadAsset( source.Asset, out var data, out _ ) || data.Header.Length < 168 ) return;
		TryGetShaderSetName( MaterialLoader.GetShaderSetGuid( data.Header ), out var shaderSetName );
		var needsNormal = !(shaderSetName?.Contains( "Unlit", StringComparison.OrdinalIgnoreCase ) ?? false)
			&& !MaterialLoader.IsVistaMaterial( name ) && !MaterialLoader.IsGodrayMaterial( name );
		var handles = new RpakPagePointer( BinaryPrimitives.ReadInt32LittleEndian( data.Header.AsSpan( 152, 4 ) ), BinaryPrimitives.ReadInt32LittleEndian( data.Header.AsSpan( 156, 4 ) ) );
		var streamed = new RpakPagePointer( BinaryPrimitives.ReadInt32LittleEndian( data.Header.AsSpan( 160, 4 ) ), BinaryPrimitives.ReadInt32LittleEndian( data.Header.AsSpan( 164, 4 ) ) );
		var count = handles.Index == streamed.Index && streamed.Offset >= handles.Offset ? (streamed.Offset - handles.Offset) / 8 : 0;
		if ( count <= 0 || count > 64 || !source.Archive.TryReadPagePointer( handles, count * 8, out var guids, out _ ) ) return;
		for ( var i = 0; i < count; i++ )
		{
			cancellation.ThrowIfCancellationRequested();
			var guid = BinaryPrimitives.ReadUInt64LittleEndian( guids.AsSpan( i * 8, 8 ) );
			if ( !_texturePaths.TryGetValue( guid, out var path ) ) continue;
			if ( !needsNormal && Titanfall2MaterialDescriptor.InferTextureSemantic( null, path, false ) == Titanfall2TextureSemantic.Normal ) continue;
			if ( GetByPath( $"mount://{Ident}/{path}.vtex" ) is Titanfall2TextureLoader loader && !prepare( loader ) ) return;
		}
	}
}
