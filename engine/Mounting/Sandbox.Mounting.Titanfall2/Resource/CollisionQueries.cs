/// <summary>Contents-based traces against imported collision, with ordinary scene objects included.</summary>
public static class Titanfall2CollisionQueries
{
	public const string ClassifiedTag = "ttf2_contents";
	// PhysicsTrace restricts an "any tag" query to NumTagFields (8) tags; extra tags are
	// silently ignored. A 32-bit retail contents mask can set nine or more bits (Shot
	// does), so the bits are split into groups of eight and each group runs its own trace.
	const int TagsPerTrace = 8;
	static readonly string[] BitTags = Enumerable.Range( 0, 32 ).Select( bit => $"ttf2_content_{bit}" ).ToArray();
	// Traces run every frame, so a mask's tag groups are built once instead of allocating
	// filtered arrays per call. Retail uses a small fixed set of masks.
	static readonly System.Collections.Concurrent.ConcurrentDictionary<uint, string[][]> TagCache = new();

	/// <summary>Preserves arbitrary retail masks. Trigger-only collision is included for bullet/Titan clips and water.</summary>
	public static SceneTraceResult Run( SceneTrace trace, Titanfall2TraceMask mask, bool includeOtherObjects = true )
		=> Run( trace, (uint)mask, includeOtherObjects );

	public static SceneTraceResult Run( SceneTrace trace, uint mask, bool includeOtherObjects = true )
	{
		var chunks = TagsFor( mask );
		// No contents selected: nothing imported can match, and the caller explicitly
		// excluded ordinary scene objects. Returning the whole world here (the old
		// fallback) made an empty mask collide with every surface.
		if ( chunks.Length == 0 )
			return includeOtherObjects ? trace.WithoutTags( ClassifiedTag ).Run() : default;

		var imported = default( SceneTraceResult );
		var importedAny = false;
		foreach ( var tags in chunks )
		{
			var hit = trace.HitTriggers().WithTag( ClassifiedTag ).WithAnyTags( tags ).Run();
			if ( !hit.Hit ) continue;
			if ( importedAny && hit.Fraction >= imported.Fraction ) continue;
			imported = hit;
			importedAny = true;
		}
		if ( !includeOtherObjects ) return imported;
		var other = trace.WithoutTags( ClassifiedTag ).Run();
		return other.Hit && (!importedAny || other.Fraction < imported.Fraction) ? other : imported;
	}

	static string[][] TagsFor( uint mask ) => TagCache.GetOrAdd( mask, static value =>
	{
		var chunks = new List<string[]>( 4 );
		var tags = new List<string>( TagsPerTrace );
		for ( var bit = 0; bit < BitTags.Length; ++bit )
		{
			if ( (value & (1u << bit)) == 0 ) continue;
			tags.Add( BitTags[bit] );
			if ( tags.Count < TagsPerTrace ) continue;
			chunks.Add( tags.ToArray() );
			tags.Clear();
		}
		if ( tags.Count > 0 ) chunks.Add( tags.ToArray() );
		return chunks.ToArray();
	} );

	/// <summary>A grapple hits the first shot-solid surface; NOGRAPPLE rejects that surface rather than tracing through it.</summary>
	public static bool CanGrapple( SceneTraceResult hit )
	{
		if ( !hit.Hit ) return false;
		var source = hit.Collider?.Components.Get<Titanfall2CollisionSource>();
		return source is null || !source.HasEffectiveContents || (source.EffectiveContents & (uint)Titanfall2Contents.NoGrapple) == 0;
	}

	public const string PlayerClipTag = "playerclip";
	public const string TitanClipTag = "titanclip";
	public const string NoGrappleTag = "nograpple";
	public const string NoWallrunTag = "nowallrun";

	/// <summary>
	/// Named tags for game code alongside the raw ttf2_content_N bits. Clip tags mark movement
	/// clips that do not block shots: solid static props expand to every clip bit (0x00EB0280,
	/// including BulletClip) and must not read as clips. NOCLIMB is the only retail wall-movement
	/// contents bit, so nowallrun is derived from it; that mapping is inferred, not taken from
	/// the retail wallrun code.
	/// </summary>
	public static (string Tag, bool Applies)[] ReadableTags( uint contents )
	{
		var blocksShots = (contents & (uint)Titanfall2TraceMask.Shot) != 0;
		return
		[
			(PlayerClipTag, !blocksShots && (contents & (uint)Titanfall2Contents.PlayerClip) != 0),
			(TitanClipTag, !blocksShots && (contents & (uint)Titanfall2Contents.TitanClip) != 0),
			(NoGrappleTag, (contents & (uint)Titanfall2Contents.NoGrapple) != 0),
			(NoWallrunTag, (contents & (uint)Titanfall2Contents.NoClimb) != 0)
		];
	}

	internal static void Classify( GameObject gameObject, ModelCollider collider, uint contents )
	{
		gameObject.Tags.Add( ClassifiedTag );
		for ( var bit = 0; bit < 32; ++bit )
		{
			if ( (contents & (1u << bit)) != 0 ) gameObject.Tags.Add( BitTags[bit] );
			else gameObject.Tags.Remove( BitTags[bit] );
		}
		foreach ( var (tag, applies) in ReadableTags( contents ) )
		{
			if ( applies ) gameObject.Tags.Add( tag );
			else gameObject.Tags.Remove( tag );
		}
		// Ordinary s&box movement keeps the pilot-solid behavior. Other original
		// masks opt into these query shapes through Run, without creating phantom walls.
		collider.IsTrigger = (contents & (uint)Titanfall2TraceMask.PlayerSolid) == 0;
	}
}

/// <summary>All world contents groups must have native physics bodies before map readiness.</summary>
[Library]
public sealed class Titanfall2CollisionManifest : Component
{
	[Property, Hide] public ModelCollider[] Colliders { get; set; } = [];
	PhysicsBody[] _bodies = [];
	public bool IsReady()
	{
		if ( Colliders.Length == 0 || Colliders.Any( collider => !collider.IsValid() || !collider.Active || collider.Scene != Scene ) ) return false;
		if ( _bodies.Length == Colliders.Length && _bodies.All( body => body.IsValid() && body.ShapeCount > 0 && Colliders.Contains( body.Component ) ) ) return true;
		_bodies = Scene.PhysicsWorld.Bodies.Where( body => body.ShapeCount > 0 && Colliders.Contains( body.Component ) ).ToArray();
		return Colliders.All( collider => _bodies.Any( body => body.Component == collider ) );
	}
}
