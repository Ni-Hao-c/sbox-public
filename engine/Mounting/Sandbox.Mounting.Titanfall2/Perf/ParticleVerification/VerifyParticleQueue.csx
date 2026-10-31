// A full PCF catalog must not discard valid systems that are still queued
// behind the per-frame spawn budget. One deliberately missing name is expected.
var mount = Sandbox.Mounting.Directory.Get( "titanfall2" );
var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
mount.GetType().GetMethod( "EnsureRuntimeTypesRegistered", flags ).Invoke( mount, null );
mount.GetType().GetMethod( "IndexParticleFiles", flags ).Invoke( mount, new object[] { 512, 5000f } );
string Fixture()
{
	using var stream = new System.IO.MemoryStream();
	using ( var writer = new System.IO.BinaryWriter( stream, System.Text.Encoding.UTF8, true ) )
	{
		writer.Write( 0x58465054 ); writer.Write( 3 ); writer.Write( 7 );
		for ( var index = 0; index < 7; ++index )
		{
			writer.Write( index < 6 ? "impact_ricochet_sparks" : "__codex_missing_particle_definition__" );
			writer.Write( "queue-case-" + index );
			foreach ( var value in new float[] { 150, index * 10, 0, 0, 0, 0, 1 } ) writer.Write( value );
			writer.Write( 0 );
		}
	}
	return Convert.ToBase64String( stream.ToArray() );
}
var scene = new Scene();
var rows = new System.Collections.Generic.List<object>();
try
{
	var component = new GameObject( scene, true, "Particle queue verification" ).AddComponent<Titanfall2ParticleStreamer>();
	component.MountIdent = "titanfall2";
	component.MapPath = "particle-queue-verification";
	component.FallbackAnchor = Vector3.Zero;
	component.EncodedEntities = Fixture();
	var type = component.GetType();
	var update = type.GetMethod( "OnUpdate", flags );
	int Count( string name ) => ((System.Collections.ICollection)type.GetField( name, flags ).GetValue( component )).Count;
	int Failed() => (int)type.GetField( "_failed", flags ).GetValue( component );
	update.Invoke( component, null );
	var firstPending = Count( "_pending" ); var firstSpawned = Count( "_spawned" );
	rows.Add( new { check = "valid-systems-remain-queued", pending = firstPending, spawned = firstSpawned, failed = Failed(),
		passed = firstPending > 0 && firstPending + firstSpawned == 6 && firstSpawned <= 4 && Failed() == 1 } );
	for ( var frame = 0; frame < 32 && Count( "_pending" ) > 0; ++frame ) update.Invoke( component, null );
	rows.Add( new { check = "queued-systems-eventually-spawn", pending = Count( "_pending" ), spawned = Count( "_spawned" ), failed = Failed(),
		passed = Count( "_pending" ) == 0 && Count( "_spawned" ) == 6 && Failed() == 1 } );
}
finally { scene.Destroy(); }
var output = System.IO.Path.Combine( exchangeDirectory, "particle-queue-results.json" );
System.IO.File.WriteAllText( output, System.Text.Json.JsonSerializer.Serialize( rows, new System.Text.Json.JsonSerializerOptions { WriteIndented = true } ) );
return new { output, rows };
