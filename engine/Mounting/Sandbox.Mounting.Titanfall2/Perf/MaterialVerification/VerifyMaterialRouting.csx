// Real retail materials that previously matched the broad "water" substring.
var host = Sandbox.Mounting.Directory.Get( "titanfall2" );
var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
var descriptors = (System.Collections.IDictionary)host.GetType().GetField( "_materialDescriptors", flags ).GetValue( host );
var rows = new System.Collections.Generic.List<object>();
foreach ( var (name, shader, layered) in new[] {
	("models/foliage/grassblades_water_02", "cutout", 0),
	("models/foliage/grassblades_water_dark_02", "cutout", 0),
	("models/backwater/foliage/backwater_grass_weed", "cutout", 0),
	("world/mud/mud_relic_dark_01_water_mud_bm", "default", 1),
	("world/dirt/dirt_forest_water_puddles_01_dirt_mud_01_bm", "default", 1),
	("world/homestead/homestead_waterwheel_metal_raw_01", "default", 0),
	("models/colony/solar_water_heater", "default", 0),
	("models/stone/rock_set_underwater_01", "default", 0),
	("world/homestead/homestead_water_whtwsh_slow_01_btt", "translucent", 2),
	("world/water/water_wild_rapid_01_transparent_bm", "decal_translucent", 2),
	("world/water/dirty_water_03", "water", 0),
	("world/homestead/homestead_test_water_slow_01", "water", 0),
	("world/water/waterfall_stream_red_01", "water", 0),
	("models/water/waterfall_badlands_01_slow", "additive", 0) } )
{
	var material = Material.Load( "mount://titanfall2/" + name + ".vmat" );
	var expected = "shaders/titanfall2_" + shader + ".shader";
	var actualLayer = material.GetVector4( "g_flT2Layered" ).x;
	var actualOpacity = material.GetVector4( "g_flT2LayeredOpacity" ).x;
	rows.Add( new { name, material.ShaderName, expected, actualLayer, actualOpacity,
		passed = material.ShaderName == expected && (layered == 0 || (layered == 1 ? actualLayer == 1 : actualOpacity == 1)) } );
}
foreach ( var name in new[] { "models/signs/tiffany_coffee_01", "world/signs/sign_krs10_cycles", "models/signs/billboard_angel_city/billboards_large_ac_cooper_01" } )
{
	var material = Material.Load( "mount://titanfall2/" + name + ".vmat" );
	var surface = material.GetVector4( "g_vT2SurfaceUvChannels" );
	var auxiliary = material.GetVector4( "g_vT2AuxUvChannels" );
	var multiplyChannel = material.GetVector4( "g_flT2EmissiveMultiplyUvChannel" ).x;
	rows.Add( new { name, surface = surface.ToString(), auxiliary = auxiliary.ToString(), multiplyChannel,
		passed = surface.x == 0 && auxiliary.y == 0 && multiplyChannel == 1
			&& material.GetVector4( "g_flT2HasEmissiveMultiply" ).x == 1
			&& material.GetTexture( "g_tEmissiveMultiply" )?.IsValid == true
			&& material.GetTexture( "g_tEmissiveMultiply" ) != material.GetTexture( "g_tEmissive" ) } );
}
var output = System.IO.Path.Combine( exchangeDirectory, "material-routing-results.json" );
System.IO.File.WriteAllText( output, System.Text.Json.JsonSerializer.Serialize( rows, new System.Text.Json.JsonSerializerOptions { WriteIndented = true } ) );
return new { output, rows };
