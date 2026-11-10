using Titanfall2;

/// <summary>Runtime mount for the locally installed Steam version of Titanfall 2.</summary>
public partial class Titanfall2Mount : BaseGameMount
{
	const long AppId = 1237970;
	static object _registeredTypeLibrary;
	string _root;
	// Material prefetch workers read these two while the mount may fill or clear them
	// (remount/shutdown), so they are concurrent rather than plain dictionaries.
	readonly System.Collections.Concurrent.ConcurrentDictionary<ulong, string> _texturePaths = new();
	readonly Dictionary<ulong, string> _shaderSetNames = new();
	readonly Dictionary<ulong, MountedAssetSource> _shaderSetSources = new();
	readonly Dictionary<ulong, MountedAssetSource> _shaderSources = new();
	readonly Dictionary<ulong, DxbcShaderReflection> _shaderReflections = new();
	readonly Dictionary<ulong, string> _shaderReflectionErrors = new();
	readonly Dictionary<string, string> _materialPaths = new( StringComparer.OrdinalIgnoreCase );
	readonly System.Collections.Concurrent.ConcurrentDictionary<string, MountedAssetSource> _materialSources = new( StringComparer.OrdinalIgnoreCase );
	readonly Dictionary<string, ITitanfall2AssetSource> _legacyMaterialSources = new( StringComparer.OrdinalIgnoreCase );
	readonly Dictionary<string, ITitanfall2AssetSource> _legacyTextureSources = new( StringComparer.OrdinalIgnoreCase );
	readonly Dictionary<string, ITitanfall2AssetSource> _modelSources = new( StringComparer.OrdinalIgnoreCase );
	readonly List<Titanfall2BrushModelSource> _brushModelSources = new();
	readonly Dictionary<string, Titanfall2VmtDefinition> _legacyMaterialDefinitions = new( StringComparer.OrdinalIgnoreCase );
	readonly Dictionary<string, string> _legacyMaterialErrors = new( StringComparer.OrdinalIgnoreCase );
	readonly Dictionary<string, string> _legacyAnimatedTexturePaths = new( StringComparer.OrdinalIgnoreCase );
	readonly Dictionary<string, Titanfall2MaterialMetadata> _materialMetadata = new( StringComparer.OrdinalIgnoreCase );
	readonly Dictionary<string, Titanfall2MaterialDescriptor> _materialDescriptors = new( StringComparer.OrdinalIgnoreCase );
	readonly Dictionary<ulong, List<Titanfall2MaterialTextureReference>> _textureGuidDependencies = new();
	readonly Dictionary<string, List<Titanfall2MaterialTextureReference>> _texturePathDependencies = new( StringComparer.OrdinalIgnoreCase );
	readonly Dictionary<string, ITitanfall2AssetSource> _soundFileSources = new( StringComparer.OrdinalIgnoreCase );
	readonly Dictionary<string, MilesSoundAsset> _milesSoundAssets = new( StringComparer.OrdinalIgnoreCase );
	readonly Dictionary<string, SoundFile> _soundFileCache = new( StringComparer.OrdinalIgnoreCase );
	readonly Dictionary<string, double> _soundFailureRetryAfter = new( StringComparer.OrdinalIgnoreCase );
	readonly object _soundCacheLock = new();
	int _failedSoundLookups;
	MilesBankArchive _milesArchive;
	// Sorted bank records, filled by MountMilesSounds together with _milesSoundAssets.
	// TryFindMilesAsset binary searches this instead of scanning the whole table.
	MilesSoundAsset[] _milesSoundOrdered;
	ITitanfall2AssetSource _soundscapeScriptSource;
	Dictionary<string, string> _soundscapeLoops;
	// Sound-load instrumentation. The map audio stage decodes every soundscape loop up
	// front, so the "ready" log reports where that time went: bank lookup, BCF read,
	// native decode or SoundFile creation.
	long _soundLookupTicks;
	long _soundReadTicks;
	long _soundDecodeTicks;
	long _soundCreateTicks;
	long _soundPcmBytes;
	int _soundLoadCount;
	readonly Dictionary<string, Texture[]> _legacyParticleTextureFrames = new( StringComparer.OrdinalIgnoreCase );
	readonly Dictionary<string, float> _legacyParticleSheetFrameRates = new( StringComparer.OrdinalIgnoreCase );
	readonly Dictionary<string, Sprite> _legacyParticleSprites = new( StringComparer.OrdinalIgnoreCase );
	readonly object _materialMetadataLock = new();
	readonly object _materialDescriptorLock = new();
	readonly object _legacyParticleSpriteLock = new();
	readonly object _shaderReflectionLock = new();

	public override string Ident => "titanfall2";
	public override string Title => "Titanfall 2";
	public override long? SteamAppId => AppId;

	protected override void Initialize( InitializeContext context )
	{
		if ( !context.IsAppInstalled( AppId ) ) return;

		_root = context.GetAppDirectory( AppId );
		if ( string.IsNullOrWhiteSpace( _root ) || !System.IO.Directory.Exists( _root ) ) return;

		var rpakDirectory = Path.Combine( _root, "r2", "paks", "Win64" );
		var vpkDirectory = Path.Combine( _root, "vpk" );
		if ( !System.IO.Directory.Exists( rpakDirectory ) || !System.IO.Directory.Exists( vpkDirectory ) ) return;

		EnsureRuntimeTypesRegistered();
		IsInstalled = true;
	}

	protected override Task Mount( MountContext context )
	{
		Titanfall2GraphicsSettings.Connect();
		InvalidateSceneReleaseWork();
		_contentFingerprint = null;
		EnsureRuntimeTypesRegistered();
		Titanfall2CollisionDiagnostics.RegisterConsoleCommands();
		var registered = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
		var profile = Environment.GetEnvironmentVariable( "SBOX_TITANFALL2_NORTHSTAR_PROFILE" );
		if ( profile is null && System.IO.Directory.Exists( Path.Combine( _root, "R2Northstar" ) ) ) profile = Path.Combine( _root, "R2Northstar" );
		var snapshot = Titanfall2ContentDiscovery.Build( _root, string.IsNullOrWhiteSpace( profile ) ? null : profile,
			Environment.GetEnvironmentVariable( "SBOX_TITANFALL2_NORTHSTAR_MAP" ) );
		if ( _sessionContentRevision is not null && _sessionContentRevision != snapshot.Revision )
			throw new InvalidOperationException( "Titanfall 2 content changed; restart the engine to apply a new Northstar profile or source revision." );
		_sessionContentRevision = snapshot.Revision;
		ClearRegistrationTables();
		_contentSnapshot = snapshot;
		_contentFingerprint = Titanfall2ProcessedCache.Key( $"{typeof( Titanfall2Mount ).Assembly.ManifestModule.ModuleVersionId}|{snapshot.Revision}" );
		MountContentResources( context, registered, snapshot );
		RegisterSkyMaterial( context, registered );
		MountMilesSounds( context, registered );
		Log.Info( $"Titanfall 2 content snapshot {_contentSnapshot.Revision}: {_contentSnapshot.Entries.Count} entries, {_contentSnapshot.Winners.Count} winners." );
		foreach ( var diagnostic in _contentSnapshot.Diagnostics ) Log.Warning( diagnostic );
		IsMounted = true;
		return Task.CompletedTask;
	}

	/// <summary>
	/// Drops every registration table the asset layers fill. The tables used to be cleared inside
	/// the two resource-mounting functions; hoisting them here keeps one reset point for all
	/// layers and is behaviour-neutral, because those functions only read files before their old
	/// clear point.
	/// </summary>
	void ClearRegistrationTables()
	{
		_texturePaths.Clear();
		_shaderSetNames.Clear();
		_shaderSetSources.Clear();
		_shaderSources.Clear();
		_materialPaths.Clear();
		_materialSources.Clear();
		_legacyMaterialSources.Clear();
		_legacyTextureSources.Clear();
		_legacyAnimatedTexturePaths.Clear();
		_modelSources.Clear();
		ResetParticleCatalog();
		lock ( _materialMetadataLock )
		{
			// Background material loads write these under the same lock.
			_legacyMaterialDefinitions.Clear();
			_legacyMaterialErrors.Clear();
			_materialMetadata.Clear();
		}
		lock ( _shaderReflectionLock )
		{
			_shaderReflections.Clear();
			_shaderReflectionErrors.Clear();
		}
		lock ( _materialDescriptorLock )
		{
			_materialDescriptors.Clear();
			_textureGuidDependencies.Clear();
			_texturePathDependencies.Clear();
		}
	}

	/// <summary>
	/// Wraps retail's cubemap (which ships without a Source 2 sky material) in the native sky
	/// shader so both the main view and the 3D skybox use mounted art. Runs after the VPK layer
	/// because it inspects that layer's texture table.
	/// </summary>
	void RegisterSkyMaterial( MountContext context, HashSet<string> registered )
	{
		if ( _legacyTextureSources.ContainsKey( "skies/skycube" ) && registered.Add( "skies/skycube.vmat" ) )
			context.Add( ResourceType.Material, "skies/skycube", new Titanfall2SkyMaterialLoader() );
	}

	protected override void Shutdown()
	{
		Titanfall2GraphicsSettings.Disconnect();
		InvalidateSceneReleaseWork();
		_contentSnapshot = null;
		ReleaseSoundEventSessions();
		Titanfall2CollisionDiagnostics.UnregisterConsoleCommands();
		ReleaseMountCaches();
		ReleaseMapImports();
		foreach ( var source in _brushModelSources ) source.Dispose();
		_brushModelSources.Clear();
		ResetParticleCatalog();
		_modelSources.Clear();
		_texturePaths.Clear();
		_shaderSetNames.Clear();
		_shaderSetSources.Clear();
		_shaderSources.Clear();
		_materialPaths.Clear();
		_materialSources.Clear();
		_legacyMaterialSources.Clear();
		_legacyTextureSources.Clear();
		_legacyAnimatedTexturePaths.Clear();
		lock ( _materialMetadataLock )
		{
			// Background material loads write these under the same lock.
			_legacyMaterialDefinitions.Clear();
			_legacyMaterialErrors.Clear();
			_materialMetadata.Clear();
		}
		lock ( _shaderReflectionLock )
		{
			_shaderReflections.Clear();
			_shaderReflectionErrors.Clear();
		}
		MaterialLoader.ShutdownSharedResources();
		StarPakIndex.Clear();
		lock ( _soundCacheLock )
		{
			_soundFileCache.Clear();
			_soundFailureRetryAfter.Clear();
		}
		_milesSoundAssets.Clear();
		_soundFileSources.Clear();
		_milesArchive = null;
		_milesSoundOrdered = null;
		_soundscapeScriptSource = null;
		_soundscapeLoops = null;
		_soundLookupTicks = 0;
		_soundReadTicks = 0;
		_soundDecodeTicks = 0;
		_soundCreateTicks = 0;
		_soundPcmBytes = 0;
		_soundLoadCount = 0;
		lock ( _materialDescriptorLock )
		{
			_materialDescriptors.Clear();
			_textureGuidDependencies.Clear();
			_texturePathDependencies.Clear();
		}
	}

	void ReleaseMountCaches()
	{
		var particleSheets = 0;
		var particleTextures = 0;
		lock ( _legacyParticleSpriteLock )
		{
			particleSheets = _legacyParticleTextureFrames.Count;
			particleTextures = _legacyParticleTextureFrames.Values.Sum( static frames => frames?.Length ?? 0 );
			// Live sprites can outlast a mount refresh. Drop our references rather
			// than invalidating textures still referenced by those scene instances.
			// Their resource wrappers release native handles after the last user dies.
			_legacyParticleTextureFrames.Clear();
			_legacyParticleSheetFrameRates.Clear();
			_legacyParticleSprites.Clear();
		}
		var rpakCache = RpakArchive.GetCacheStatistics();
		RpakArchive.ClearStrongBufferCache();
		Log.Info( $"Titanfall 2 mount caches released: {particleSheets} particle sheets/{particleTextures} textures, "
			+ $"{rpakCache.StrongBuffers} RPAK buffers/{rpakCache.StrongBufferBytes / (1024.0 * 1024.0):0.0} MB." );
	}

	/// <summary>
	/// Resolves a map-authored sound name to a playable <see cref="SoundFile"/>.
	/// Names come from <c>ambient_generic.soundName</c> and soundscape <c>playlooping</c>
	/// entries, so both the BCF bank (<c>sound/&lt;name&gt;</c>) and loose <c>.wav</c> entries
	/// are searched. Files are cached per loop flag because looping is baked into the sound file.
	/// </summary>
	internal bool TryGetSoundFile( string soundName, bool loop, out SoundFile soundFile, out string error )
	{
		soundFile = null;
		error = null;

		try
		{
			if ( string.IsNullOrWhiteSpace( soundName ) )
			{
				error = "empty sound name";
				return false;
			}

			var key = NormalizeSoundKey( soundName );
			var cacheKey = loop ? key + "|loop" : key;
			lock ( _soundCacheLock )
			{
				if ( _soundFileCache.TryGetValue( cacheKey, out soundFile ) ) return true;
				// A missing/failed sound used to be cached as a permanent null, so one
				// transient Miles decode failure silenced it for the whole mount. Retry
				// after the same backoff the model loader uses.
				if ( _soundFailureRetryAfter.TryGetValue( cacheKey, out var retryAt )
					&& Titanfall2Log.ElapsedSeconds < retryAt )
				{
					error = $"sound '{soundName}' is unavailable";
					return false;
				}
			}

			var created = LoadSoundFile( key, loop, out error );
			lock ( _soundCacheLock )
			{
				if ( created is not null )
				{
					_soundFileCache[cacheKey] = created;
					_soundFailureRetryAfter.Remove( cacheKey );
				}
				else
				{
					_soundFailureRetryAfter[cacheKey] = Titanfall2Log.ElapsedSeconds + ModelLoader.FailureRetrySeconds;
				}
			}
			soundFile = created;
			return created is not null;
		}
		catch ( Exception exception )
		{
			error = exception.Message;
			return false;
		}
	}

	SoundFile LoadSoundFile( string key, bool loop, out string error )
	{
		error = null;

		var started = System.Diagnostics.Stopwatch.GetTimestamp();
		// The out argument has to be declared up front: the compiler cannot prove it is assigned
		// when the lookup result is carried through a bool local instead of an if condition.
		MilesSoundAsset asset = default;
		// Explicit Mod file replacement wins before bank/event lookup. A broken winner
		// fails here, rather than playing the original and hiding a failed override.
		if ( _contentSnapshot?.TryGet( key + ".wav", out var replacement ) == true
			&& replacement.SourceId.StartsWith( "northstar:", StringComparison.Ordinal ) )
		{
			var replacementSource = _contentSnapshot.GetSource( replacement.Path );
			if ( !replacementSource.TryReadAllBytes( out var wav, out error ) ) return null;
			var replacementSound = SoundFile.FromWav( key + ".wav", wav, new SoundFile.LoadOptions { Loop = loop } );
			if ( replacementSound is null ) error = "Mod SoundFile.FromWav returned null";
			return replacementSound;
		}
		var inBank = _milesArchive is not null && TryFindMilesAsset( key, out asset );
		_soundLookupTicks += System.Diagnostics.Stopwatch.GetTimestamp() - started;
		if ( inBank )
		{
			started = System.Diagnostics.Stopwatch.GetTimestamp();
			var read = _milesArchive.TryReadBcf( asset, out var bcf, out var readError );
			_soundReadTicks += System.Diagnostics.Stopwatch.GetTimestamp() - started;
			if ( !read )
			{
				error = readError;
			}
			else
			{
				started = System.Diagnostics.Stopwatch.GetTimestamp();
				var decoded = _milesArchive.TryDecode( bcf, out var pcm, out var rate, out var channels, out var decodeError );
				_soundDecodeTicks += System.Diagnostics.Stopwatch.GetTimestamp() - started;
				if ( !decoded )
				{
					error = decodeError;
				}
				else
				{
					started = System.Diagnostics.Stopwatch.GetTimestamp();
					var sound = SoundFile.FromPcm( key, pcm, new SoundFile.PcmOptions
					{
						Channels = channels,
						Rate = rate,
						Bits = 16,
						Loop = loop
					} );
					_soundCreateTicks += System.Diagnostics.Stopwatch.GetTimestamp() - started;
					if ( sound is not null )
					{
						_soundPcmBytes += pcm.Length;
						_soundLoadCount++;
						return sound;
					}
					error = "SoundFile.FromPcm returned null";
				}
			}
		}

		if ( _soundFileSources.TryGetValue( key + ".wav", out var source ) )
		{
			if ( !source.TryReadAllBytes( out var data, out var wavError ) )
			{
				error = wavError;
			}
			else
			{
				var sound = SoundFile.FromWav( key + ".wav", data, new SoundFile.LoadOptions { Loop = loop } );
				if ( sound is not null ) return sound;
				error = "SoundFile.FromWav returned null";
			}
		}

		error ??= $"sound '{key}' was not found";
		if ( _failedSoundLookups++ < 4 )
		{
			var tail = StripSoundPrefix( key );
			var samples = _milesSoundAssets.Keys
				.Where( existing => existing.Contains( tail, StringComparison.OrdinalIgnoreCase ) )
				.Take( 4 );
			Titanfall2Log.Warning( $"Titanfall 2 sound lookup missed '{key}' "
				+ $"({_milesSoundAssets.Count} bank entries / {_soundFileSources.Count} loose wav entries; "
				+ $"bank keys containing '{tail}': {string.Join( " | ", samples )})." );
		}
		return null;
	}

	/// <summary>
	/// Finds the bank record for a sound name. Entity names are flat while the bank can store
	/// them under a path, so an exact lookup is followed by a suffixed variant match.
	/// <para>
	/// <paramref name="key"/> always comes from <c>NormalizeSoundKey</c>, so it carries
	/// the "sound/" prefix every bank name has.
	/// </para>
	/// </summary>
	bool TryFindMilesAsset( string key, out MilesSoundAsset asset )
	{
		if ( _milesSoundAssets.TryGetValue( key, out asset ) ) return true;

		var bareKey = StripSoundPrefix( key );

		// 1: exact bare name. The dictionary lookup above already is that match: every bank
		//    name is stored as "sound/<path>" (MilesBankArchive.TryNormalizeName), so a
		//    stripped bank name equals bareKey only when the full name equals key. The table
		//    wide scan that used to repeat this lookup could never hit.

		// 2: authored logical name resolved to a bank variant. Map soundscapes reference
		//    "amb_x_INT_SmallRoom" while the bank stores channel/format variants such as
		//    "amb_x_int_smallroom_6ch_v1_lp". Prefer the shortest variant so the generic
		//    loop wins over special-purpose ones. The records are sorted by resource name, so
		//    a binary search finds the run sharing the "<key>_" prefix and only that run is
		//    scanned. Candidates still arrive in table order, so ties resolve as before.
		MilesSoundAsset best = null;
		string bestKey = null;
		var ordered = _milesSoundOrdered;
		if ( ordered is not null )
		{
			var low = 0;
			var high = ordered.Length - 1;
			var first = -1;
			while ( low <= high )
			{
				var middle = (low + high) >> 1;
				if ( StringComparer.OrdinalIgnoreCase.Compare( ordered[middle].ResourceName, key ) < 0 )
				{
					low = middle + 1;
				}
				else
				{
					first = middle;
					high = middle - 1;
				}
			}

			if ( first >= 0 )
			{
				for ( var index = first; index < ordered.Length; index++ )
				{
					var name = ordered[index].ResourceName;
					if ( !name.StartsWith( key, StringComparison.OrdinalIgnoreCase ) ) break;
					if ( name.Length <= key.Length || name[key.Length] != '_' ) continue;
					if ( bestKey is not null && name.Length >= bestKey.Length ) continue;
					best = ordered[index];
					bestKey = name;
				}
			}
		}

		if ( best is not null )
		{
			asset = best;
			return true;
		}

		// 3: the bank's string table contains authored sound event names ("amb_x_INT_SmallRoom",
		//    "amb_angelcity_emitter_dockwater_02") that are not source records. The sources are
		//    channel/format variants ("..._6ch_v1_lp", "amb_emit_..._1ch_02"), so fall back to a
		//    token score: equal tokens count fully, tokens sharing a 4 character stem
		//    ("emitter"/"emit") count partially, and numbers must match exactly.
		var queryTokens = Tokenize( bareKey );
		if ( queryTokens.Length > 0 )
		{
			var bestScore = 0.55;
			foreach ( var pair in _milesSoundAssets )
			{
				var bare = StripSoundPrefix( pair.Key );
				var score = ScoreTokens( queryTokens, Tokenize( bare ) );
				if ( score < bestScore ) continue;
				if ( score == bestScore && bestKey is not null && bare.Length >= bestKey.Length ) continue;
				bestScore = score;
				bestKey = bare;
				best = pair.Value;
			}

			if ( best is not null )
			{
				if ( Titanfall2Log.VerboseLogging )
					Titanfall2Log.Info( $"Titanfall 2 sound '{key}' resolved to bank source '{bestKey}' (score {bestScore:0.00})." );
				asset = best;
				return true;
			}
		}

		asset = null;
		return false;
	}

	static string[] Tokenize( string value )
		=> value.ToLowerInvariant().Split( ['_', '-', '.', '/', ' '], StringSplitOptions.RemoveEmptyEntries );

	static double ScoreTokens( string[] query, string[] candidate )
	{
		if ( query.Length == 0 || candidate.Length == 0 ) return 0;

		double score = 0;
		foreach ( var token in query )
		{
			var best = 0.0;
			foreach ( var other in candidate )
			{
				if ( token == other )
				{
					best = 1.0;
					break;
				}

				if ( token.Length >= 4 && other.Length >= 4
					&& string.CompareOrdinal( token, 0, other, 0, 4 ) == 0 )
				{
					best = Math.Max( best, 0.8 );
				}
			}

			score += best;
		}

		return score / query.Length;
	}

	static string StripSoundPrefix( string path )
		=> path.StartsWith( "sound/", StringComparison.OrdinalIgnoreCase ) ? path["sound/".Length..] : path;

	/// <summary>Maps a <c>trigger_soundscape.play_event</c> soundscape name to its looping sound.</summary>
	internal int MilesSoundCount => _milesSoundAssets.Count;
	internal int WavSoundCount => _soundFileSources.Count;

	/// <summary>Where the time of the sound files loaded through this mount went.</summary>
	internal readonly record struct SoundLoadStatistics(
		int Sounds,
		long LookupTicks,
		long ReadTicks,
		long DecodeTicks,
		long CreateTicks,
		long PcmBytes )
	{
		public double LookupMilliseconds => TicksToMilliseconds( LookupTicks );
		public double ReadMilliseconds => TicksToMilliseconds( ReadTicks );
		public double DecodeMilliseconds => TicksToMilliseconds( DecodeTicks );
		public double CreateMilliseconds => TicksToMilliseconds( CreateTicks );
		static double TicksToMilliseconds( long ticks ) => ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
	}

	internal SoundLoadStatistics SoundLoads => new(
		_soundLoadCount,
		_soundLookupTicks,
		_soundReadTicks,
		_soundDecodeTicks,
		_soundCreateTicks,
		_soundPcmBytes );

	internal bool TryGetSoundscapeLoop( string soundscapeName, out string soundName )
	{
		soundName = null;
		if ( string.IsNullOrWhiteSpace( soundscapeName ) ) return false;

		EnsureSoundscapeLoops();
		return _soundscapeLoops.TryGetValue( soundscapeName.Trim(), out soundName )
			&& !string.IsNullOrWhiteSpace( soundName );
	}

	/// <summary>Returns the loop of a soundscape, falling back to the soundscape name itself.</summary>
	internal string ResolveSoundscapeLoop( string soundscapeName )
		=> TryGetSoundscapeLoop( soundscapeName, out var soundName ) ? soundName : soundscapeName;

	void EnsureSoundscapeLoops()
	{
		if ( _soundscapeLoops is not null ) return;

		_soundscapeLoops = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase );
		if ( _soundscapeScriptSource is null ) return;
		if ( !_soundscapeScriptSource.TryReadAllBytes( out var bytes, out var readError ) )
		{
			Titanfall2Log.Warning( $"Unable to read the Titanfall 2 soundscape script: {readError}" );
			return;
		}

		foreach ( var pair in Titanfall2SoundscapeScript.Parse( System.Text.Encoding.UTF8.GetString( bytes ) ) )
			_soundscapeLoops[pair.Key] = pair.Value;
	}

	static string NormalizeSoundKey( string name )
	{
		var normalized = name.Replace( '\\', '/' ).Trim().Trim( '/' ).Trim( '"' );
		foreach ( var extension in new[] { ".vsnd", ".wav" } )
		{
			if ( normalized.EndsWith( extension, StringComparison.OrdinalIgnoreCase ) )
				normalized = normalized[..^extension.Length];
		}

		return normalized.StartsWith( "sound/", StringComparison.OrdinalIgnoreCase )
			? normalized
			: "sound/" + normalized;
	}

	internal void EnsureRuntimeTypesRegistered()
	{
		try
		{
			var typeLibrary = Game.TypeLibrary;
			if ( typeLibrary is null || ReferenceEquals( _registeredTypeLibrary, typeLibrary ) ) return;
			var addAssembly = typeLibrary?.GetType().GetMethod(
				"AddAssembly",
				System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
				null,
				[typeof( System.Reflection.Assembly ), typeof( bool )],
				null );
			if ( addAssembly is null )
			{
				Log.Warning( "Unable to register Titanfall 2 runtime components: TypeLibrary.AddAssembly was not found." );
				return;
			}

			// Mount assemblies are discovered by the mount host, but are not automatically
			// added to the game's TypeLibrary. Register only explicitly exposed [Library]
			// types so mounted scenes can serialize and restore their streaming components.
			addAssembly.Invoke( typeLibrary, [typeof( Titanfall2Mount ).Assembly, false] );
			_registeredTypeLibrary = typeLibrary;
			Log.Info( "Titanfall 2 runtime streaming components registered with the game TypeLibrary." );
		}
		catch ( Exception exception )
		{
			Log.Warning( exception, "Unable to register Titanfall 2 runtime components." );
		}
	}

	void MountMilesSounds( MountContext context, HashSet<string> registered )
	{
		var entries = _contentSnapshot.Winners.Where( entry => entry.Kind == "miles" ).ToArray();
		if ( entries.Length == 0 ) return;
		var archive = entries[0].Bank;
		_milesArchive = archive;
		_milesSoundOrdered = entries.Select( entry => entry.Sound ).ToArray();
		var sounds = 0;
		foreach ( var asset in _milesSoundOrdered )
		{
			_milesSoundAssets[asset.ResourceName] = asset;
			if ( !registered.Add( asset.ResourceName + ".vsnd" ) ) continue;
			context.Add( ResourceType.Sound, asset.ResourceName, new MilesSoundLoader( archive, asset ) );
			sounds++;
		}
		Log.Info( $"Titanfall 2 Miles bank mounted: {sounds} BCF sounds (localized language index {archive.PreferredLanguage})." );
	}

	static bool IsDepotVpkPath( string path )
	{
		if ( string.IsNullOrWhiteSpace( path ) ) return false;
		var normalized = path.Replace( '\\', '/' ).Trim().TrimStart( '/' );
		return normalized.Equals( "depot", StringComparison.OrdinalIgnoreCase )
			|| normalized.StartsWith( "depot/", StringComparison.OrdinalIgnoreCase );
	}

	static bool TryGetEntityMapName( string path, string suffix, out string mapName )
	{
		mapName = null;
		if ( string.IsNullOrWhiteSpace( path ) || !path.EndsWith( suffix + ".ent", StringComparison.OrdinalIgnoreCase ) )
			return false;

		var fileName = Path.GetFileNameWithoutExtension( path );
		if ( string.IsNullOrWhiteSpace( fileName ) || !fileName.EndsWith( suffix, StringComparison.OrdinalIgnoreCase ) )
			return false;

		mapName = fileName[..^suffix.Length];
		return !string.IsNullOrWhiteSpace( mapName );
	}

	internal static int GetPatchRank( string path )
	{
		var match = System.Text.RegularExpressions.Regex.Match( Path.GetFileNameWithoutExtension( path ), @"\((\d\d)\)$" );
		return match.Success && int.TryParse( match.Groups[1].Value, out var rank ) ? rank : 0;
	}

	internal bool TryGetModelSource( string modelPath, out ITitanfall2AssetSource source ) =>
		_modelSources.TryGetValue( NormalizeModelSourcePath( modelPath ), out source );
	internal bool TryGetTexturePath( ulong guid, out string path ) => _texturePaths.TryGetValue( guid, out path );
	internal bool TryGetShaderSetName( ulong guid, out string name ) => _shaderSetNames.TryGetValue( guid, out name );
	internal bool TryGetMaterialPath( string name, out string path ) => _materialPaths.TryGetValue( name, out path );
	internal bool TryGetLegacyAnimatedTexturePath( string name, out string path ) =>
		_legacyAnimatedTexturePaths.TryGetValue( Titanfall2LegacyPath.NormalizeTextureName( name ), out path );

	internal void RegisterMaterialDescriptor( Titanfall2MaterialDescriptor descriptor, bool persist = true )
	{
		if ( descriptor is null || string.IsNullOrWhiteSpace( descriptor.Name ) ) return;
		lock ( _materialDescriptorLock )
		{
			if ( !_materialDescriptors.TryAdd( descriptor.Name, descriptor ) ) return;
			foreach ( var binding in descriptor.TextureBindings )
			{
				var reference = new Titanfall2MaterialTextureReference(
					descriptor.Name, binding.Semantic, binding.ShaderRegister, binding.ShaderResourceName );
				if ( binding.TextureGuid != 0 ) AddDependency( _textureGuidDependencies, binding.TextureGuid, reference );
				if ( !string.IsNullOrWhiteSpace( binding.TexturePath ) )
					AddDependency( _texturePathDependencies, binding.TexturePath, reference );
			}
		}
		if ( persist && descriptor.SourceKind == Titanfall2MaterialSourceKind.Rpak ) WriteMaterialCache( descriptor );
	}

	internal bool TryGetMaterialDescriptor( string name, out Titanfall2MaterialDescriptor descriptor )
	{
		lock ( _materialDescriptorLock ) return _materialDescriptors.TryGetValue( name, out descriptor );
	}

	internal bool TryGetOrCreateLegacyMaterialDescriptor( string name, out Titanfall2MaterialDescriptor descriptor, out string error )
	{
		if ( TryGetMaterialDescriptor( name, out descriptor ) )
		{
			error = null;
			return true;
		}
		if ( !TryGetLegacyMaterialDefinition( name, out var definition, out error ) ) return false;
		descriptor = Titanfall2MaterialDescriptor.FromVmt( name, definition );
		RegisterMaterialDescriptor( descriptor );
		return true;
	}

	internal bool TryGetTextureDependents( ulong guid, out IReadOnlyList<Titanfall2MaterialTextureReference> references )
	{
		lock ( _materialDescriptorLock )
		{
			if ( _textureGuidDependencies.TryGetValue( guid, out var found ) )
			{
				// Copy under the lock: background material loads can still append to the list.
				references = found.ToArray();
				return true;
			}
		}
		references = Array.Empty<Titanfall2MaterialTextureReference>();
		return false;
	}

	static void AddDependency<TKey>( Dictionary<TKey, List<Titanfall2MaterialTextureReference>> dependencies,
		TKey key, Titanfall2MaterialTextureReference reference )
	{
		if ( !dependencies.TryGetValue( key, out var references ) ) dependencies.Add( key, references = [] );
		if ( !references.Contains( reference ) ) references.Add( reference );
	}

	internal (int Materials, int TextureGuids, int TexturePaths, int References, int Reflected, int Unknown) GetMaterialDependencyStatistics()
	{
		lock ( _materialDescriptorLock )
		{
			var bindings = _materialDescriptors.Values.SelectMany( descriptor => descriptor.TextureBindings ).ToArray();
			return (
				_materialDescriptors.Count,
				_textureGuidDependencies.Count,
				_texturePathDependencies.Count,
				bindings.Length,
				bindings.Count( binding => binding.Reflected ),
				bindings.Count( binding => binding.Semantic == Titanfall2TextureSemantic.Unknown ));
		}
	}

	/// <summary>
	/// Builds a sprite for a legacy particle texture. For VTF particle sheets,
	/// <paramref name="sequence"/> selects the sheet sequence; a one-frame sequence
	/// becomes a static sprite cropped to that frame.
	/// </summary>
	internal bool TryGetLegacyParticleSprite( string textureName, float frameRate, out Sprite sprite, out string error, int sequence = 0 )
	{
		sprite = null;
		error = null;
		var normalized = Titanfall2LegacyPath.NormalizeTextureName( textureName );
		if ( string.IsNullOrWhiteSpace( normalized ) )
		{
			error = "Particle texture name is empty.";
			return false;
		}

		frameRate = Math.Clamp( frameRate > 0f ? frameRate : 1f, 0.1f, 120f );
		var spriteKey = $"{normalized}|{MathF.Round( frameRate * 100f )}|{sequence}";
		var sheetKey = $"{normalized}#{sequence}";
		lock ( _legacyParticleSpriteLock )
		{
			if ( _legacyParticleSprites.TryGetValue( spriteKey, out sprite ) ) return true;
			if ( _legacyTextureSources.TryGetValue( normalized, out var source ) )
			{
				if ( source.TryReadAllBytes( out var bytes, out var readError ) )
				{
					if ( !_legacyParticleTextureFrames.TryGetValue( normalized, out var frames )
						&& !_legacyParticleTextureFrames.TryGetValue( sheetKey, out frames ) )
					{
						if ( VtfTextureDecoder.GetFrameCount( bytes ) > 1 )
						{
							if ( !VtfTextureDecoder.TryCreateFrames( bytes, out frames, out error ) ) return false;
							if ( frames.Length > 1 ) _legacyParticleTextureFrames.Add( normalized, frames );
						}
						else if ( VtfTextureDecoder.TryCreateSheetFrames( bytes, sequence, out frames, out var sheetFrameRate, out _ ) )
						{
							_legacyParticleSheetFrameRates[sheetKey] = sheetFrameRate;
							_legacyParticleTextureFrames.Add( sheetKey, frames );
						}
					}
					if ( frames is { Length: > 0 } )
					{
						var effectiveRate = _legacyParticleSheetFrameRates.TryGetValue( sheetKey, out var sheetRate )
							? Math.Clamp( sheetRate * frameRate, 0.1f, 120f )
							: frameRate;
						sprite = frames.Length > 1 ? Sprite.FromTextures( frames, effectiveRate ) : Sprite.FromTexture( frames[0] );
						_legacyParticleSprites.Add( spriteKey, sprite );
						return true;
					}
				}
				else error = readError;
			}

			var texture = Texture.Load( $"mount://{Ident}/{normalized}.vtex", false );
			if ( texture is null || texture.IsError || !texture.IsValid )
			{
				error ??= $"Particle texture '{normalized}' could not be loaded.";
				return false;
			}
			sprite = Sprite.FromTexture( texture );
			_legacyParticleSprites.Add( spriteKey, sprite );
			return true;
		}
	}

	internal bool TryGetLegacyMaterialDefinition( string name, out Titanfall2VmtDefinition definition, out string error )
	{
		definition = null;
		error = null;
		if ( string.IsNullOrWhiteSpace( name ) )
		{
			error = "Material name is empty.";
			return false;
		}
		lock ( _materialMetadataLock )
		{
			if ( _legacyMaterialDefinitions.TryGetValue( name, out definition ) ) return true;
			if ( _legacyMaterialErrors.TryGetValue( name, out error ) ) return false;
		}
		if ( !_legacyMaterialSources.TryGetValue( name, out var source ) )
		{
			error = $"Legacy material source is not registered: {name}";
			return false;
		}
		if ( !source.TryReadAllBytes( out var bytes, out error )
			|| !Titanfall2VmtReader.TryRead( bytes, out definition, out error ) )
		{
			lock ( _materialMetadataLock ) _legacyMaterialErrors.TryAdd( name, error ?? "Unable to read VMT." );
			return false;
		}
		lock ( _materialMetadataLock ) _legacyMaterialDefinitions.TryAdd( name, definition );
		return true;
	}

	internal bool TryReadMaterialParameters( RpakArchive materialArchive, RpakAsset materialAsset, ulong shaderSetGuid,
		string shaderSetName, out Titanfall2MaterialParameters parameters, out string error )
	{
		parameters = default;
		if ( !materialArchive.TryReadCpuData( materialAsset, out var cpuData, out error ) ) return false;
		if ( cpuData.Length == 0 )
		{
			error = "Material has no CPU constant data.";
			return false;
		}
		if ( !TryGetShaderReflection( shaderSetGuid, out var reflection, out error ) ) return false;
		if ( !reflection.TryGetConstantBuffer( "CBufUberStatic", out var layout ) )
		{
			error = "DXBC constant buffer 'CBufUberStatic' was not found.";
			return false;
		}
		parameters = Titanfall2MaterialParameters.Read( layout, cpuData, shaderSetName );
		return true;
	}

	internal bool TryGetShaderReflection( ulong shaderSetGuid, out DxbcShaderReflection reflection, out string error )
	{
		lock ( _shaderReflectionLock )
		{
			if ( _shaderReflections.TryGetValue( shaderSetGuid, out reflection ) )
			{
				error = null;
				return true;
			}
			if ( _shaderReflectionErrors.TryGetValue( shaderSetGuid, out error ) ) return false;
		}

		reflection = null;
		error = null;
		if ( shaderSetGuid == 0 || !_shaderSetSources.TryGetValue( shaderSetGuid, out var shaderSetSource ) )
		{
			error = $"Shader set GUID 0x{shaderSetGuid:X16} is not registered.";
			CacheShaderReflectionError( shaderSetGuid, error );
			return false;
		}
		if ( shaderSetSource.Asset.Version != 8
			|| !shaderSetSource.Archive.TryReadAsset( shaderSetSource.Asset, out var shaderSetData, out error )
			|| shaderSetData.Header.Length < 88 )
		{
			error ??= $"Shader set '{shaderSetSource.Asset.Name}' has an unsupported or truncated v{shaderSetSource.Asset.Version} header.";
			CacheShaderReflectionError( shaderSetGuid, error );
			return false;
		}

		var pixelShaderGuid = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian( shaderSetData.Header.AsSpan( 0x50, 8 ) );
		if ( pixelShaderGuid == 0 || !_shaderSources.TryGetValue( pixelShaderGuid, out var shaderSource ) )
		{
			error = $"Pixel shader GUID 0x{pixelShaderGuid:X16} for '{shaderSetSource.Asset.Name}' is not registered.";
			CacheShaderReflectionError( shaderSetGuid, error );
			return false;
		}
		if ( !shaderSource.Archive.TryReadCpuData( shaderSource.Asset, out var dxbc, out error )
			|| !DxbcShaderReflection.TryParse( dxbc, out reflection, out error ) )
		{
			CacheShaderReflectionError( shaderSetGuid, error );
			return false;
		}

		lock ( _shaderReflectionLock ) _shaderReflections.TryAdd( shaderSetGuid, reflection );
		return true;
	}

	void CacheShaderReflectionError( ulong shaderSetGuid, string error )
	{
		lock ( _shaderReflectionLock ) _shaderReflectionErrors.TryAdd( shaderSetGuid, error ?? "Unable to reflect shader resources." );
	}

	internal bool TryGetMaterialMetadata( string name, out Titanfall2MaterialMetadata metadata )
	{
		metadata = default;
		if ( string.IsNullOrWhiteSpace( name ) ) return false;
		lock ( _materialMetadataLock )
		{
			if ( _materialMetadata.TryGetValue( name, out metadata ) ) return true;
		}
		if ( TryGetCachedMaterialDescriptor( name, out var cached ) )
		{
			metadata = cached.Metadata;
			lock ( _materialMetadataLock ) _materialMetadata.TryAdd( name, metadata );
			return true;
		}
		if ( !_materialSources.TryGetValue( name, out var source ) )
		{
			if ( !TryGetLegacyMaterialDefinition( name, out var legacyDefinition, out _ ) ) return false;
			metadata = legacyDefinition.GetMetadata( name );
			lock ( _materialMetadataLock ) _materialMetadata.TryAdd( name, metadata );
			return true;
		}
		if ( source.Asset.Version != 12
			|| !source.Archive.TryReadAsset( source.Asset, out var data, out _ ) ) return false;

		var shaderSetGuid = MaterialLoader.GetShaderSetGuid( data.Header );
		TryGetShaderSetName( shaderSetGuid, out var shaderSetName );
		metadata = MaterialLoader.ReadMetadata( data.Header, source.Asset.Name, shaderSetName );
		lock ( _materialMetadataLock ) _materialMetadata.TryAdd( name, metadata );
		return true;
	}

	readonly record struct MountedAssetSource( RpakArchive Archive, RpakAsset Asset );
}
