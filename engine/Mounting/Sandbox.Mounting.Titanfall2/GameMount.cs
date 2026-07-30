/// <summary>Runtime mount for the locally installed Steam version of Titanfall 2.</summary>
public partial class Titanfall2Mount : BaseGameMount
{
	const long AppId = 1237970;
	static object _registeredTypeLibrary;
	string _root;
	readonly Dictionary<ulong, string> _texturePaths = new();
	readonly Dictionary<ulong, string> _shaderSetNames = new();
	readonly Dictionary<ulong, MountedAssetSource> _shaderSetSources = new();
	readonly Dictionary<ulong, MountedAssetSource> _shaderSources = new();
	readonly Dictionary<ulong, DxbcShaderReflection> _shaderReflections = new();
	readonly Dictionary<ulong, string> _shaderReflectionErrors = new();
	readonly Dictionary<string, string> _materialPaths = new( StringComparer.OrdinalIgnoreCase );
	readonly Dictionary<string, MountedAssetSource> _materialSources = new( StringComparer.OrdinalIgnoreCase );
	readonly Dictionary<string, ITitanfall2AssetSource> _legacyMaterialSources = new( StringComparer.OrdinalIgnoreCase );
	readonly Dictionary<string, ITitanfall2AssetSource> _legacyTextureSources = new( StringComparer.OrdinalIgnoreCase );
	readonly Dictionary<string, Titanfall2VmtDefinition> _legacyMaterialDefinitions = new( StringComparer.OrdinalIgnoreCase );
	readonly Dictionary<string, string> _legacyMaterialErrors = new( StringComparer.OrdinalIgnoreCase );
	readonly Dictionary<string, string> _legacyAnimatedTexturePaths = new( StringComparer.OrdinalIgnoreCase );
	readonly Dictionary<string, Titanfall2MaterialMetadata> _materialMetadata = new( StringComparer.OrdinalIgnoreCase );
	readonly Dictionary<string, Titanfall2MaterialDescriptor> _materialDescriptors = new( StringComparer.OrdinalIgnoreCase );
	readonly Dictionary<ulong, List<Titanfall2MaterialTextureReference>> _textureGuidDependencies = new();
	readonly Dictionary<string, List<Titanfall2MaterialTextureReference>> _texturePathDependencies = new( StringComparer.OrdinalIgnoreCase );
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
		EnsureRuntimeTypesRegistered();
		var registered = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
		// Titanfall 2 resolves native RPAK MATL/TXTR assets before legacy VMT/VTF
		// search paths. Register RPAK first so VPK assets only fill missing names.
		MountRpakResources( context, registered );
		MountVpkResources( context, registered );
		MountMilesSounds( context, registered );
		IsMounted = true;
		if ( Environment.GetEnvironmentVariable( "SBOX_TITANFALL2_SELFTEST" ) == "1" ) _ = RunSelfTest();
		return Task.CompletedTask;
	}

	protected override void Shutdown()
	{
		ReleaseSceneResources();
		ResetParticleCatalog();
		MaterialLoader.ShutdownSharedResources();
		StarPakIndex.Clear();
		lock ( _materialDescriptorLock )
		{
			_materialDescriptors.Clear();
			_textureGuidDependencies.Clear();
			_texturePathDependencies.Clear();
		}
	}

	internal void ReleaseSceneResources()
	{
		var particleSheets = 0;
		var particleTextures = 0;
		lock ( _legacyParticleSpriteLock )
		{
			particleSheets = _legacyParticleTextureFrames.Count;
			particleTextures = _legacyParticleTextureFrames.Values.Sum( static frames => frames?.Length ?? 0 );
			foreach ( var frames in _legacyParticleTextureFrames.Values )
				foreach ( var texture in frames ) texture?.Dispose();
			_legacyParticleTextureFrames.Clear();
			_legacyParticleSheetFrameRates.Clear();
			_legacyParticleSprites.Clear();
		}
		var rpakCache = RpakArchive.GetCacheStatistics();
		RpakArchive.ClearStrongBufferCache();
		Log.Info( $"Titanfall 2 scene caches released: {particleSheets} particle sheets/{particleTextures} textures, "
			+ $"{rpakCache.StrongBuffers} RPAK buffers/{rpakCache.StrongBufferBytes / (1024.0 * 1024.0):0.0} MB." );
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
		var bankPath = Path.Combine( _root, "r2", "sound", "general.mbnk" );
		if ( !File.Exists( bankPath ) ) return;
		if ( !MilesBankArchive.TryOpen( _root, bankPath, out var archive, out var error ) )
		{
			Log.Warning( $"Unable to index Titanfall 2 Miles bank '{Path.GetFileName( bankPath )}': {error}" );
			return;
		}

		var sounds = 0;
		foreach ( var asset in archive.Assets )
		{
			if ( !registered.Add( asset.ResourceName + ".vsnd" ) ) continue;
			context.Add( ResourceType.Sound, asset.ResourceName, new MilesSoundLoader( archive, asset ) );
			sounds++;
		}
		Log.Info( $"Titanfall 2 Miles bank mounted: {sounds} BCF sounds (localized language index {archive.PreferredLanguage})." );
	}

	void MountVpkResources( MountContext context, HashSet<string> registered )
	{
		var vpkDirectory = Path.Combine( _root, "vpk" );
		_legacyMaterialSources.Clear();
		_legacyTextureSources.Clear();
		_legacyMaterialDefinitions.Clear();
		_legacyMaterialErrors.Clear();
		_legacyAnimatedTexturePaths.Clear();
		ResetParticleCatalog();
		var archives = new List<VpkArchive>();
		foreach ( var directoryFile in System.IO.Directory.EnumerateFiles( vpkDirectory, "*_dir.vpk", SearchOption.TopDirectoryOnly )
			.OrderBy( static path => string.Equals( Path.GetFileName( path ), "englishclient_mp_common.bsp.pak000_dir.vpk", StringComparison.OrdinalIgnoreCase ) ? 0 : 1 )
			.ThenBy( static path => path, StringComparer.OrdinalIgnoreCase ) )
		{
			if ( !VpkArchive.TryOpen( directoryFile, out var archive, out var error ) )
			{
				Log.Warning( $"Unable to index Titanfall 2 VPK '{Path.GetFileName( directoryFile )}': {error}" );
				continue;
			}
			archives.Add( archive );
		}
		var vpkResolver = new VpkPathResolver( archives );

		var spawnEntitySources = new Dictionary<string, ITitanfall2AssetSource>( StringComparer.OrdinalIgnoreCase );
		var environmentEntitySources = new Dictionary<string, ITitanfall2AssetSource>( StringComparer.OrdinalIgnoreCase );
		var particleEntitySources = new Dictionary<string, ITitanfall2AssetSource>( StringComparer.OrdinalIgnoreCase );
		var scriptEntitySources = new Dictionary<string, ITitanfall2AssetSource>( StringComparer.OrdinalIgnoreCase );
		foreach ( var archive in archives )
		{
			foreach ( var entry in archive.Entries )
			{
				if ( TryGetEntityMapName( entry.Path, "_spawn", out var spawnMapName ) )
					spawnEntitySources.TryAdd( spawnMapName, new VpkAssetSource( archive, entry, vpkResolver ) );
				else if ( TryGetEntityMapName( entry.Path, "_env", out var environmentMapName ) )
					environmentEntitySources.TryAdd( environmentMapName, new VpkAssetSource( archive, entry, vpkResolver ) );
				else if ( TryGetEntityMapName( entry.Path, "_fx", out var particleMapName ) )
					particleEntitySources.TryAdd( particleMapName, new VpkAssetSource( archive, entry, vpkResolver ) );
				else if ( TryGetEntityMapName( entry.Path, "_script", out var scriptMapName ) )
					scriptEntitySources.TryAdd( scriptMapName, new VpkAssetSource( archive, entry, vpkResolver ) );
			}
		}

		var legacyMaterials = 0;
		var legacyTextures = 0;
		foreach ( var archive in archives )
		{
			foreach ( var entry in archive.Entries )
			{
				var extension = Path.GetExtension( entry.Path ).ToLowerInvariant();
				if ( extension == ".vmt" && Titanfall2LegacyPath.TryGetMaterialName( entry.Path, out var materialName ) )
				{
					var resourceKey = materialName + ".vmat";
					var legacyMaterialSource = new VpkAssetSource( archive, entry, vpkResolver );
					_legacyMaterialSources.TryAdd( materialName, legacyMaterialSource );
					legacyMaterials++;
					if ( registered.Add( resourceKey ) )
					{
						_materialPaths.TryAdd( materialName, materialName );
						context.Add( ResourceType.Material, materialName, new LegacyMaterialLoader( materialName ) );
					}
					continue;
				}
				if ( extension == ".vtf" && Titanfall2LegacyPath.TryGetTextureName( entry.Path, out var textureName ) )
				{
					var resourceKey = textureName + ".vtex";
					var legacyTextureSource = new VpkAssetSource( archive, entry, vpkResolver );
					_legacyTextureSources.TryAdd( textureName, legacyTextureSource );
					if ( registered.Add( resourceKey ) )
						context.Add( ResourceType.Texture, textureName, new Titanfall2TextureLoader( legacyTextureSource ) );
					var animatedName = Titanfall2LegacyPath.GetAnimatedTextureName( textureName );
					if ( registered.Add( animatedName + ".vtex" ) )
					{
						context.Add( ResourceType.Texture, animatedName, new Titanfall2TextureLoader( legacyTextureSource, animatedVtfAtlas: true ) );
						_legacyAnimatedTexturePaths.TryAdd( textureName, animatedName );
					}
					legacyTextures++;
					continue;
				}
				if ( extension == ".pcf" )
				{
					RegisterParticleFile( entry.Path, new VpkAssetSource( archive, entry, vpkResolver ) );
					continue;
				}
				if ( extension is not (".bsp" or ".mdl" or ".rmdl" or ".wav") ) continue;
				if ( !registered.Add( entry.Path ) ) continue;

				var source = new VpkAssetSource( archive, entry, vpkResolver );
				switch ( extension )
				{
					case ".bsp":
						var mapName = Path.GetFileNameWithoutExtension( entry.Path );
						spawnEntitySources.TryGetValue( mapName, out var spawnEntitySource );
						environmentEntitySources.TryGetValue( mapName, out var environmentEntitySource );
						particleEntitySources.TryGetValue( mapName, out var particleEntitySource );
						scriptEntitySources.TryGetValue( mapName, out var scriptEntitySource );
						context.Add( ResourceType.Scene, entry.Path, new MapLoader(
							source,
							spawnEntitySource,
							environmentEntitySource,
							particleEntitySource,
							scriptEntitySource ) );
						break;
					case ".mdl":
					case ".rmdl":
						context.Add( ResourceType.Model, entry.Path, new ModelLoader( source ) );
						var staticInstancePath = ModelLoader.GetStaticInstancePath( entry.Path );
						if ( registered.Add( staticInstancePath ) )
							context.Add( ResourceType.Model, staticInstancePath, new ModelLoader( source, staticInstance: true ) );
						break;
					case ".wav":
						context.Add( ResourceType.Sound, entry.Path, new SoundLoader( source ) );
						break;
				}
			}
		}
		Log.Info( $"Titanfall 2 VPK mounted: {archives.Count} archives, {legacyMaterials} fallback VMT materials, "
			+ $"{legacyTextures} fallback VTF textures and {ParticleFileCount} PCF particle libraries "
			+ $"({particleEntitySources.Count} map FX and {scriptEntitySources.Count} script partitions; "
			+ "RPAK names retained at higher priority)." );
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

	void MountRpakResources( MountContext context, HashSet<string> registered )
	{
		var rpakDirectory = Path.Combine( _root, "r2", "paks", "Win64" );
		var packagePaths = System.IO.Directory.EnumerateFiles( rpakDirectory, "*.rpak", SearchOption.TopDirectoryOnly )
			.OrderByDescending( GetPatchRank )
			.ThenBy( static path => string.Equals( Path.GetFileName( path ), "common.rpak", StringComparison.OrdinalIgnoreCase ) ? 0 : 1 )
			.ThenBy( static path => path, StringComparer.OrdinalIgnoreCase )
			.ToArray();

		var archives = new List<RpakArchive>( packagePaths.Length );
		var failures = 0;
		foreach ( var path in packagePaths )
		{
			if ( RpakArchive.TryOpen( path, out var archive, out var error ) ) archives.Add( archive );
			else
			{
				failures++;
				Log.Warning( $"Unable to index Titanfall 2 RPAK '{Path.GetFileName( path )}': {error}" );
			}
		}

		_texturePaths.Clear();
		_shaderSetNames.Clear();
		_shaderSetSources.Clear();
		_shaderSources.Clear();
		_materialPaths.Clear();
		_materialSources.Clear();
		lock ( _materialMetadataLock ) _materialMetadata.Clear();
		lock ( _materialDescriptorLock )
		{
			_materialDescriptors.Clear();
			_textureGuidDependencies.Clear();
			_texturePathDependencies.Clear();
		}
		lock ( _shaderReflectionLock )
		{
			_shaderReflections.Clear();
			_shaderReflectionErrors.Clear();
		}
		foreach ( var archive in archives )
		{
			foreach ( var asset in archive.Assets )
			{
				if ( asset.Type == "txtr" ) _texturePaths.TryAdd( asset.Guid, asset.Name );
				else if ( asset.Type == "shds" )
				{
					_shaderSetNames.TryAdd( asset.Guid, asset.Name );
					_shaderSetSources.TryAdd( asset.Guid, new MountedAssetSource( archive, asset ) );
				}
				else if ( asset.Type == "shdr" ) _shaderSources.TryAdd( asset.Guid, new MountedAssetSource( archive, asset ) );
				else if ( asset.Type == "matl" && _materialPaths.TryAdd( asset.Name, asset.Name ) )
					_materialSources.TryAdd( asset.Name, new MountedAssetSource( archive, asset ) );
			}
		}

		var textures = 0;
		var materials = 0;
		foreach ( var archive in archives )
		{
			foreach ( var asset in archive.Assets )
			{
				if ( asset.Type == "txtr" && registered.Add( asset.Name + ".vtex" ) )
				{
					context.Add( ResourceType.Texture, asset.Name, new Titanfall2TextureLoader( new RpakTextureAssetSource( archive, asset ) ) );
					textures++;
				}
				else if ( asset.Type == "matl" && registered.Add( asset.Name + ".vmat" ) )
				{
					context.Add( ResourceType.Material, asset.Name, new MaterialLoader( archive, asset ) );
					materials++;
				}
			}
		}

		var cache = RpakArchive.GetCacheStatistics();
		Log.Info( $"Titanfall 2 RPAK mounted: {archives.Count}/{packagePaths.Length} packages, {textures} textures, {materials} materials, "
			+ $"{_shaderSetNames.Count} shader sets, {_shaderSources.Count} shaders, {failures} failures; "
			+ $"index cache {cache.IndexHits} hits/{cache.IndexMisses} rebuilds, decompressed-buffer budget "
			+ $"{cache.StrongBufferLimit / (1024 * 1024)} MB (patch packages contribute self-contained changed pages)." );
	}

	static int GetPatchRank( string path )
	{
		var match = System.Text.RegularExpressions.Regex.Match( Path.GetFileNameWithoutExtension( path ), @"\((\d\d)\)$" );
		return match.Success && int.TryParse( match.Groups[1].Value, out var rank ) ? rank : 0;
	}

	internal bool TryGetTexturePath( ulong guid, out string path ) => _texturePaths.TryGetValue( guid, out path );
	internal bool TryGetShaderSetName( ulong guid, out string name ) => _shaderSetNames.TryGetValue( guid, out name );
	internal bool TryGetMaterialPath( string name, out string path ) => _materialPaths.TryGetValue( name, out path );
	internal bool TryGetLegacyAnimatedTexturePath( string name, out string path ) =>
		_legacyAnimatedTexturePaths.TryGetValue( Titanfall2LegacyPath.NormalizeTextureName( name ), out path );

	internal void RegisterMaterialDescriptor( Titanfall2MaterialDescriptor descriptor )
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
				references = found;
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
				bindings.Count( binding => binding.Semantic == Titanfall2TextureSemantic.Unknown ) );
		}
	}

	internal bool TryGetLegacyParticleSprite( string textureName, float frameRate, out Sprite sprite, out string error )
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
		var spriteKey = $"{normalized}|{MathF.Round( frameRate * 100f )}";
		lock ( _legacyParticleSpriteLock )
		{
			if ( _legacyParticleSprites.TryGetValue( spriteKey, out sprite ) ) return true;
			if ( _legacyTextureSources.TryGetValue( normalized, out var source ) )
			{
				if ( source.TryReadAllBytes( out var bytes, out var readError ) )
				{
					if ( !_legacyParticleTextureFrames.TryGetValue( normalized, out var frames ) )
					{
						if ( VtfTextureDecoder.GetFrameCount( bytes ) > 1 )
						{
							if ( !VtfTextureDecoder.TryCreateFrames( bytes, out frames, out error ) ) return false;
						}
						else if ( VtfTextureDecoder.TryCreateSheetFrames( bytes, out frames, out var sheetFrameRate, out _ ) )
							_legacyParticleSheetFrameRates[normalized] = sheetFrameRate;
						if ( frames is not null && frames.Length > 1 ) _legacyParticleTextureFrames.Add( normalized, frames );
					}
					if ( frames is not null && frames.Length > 1 )
					{
						var effectiveRate = _legacyParticleSheetFrameRates.TryGetValue( normalized, out var sheetRate )
							? Math.Clamp( sheetRate * frameRate, 0.1f, 120f )
							: frameRate;
						sprite = Sprite.FromTextures( frames, effectiveRate );
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

	async Task RunSelfTest()
	{
		await Task.Yield();
		try
		{
			var regressionPaths = new[]
			{
				$"mount://{Ident}/maps/mp_angel_city.bsp.scene",
				$"mount://{Ident}/maps/mp_homestead.bsp.scene",
				$"mount://{Ident}/maps/mp_wargames.bsp.scene",
				$"mount://{Ident}/models/robots/super_spectre/super_spectre_v1.mdl.vmdl",
				$"mount://{Ident}/models/humans/grunts/imc_grunt_rifle.mdl.vmdl",
				$"mount://{Ident}/models/titans/heavy/titan_heavy_legion_prime.mdl.vmdl",
				$"mount://{Ident}/models/vehicle/crow_dropship/crow_dropship.mdl.vmdl",
				$"mount://{Ident}/models/levels_terrain/mp_angel_city/ac_hardpointb_decals.mdl.vmdl"
			};
			var missingRegressionResources = regressionPaths.Where( path => GetByPath( path ) is null ).ToArray();

			while ( !IsParticleCatalogComplete )
			{
				IndexParticleFiles( 32, 20f );
				await Task.Yield();
			}
			var particleSamples = new[]
			{
				"env_sun_fracture",
				"env_ground_fog_far",
				"glow_imc_light04",
				"P_steam_leak_LG",
				"P_wargames_streaks_400_blue"
			};
			var missingParticleSamples = particleSamples.Where( name => !TryGetParticleDefinition( name, out _ ) ).ToArray();
			var modelPath = $"mount://{Ident}/models/backwater/backwater_industrial_ground_marker.mdl.vmdl";
			var texturePath = $"mount://{Ident}/decals/blood1_col.vtex";
			var materialPath = $"mount://{Ident}/decals/blood1.vmat";
			var soundPath = $"mount://{Ident}/sound/12_gauge_shotgun_shell_drop_01.vsnd";
			var scenePath = $"mount://{Ident}/maps/mp_angel_city.bsp.scene";
			var model = Model.Load( modelPath );
			var texture = Texture.Load( texturePath, false );
			var material = Material.Load( materialPath );
			var dependencyStats = GetMaterialDependencyStatistics();
			var textureStats = RpakTextureDecoder.GetStatistics();
			var rpakStats = RpakArchive.GetCacheStatistics();
			var starpakStats = StarPakIndex.GetStatistics();
			var sound = SoundFile.Load( soundPath );
			var soundDirectory = System.IO.Path.Combine( _root, "r2", "sound" );
			var audioLayout = File.Exists( System.IO.Path.Combine( soundDirectory, "general.mbnk" ) )
				&& System.IO.Directory.EnumerateFiles( soundDirectory, "*.mstr", SearchOption.TopDirectoryOnly ).Any();
			var scene = SceneFile.Load( scenePath );
			Log.Info( $"Titanfall 2 mount self-test: model={model is not null && model != Model.Error}, texture={texture is not null && !texture.IsError}, material={material is not null}, sound={sound is not null || Application.IsHeadless}, audioLayoutMbnkMstr={audioLayout}, scene={scene is not null}." );
			Log.Info( $"Titanfall 2 material descriptors: {dependencyStats.Materials} materials, {dependencyStats.References} bindings, "
				+ $"{dependencyStats.Reflected} reflected, {dependencyStats.Unknown} unknown, "
				+ $"{dependencyStats.TextureGuids} GUID and {dependencyStats.TexturePaths} path dependency keys." );
			Log.Info( $"Titanfall 2 streaming statistics: textures={textureStats.Created} low-resident/"
				+ $"{textureStats.Failed} failed, {textureStats.UploadedBytes / (1024.0 * 1024.0):0.0} MB uploaded at max {textureStats.MaximumDimension}; "
				+ $"RPAK={rpakStats.StrongBuffers} buffers, {rpakStats.StrongBufferBytes / (1024.0 * 1024.0):0.0}/"
				+ $"{rpakStats.StrongBufferLimit / (1024.0 * 1024.0):0} MB, hits={rpakStats.StrongHits}/{rpakStats.WeakHits}, "
				+ $"misses={rpakStats.StrongMisses}, evictions={rpakStats.Evictions}; STARPAK={starpakStats.RangeReads} reads, "
				+ $"{starpakStats.BytesRead / (1024.0 * 1024.0):0.0} MB, {starpakStats.Failures} failures." );
			Log.Info( $"Titanfall 2 regression registration: {regressionPaths.Length - missingRegressionResources.Length}/{regressionPaths.Length} samples; "
				+ $"particles={particleSamples.Length - missingParticleSamples.Length}/{particleSamples.Length}, "
				+ $"catalog={ParticleDefinitionCount} definitions from {IndexedParticleFileCount}/{ParticleFileCount} PCFs."
				+ (missingRegressionResources.Length > 0 ? $" Missing resources: {string.Join( ", ", missingRegressionResources )}." : string.Empty)
				+ (missingParticleSamples.Length > 0 ? $" Missing FX: {string.Join( ", ", missingParticleSamples )}." : string.Empty) );
		}
		catch ( Exception exception )
		{
			Log.Error( exception, "Titanfall 2 mount self-test failed." );
		}
	}

	readonly record struct MountedAssetSource( RpakArchive Archive, RpakAsset Asset );
}
