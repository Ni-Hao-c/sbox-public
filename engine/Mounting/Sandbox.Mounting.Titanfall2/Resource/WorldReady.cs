/// <summary>Publishes readiness after world and resident prop collision enter the physics world.</summary>
[Library]
public sealed class Titanfall2WorldReady : Component, Sandbox.Internal.IUpdateSubscriber
{
	public const string ReadyTag = "ttf2_map_ready";
	[Property, Hide] public Titanfall2StaticPropStreamer Props { get; set; }
	[Property, Hide] public ModelCollider WorldCollision { get; set; }
	PhysicsBody _worldBody;
	Titanfall2StaticPropStreamer _verifiedProps;
	int _verifiedPropCount;
	RealTimeSince _sinceVerify = 10f;

	protected override Task OnLoad( LoadingContext context )
	{
		ClearReady();
		return Task.CompletedTask;
	}

	protected override void OnEnabled()
	{
		ClearReady();
		_sinceVerify = 10f;
	}

	protected override void OnUpdate()
	{
		var ready = Props.IsValid() && Props.Scene == Scene && Props.Active
			&& Props.KeepAllCollisions && Props.ResidentCollisionsReady
			&& WorldCollision.IsValid() && WorldCollision.Scene == Scene && WorldCollision.Active
			&& ModelLoader.IsUsableModel( WorldCollision.Model ) && WorldCollision.Model.Physics is { Parts.Count: > 0 };
		if ( !ready )
		{
			ClearReady();
			// A missing/disabled dependency does no native scan. Permit an immediate
			// check when it returns instead of inheriting the previous retry delay.
			_sinceVerify = 10f;
			return;
		}
		// Keep checking the verified native body after publication. A live component can
		// lose its shapes when its model is cleared, or acquire a different body on rebuild.
		// Retaining the body avoids scanning thousands of map bodies every frame.
		if ( GameObject.Tags.Has( ReadyTag ) && _worldBody.IsValid() && _worldBody.ShapeCount > 0
			&& _worldBody.Component == WorldCollision && ReferenceEquals( _verifiedProps, Props )
			&& _verifiedPropCount == Props.ResidentCollisionCount ) return;
		ClearReady();
		// The scans below walk every body in the physics world. While the counts disagree
		// they would otherwise run every frame, possibly for the whole session.
		if ( _sinceVerify < 0.25f ) return;
		_sinceVerify = 0;
		// Checked after the throttle: IsReady() also enumerates the physics world.
		var manifest = Components.Get<Titanfall2CollisionManifest>();
		if ( manifest.IsValid() && !manifest.IsReady() ) return;
		// Avoid obsolete/private Collider.KeyframeBody APIs. Verify the public
		// physics-world body, not just a serialized Model reference.
		PhysicsBody worldBody = null;
		var props = 0;
		foreach ( var body in Scene.PhysicsWorld.Bodies )
		{
			if ( body.ShapeCount <= 0 ) continue;
			if ( body.Component == WorldCollision ) worldBody = body;
			if ( body.Component is ModelCollider collider && collider.GameObject.Parent == Props.GameObject ) props++;
		}
		if ( worldBody.IsValid() && props == Props.ResidentCollisionCount )
		{
			_worldBody = worldBody;
			_verifiedProps = Props;
			_verifiedPropCount = props;
			GameObject.Tags.Add( ReadyTag );
		}
	}

	void ClearReady()
	{
		GameObject.Tags.Remove( ReadyTag );
		_worldBody = null;
		_verifiedProps = null;
		_verifiedPropCount = 0;
	}

	protected override void OnDisabled() => ClearReady();
}
