using Titanfall2;

/// <summary>
/// Reconstructs Titanfall 2 map ambience from the <c>_snd.ent</c> partition.
/// <para>
/// Retail uses two systems and both are rebuilt here: <c>ambient_generic</c> point
/// emitters (a looping sound with an audible radius) and <c>trigger_soundscape</c>
/// zones (a named soundscape that swaps the map's looping ambience). Emitters are
/// played as independent <see cref="SoundHandle"/>s because the engine keeps only one
/// <see cref="SoundscapeTrigger"/> active at a time - correct for zones, not for
/// point emitters.
/// </para>
/// <para>
/// The map data is encoded into <see cref="EncodedEmitters"/> / <see cref="EncodedZones"/>
/// because a mount scene is serialized after loading: runtime-only fields would come back
/// empty (same reason <c>Titanfall2ParticleStreamer</c> encodes its entity list).
/// </para>
/// </summary>
[Library]
public sealed class Titanfall2MapAudio : Component, Component.DontExecuteOnServer, Sandbox.Internal.IUpdateSubscriber
{
	static readonly Sandbox.Diagnostics.Logger Log = new( "Titanfall2Audio" );
	const int DataMagic = 0x41504654; // TFPA
	const int DataVersion = 1;
	const int MaximumEntryCount = 4096;

	/// <summary>Refresh cadence for the emitter selection pass.</summary>
	const float RefreshInterval = 0.25f;
	/// <summary>Sound file loads (BCF decode) are the expensive part of starting an emitter.</summary>
	const int StartsPerRefresh = 2;

	public readonly record struct Emitter( string SoundName, Vector3 Position, float Radius, float Volume );
	public readonly record struct SoundscapeZone( string SoundName, Vector3 Position, float Radius );

	sealed class EmitterState
	{
		public Emitter Definition;
		public SoundHandle Handle;
		public Titanfall2SoundEventHandle Event;
		public bool IsPlaying => Handle.IsValid() || Event?.IsValid == true;
		public float PlayDistanceSquared;
		public float StopDistanceSquared;
		public bool Keep;
	}

	[Property, Hide] public string MountIdent { get; set; }
	[Property, Hide] public string MapPath { get; set; }
	[Property, Hide] public string EncodedEmitters { get; set; }
	[Property, Hide] public string EncodedZones { get; set; }
	[Property, Hide] public float VolumeScale { get; set; } = 1f;
	[Property, Hide] public int MaxEmitters { get; set; } = 12;

	readonly List<EmitterState> _emitters = new();
	readonly List<(EmitterState State, float DistanceSquared)> _candidates = new();
	readonly List<Soundscape> _ownedSoundscapes = new();
	readonly List<GameObject> _zoneObjects = new();

	Titanfall2Mount _mount;
	Titanfall2SoundEventSession _events;
	bool _eventLoadAttempted;
	RealTimeSince _sinceRefresh;
	int _failedLoads;
	int _zoneCount;
	int _startedEmitters;
	bool _initialized;

	public int EmitterCount => _emitters.Count;
	public int ZoneCount => _zoneCount;
	public int PlayingEmitters => _emitters.Count( static state => state.IsPlaying );

	internal void Configure( Titanfall2Mount mount, string mapPath, IReadOnlyList<Emitter> emitters,
		IReadOnlyList<SoundscapeZone> zones, float volumeScale, int maximumEmitters )
	{
		Release();
		_mount = mount;
		MountIdent = mount?.Ident;
		MapPath = mapPath;
		VolumeScale = Math.Clamp( volumeScale, 0.05f, 2f );
		MaxEmitters = Math.Max( 1, maximumEmitters );
		EncodedEmitters = EncodeEmitters( emitters );
		EncodedZones = EncodeZones( zones );

		// AddComponent can run OnAwake before Configure; force a fresh decode with the real data.
		_initialized = false;
		Initialize();
	}

	protected override void OnAwake() => Initialize();

	protected override void OnUpdate()
	{
		if ( !_initialized ) Initialize();
		if ( Application.IsHeadless || _emitters.Count == 0 ) return;

		if ( _mount is null )
		{
			_mount = Sandbox.Mounting.Directory.Get( MountIdent ) as Titanfall2Mount;
			if ( _mount is null || !_mount.IsMounted ) return;
		}

		if ( _sinceRefresh < RefreshInterval ) return;
		_sinceRefresh = 0f;

		if ( !_eventLoadAttempted )
		{
			_eventLoadAttempted = true;
			try { _events = _mount.CreateSoundEventSession(); }
			catch ( Exception exception ) { Log.Warning( $"Miles event runtime unavailable: {exception.Message}" ); }
		}
		_events?.UpdateListener( Sound.Listener, VolumeScale );
		var listener = Sound.Listener.Position;
		_candidates.Clear();
		foreach ( var state in _emitters )
		{
			// Emitters outside the candidate radius must lose last refresh's selection,
			// otherwise a source that was once nearby keeps playing after we leave it.
			state.Keep = false;
			var distanceSquared = state.Definition.Position.DistanceSquared( listener );
			var threshold = state.IsPlaying ? state.StopDistanceSquared : state.PlayDistanceSquared;
			if ( distanceSquared > threshold ) continue;
			_candidates.Add( (state, distanceSquared) );
		}

		// Keep the closest emitters when a map authors more point sources than the budget.
		_candidates.Sort( static ( left, right ) => left.DistanceSquared.CompareTo( right.DistanceSquared ) );
		var keep = Math.Min( _candidates.Count, MaxEmitters );
		for ( var index = 0; index < _candidates.Count; index++ )
			_candidates[index].State.Keep = index < keep;

		var starts = StartsPerRefresh;
		foreach ( var state in _emitters )
		{
			if ( !state.Keep )
			{
				StopEmitter( state );
				continue;
			}

			if ( state.IsPlaying || starts <= 0 ) continue;
			// Failed decodes consume the same synchronous work as successful starts.
			starts--;
			if ( TryStartEmitter( state ) )
			{
				_startedEmitters++;
			}
		}
	}

	// SoundHandles are not owned by the GameObject: without this a disabled map keeps
	// its ambient loops playing. Emitters restart on the next refresh after re-enable.
	protected override void OnEnabled()
	{
		foreach ( var zone in _zoneObjects )
			if ( zone.IsValid() ) zone.Enabled = true;
		_sinceRefresh = RefreshInterval;
	}

	protected override void OnDisabled()
	{
		foreach ( var state in _emitters ) StopEmitter( state );
		foreach ( var zone in _zoneObjects )
			if ( zone.IsValid() ) zone.Enabled = false;
	}

	protected override void OnDestroy() => Release();

	void Release()
	{
		foreach ( var state in _emitters ) StopEmitter( state );
		_emitters.Clear();
		_candidates.Clear();
		// Track ownership explicitly, including zones moved away from the host.
		var zones = _zoneObjects.ToArray();
		_zoneObjects.Clear();
		foreach ( var zone in zones )
			if ( zone.IsValid() ) zone.DestroyImmediate();
		_ownedSoundscapes.Clear();
		_zoneCount = 0;
		_events?.Dispose();
		_events = null;
		_eventLoadAttempted = false;
	}

	void Initialize()
	{
		if ( _initialized ) return;
		_initialized = true;

		if ( string.IsNullOrWhiteSpace( EncodedEmitters ) && string.IsNullOrWhiteSpace( EncodedZones ) ) return;

		try
		{
			// Decode both payloads completely before publishing runtime resources.
			// A truncated later zone must not leave earlier loops/triggers alive.
			var emitters = DecodeEmitters( EncodedEmitters ).ToArray();
			var zones = DecodeZones( EncodedZones ).ToArray();
			foreach ( var emitter in emitters )
			{
				// radius == 0 means "attenuation comes from the sound itself"; the loader
				// substitutes a default radius, so this is only a guard.
				var radius = emitter.Radius > 0f ? emitter.Radius : 1024f;
				_emitters.Add( new EmitterState
				{
					Definition = emitter,
					PlayDistanceSquared = radius * radius,
					StopDistanceSquared = radius * radius * 2.25f
				} );
			}

			foreach ( var zone in zones ) CreateSoundscapeZone( zone );

			Log.Info( $"Titanfall 2 map audio ready: {_emitters.Count} ambient emitters, {_zoneCount} soundscape zones, "
				+ $"volume {VolumeScale:0.##}, up to {MaxEmitters} concurrent emitters ({MapPath})." );
		}
		catch ( Exception exception )
		{
			Release();
			Log.Warning( $"Failed to initialize Titanfall 2 map audio for '{MapPath}': {exception.Message}" );
		}
	}

	bool TryStartEmitter( EmitterState state )
	{
		if ( _events?.EventNames.Contains( state.Definition.SoundName, StringComparer.OrdinalIgnoreCase ) == true )
		{
			state.Event = _events.Play( state.Definition.SoundName, state.Definition.Position );
			return state.Event is not null;
		}
		if ( !TryResolveSound( state.Definition.SoundName, out var soundFile ) ) return false;

		var handle = Sound.PlayFile( soundFile,
			Math.Clamp( state.Definition.Volume * VolumeScale, 0.01f, 1f ), 1f, 0f, 0.5f );
		if ( !handle.IsValid() ) return false;

		handle.Position = state.Definition.Position;
		handle.Distance = MathF.Max( 64f, state.Definition.Radius );
		handle.SpacialBlend = 1f;
		handle.DistanceAttenuation = true;
		// Per-emitter occlusion/reflection traces are not worth the cost for ambience.
		handle.OcclusionEnabled = false;
		handle.ReverbEnabled = false;
		state.Handle = handle;
		return true;
	}

	static void StopEmitter( EmitterState state )
	{
		state.Event?.Stop();
		state.Event = null;
		if ( !state.Handle.IsValid() )
		{
			state.Handle = null;
			return;
		}

		state.Handle.Stop( 0.75f );
		state.Handle = null;
	}

	void CreateSoundscapeZone( SoundscapeZone zone )
	{
		if ( !TryResolveSound( zone.SoundName, out var soundFile ) ) return;

		// The engine's SoundscapeTrigger keeps a single zone active and cross-fades between
		// them, which matches how retail swaps interior/exterior ambience.
		var soundscape = new Soundscape { MasterVolume = 1f };
		soundscape.LoopedSounds.Add( new Soundscape.LoopedSound { SoundFile = soundFile, Volume = 1f } );
		_ownedSoundscapes.Add( soundscape );

		var zoneObject = new GameObject( GameObject, false, $"titanfall2_soundscape_{_zoneCount}" );
		// EncodedZones is the persistent source; runtime Soundscapes have no path.
		zoneObject.Flags |= GameObjectFlags.NotSaved;
		_zoneObjects.Add( zoneObject );
		zoneObject.WorldPosition = zone.Position;
		var trigger = zoneObject.AddComponent<SoundscapeTrigger>();
		trigger.Type = SoundscapeTrigger.TriggerType.Sphere;
		trigger.Radius = MathF.Max( 128f, zone.Radius );
		trigger.Soundscape = soundscape;
		trigger.StayActiveOnExit = false;
		trigger.Volume = VolumeScale;
		zoneObject.Enabled = Active;
		_zoneCount++;
	}

	bool TryResolveSound( string soundName, out SoundFile soundFile )
	{
		soundFile = null;
		if ( _mount is null ) _mount = Sandbox.Mounting.Directory.Get( MountIdent ) as Titanfall2Mount;
		if ( _mount is null ) return false;
		if ( _mount.TryGetSoundFile( soundName, loop: true, out soundFile, out var error ) ) return true;

		if ( _failedLoads++ < 8 )
			Titanfall2Log.Warning( $"Titanfall 2 map audio could not load '{soundName}': {error} ({MapPath})." );
		return false;
	}

	static string EncodeEmitters( IReadOnlyList<Emitter> emitters )
	{
		using var stream = new MemoryStream();
		using ( var writer = new BinaryWriter( stream, System.Text.Encoding.UTF8, true ) )
		{
			writer.Write( DataMagic );
			writer.Write( DataVersion );
			writer.Write( emitters?.Count ?? 0 );
			foreach ( var emitter in emitters ?? Array.Empty<Emitter>() )
			{
				writer.Write( emitter.SoundName ?? string.Empty );
				writer.Write( emitter.Position.x );
				writer.Write( emitter.Position.y );
				writer.Write( emitter.Position.z );
				writer.Write( emitter.Radius );
				writer.Write( emitter.Volume );
			}
		}

		return Convert.ToBase64String( stream.GetBuffer(), 0, checked((int)stream.Length) );
	}

	static string EncodeZones( IReadOnlyList<SoundscapeZone> zones )
	{
		using var stream = new MemoryStream();
		using ( var writer = new BinaryWriter( stream, System.Text.Encoding.UTF8, true ) )
		{
			writer.Write( DataMagic );
			writer.Write( DataVersion );
			writer.Write( zones?.Count ?? 0 );
			foreach ( var zone in zones ?? Array.Empty<SoundscapeZone>() )
			{
				writer.Write( zone.SoundName ?? string.Empty );
				writer.Write( zone.Position.x );
				writer.Write( zone.Position.y );
				writer.Write( zone.Position.z );
				writer.Write( zone.Radius );
			}
		}

		return Convert.ToBase64String( stream.GetBuffer(), 0, checked((int)stream.Length) );
	}

	static IEnumerable<Emitter> DecodeEmitters( string encoded )
	{
		if ( string.IsNullOrWhiteSpace( encoded ) ) yield break;

		using var stream = new MemoryStream( Convert.FromBase64String( encoded ), false );
		using var reader = new BinaryReader( stream, System.Text.Encoding.UTF8, false );
		ReadHeader( reader, "emitters", out var count );
		for ( var index = 0; index < count; index++ )
		{
			var soundName = reader.ReadString();
			var position = new Vector3( reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle() );
			var radius = reader.ReadSingle();
			var volume = reader.ReadSingle();
			if ( string.IsNullOrWhiteSpace( soundName ) ) continue;
			yield return new Emitter( soundName, position, radius, Math.Clamp( volume, 0.01f, 1f ) );
		}
	}

	static IEnumerable<SoundscapeZone> DecodeZones( string encoded )
	{
		if ( string.IsNullOrWhiteSpace( encoded ) ) yield break;

		using var stream = new MemoryStream( Convert.FromBase64String( encoded ), false );
		using var reader = new BinaryReader( stream, System.Text.Encoding.UTF8, false );
		ReadHeader( reader, "soundscape zones", out var count );
		for ( var index = 0; index < count; index++ )
		{
			var soundName = reader.ReadString();
			var position = new Vector3( reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle() );
			var radius = reader.ReadSingle();
			if ( string.IsNullOrWhiteSpace( soundName ) ) continue;
			yield return new SoundscapeZone( soundName, position, radius );
		}
	}

	static void ReadHeader( BinaryReader reader, string label, out int count )
	{
		if ( reader.ReadInt32() != DataMagic ) throw new InvalidDataException( $"Titanfall 2 map audio {label} has an invalid signature." );
		if ( reader.ReadInt32() != DataVersion ) throw new InvalidDataException( $"Titanfall 2 map audio {label} has an unsupported version." );
		count = reader.ReadInt32();
		if ( count < 0 || count > MaximumEntryCount ) throw new InvalidDataException( $"Invalid Titanfall 2 map audio {label} count {count}." );
	}

	public override string ToString()
		=> $"Titanfall2MapAudio ({_emitters.Count} emitters, {_zoneCount} zones, {_startedEmitters} started)";
}
