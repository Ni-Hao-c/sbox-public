// Dedicated test editor. Builds two isolated worldspawn clones from the same
// cached retail scene, then closes both and reopens after resource-cache expiry.
await Sandbox.Mounting.Directory.Mount( "titanfall2" );
var path = "mount://titanfall2/maps/sp_beacon.bsp.scene";
var file = SceneFile.Load( path );
if ( file?.GameObjects is null ) throw new Exception( "sp_beacon is unavailable." );
var world = file.GameObjects.Single( json => json["Name"]?.ToString() == "worldspawn" );
var rows = new System.Collections.Generic.List<object>();
void Check( string name, bool passed, int count ) => rows.Add( new { name, passed, count } );
Scene MakeScene()
{
	var scene = new Scene();
	using ( scene.Push() ) new GameObject( false ).Deserialize( (System.Text.Json.Nodes.JsonObject)world.DeepClone() );
	return scene;
}
void Inspect( Scene scene, string phase )
{
	var models = scene.GetAllComponents<AggregateRenderer>().Select( r => r.Model )
		.Concat( scene.GetAllComponents<ModelRenderer>().Where( r => r.GameObject.Name.StartsWith( "world_" )
			|| r.GameObject.Name.StartsWith( "titanfall2_world_translucent_" ) ).Select( r => r.Model ) ).ToArray();
	Check( phase + "-generated-models", models.Length > 0 && models.All( m => m is { IsValid: true, IsError: false } ), models.Length );
	var materials = models.Where( m => m is { IsValid: true, IsError: false } ).SelectMany( m => m.Materials )
		.Where( m => m.ShaderName == "shaders/titanfall2_world_lightmapped.shader" ).Distinct().ToArray();
	foreach ( var slot in new[] { "g_tLightmapSkyA", "g_tLightmapSkyB", "g_tLightmapRealTimeA", "g_tLightmapRealTimeB" } )
		Check( phase + "-" + slot, materials.Length > 0 && materials.All( m => m.GetTexture( slot ) is { IsValid: true, IsError: false } ), materials.Length );
}
Scene first = null, second = null, reopened = null;
try
{
	first = MakeScene();
	second = MakeScene();
	Inspect( first, "first-clone" );
	first.Destroy();
	first = null;
	Inspect( second, "surviving-clone" );
	second.Destroy();
	second = null;
	GC.Collect();
	GC.WaitForPendingFinalizers();
	await Task.Delay( 13000, cancellationToken );
	GC.Collect();
	GC.WaitForPendingFinalizers();
	reopened = MakeScene();
	Inspect( reopened, "reopened-after-expiry-and-gc" );
}
finally
{
	first?.Destroy();
	second?.Destroy();
	reopened?.Destroy();
}
var output = System.IO.Path.Combine( exchangeDirectory, "cached-world-lifetime.json" );
System.IO.File.WriteAllText( output, System.Text.Json.JsonSerializer.Serialize( rows, new System.Text.Json.JsonSerializerOptions { WriteIndented = true } ) );
return new { output, rows };
