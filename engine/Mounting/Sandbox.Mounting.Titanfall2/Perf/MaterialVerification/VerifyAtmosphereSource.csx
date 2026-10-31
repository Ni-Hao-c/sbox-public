await Sandbox.Mounting.Directory.Mount( "titanfall2" );
var mount = Sandbox.Mounting.Directory.Get( "titanfall2" );
var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
object Get( object value, string name ) => value?.GetType().GetProperty( name, flags )?.GetValue( value );
var rows = new System.Collections.Generic.List<object>();
foreach ( var (name, scale, bias, exponent, outer, shader) in new[]
{
	("godray_500_fade", 1f / 470f, -30f / 470f, 2f, 0f, "godray"),
	("atmosphere_1500_fade", 1f / 3200f, -300f / 3200f, 1f, 0f, "godray"),
	("beacon_clouds", 1f / 3495f, -5f / 3495f, 1f, -0.5f, "translucent"),
	("beacon_se_f", 0f, 1f, 1f, -0.5f, "translucent"),
	("atmosphere_dark_1500_fade", 1f / 1470f, -30f / 1470f, 2f, 0f, "translucent")
} )
{
	var path = "world/atmosphere/" + name;
	var material = Material.Load( "mount://titanfall2/" + path + ".vmat" );
	var args = new object[] { path, null };
	mount.GetType().GetMethod( "TryGetMaterialDescriptor", flags ).Invoke( mount, args );
	var parameters = Get( args[1], "Parameters" );
	var auxiliaryUv = material.GetVector4( "g_vT2AuxUvChannels" );
	var atmosphereOpacity = material.GetVector4( "g_flT2AtmosphereOpacityScale" ).x;
	var atmosphereDepth = material.GetVector4( "g_flT2AtmosphereDepthFeather" ).x;
	var cloudBindings = name != "beacon_clouds" || (auxiliaryUv.x == 1f && auxiliaryUv.y == 0f
		&& MathF.Abs( atmosphereOpacity - 0.15f ) < 0.00001f && atmosphereDepth == 96f);
	bool Near( string key, float expected ) => Get( parameters, key ) is float actual && MathF.Abs( actual - expected ) < 0.00001f;
	rows.Add( new { name, material.ShaderName, parameters, auxiliaryUv = auxiliaryUv.ToString(), atmosphereOpacity, atmosphereDepth,
		passed = cloudBindings && material.GetFeature( "F_RENDER_BACKFACES" ) == 0 && material.ShaderName == "shaders/titanfall2_" + shader + ".shader"
		&& Near( "AlphaDistanceFadeScale", scale ) && Near( "AlphaDistanceFadeBias", bias )
		&& Near( "AlphaEdgeFadeExponent", exponent ) && Near( "AlphaEdgeFadeInner", 1f ) && Near( "AlphaEdgeFadeOuter", outer ) } );
}
mount.GetType().GetMethod( "IndexParticleFiles", flags ).Invoke( mount, new object[] { 512, 5000f } );
foreach ( var name in new[] { "env_ground_dust_128_wind", "env_dust_motes_256" } )
{
	var args = new object[] { name, null };
	mount.GetType().GetMethod( "TryGetParticleDefinition", flags ).Invoke( mount, args );
	var definition = args[1];
	float Number( string key ) => Get( definition, key ) is float value ? value : float.NaN;
	var scale = Get( definition, "RadiusScale" );
	var passed = name == "env_ground_dust_128_wind"
		? Number( "LifetimeMinimum" ) == 1.5f && Number( "LifetimeMaximum" ) == 6f
			&& Number( "RotationMinimum" ) == -10f && Number( "RotationMaximum" ) == 10f
			&& Get( scale, "StartScale" ) is float start && start == 0.5f && Get( scale, "EndScale" ) is float end && end == 1.2f
		: MathF.Abs( Number( "AlphaMinimum" ) - 75f / 255f ) < 0.00001f && MathF.Abs( Number( "AlphaMaximum" ) - 180f / 255f ) < 0.00001f;
	rows.Add( new { name, lifeMin = Number( "LifetimeMinimum" ), lifeMax = Number( "LifetimeMaximum" ),
		alphaMin = Number( "AlphaMinimum" ), alphaMax = Number( "AlphaMaximum" ), radiusScale = scale,
		rotationMin = Number( "RotationMinimum" ), rotationMax = Number( "RotationMaximum" ), passed } );
}
var output = System.IO.Path.Combine( exchangeDirectory, "atmosphere-source-results.json" );
System.IO.File.WriteAllText( output, System.Text.Json.JsonSerializer.Serialize( rows, new System.Text.Json.JsonSerializerOptions { WriteIndented = true } ) );
return new { output, rows };
