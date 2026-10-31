// New control-point data must survive scene serialization, while old maps still load.
var assembly = Sandbox.Mounting.Directory.Get( "titanfall2" ).GetType().Assembly;
var flags = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic;
var properties = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
var streamer = assembly.GetType( "Titanfall2ParticleStreamer" );
var decode = streamer.GetMethod( "Decode", flags );
var encode = streamer.GetMethod( "Encode", flags );
object[] Decode( string text ) => ((System.Collections.IEnumerable)decode.Invoke( null, new object[] { text } )).Cast<object>().ToArray();
object Read( object value, string name ) => value.GetType().GetProperty( name, properties ).GetValue( value );
var pointType = assembly.GetType( "Titanfall2ParticleMapControlPoint" );
var entityType = assembly.GetType( "Titanfall2ParticleMapEntity" );
var points = Array.CreateInstance( pointType, 1 );
points.SetValue( Activator.CreateInstance( pointType, new object[] { 2, new Vector3( 300, 400, 500 ) } ), 0 );
var entities = Array.CreateInstance( entityType, 1 );
entities.SetValue( Activator.CreateInstance( entityType, new object[] { "tw_harv_beam_rope_solid", "map-beam", new Vector3( 10, 20, 30 ), new Angles( 5, 10, 15 ), 2f, points } ), 0 );
var encoded = (string)encode.Invoke( null, new object[] { entities } );
var decoded = Decode( encoded );
var restoredPoints = ((System.Collections.IEnumerable)Read( decoded[0], "ControlPoints" )).Cast<object>().ToArray();
var rows = new System.Collections.Generic.List<object>();
rows.Add( new { check = "control-point-scene-roundtrip", passed = decoded.Length == 1 && restoredPoints.Length == 1
	&& (Vector3)Read( restoredPoints[0], "Position" ) == new Vector3( 300, 400, 500 )
	&& (int)Read( restoredPoints[0], "Index" ) == 2 && (Vector3)Read( decoded[0], "Position" ) == new Vector3( 10, 20, 30 ) } );
string Fixture( int version, int pointCount )
{
	using var stream = new System.IO.MemoryStream();
	using ( var writer = new System.IO.BinaryWriter( stream, System.Text.Encoding.UTF8, true ) )
	{
		writer.Write( 0x58465054 ); writer.Write( version ); writer.Write( 1 );
		writer.Write( "old-map-effect" ); writer.Write( "old-map-entity" );
		foreach ( var value in new float[] { 10, 20, 30, 0, 90, 0, 1 } ) writer.Write( value );
		if ( version >= 3 ) writer.Write( pointCount );
	}
	return Convert.ToBase64String( stream.ToArray() );
}
var old = Decode( Fixture( 2, 0 ) );
rows.Add( new { check = "version-2-map-compatibility", passed = old.Length == 1
	&& (string)Read( old[0], "EffectName" ) == "old-map-effect"
	&& !((System.Collections.IEnumerable)Read( old[0], "ControlPoints" )).Cast<object>().Any() } );
var rejected = false;
try { _ = Decode( Fixture( 3, 65 ) ); }
catch ( System.IO.InvalidDataException ) { rejected = true; }
rows.Add( new { check = "invalid-control-point-count-rejected", passed = rejected } );
var output = System.IO.Path.Combine( exchangeDirectory, "particle-map-data-results.json" );
System.IO.File.WriteAllText( output, System.Text.Json.JsonSerializer.Serialize( rows, new System.Text.Json.JsonSerializerOptions { WriteIndented = true } ) );
return new { output, rows };
