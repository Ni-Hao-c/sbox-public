// Run in a loaded map with Titanfall2MaterialAnimator active. Unlike the
// deterministic fixture, this verifies the actual scene update subscription.
var logo = Material.Load( "mount://titanfall2/models/holo_screens/tv_imc_saver_logo.vmat" );
var map = Material.Load( "mount://titanfall2/models/holo_screens/tv_imc_map_01.vmat" );
var before = logo.GetVector4( "g_vVmtTexture2Transform" );
await Task.Delay( 600, cancellationToken );
var after = logo.GetVector4( "g_vVmtTexture2Transform" );
var alpha = logo.GetVector4( "g_flVmtMaterialAlpha" ).x;
var scale = map.GetVector4( "g_vVmtTexture2Transform" ).x;
var rows = new[] {
	new { name = "scene-updates-retail-logo-rotation", value = after.y, passed = float.IsFinite( after.y ) && MathF.Abs( after.y - before.y ) > .05f },
	new { name = "retail-logo-alpha-stays-in-authored-range", value = alpha, passed = alpha >= .95f && alpha <= 1f },
	new { name = "retail-map-scale-stays-in-authored-range", value = scale, passed = scale >= .4f && scale <= .7f }
};
var output = System.IO.Path.Combine( exchangeDirectory, "live-vmt-proxy-results.json" );
System.IO.File.WriteAllText( output, System.Text.Json.JsonSerializer.Serialize( rows, new System.Text.Json.JsonSerializerOptions { WriteIndented = true } ) );
return new { output, rows };
