// Run in a fresh minimal project, before opening a map, so the last-animator
// transition is tested rather than hidden by another scene's animator.
var assembly = typeof( Titanfall2Mount ).Assembly;
var statics = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
var instance = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
var registry = assembly.GetType( "Titanfall2MaterialAnimationRegistry" );
var initialCount = (int)registry.GetField( "_animatorCount", statics ).GetValue( null );
if ( initialCount != 0 ) throw new Exception( "Run proxy verification before opening a map; an animator is already active." );
var update = registry.GetMethod( "Update", statics );
var mount = Sandbox.Mounting.Directory.Get( "titanfall2" );
var reader = assembly.GetType( "Titanfall2VmtReader" ).GetMethod( "TryRead", statics );
var factory = assembly.GetType( "LegacyMaterialLoader" ).GetMethod( "CreateUnlitTwoTexture", statics );
var source = """
UnlitTwoTexture {
 "$basetexture" "models/holo_screens/holo_map_grid" "$texture2" "dev/noise_post"
 "$translucent" 1 "$addlayers" 0 "$translation" "[0 0]" "$rotation" 0 "$scale" 1 "$alpha" 1 "$scroll" "[0.1 0.2]"
 Proxies {
  TextureScroll { texturescrollvar "$translation" texturescrollrate .25 texturescrollangle 0 }
  LinearRamp { resultvar "$rotation" initialvalue 10 rate 90 }
  Sine { resultvar "$scale" sinemin .5 sinemax 1 sineperiod 4 }
  Sine { resultvar "$alpha" sinemin .2 sinemax .8 sineperiod 4 }
  TextureTransform { resultvar "$texture2transform" translatevar "$translation" rotatevar "$rotation" scalevar "$scale" }
  UniformNoise { resultvar "$scroll[0]" minval .25 maxval .5 }
  UniformNoise { resultvar "$scroll[1]" minval -.5 maxval -.25 }
  TextureTransform { resultvar "$basetexturetransform" translatevar "$scroll" }
  TextureTransform { }
 }
}
""";
object Parse( string text )
{
	var args = new object[] { System.Text.Encoding.UTF8.GetBytes( text ), null, null };
	if ( !(bool)reader.Invoke( null, args ) ) throw new Exception( args[2]?.ToString() );
	return args[1];
}
Material Create( object definition, string path )
{
	var metadata = definition.GetType().GetMethod( "GetMetadata", instance ).Invoke( definition, new object[] { path } );
	return (Material)factory.Invoke( null, new object[] { mount, path, definition, metadata, (Action<string>)(warning => throw new Exception( warning )) } );
}
var rows = new System.Collections.Generic.List<object>();
void Check( string name, bool passed, string detail = null ) => rows.Add( new { name, passed, detail } );
bool Near( float x, float y ) => MathF.Abs( x - y ) < .0001f;
var definition = Parse( source );
var material = Create( definition, "t2-proxy-audit" );
foreach ( var time in new[] { 0f, 1f, 2f, 4f } )
{
	update.Invoke( null, new object[] { time } );
	var transform = material.GetVector4( "g_vVmtTexture2Transform" );
	var scale = .75f + .25f * MathF.Sin( MathF.Tau * time / 4 );
	var alpha = .5f + .3f * MathF.Sin( MathF.Tau * time / 4 );
	Check( "ordered-transform-at-" + time, Near( transform.x, scale ) && Near( transform.y, (10 + 90 * time) * MathF.PI / 180 )
		&& Near( transform.z, time * .25f % 1 ) && Near( transform.w, 0 ), transform.ToString() );
	Check( "legacy-alpha-at-" + time, Near( material.GetVector4( "g_flVmtMaterialAlpha" ).x, alpha ) );
}
var noiseTransform = material.GetVector4( "g_vVmtTexture1Transform" );
Check( "vector-component-noise-reaches-transform", noiseTransform.z >= .25f && noiseTransform.z <= .5f && noiseTransform.w >= -.5f && noiseTransform.w <= -.25f );
Check( "cpu-transform-is-not-scrolled-again-in-shader", material.GetVector4( "g_vVmtTexture1Scroll" ) == Vector4.Zero
	&& material.GetVector4( "g_vVmtTexture2Scroll" ) == Vector4.Zero && material.GetVector4( "g_flVmtNoiseEnabled" ).x == 0 );
registry.GetMethod( "Activate", statics ).Invoke( null, null );
registry.GetMethod( "Deactivate", statics ).Invoke( null, null );
update.Invoke( null, new object[] { 1f } );
Check( "cached-material-proxies-survive-last-deactivate", Near( material.GetVector4( "g_vVmtTexture2Transform" ).z, .25f )
	&& Near( material.GetVector4( "g_flVmtMaterialAlpha" ).x, .8f ) );
var explicitSum = Create( Parse( source.Replace( "\"$addlayers\" 0", "\"$addlayers\" 1" ) ), "t2-proxy-explicit-sum" );
Check( "authored-addlayers-preserves-sum", explicitSum.GetVector4( "g_flVmtMultiplyLayers" ).x == 0 );
var cube = Material.Load( "mount://titanfall2/world/test/kodai_fake_screen_01_world.vmat" );
Check( "live-fire-cubemap-keeps-separate-path", cube.ShaderName == "shaders/titanfall2_vmt_unlit_two_texture_cube.shader"
	&& cube.GetTexture( "g_tCubeTexture2" )?.Depth == 6 );
var scene = new Scene();
using var target = Texture.CreateRenderTarget().WithSize( 128, 128 ).Create();
SceneObject obj = null;
try
{
	material.Set( "g_tTexture1", Texture.White ); material.Set( "g_tTexture2", Texture.White );
	var camera = new GameObject( scene, true, "Proxy GPU verification" ).AddComponent<CameraComponent>();
	camera.BackgroundColor = Color.Black; camera.EnablePostProcessing = false; camera.ZNear = 1; camera.ZFar = 1000;
	var vertices = new Vertex[4];
	for ( var i = 0; i < 4; ++i )
	{
		vertices[i].Position = new Vector3( 128, i == 1 || i == 2 ? 100 : -100, i >= 2 ? 100 : -100 );
		vertices[i].Normal = Vector3.Backward; vertices[i].Color = Color.White;
	}
	var mesh = new Mesh( "proxy-alpha", material ); mesh.CreateVertexBuffer( 4, vertices ); mesh.CreateIndexBuffer( 6, new[] { 0, 2, 1, 0, 3, 2 } );
	obj = new SceneObject( scene.SceneWorld, Model.Builder.AddMesh( mesh ).Create() );
	foreach ( var time in new[] { 0f, 1f } )
	{
		update.Invoke( null, new object[] { time } );
		for ( var frame = 0; frame < 3; ++frame ) { await Task.Delay( 40, cancellationToken ); camera.RenderToTexture( target ); }
		using var bitmap = target.GetBitmap(); var pixel = bitmap.GetPixel( 64, 64 );
		float Linear( float x ) => x <= .04045f ? x / 12.92f : MathF.Pow( (x + .055f) / 1.055f, 2.4f );
		var expected = time == 0 ? .5f : .8f;
		Check( "proxy-alpha-reaches-gpu-at-" + time, MathF.Abs( Linear( pixel.r ) - expected ) < .015f
			&& MathF.Abs( Linear( pixel.g ) - expected ) < .015f && MathF.Abs( Linear( pixel.b ) - expected ) < .015f, pixel.ToString() );
	}
}
finally { obj?.Delete(); scene.Destroy(); }
var output = System.IO.Path.Combine( exchangeDirectory, "vmt-proxy-results.json" );
System.IO.File.WriteAllText( output, System.Text.Json.JsonSerializer.Serialize( rows, new System.Text.Json.JsonSerializerOptions { WriteIndented = true } ) );
return new { output, rows };
