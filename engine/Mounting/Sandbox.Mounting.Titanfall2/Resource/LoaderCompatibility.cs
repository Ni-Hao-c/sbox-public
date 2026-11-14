// The source engine has cache-release helpers that are not in every Steam build.
// Keep that difference inside the mount, without replacing any engine assemblies.
abstract class Titanfall2ResourceLoader : ResourceLoader<Titanfall2Mount>
{
	protected bool ReleaseLoaderResult()
	{
#if SBOX_OFFICIAL_ENGINE
		return Titanfall2OfficialLoaderCache.ReleaseResult( this );
#else
		return ReleaseCachedResult();
#endif
	}
}

abstract class Titanfall2SceneLoader : SceneLoader<Titanfall2Mount>
{
	protected bool IsLoaderBusy
	{
		get
		{
#if SBOX_OFFICIAL_ENGINE
			return Titanfall2OfficialLoaderCache.IsLoading( this );
#else
			return IsLoading;
#endif
		}
	}

	protected bool ReleaseLoaderResult()
	{
#if SBOX_OFFICIAL_ENGINE
		return Titanfall2OfficialLoaderCache.ReleaseResult( this );
#else
		return ReleaseCachedResult();
#endif
	}

	protected bool ReleaseLoaderScene()
	{
#if SBOX_OFFICIAL_ENGINE
		return Titanfall2OfficialLoaderCache.ReleaseScene( this );
#else
		return ReleaseCachedScene();
#endif
	}
}

#if SBOX_OFFICIAL_ENGINE
/// <summary>
/// Mirrors the source engine's release operations using the Steam loader's existing
/// lock and cache. This runs only during scene teardown, never during rendering.
/// Validate private field types so a changed Steam layout fails explicitly rather
/// than disposing resources behind a still-cached scene.
/// </summary>
static class Titanfall2OfficialLoaderCache
{
	const System.Reflection.BindingFlags PrivateInstance = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
	static readonly System.Reflection.FieldInfo Cache = RequireField( typeof( ResourceLoader ), "_cachedResult", typeof( object ) );
	static readonly System.Reflection.FieldInfo Gate = RequireField( typeof( ResourceLoader ), "_lock", typeof( System.Threading.Lock ) );
	static readonly System.Reflection.FieldInfo Pending = RequireField( typeof( ResourceLoader ), "_loadTask", typeof( Task<object> ) );
	static readonly System.Reflection.FieldInfo Scene = RequireField( typeof( SceneLoader<Titanfall2Mount> ), "_scene", typeof( SceneFile ) );
	static readonly Action<GameResource> DestroyScene = (Action<GameResource>)(typeof( GameResource )
		.GetMethod( "DestroyInternal", PrivateInstance, Type.EmptyTypes )
		?? throw new MissingMethodException( typeof( GameResource ).FullName, "DestroyInternal" ))
		.CreateDelegate( typeof( Action<GameResource> ) );

	static System.Reflection.FieldInfo RequireField( Type owner, string name, Type expected )
	{
		var field = owner.GetField( name, PrivateInstance );
		if ( field?.FieldType != expected )
			throw new NotSupportedException( $"Unsupported s&box loader layout: {owner.FullName}.{name} must be {expected.FullName}. Rebuild the Titanfall 2 mount for this engine version." );
		return field;
	}

	internal static bool IsLoading( ResourceLoader loader )
	{
		var gate = (System.Threading.Lock)Gate.GetValue( loader );
		if ( !gate.TryEnter() ) return true;
		try
		{
			return Pending.GetValue( loader ) is Task { IsCompleted: false };
		}
		finally
		{
			gate.Exit();
		}
	}

	internal static bool ReleaseResult( ResourceLoader loader )
	{
		var gate = (System.Threading.Lock)Gate.GetValue( loader );
		if ( !gate.TryEnter() ) return false;
		try
		{
			if ( Pending.GetValue( loader ) is Task { IsCompleted: false } ) return false;
			var released = Cache.GetValue( loader ) is not null;
			Cache.SetValue( loader, null );
			return released;
		}
		finally
		{
			gate.Exit();
		}
	}

	// The owner has already checked that no load or live map scene uses this loader.
	internal static bool ReleaseScene( SceneLoader<Titanfall2Mount> loader )
	{
		if ( Scene.GetValue( loader ) is not SceneFile scene ) return false;
		DestroyScene( scene );
		Scene.SetValue( loader, null );
		return true;
	}
}
#endif
