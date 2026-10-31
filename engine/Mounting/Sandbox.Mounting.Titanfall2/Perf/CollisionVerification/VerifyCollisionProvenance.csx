// Booted editor/game required. Uses a real retail model and the production
// streamer collider-creation path. Instance identity/masks are synthetic controls.
await Sandbox.Mounting.Directory.Mount( "titanfall2" );
var mount = Sandbox.Mounting.Directory.Get( "titanfall2" );
var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
mount.GetType().GetMethod( "EnsureRuntimeTypesRegistered", flags ).Invoke( mount, null );
var modelPath = "models/rocks/rock_jagged_granite_large_01.mdl";
var model = Model.Load( "mount://titanfall2/" + modelPath + ".vmdl" );
if ( model is not { IsValid: true, IsError: false } ) throw new Exception( "Retail fixture model failed to load." );
var rows = new System.Collections.Generic.List<object>();
void Check( string name, bool passed ) => rows.Add( new { name, passed } );
var metadata = model.GetData<Titanfall2ModelCollisionData>();
Check( "model-stores-collision-provenance", metadata is not null && metadata.HullCount > 0 && metadata.GeometrySource != "none" );
var sources = (System.Collections.IDictionary)mount.GetType().GetField( "_modelSources", flags ).GetValue( mount );
var asset = sources[modelPath];
var read = new object[] { null, null };
if ( !(bool)asset.GetType().GetMethod( "TryReadAllBytes", flags ).Invoke( asset, read ) ) throw new Exception( "Unable to read fixture MDL." );
var original = Titanfall2.Formats.Titanfall2Mdl53Reader.Parse( (byte[])read[0] );
Check( "model-contents-keeps-original-bit-pattern", metadata?.ModelContents == unchecked((uint)original.Header.Contents) );
var scene = new Scene();
object report = null;
try
{
	using var scope = scene.Push();
	var go = new GameObject( scene, true, "Collision provenance fixture" );
	var streamer = go.AddComponent<Titanfall2StaticPropStreamer>();
	streamer.MountIdent = "titanfall2";
	streamer.MapPath = "synthetic-instance-with-retail-model";
	var staticFlags = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic;
	var props = new[] { new Titanfall2.Titanfall2BspReader.StaticPropInstance
	{
		SourceIndex = 12345, ModelPath = modelPath, SolidType = 6,
		ReservedCollisionData = 0xFEDCBA98, CollisionFlagsRemove = 0x80000000
	} };
	var encoded = (string)streamer.GetType().GetMethod( "Encode", staticFlags )
		.Invoke( null, new object[] { props, new Titanfall2.Titanfall2BspReader.BspVisibility() } );
	var prop = ((System.Collections.IEnumerable)streamer.GetType().GetMethod( "Decode", staticFlags ).Invoke( null, new object[] { encoded } )).Cast<object>().Single();
	var instanceType = streamer.GetType().GetNestedType( "PropInstance", System.Reflection.BindingFlags.NonPublic );
	var instance = Activator.CreateInstance( instanceType, flags, null, new object[] { prop, null, model.Bounds.Size.Length, true, false, true }, null );
	streamer.GetType().GetMethod( "EnsureCollisionState", flags ).Invoke( streamer, new object[] { instance, Vector3.Zero, null } );
	var source = scene.GetAllComponents<Titanfall2CollisionSource>().Single();
	Check( "streamer-carries-instance-identity", source.HasInstanceMetadata && source.SourceIndex == 12345 && source.ModelPath == modelPath );
	Check( "streamer-carries-full-width-masks", source.ReservedCollisionData == 0xFEDCBA98 && source.CollisionFlagsRemove == 0x80000000 && source.SolidType == 6 );
	Check( "streamer-records-effective-retail-contents", source.HasEffectiveContents
		&& source.EffectiveContents == Titanfall2CollisionSource.StaticPropContents( metadata.ModelContents, 0x80000000 ) );
	var center = model.Bounds.Center;
	var distance = model.Bounds.Size.Length + 128;
	var hit = default( SceneTraceResult );
	foreach ( var axis in new[] { Vector3.Up, Vector3.Forward, Vector3.Left } )
	{
		hit = scene.Trace.Ray( center + axis * distance, center - axis * distance ).Run();
		if ( hit.Hit ) break;
	}
	Check( "real-physics-ray-hits-fixture", hit.Hit && hit.Collider?.GameObject == source.GameObject );
	report = Titanfall2CollisionSource.DescribeHit( hit );
	var reportedSource = report.GetType().GetProperty( "Source" ).GetValue( report );
	Check( "trace-resolves-exact-source-component", reportedSource is not null && (int)reportedSource.GetType().GetProperty( "SourceIndex" ).GetValue( reportedSource ) == 12345 );
	var reportedModel = (Titanfall2ModelCollisionData)report.GetType().GetProperty( "ModelData" ).GetValue( report );
	Check( "trace-resolves-model-data", reportedModel is not null && reportedModel.GeometrySource == metadata?.GeometrySource
		&& reportedModel.ModelContents == metadata?.ModelContents && reportedModel.HullCount == metadata?.HullCount );
	var excludedProps = new[] { new Titanfall2.Titanfall2BspReader.StaticPropInstance
	{
		SourceIndex = 12346, ModelPath = modelPath, SolidType = 6,
		Origin = new System.Numerics.Vector3( 2048, 0, 0 ), CollisionFlagsRemove = 0xFFFFFFFF
	} };
	var excludedEncoded = (string)streamer.GetType().GetMethod( "Encode", staticFlags )
		.Invoke( null, new object[] { excludedProps, new Titanfall2.Titanfall2BspReader.BspVisibility() } );
	var excludedProp = ((System.Collections.IEnumerable)streamer.GetType().GetMethod( "Decode", staticFlags ).Invoke( null, new object[] { excludedEncoded } )).Cast<object>().Single();
	var excludedInstance = Activator.CreateInstance( instanceType, flags, null, new object[] { excludedProp, null, model.Bounds.Size.Length, true, false, true }, null );
	var ensure = streamer.GetType().GetMethod( "EnsureCollisionState", flags );
	ensure.Invoke( streamer, new object[] { excludedInstance, Vector3.Zero, null } );
	Check( "zero-contents-instance-has-no-physics-object", (bool)instanceType.GetProperty( "CollisionFiltered" ).GetValue( excludedInstance )
		&& instanceType.GetProperty( "CollisionObject" ).GetValue( excludedInstance ) is null );
	var excludedCenter = center + new Vector3( 2048, 0, 0 );
	Check( "ray-does-not-hit-excluded-retail-model", !scene.Trace.Ray( excludedCenter + Vector3.Up * distance, excludedCenter - Vector3.Up * distance ).Run().Hit );
	Check( "zero-contents-filter-is-idempotent", !(bool)ensure.Invoke( streamer, new object[] { excludedInstance, Vector3.Zero, null } )
		&& (int)streamer.GetType().GetField( "_collisionFilteredProps", flags ).GetValue( streamer ) == 1 );
}
finally { scene.Destroy(); }
var output = System.IO.Path.Combine( exchangeDirectory, "collision-provenance.json" );
System.IO.File.WriteAllText( output, System.Text.Json.JsonSerializer.Serialize( new { rows, report }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true, IncludeFields = true } ) );
return new { output, rows, report };
