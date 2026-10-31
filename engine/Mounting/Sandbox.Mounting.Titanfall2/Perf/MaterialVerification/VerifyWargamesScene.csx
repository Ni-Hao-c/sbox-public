// Run in the mounted mp_wargames Play scene after loading completes.
var scene = Game.ActiveScene;
if ( scene?.Name?.Contains( "mp_wargames" ) != true ) throw new Exception( "Load mp_wargames before running this check." );
var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
object Get( object o, string name ) => o?.GetType().GetProperty( name, flags )?.GetValue( o );
var rows = new System.Collections.Generic.List<object>();
var grids = scene.GetComponentsInChildren<ModelRenderer>().Where( r => r.Model?.Materials.Any( m => m.Name.Contains( "holo_map_grid_orange" ) ) == true ).ToArray();
rows.Add( new { name = "grid-uses-translucent-renderers", count = grids.Length,
	passed = grids.Length > 0 && grids.All( r => r.SceneObject?.Flags.IsTranslucent == true && !r.SceneObject.Flags.IsOpaque ) } );
rows.Add( new { name = "grid-does-not-cast-solid-shadows", passed = grids.Length > 0 && grids.All( r => r.RenderType == ModelRenderer.ShadowRenderType.Off ) } );
rows.Add( new { name = "grid-is-excluded-from-opaque-aggregates", passed = !scene.GetComponentsInChildren<AggregateRenderer>()
	.Any( r => r.Model?.Materials.Any( m => m.Name.Contains( "holo_map_grid_orange" ) ) == true ) } );
foreach ( var spectator in scene.GetComponentsInChildren<Titanfall2SpectatorCamera>() ) spectator.Enabled = false;
var camera = scene.Camera; var originalTransform = camera.WorldTransform;
try
{
	camera.WorldTransform = new Transform( new Vector3( 900, 2700, 450 ), Rotation.LookAt( new Vector3( 780, -575, -370 ) ) );
	await Task.Delay( 700, cancellationToken );
	var found = false;
	foreach ( var streamer in scene.GetComponentsInChildren<Titanfall2StaticPropStreamer>() )
	{
		var cells = (System.Collections.IDictionary)streamer.GetType().GetField( "_cells", flags ).GetValue( streamer );
		var visibility = (Titanfall2.Titanfall2BspReader.BspVisibility)streamer.GetType().GetField( "_visibility", flags ).GetValue( streamer );
		foreach ( var cell in cells.Values ) foreach ( var instance in ((System.Collections.IEnumerable)Get( cell, "Instances" )).Cast<object>() )
		{
			var source = Get( instance, "Source" );
			if ( Get( source, "ModelPath" )?.ToString().Contains( "goblin_dropship.mdl" ) != true ) continue;
			found = true;
			var position = (Vector3)Get( instance, "Position" );
			var originCell = visibility.FindCell( new System.Numerics.Vector3( position.x, position.y, position.z ) );
			var mask = (Titanfall2.Titanfall2BspReader.VisibilityCellMask)Get( source, "PvsCells" );
			var render = (SceneObject)Get( instance, "RenderObject" );
			rows.Add( new { name = "goblin-origin-cell-and-render-visibility", originCell,
				passed = originCell >= 0 && mask.Contains( originCell ) && render?.RenderingEnabled == true && render.Model?.IsError == false } );
		}
	}
	rows.Add( new { name = "goblin-source-instance-exists", passed = found } );
}
finally { camera.WorldTransform = originalTransform; }
var output = System.IO.Path.Combine( exchangeDirectory, "wargames-scene-results.json" );
System.IO.File.WriteAllText( output, System.Text.Json.JsonSerializer.Serialize( rows, new System.Text.Json.JsonSerializerOptions { WriteIndented = true } ) );
return new { output, rows };
