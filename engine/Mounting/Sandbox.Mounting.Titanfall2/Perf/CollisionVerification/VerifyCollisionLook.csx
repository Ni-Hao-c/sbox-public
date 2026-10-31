// Trusted MCP method body. No scene save, map reload, cache refresh or player movement.
// A source-only probe may load CollisionDiagnostics.cs in a separate assembly;
// otherwise this uses the deployed mount's diagnostic class.
var flags = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
var diagnostics = (AppDomain.CurrentDomain.GetData( "Titanfall2CollisionDiagnosticsTestAssembly" ) as System.Reflection.Assembly)?.GetType( "Titanfall2CollisionDiagnostics" )
	?? typeof( Titanfall2Mount ).Assembly.GetType( "Titanfall2CollisionDiagnostics" )
	?? throw new Exception( "Deploy the collision diagnostic or load its isolated source probe first." );
var traceMethod = diagnostics.GetMethod( "Trace", flags );
object Trace( Scene scene, Ray ray, float distance = 512f, float radius = 0f, GameObject ignored = null ) =>
	traceMethod.Invoke( null, new object[] { scene, ray, distance, radius, ignored } );
T Field<T>( object record, string name ) => (T)record.GetType().GetProperty( name ).GetValue( record );
var rows = new System.Collections.Generic.List<object>();
void Check( string name, bool passed ) => rows.Add( new { name, passed } );
bool Rejects( Scene scene, Ray ray, float distance, float radius )
{
	try { Trace( scene, ray, distance, radius ); return false; }
	catch ( System.Reflection.TargetInvocationException e ) { return e.InnerException is ArgumentException; }
}
var originalScene = Game.ActiveScene;
var scene = new Scene();
object sample = null;
try
{
	using var scope = scene.Push();
	BoxCollider Box( string name, Vector3 position )
	{
		var go = new GameObject( scene, true, name );
		go.WorldPosition = position;
		go.IsStatic = true;
		var box = go.AddComponent<BoxCollider>();
		box.Scale = new Vector3( 16 );
		return box;
	}
	var near = Box( "actual-near-collider", new Vector3( 128, 0, 0 ) );
	var source = near.GameObject.AddComponent<Titanfall2CollisionSource>();
	source.MapPath = "synthetic-collision-look-fixture";
	source.ModelPath = "models/fixture/not-a-retail-claim.mdl";
	source.SourceIndex = 31415;
	source.HasInstanceMetadata = true;
	source.SolidType = 6;
	source.ReservedCollisionData = 0xFEDCBA98;
	source.CollisionFlagsRemove = 0xFFFFFFFF;
	source.GeometrySource = "synthetic-box-test";
	var far = Box( "far-collider", new Vector3( 256, 0, 0 ) );
	var trigger = Box( "near-trigger", new Vector3( 32, 0, 0 ) );
	trigger.IsTrigger = true;
	var decoy = new GameObject( scene, true, "render-only-decoy" );
	decoy.WorldPosition = new Vector3( 64, 0, 0 );
	decoy.AddComponent<ModelRenderer>().Model = Model.Cube;
	var decoySource = decoy.AddComponent<Titanfall2CollisionSource>();
	decoySource.SourceIndex = 999;
	decoySource.HasInstanceMetadata = true;
	var ray = new Ray( Vector3.Zero, Vector3.Forward );
	sample = Trace( scene, ray );
	var hit = Field<object>( sample, "Details" );
	var identity = Field<object>( hit, "Source" );
	Check( "nearest-physics-hit-skips-trigger-and-render-decoy", Field<string>( hit, "ObjectName" ) == near.GameObject.Name );
	Check( "identity-belongs-to-hit-not-nearby-renderer", Field<int>( identity, "SourceIndex" ) == 31415 );
	Check( "full-width-masks-readable-in-hex", Field<string>( sample, "CollisionFlagsRemoveHex" ) == "0xFFFFFFFF"
		&& Field<string>( sample, "ReservedCollisionDataHex" ) == "0xFEDCBA98" );
	Check( "physics-hit-carries-shape-and-bounds", Field<object>( hit, "ShapeType" ) is not null && sample.GetType().GetProperty( "PhysicsBounds" ).GetValue( sample ) is not null );
	Check( "distance-stops-before-obstacle", Field<string>( Trace( scene, ray, 100 ), "Outcome" ) == "no-hit" );
	var offsetRay = new Ray( new Vector3( 0, 18, 0 ), Vector3.Forward );
	Check( "thin-ray-misses-off-axis-obstacle", Field<string>( Trace( scene, offsetRay ), "Outcome" ) == "no-hit" );
	Check( "sphere-sweep-detects-off-axis-obstacle", Field<string>( Trace( scene, offsetRay, 512, 12 ), "Outcome" ) == "hit" );
	var ignored = Trace( scene, ray, 512, 0, near.GameObject );
	Check( "ignored-hierarchy-does-not-hide-next-obstacle", Field<string>( Field<object>( ignored, "Details" ), "ObjectName" ) == far.GameObject.Name );
	Check( "unannotated-collider-keeps-metadata-unknown", Field<string>( ignored, "CollisionFlagsRemoveHex" ) is null
		&& Field<string>( ignored, "ModelContentsHex" ) is null && Field<string>( ignored, "GeometrySource" ) == "unknown" );
	source.HasInstanceMetadata = false;
	var legacy = Trace( scene, ray );
	Check( "legacy-identity-not-reported-as-known-zero", legacy.GetType().GetProperty( "SolidType" ).GetValue( legacy ) is null
		&& Field<string>( legacy, "CollisionFlagsRemoveHex" ) is null );
	Check( "initial-overlap-is-explicit", Field<string>( Trace( scene, new Ray( near.WorldPosition, Vector3.Forward ) ), "Outcome" ) == "started-solid" );
	Check( "invalid-inputs-rejected", Rejects( scene, ray, float.NaN, 0 ) && Rejects( scene, ray, -1, 0 )
		&& Rejects( scene, ray, 512, -1 ) && Rejects( scene, ray, 512, float.PositiveInfinity )
		&& Rejects( scene, new Ray( Vector3.Zero, Vector3.Zero ), 512, 0 ) );
	var cameraObject = new GameObject( scene, true, "diagnostic-camera" );
	var camera = cameraObject.AddComponent<CameraComponent>();
	camera.IsMainCamera = true;
	camera.ComposeView();
	var fromCamera = diagnostics.GetMethod( "TraceCamera", flags ).Invoke( null, new object[] { scene, 512f, 0f } );
	Check( "camera-entry-hits-same-physics-object", Field<string>( Field<object>( fromCamera, "Details" ), "ObjectName" ) == near.GameObject.Name );
	var json = (string)diagnostics.GetMethod( "ToJson", flags ).Invoke( null, new object[] { sample } );
	using var parsed = System.Text.Json.JsonDocument.Parse( json );
	Check( "json-is-copyable-and-keeps-original-index", parsed.RootElement.GetProperty( "Details" ).GetProperty( "Source" ).GetProperty( "SourceIndex" ).GetInt32() == 31415 );
	var registry = typeof( ConsoleSystem ).Assembly.GetType( "Sandbox.ConVarSystem" );
	var members = (System.Collections.IDictionary)registry.GetField( "Members", flags ).GetValue( null );
	var alreadyRegistered = members.Contains( "titanfall2_collision_look" );
	try
	{
		if ( !alreadyRegistered ) diagnostics.GetMethod( "RegisterConsoleCommands", flags ).Invoke( null, null );
		Check( "console-command-is-registered", members.Contains( "titanfall2_collision_look" ) );
		ConsoleSystem.Run( "titanfall2_collision_look", 512f, 0f );
		Check( "registered-command-can-execute", true );
	}
	finally
	{
		if ( !alreadyRegistered ) diagnostics.GetMethod( "UnregisterConsoleCommands", flags ).Invoke( null, null );
	}
	Check( "temporary-command-registration-restored", members.Contains( "titanfall2_collision_look" ) == alreadyRegistered );
}
finally { scene.Destroy(); }
Check( "host-scene-restored", Game.ActiveScene == originalScene );
var output = System.IO.Path.Combine( exchangeDirectory, "collision-look-verification.json" );
System.IO.File.WriteAllText( output, System.Text.Json.JsonSerializer.Serialize( new { rows, sample }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true, IncludeFields = true } ) );
return new { output, diagnosticMvid = diagnostics.Module.ModuleVersionId, rows, sample };
