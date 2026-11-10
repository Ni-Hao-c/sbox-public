using Titanfall2;

public partial class Titanfall2Mount
{
	Titanfall2ContentSnapshot _contentSnapshot;
	string _sessionContentRevision;

	/// <summary>Diagnostics are built only on explicit request, never on the frame path.</summary>
	public object GetContentStatistics() => new
	{
		Revision = _contentSnapshot?.Revision,
		SelectedMap = _contentSnapshot?.SelectedMap,
		Entries = _contentSnapshot?.Entries.Count ?? 0,
		Winners = _contentSnapshot?.Winners.Count ?? 0,
		Diagnostics = _contentSnapshot?.Diagnostics
	};

	public object ExplainContent( string path, string kind = "file" ) => _contentSnapshot?.Explain( path, kind )
		.Select( entry => new { entry.Path, entry.Kind, entry.SourceId, entry.SourceIdentity, NativeId = entry.Archive is null ? (ulong?)null : entry.Asset.Guid } ).ToArray();

	/// <summary>Paged virtual directory, including dependencies without engine loaders.</summary>
	public object GetContentFiles( string prefix = "", int offset = 0, int count = 100 ) => _contentSnapshot?.Winners
		.Where( entry => entry.Path.StartsWith( prefix ?? "", StringComparison.OrdinalIgnoreCase ) )
		.Skip( Math.Max( 0, offset ) ).Take( Math.Clamp( count, 1, 1000 ) )
		.Select( entry => new { entry.Path, entry.Kind, entry.SourceId, NativeId = entry.Archive is null ? (ulong?)null : entry.Asset.Guid } ).ToArray();

	internal sealed record ContentRegistration( ResourceType Type, string Path, ResourceLoader Loader );

	void MountContentResources( MountContext context, HashSet<string> registered, Titanfall2ContentSnapshot snapshot )
	{
		var catalog = PrepareContentCatalog( registered, snapshot );
		foreach ( var asset in catalog ) context.Add( asset.Type, asset.Path, asset.Loader );
		Log.Info( $"Titanfall 2 catalog: {catalog.Count} loaders, {ParticleFileCount} particle libraries; all raw reads use one immutable snapshot." );
	}

	internal IReadOnlyList<ContentRegistration> PrepareContentCatalog( HashSet<string> registered, Titanfall2ContentSnapshot snapshot )
	{
		var catalog = new List<ContentRegistration>();
		// GUID dependencies need all native winners, including two GUIDs with one name.
		foreach ( var entry in snapshot.Entries.Where( entry => entry.Archive is not null ) )
		{
			var asset = entry.Asset;
			if ( asset.Type == "txtr" ) _texturePaths.TryAdd( asset.Guid, entry.Path );
			else if ( asset.Type == "shds" )
			{
				_shaderSetNames.TryAdd( asset.Guid, asset.Name );
				_shaderSetSources.TryAdd( asset.Guid, new MountedAssetSource( entry.Archive, asset ) );
			}
			else if ( asset.Type == "shdr" ) _shaderSources.TryAdd( asset.Guid, new MountedAssetSource( entry.Archive, asset ) );
		}
		var spawnEntitySources = new Dictionary<string, ITitanfall2AssetSource>( StringComparer.OrdinalIgnoreCase );
		var environmentEntitySources = new Dictionary<string, ITitanfall2AssetSource>( StringComparer.OrdinalIgnoreCase );
		var particleEntitySources = new Dictionary<string, ITitanfall2AssetSource>( StringComparer.OrdinalIgnoreCase );
		var scriptEntitySources = new Dictionary<string, ITitanfall2AssetSource>( StringComparer.OrdinalIgnoreCase );
		var soundEntitySources = new Dictionary<string, ITitanfall2AssetSource>( StringComparer.OrdinalIgnoreCase );
		foreach ( var entry in snapshot.Winners.Where( entry => entry.Kind == "file" && !IsDepotVpkPath( entry.Path ) ) )
		{
			if ( TryGetEntityMapName( entry.Path, "_spawn", out var spawnMapName ) ) spawnEntitySources.TryAdd( spawnMapName, snapshot.GetSource( entry.Path ) );
			else if ( TryGetEntityMapName( entry.Path, "_env", out var environmentMapName ) ) environmentEntitySources.TryAdd( environmentMapName, snapshot.GetSource( entry.Path ) );
			else if ( TryGetEntityMapName( entry.Path, "_fx", out var particleMapName ) ) particleEntitySources.TryAdd( particleMapName, snapshot.GetSource( entry.Path ) );
			else if ( TryGetEntityMapName( entry.Path, "_script", out var scriptMapName ) ) scriptEntitySources.TryAdd( scriptMapName, snapshot.GetSource( entry.Path ) );
			else if ( TryGetEntityMapName( entry.Path, "_snd", out var soundMapName ) ) soundEntitySources.TryAdd( soundMapName, snapshot.GetSource( entry.Path ) );
		}
		foreach ( var entry in snapshot.Winners )
		{
			if ( entry.Archive is not null )
			{
				if ( entry.Kind == "txtr" && registered.Add( entry.Path + ".vtex" ) )
					catalog.Add( new( ResourceType.Texture, entry.Path, new Titanfall2TextureLoader( new RpakTextureAssetSource( entry.Archive, entry.Asset ) ) ) );
				else if ( entry.Kind == "matl" && registered.Add( entry.Path + ".vmat" ) )
				{
					_materialPaths.TryAdd( entry.Path, entry.Path );
					_materialSources.TryAdd( entry.Path, new MountedAssetSource( entry.Archive, entry.Asset ) );
					catalog.Add( new( ResourceType.Material, entry.Path, new MaterialLoader( entry.Archive, entry.Asset ) ) );
				}
				continue;
			}
			if ( entry.Kind != "file" ) continue;
			if ( IsDepotVpkPath( entry.Path ) ) continue;
			// The soundscape script is the only definition of a trigger_soundscape's loop.
			if ( _soundscapeScriptSource is null
				&& entry.Path.Equals( "scripts/audio/audio_soundscapes.txt", StringComparison.OrdinalIgnoreCase ) )
			{
				_soundscapeScriptSource = snapshot.GetSource( entry.Path );
			}

			var extension = Path.GetExtension( entry.Path ).ToLowerInvariant();
			if ( extension == ".vmt" && Titanfall2LegacyPath.TryGetMaterialName( entry.Path, out var materialName ) )
			{
				var resourceKey = materialName + ".vmat";
				var legacyMaterialSource = snapshot.GetSource( entry.Path );
				_legacyMaterialSources.TryAdd( materialName, legacyMaterialSource );
				if ( registered.Add( resourceKey ) )
				{
					_materialPaths.TryAdd( materialName, materialName );
					catalog.Add( new( ResourceType.Material, materialName, new LegacyMaterialLoader( materialName ) ) );
				}
				continue;
			}
			if ( extension == ".vtf" && Titanfall2LegacyPath.TryGetTextureName( entry.Path, out var textureName ) )
			{
				var resourceKey = textureName + ".vtex";
				var legacyTextureSource = snapshot.GetSource( entry.Path );
				_legacyTextureSources.TryAdd( textureName, legacyTextureSource );
				if ( registered.Add( resourceKey ) )
					catalog.Add( new( ResourceType.Texture, textureName, new Titanfall2TextureLoader( legacyTextureSource ) ) );
				var animatedName = Titanfall2LegacyPath.GetAnimatedTextureName( textureName );
				if ( registered.Add( animatedName + ".vtex" ) )
				{
					catalog.Add( new( ResourceType.Texture, animatedName, new Titanfall2TextureLoader( legacyTextureSource, animatedVtfAtlas: true ) ) );
					_legacyAnimatedTexturePaths.TryAdd( textureName, animatedName );
				}
				continue;
			}
			// Colour-correction tables are mounted as volume textures, keyed on their own
			// content path, so a map scene references one instead of embedding its samples.
			if ( extension == ".raw" && entry.Path.StartsWith( "materials/correction/", StringComparison.OrdinalIgnoreCase ) )
			{
				if ( registered.Add( entry.Path + ".vtex" ) )
					catalog.Add( new( ResourceType.Texture, entry.Path, new Titanfall2ColorLookupLoader( snapshot.GetSource( entry.Path ) ) ) );
				continue;
			}
			if ( extension == ".pcf" )
			{
				RegisterParticleFile( entry.Path, snapshot.GetSource( entry.Path ) );
				continue;
			}
			if ( extension is not (".bsp" or ".mdl" or ".rmdl" or ".wav") ) continue;
			if ( !registered.Add( entry.Path ) ) continue;

			var source = snapshot.GetSource( entry.Path );
			switch ( extension )
			{
				case ".bsp":
					var mapName = Path.GetFileNameWithoutExtension( entry.Path );
					if ( snapshot.SelectedMap is not null && !mapName.Equals( snapshot.SelectedMap, StringComparison.OrdinalIgnoreCase ) ) break;
					spawnEntitySources.TryGetValue( mapName, out var spawnEntitySource );
					environmentEntitySources.TryGetValue( mapName, out var environmentEntitySource );
					particleEntitySources.TryGetValue( mapName, out var particleEntitySource );
					scriptEntitySources.TryGetValue( mapName, out var scriptEntitySource );
					soundEntitySources.TryGetValue( mapName, out var soundEntitySource );
					catalog.Add( new( ResourceType.Scene, entry.Path, new MapLoader(
						source,
						spawnEntitySource,
						environmentEntitySource,
						particleEntitySource,
						scriptEntitySource,
						soundEntitySource ) ) );
					// Additive path contract: the legacy preview scene keeps its cameras
					// and spawns; games can request only mounted world assets.
					catalog.Add( new( ResourceType.Scene, entry.Path + ".world", new MapLoader(
						source, spawnEntitySource, environmentEntitySource, particleEntitySource,
						scriptEntitySource, soundEntitySource, pureWorld: true ) ) );
					try
					{
						var brushCount = Titanfall2BrushModelSource.ReadCount( source );
						if ( brushCount > 0 )
						{
							var brushSource = new Titanfall2BrushModelSource( source, environmentEntitySource );
							_brushModelSources.Add( brushSource );
							for ( var index = 1; index <= brushCount; index++ )
								catalog.Add( new( ResourceType.Model, $"{entry.Path}/brush/{index}", new Titanfall2BrushModelLoader( brushSource, index ) ) );
						}
					}
					catch ( Exception exception )
					{
						Log.Warning( $"Cannot register BSP inline models for {entry.Path}: {exception.Message}" );
					}
					break;
				case ".mdl":
				case ".rmdl":
					_modelSources.TryAdd( NormalizeModelSourcePath( entry.Path ), source );
					catalog.Add( new( ResourceType.Model, entry.Path, new ModelLoader( source ) ) );
					// The static-instance resource is one .t2static model that carries every LOD.
					var allLodsPath = ModelLoader.GetStaticInstanceAllLodsPath( entry.Path );
					if ( registered.Add( allLodsPath ) )
						catalog.Add( new( ResourceType.Model, allLodsPath, new ModelLoader( source, staticInstance: true ) ) );
					break;
				case ".wav":
					// First registration wins, like every other source table. Retail ships no
					// loose .wav entries at all (every sound lives in the Miles bank), so this
					// can only matter for repacks and for a mod layer registering ahead of us.
					_soundFileSources.TryAdd( NormalizeSoundKey( entry.Path ) + ".wav", source );
					catalog.Add( new( ResourceType.Sound, entry.Path, new SoundLoader( source ) ) );
					break;
			}
		}
		return catalog.AsReadOnly();
	}
}
