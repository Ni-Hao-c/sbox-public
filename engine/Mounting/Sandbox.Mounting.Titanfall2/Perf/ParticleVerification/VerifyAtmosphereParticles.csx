// Source dust must occupy a shallow layer around the emitter offset, including
// translated/rotated collections. A full sphere passes none of these bounds.
await Sandbox.Mounting.Directory.Mount( "titanfall2" );
var mount = Sandbox.Mounting.Directory.Get( "titanfall2" );
var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
mount.GetType().GetMethod( "IndexParticleFiles", flags ).Invoke( mount, new object[] { 512, 5000f } );
var arguments = new object[] { "env_ground_dust_128_wind", null };
if ( !(bool)mount.GetType().GetMethod( "TryGetParticleDefinition", flags ).Invoke( mount, arguments ) ) throw new Exception( "Missing retail dust PCF." );
var definition = arguments[1];
var factory = mount.GetType().Assembly.GetType( "Titanfall2ParticleFactory" ).GetMethod( "TryCreate", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic );
var rows = new System.Collections.Generic.List<object>();
var scene = new Scene();
try
{
	foreach ( var rotation in new[] { Rotation.Identity, new Angles( 25, 70, 10 ).ToRotation() } )
	{
		var root = new GameObject( scene, true, "dust distribution" );
		root.WorldPosition = new Vector3( 1500, -900, 500 ); root.WorldRotation = rotation;
		var create = new object[] { root, mount, definition, 0, null };
		if ( !(bool)factory.Invoke( null, create ) ) throw new Exception( create[4]?.ToString() );
		var effect = root.GetComponentsInChildren<ParticleEffect>().First();
		var emitter = effect.GameObject.GetComponentsInChildren<ParticleSphereEmitter>().First();
		for ( var index = 0; index < 100; ++index ) emitter.Emit( effect );
		var local = effect.Particles.Select( particle => root.WorldTransform.PointToLocal( particle.Position ) ).ToArray();
		var minZ = local.Min( point => point.z ); var maxZ = local.Max( point => point.z );
		var extentXY = local.Max( point => MathF.Max( MathF.Abs( point.x ), MathF.Abs( point.y ) ) );
		rows.Add( new { check = "retail-flat-dust-distribution", rotation = rotation.Angles().ToString(), count = local.Length, minZ, maxZ, extentXY,
			passed = local.Length == 100 && minZ >= 9.99f && maxZ <= 50.01f && extentXY <= 200.01f && extentXY > 50f } );
		root.Destroy();
	}
}
finally { scene.Destroy(); }
var output = System.IO.Path.Combine( exchangeDirectory, "atmosphere-particles-results.json" );
System.IO.File.WriteAllText( output, System.Text.Json.JsonSerializer.Serialize( rows, new System.Text.Json.JsonSerializerOptions { WriteIndented = true } ) );
return new { output, rows };
