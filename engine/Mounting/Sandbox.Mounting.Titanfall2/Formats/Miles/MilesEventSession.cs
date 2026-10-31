using System.Runtime.InteropServices;
using System.Text;

/// <summary>Owns an installed Miles project and its authored event interpreter.</summary>
public sealed class Titanfall2SoundEventSession : IDisposable
{
	[UnmanagedFunctionPointer( CallingConvention.Cdecl, CharSet = CharSet.Ansi )]
	delegate nint Open( [MarshalAs( UnmanagedType.LPWStr )] string root, string language, int silent, StringBuilder error, uint capacity );
	[UnmanagedFunctionPointer( CallingConvention.Cdecl )] delegate void Close( nint session );
	[UnmanagedFunctionPointer( CallingConvention.Cdecl )] delegate nint Name( nint session, int index );
	[UnmanagedFunctionPointer( CallingConvention.Cdecl, CharSet = CharSet.Ansi )] delegate ulong PlayEvent( nint session, string name, float x, float y, float z );
	[UnmanagedFunctionPointer( CallingConvention.Cdecl )] delegate void StopEvent( nint session, ulong id );
	[UnmanagedFunctionPointer( CallingConvention.Cdecl, CharSet = CharSet.Ansi )] delegate void UpdateEvent( nint session, ulong id, float x, float y, float z, string controller, float value );
	[UnmanagedFunctionPointer( CallingConvention.Cdecl )] delegate void Listener( nint session, float x, float y, float z, float fx, float fy, float fz, float ux, float uy, float uz, float volume );
	static readonly Lazy<nint> Library = new( () => NativeLibrary.Load( Path.Combine( Path.GetDirectoryName( typeof( Titanfall2SoundEventSession ).Assembly.Location ) ?? "", "titanfall2_miles.dll" ) ) );
	static T Export<T>( string name ) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>( NativeLibrary.GetExport( Library.Value, name ) );
	readonly Close _close;
	readonly PlayEvent _play;
	readonly StopEvent _stop;
	readonly UpdateEvent _update;
	readonly Listener _listener;
	readonly HashSet<string> _names = new( StringComparer.OrdinalIgnoreCase );
	readonly object _sync = new();
	nint _session;
	public IReadOnlyCollection<string> EventNames => _names;
	public bool IsValid => _session != 0;

	internal Titanfall2SoundEventSession( string root, string language, bool silent )
	{
		_close = Export<Close>( "tf2_miles_events_close" );
		_play = Export<PlayEvent>( "tf2_miles_event_play" );
		_stop = Export<StopEvent>( "tf2_miles_event_stop" );
		_update = Export<UpdateEvent>( "tf2_miles_event_update" );
		_listener = Export<Listener>( "tf2_miles_events_listener" );
		var error = new StringBuilder( 2048 );
		_session = Export<Open>( "tf2_miles_events_open" )( root, language, silent ? 1 : 0, error, (uint)error.Capacity );
		if ( _session == 0 ) throw new InvalidOperationException( error.ToString() );
		try
		{
			var name = Export<Name>( "tf2_miles_events_name" );
			for ( var index = 0; index < 1_000_000; ++index )
			{
				var pointer = name( _session, index );
				if ( pointer == 0 ) break;
				_names.Add( Marshal.PtrToStringAnsi( pointer ) );
			}
		}
		catch { Dispose(); throw; }
	}

	public Titanfall2SoundEventHandle Play( string name, Vector3 position )
	{
		lock ( _sync )
		{
			if ( _session == 0 || !_names.TryGetValue( name, out var canonical ) ) return null;
			var id = _play( _session, canonical, position.x, position.y, position.z );
			return id == 0 ? null : new Titanfall2SoundEventHandle( this, id, position );
		}
	}
	public void SetController( string name, float value ) => Update( 0, Vector3.Zero, name, value );
	internal void Update( ulong id, Vector3 position, string controller = null, float value = 0f )
	{
		if ( !float.IsFinite( value ) || !Titanfall2ParticleControlPoints.IsFinite( position ) ) return;
		lock ( _sync ) if ( _session != 0 ) _update( _session, id, position.x, position.y, position.z, controller, value );
	}
	internal void Stop( ulong id )
	{
		lock ( _sync ) if ( _session != 0 ) _stop( _session, id );
	}
	public void UpdateListener( Transform transform, float volume = 1f )
	{
		var p = transform.Position; var f = transform.Rotation.Forward; var u = transform.Rotation.Up;
		lock ( _sync ) if ( _session != 0 ) _listener( _session, p.x, p.y, p.z, f.x, f.y, f.z, u.x, u.y, u.z, float.IsFinite( volume ) ? volume : 0f );
	}
	public void Dispose()
	{
		lock ( _sync )
		{
			if ( _session != 0 ) _close( _session );
			_session = 0;
		}
		GC.SuppressFinalize( this );
	}
	~Titanfall2SoundEventSession() => Dispose();
}

/// <summary>A retail event instance; stop and controller updates address its unique event ID.</summary>
public sealed class Titanfall2SoundEventHandle : IDisposable
{
	Titanfall2SoundEventSession _session;
	readonly ulong _id;
	Vector3 _position;
	internal Titanfall2SoundEventHandle( Titanfall2SoundEventSession session, ulong id, Vector3 position ) { _session = session; _id = id; _position = position; }
	/// <summary>Whether this handle is attached to a live session, including delayed/looping events.</summary>
	public bool IsValid => _session?.IsValid == true;
	public Vector3 Position { get => _position; set { _position = value; _session?.Update( _id, value ); } }
	public void SetController( string name, float value ) => _session?.Update( _id, _position, name, value );
	public void Stop() { _session?.Stop( _id ); _session = null; }
	public void Dispose() => Stop();
}

public partial class Titanfall2Mount
{
	readonly List<WeakReference<Titanfall2SoundEventSession>> _eventSessions = [];
	/// <summary>Creates the original event runtime. Caller owns the session; mount shutdown also closes it.</summary>
	public Titanfall2SoundEventSession CreateSoundEventSession( string language = null, bool silent = false )
	{
		if ( !IsMounted ) throw new InvalidOperationException( "Titanfall 2 is not mounted." );
		var audio = Path.Combine( _root, "r2", "sound" );
		language ??= System.IO.Directory.EnumerateFiles( audio, "general_*.mstr" )
			.Select( Path.GetFileNameWithoutExtension ).Where( name => name != "general_stream" && !name.Contains( "_patch_", StringComparison.Ordinal ) )
			.OrderBy( name => name == "general_tchinese" ? 0 : 1 ).ThenBy( name => name, StringComparer.Ordinal ).Select( name => name[8..] ).FirstOrDefault() ?? "english";
		var session = new Titanfall2SoundEventSession( _root, language, silent );
		lock ( _eventSessions )
		{
			_eventSessions.RemoveAll( weak => !weak.TryGetTarget( out _ ) );
			_eventSessions.Add( new( session ) );
		}
		return session;
	}
	void ReleaseSoundEventSessions()
	{
		lock ( _eventSessions )
		{
			foreach ( var weak in _eventSessions ) if ( weak.TryGetTarget( out var session ) ) session.Dispose();
			_eventSessions.Clear();
		}
	}
}
