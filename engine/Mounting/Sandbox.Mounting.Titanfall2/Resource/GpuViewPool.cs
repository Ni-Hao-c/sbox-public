using System;

/// <summary>
/// Fixed, eagerly allocated output slots. Each recording owns its output for the rest of
/// the frame; exhaustion never allocates or reuses an in-flight slot. The renderer supplies
/// a read-only conservative output for overflow recordings.
/// </summary>
sealed class Titanfall2GpuViewPool<T> : IDisposable where T : class, IDisposable
{
	readonly object _lock = new();
	readonly T[] _slots;
	ulong _frame;
	int _next;
	int _highWaterMark;
	long _overflowCount;
	bool _disposed;

	public int Capacity => _slots.Length;
	public int HighWaterMark { get { lock ( _lock ) return _highWaterMark; } }
	public long OverflowCount { get { lock ( _lock ) return _overflowCount; } }

	public Titanfall2GpuViewPool( Func<T> create, int capacity = 8 )
	{
		ArgumentNullException.ThrowIfNull( create );
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero( capacity );
		_slots = new T[capacity];
		try
		{
			for ( var i = 0; i < capacity; ++i )
				_slots[i] = create() ?? throw new InvalidOperationException( "A GPU view slot factory returned null." );
		}
		catch
		{
			Dispose();
			throw;
		}
	}

	public bool TryAcquire( ulong frame, out T slot )
	{
		lock ( _lock )
		{
			ObjectDisposedException.ThrowIf( _disposed, this );
			if ( _frame != frame ) { _frame = frame; _next = 0; }
			if ( _next == _slots.Length )
			{
				_overflowCount++;
				slot = null;
				return false;
			}
			slot = _slots[_next++];
			_highWaterMark = Math.Max( _highWaterMark, _next );
			return true;
		}
	}

	// Retained for existing contract fixtures. Production uses TryAcquire and renders overflow.
	internal T Acquire( ulong frame ) => TryAcquire( frame, out var slot ) ? slot
		: throw new InvalidOperationException( "The GPU view pool is exhausted for this frame." );

	public void Dispose()
	{
		lock ( _lock )
		{
			if ( _disposed ) return;
			_disposed = true;
			foreach ( var slot in _slots ) slot?.Dispose();
		}
	}
}
