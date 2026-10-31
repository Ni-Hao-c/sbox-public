// Run on first load and again after closing the map, cache expiry and GC.
var scene = Game.ActiveScene;
if ( scene?.Name?.Contains( "mp_wargames" ) != true ) throw new Exception( "Load mp_wargames before checking generated resources." );
var aggregates = scene.GetComponentsInChildren<AggregateRenderer>().ToArray();
var translucent = scene.GetComponentsInChildren<ModelRenderer>().Where( r => r.GameObject.Name.StartsWith( "titanfall2_world_translucent_" ) ).ToArray();
var models = aggregates.Select( a => a.Model ).Concat( translucent.Select( r => r.Model ) ).ToArray();
var lightmappedMaterials = models.Where( m => m is not null && !m.IsError ).SelectMany( m => m.Materials )
	.Where( m => m.ShaderName == "shaders/titanfall2_world_lightmapped.shader" ).Distinct().ToArray();
var rows = new System.Collections.Generic.List<object>();
rows.Add( new { name = "all-opaque-aggregate-models-survive", count = aggregates.Length,
	passed = aggregates.Length > 0 && aggregates.All( a => a.Model is { IsValid: true, IsError: false } ) } );
rows.Add( new { name = "all-translucent-models-survive", count = translucent.Length,
	passed = translucent.Length > 0 && translucent.All( r => r.Model is { IsValid: true, IsError: false } ) } );
foreach ( var slot in new[] { "g_tLightmapSkyA", "g_tLightmapSkyB", "g_tLightmapRealTimeA", "g_tLightmapRealTimeB" } )
	rows.Add( new { name = "generated-lightmap-survives-" + slot, count = lightmappedMaterials.Length,
		passed = lightmappedMaterials.Length > 0 && lightmappedMaterials.All( m => m.GetTexture( slot ) is { IsValid: true, IsError: false } ) } );
var output = System.IO.Path.Combine( exchangeDirectory, "generated-world-resources-results.json" );
System.IO.File.WriteAllText( output, System.Text.Json.JsonSerializer.Serialize( rows, new System.Text.Json.JsonSerializerOptions { WriteIndented = true } ) );
return new { output, rows };
