using System.Text.Json;
using Sandbox;

/// <summary>On-demand inspection of actual physics hits; never changes collision rules.</summary>
public static class Titanfall2CollisionDiagnostics
{
	static readonly Sandbox.Diagnostics.Logger DiagnosticLog = new( "Titanfall2Collision" );
	static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, IncludeFields = true };
	static bool _consoleRegistered;

	// Mount assemblies are not enrolled by PackageLoader, so [ConCmd] alone is
	// insufficient. Keep this bridge in the trusted mount, like TypeLibrary
	// enrollment, rather than exposing engine internals to game assemblies.
	internal static void RegisterConsoleCommands()
	{
		if ( _consoleRegistered ) return;
		UpdateConsoleRegistration( "AddAssembly", [typeof( Titanfall2CollisionDiagnostics ).Assembly, "mount.titanfall2", null] );
		_consoleRegistered = true;
	}

	internal static void UnregisterConsoleCommands()
	{
		if ( !_consoleRegistered ) return;
		UpdateConsoleRegistration( "RemoveAssembly", [typeof( Titanfall2CollisionDiagnostics ).Assembly] );
		_consoleRegistered = false;
	}

	static void UpdateConsoleRegistration( string method, object[] arguments )
	{
		var registry = typeof( ConsoleSystem ).Assembly.GetType( "Sandbox.ConVarSystem", throwOnError: true );
		var entry = registry.GetMethod( method, System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic );
		if ( entry is null ) throw new MissingMethodException( registry.FullName, method );
		entry.Invoke( null, arguments );
	}

	/// <summary>
	/// Inspect the object under the running scene camera. Radius zero is a ray;
	/// a positive radius sweeps a sphere. This is not a player movement simulation.
	/// </summary>
	[ConCmd( "titanfall2_collision_look", Help = "Report actual physics under the camera: distance (default 512), sphere radius (default 0). Does not change collision." )]
	public static void LookCommand( float distance = 512f, float radius = 0f )
	{
		try
		{
			DiagnosticLog.Info( ToJson( TraceCamera( Game.ActiveScene, distance, radius ) ) );
		}
		catch ( Exception exception ) when ( exception is ArgumentException or InvalidOperationException )
		{
			DiagnosticLog.Warning( exception.Message );
		}
	}

	/// <summary>Use the composed camera view and ignore its local player, when identifiable.</summary>
	public static object TraceCamera( Scene scene, float distance = 512f, float radius = 0f )
	{
		if ( !scene.IsValid() || !scene.Camera.IsValid() )
			throw new InvalidOperationException( "Collision diagnostic needs a running scene with an active camera. Enter Play mode first." );

		var camera = scene.Camera;
		var player = camera.GetComponentInParent<PlayerController>( true );
		if ( !player.IsValid() )
		{
			// A game's main camera can be separate from the player's hierarchy.
			// Do not guess between several local controllers or ignore all players.
			var candidates = scene.GetComponentsInChildren<PlayerController>()
				.Where( controller => controller.Active && !controller.IsProxy && controller.UseCameraControls ).Take( 2 ).ToArray();
			if ( candidates.Length == 1 ) player = candidates[0];
		}
		var ray = camera.View.FieldOfView > 0f ? camera.View.ForwardRay : camera.WorldTransform.ForwardRay;
		return Trace( scene, ray, distance, radius, player.IsValid() ? player.GameObject : camera.GameObject );
	}

	/// <summary>
	/// Trace live physics, excluding triggers and render-only meshes. The optional
	/// ignored hierarchy must belong to this scene. Raw Titanfall masks are reported,
	/// not applied as if they were Source 2 masks.
	/// </summary>
	public static object Trace( Scene scene, Ray ray, float distance = 512f, float radius = 0f, GameObject ignored = null )
	{
		if ( !scene.IsValid() ) throw new ArgumentException( "A valid scene is required.", nameof( scene ) );
		if ( !float.IsFinite( distance ) || distance <= 0f || distance > 65536f )
			throw new ArgumentOutOfRangeException( nameof( distance ), "Distance must be finite and in (0, 65536]." );
		if ( !float.IsFinite( radius ) || radius < 0f || radius > 128f )
			throw new ArgumentOutOfRangeException( nameof( radius ), "Sphere radius must be finite and in [0, 128]." );
		if ( !IsFinite( ray.Position ) || !IsFinite( ray.Forward ) || ray.Forward.LengthSquared < 1e-12f )
			throw new ArgumentException( "Trace origin and direction must be finite, with a nonzero direction.", nameof( ray ) );
		if ( ignored.IsValid() && ignored.Scene != scene )
			throw new ArgumentException( "The ignored hierarchy must belong to the traced scene.", nameof( ignored ) );

		var direction = ray.Forward.Normal;
		var requestedEnd = ray.Position + direction * distance;
		var trace = scene.Trace.Ray( ray.Position, requestedEnd ).Radius( radius ).UseHitPosition();
		if ( ignored.IsValid() ) trace = trace.IgnoreGameObjectHierarchy( ignored );
		var hit = trace.Run();
		var source = hit.Collider?.GameObject.Components.Get<Titanfall2CollisionSource>();
		var model = (hit.Collider as ModelCollider)?.Model;
		var data = model is { IsValid: true, IsError: false } ? model.GetData<Titanfall2ModelCollisionData>() : null;
		var hasIdentity = source?.HasInstanceMetadata == true;
		return new
		{
			Outcome = hit.StartedSolid ? "started-solid" : hit.Hit ? "hit" : "no-hit",
			Query = radius > 0f ? "physics-sphere" : "physics-ray",
			QueryRules = "s&box physics; triggers excluded; retail query layers not converted",
			Start = ray.Position,
			RequestedEnd = requestedEnd,
			MaxDistance = distance,
			SphereRadius = radius,
			TravelDistance = hit.EndPosition.Distance( ray.Position ),
			ContactPosition = hit.Hit ? (Vector3?)hit.HitPosition : null,
			IsMeshShape = hit.Shape?.IsMeshShape,
			IsHullShape = hit.Shape?.IsHullShape,
			SolidCollisionsEnabled = hit.Shape?.EnableSolidCollisions,
			ShapeTags = hit.Shape?.Tags?.ToArray(),
			PhysicsBounds = hit.Body?.GetBounds(),
			IgnoredObject = ignored.IsValid() ? ignored.Name : null,
			GeometrySource = data?.GeometrySource ?? source?.GeometrySource ?? (hit.Hit ? "unknown" : "no-hit"),
			SolidType = hasIdentity ? (byte?)source.SolidType : null,
			ReservedCollisionDataHex = hasIdentity ? $"0x{source.ReservedCollisionData:X8}" : null,
			CollisionFlagsRemoveHex = hasIdentity ? $"0x{source.CollisionFlagsRemove:X8}" : null,
			ModelContentsHex = data is null ? null : $"0x{data.ModelContents:X8}",
			EffectiveContentsHex = source?.HasEffectiveContents == true ? $"0x{source.EffectiveContents:X8}" : null,
			Details = Titanfall2CollisionSource.DescribeHit( hit )
		};
	}

	/// <summary>JSON that can be copied directly from the console or a trusted MCP script.</summary>
	public static string ToJson( object report ) => JsonSerializer.Serialize( report, JsonOptions );

	static bool IsFinite( Vector3 value ) => float.IsFinite( value.x ) && float.IsFinite( value.y ) && float.IsFinite( value.z );
}
