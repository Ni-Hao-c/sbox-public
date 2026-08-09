using System.Diagnostics;
using Titanfall2;

public partial class Titanfall2Mount
{
	readonly List<ParticleFileSource> _particleFiles = [];
	readonly Dictionary<string, Titanfall2ParticleDefinition> _particleDefinitions = new( StringComparer.OrdinalIgnoreCase );
	readonly object _particleCatalogLock = new();
	int _nextParticleFile;
	int _particleFileFailures;
	int _particleFailureLogs;

	internal int ParticleFileCount
	{
		get { lock ( _particleCatalogLock ) return _particleFiles.Count; }
	}

	internal int IndexedParticleFileCount
	{
		get { lock ( _particleCatalogLock ) return _nextParticleFile; }
	}

	internal int ParticleDefinitionCount
	{
		get { lock ( _particleCatalogLock ) return _particleDefinitions.Count; }
	}

	internal bool IsParticleCatalogComplete
	{
		get { lock ( _particleCatalogLock ) return _nextParticleFile >= _particleFiles.Count; }
	}

	internal void ResetParticleCatalog()
	{
		lock ( _particleCatalogLock )
		{
			_particleFiles.Clear();
			_particleDefinitions.Clear();
			_nextParticleFile = 0;
			_particleFileFailures = 0;
			_particleFailureLogs = 0;
		}
	}

	internal void RegisterParticleFile( string path, ITitanfall2AssetSource source )
	{
		if ( source is null || string.IsNullOrWhiteSpace( path ) ) return;
		lock ( _particleCatalogLock ) _particleFiles.Add( new ParticleFileSource( path, source ) );
	}

	internal bool TryGetParticleDefinition( string name, out Titanfall2ParticleDefinition definition )
	{
		definition = null;
		if ( string.IsNullOrWhiteSpace( name ) ) return false;
		lock ( _particleCatalogLock ) return _particleDefinitions.TryGetValue( name.Trim(), out definition );
	}

	/// <summary>
	/// Decompresses and parses a bounded number of PCFs. This is called by the
	/// runtime map streamer so opening a map never parses all particle libraries
	/// on the loading screen.
	/// </summary>
	internal ParticleCatalogProgress IndexParticleFiles( int maximumFiles, float maximumMilliseconds )
	{
		maximumFiles = Math.Max( 1, maximumFiles );
		maximumMilliseconds = MathF.Max( 0.1f, maximumMilliseconds );
		var timer = Stopwatch.StartNew();
		var processed = 0;
		var added = 0;
		while ( processed < maximumFiles && timer.Elapsed.TotalMilliseconds < maximumMilliseconds )
		{
			ParticleFileSource file;
			lock ( _particleCatalogLock )
			{
				if ( _nextParticleFile >= _particleFiles.Count ) break;
				file = _particleFiles[_nextParticleFile++];
			}
			processed++;

			if ( !file.Source.TryReadAllBytes( out var bytes, out var readError ) )
			{
				RecordParticleFailure( file.Path, readError );
				continue;
			}
			if ( !Titanfall2PcfReader.TryReadDefinitions( bytes, file.Path, out var definitions, out var parseError ) )
			{
				RecordParticleFailure( file.Path, parseError );
				continue;
			}

			lock ( _particleCatalogLock )
			{
				foreach ( var definition in definitions )
				{
					if ( _particleDefinitions.TryAdd( definition.Name, definition ) ) added++;
				}
			}
		}

		return new ParticleCatalogProgress(
			processed,
			added,
			IndexedParticleFileCount,
			ParticleFileCount,
			ParticleDefinitionCount,
			_particleFileFailures,
			IsParticleCatalogComplete );
	}

	void RecordParticleFailure( string path, string error )
	{
		lock ( _particleCatalogLock ) _particleFileFailures++;
		if ( _particleFailureLogs++ < 16 )
			Titanfall2Log.Warning( $"Unable to index Titanfall 2 particle library '{path}': {error}" );
	}

	readonly record struct ParticleFileSource( string Path, ITitanfall2AssetSource Source );
}

readonly record struct ParticleCatalogProgress(
	int ProcessedFiles,
	int AddedDefinitions,
	int IndexedFiles,
	int TotalFiles,
	int TotalDefinitions,
	int FailedFiles,
	bool IsComplete );
