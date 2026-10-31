var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
var rows = new System.Collections.Generic.List<object>();
var scene = new Scene();
SceneObject fixture = null;
using var target = Texture.CreateRenderTarget().WithSize( 129, 65 ).Create();
float Linear( float x ) => x <= .04045f ? x / 12.92f : MathF.Pow( (x + .055f) / 1.055f, 2.4f );
float Curve( float x ) => x * (10f * x + .3f) / (x * (10f * x + .5f) + 1.5f);
float Exposure( float l, float ev ) => (1.03f - 2f / (MathF.Log( l + 1.0002f ) + 2f)) / MathF.Max( l, .000001f ) * MathF.Pow( 2, ev );
try
{
	var camera = new GameObject( scene, true, "Retail display fixture" ).AddComponent<CameraComponent>();
	camera.WorldTransform = Transform.Zero; camera.BackgroundColor = Color.Black; camera.ZNear = 1; camera.ZFar = 1000;
	var post = typeof( CameraComponent ).GetProperty( "PostProcess", flags ).GetValue( camera );
	var postStage = post.GetType().GetMethod( "OnRenderStage" );
	typeof( CameraComponent ).GetField( "executeCommandListsHook", flags ).SetValue( camera,
		(Action<Sandbox.Rendering.Stage, SceneCamera>)((stage, cam) => postStage.Invoke( post, new object[] { stage } )) );
	var displayRoot = new GameObject( scene, true, "Retail display volume" );
	var volume = displayRoot.AddComponent<PostProcessVolume>();
	volume.SceneVolume = new Sandbox.Volumes.SceneVolume { Type = Sandbox.Volumes.SceneVolume.VolumeTypes.Infinite };
	var effect = displayRoot.AddComponent<Titanfall2Tonemapping>();
	effect.BloomStrength = 0; // Isolate display/exposure; bloom has a separate GPU fixture.
	var material = Material.Create( "retail-display-fixture", "shaders/titanfall2_default.shader" );
	material.Set( "g_flT2Unlit", 1f ); material.Set( "g_tAlbedo", Texture.White ); material.Set( "g_tEmissive", Texture.Black );
	material.Set( "g_tAO", Texture.White ); material.Set( "g_tOpacity", Texture.White );
	var vertices = new Vertex[4];
	for ( var i=0; i<4; ++i ) { vertices[i].Position = new Vector3( 128, i==1||i==2 ? 256 : -256, i>=2 ? 256 : -256 ); vertices[i].Normal = Vector3.Backward; vertices[i].Color = Color.White; }
	var mesh = new Mesh( "retail-display-fixture", material ); mesh.CreateVertexBuffer( 4, vertices ); mesh.CreateIndexBuffer( 6, new[] { 0,2,1,0,3,2 } );
	fixture = new SceneObject( scene.SceneWorld, Model.Builder.WithName( "retail-display-fixture" ).AddMesh( mesh ).Create() ); fixture.Flags.CastShadows = false;
	async Task<Vector3> Render( int frames=3 )
	{
		for(var f=0; f<frames; ++f)
		{
			scene.GameTick( 1.0/60 );
			typeof( PostProcessSystem ).GetMethod( "UpdateCamera", flags ).Invoke( scene.GetSystem<PostProcessSystem>(), new object[] { camera } );
			await Task.Delay( 40, cancellationToken );
			camera.RenderToTexture( target, new Sandbox.Rendering.ViewSetup { EnablePostprocessing=true, EnableUI=false } );
		}
		using var bitmap = target.GetBitmap(); var pixel = bitmap.GetPixel( 64,32 ); return new Vector3( Linear(pixel.r),Linear(pixel.g),Linear(pixel.b) );
	}
	Texture History()
	{
		var states=(System.Collections.IDictionary)typeof(Titanfall2Tonemapping).GetField("_cameras",flags).GetValue(effect);
		return (Texture)states[camera].GetType().GetField("Exposure",flags).GetValue(states[camera]);
	}
	Vector4 ExposureValues()
	{
		var values = new Vector4[1]; History().GetPixels<Vector4>( (0,0,1,1),0,0,values,ImageFormat.RGBA32323232F ); return values[0];
	}
	effect.ForcedExposure=1;
	foreach(var x in new[]{0f,.01f,.05f,.18f,.5f,1f,4f,16f})
	{
		material.Set("g_vT2AlbedoTint",new Vector4(x,x,x,1)); var actual=await Render(); var expected=new Vector3(Curve(x));
		rows.Add(new{name="retail-curve-"+x,actual=actual.ToString(),expected=expected.ToString(),passed=actual.Distance(expected)<.014f});
	}
	material.Set("g_vT2AlbedoTint",new Vector4(.05f,.5f,4f,1));
	var rgb=await Render(); rows.Add(new{name="retail-curve-independent-rgb",passed=rgb.Distance(new Vector3(Curve(.05f),Curve(.5f),Curve(4f)))<.014f});
	effect.ForcedExposure=0;effect.AdaptationRate=64;
	foreach(var (l,ev) in new[]{(.001f,0f),(.01f,0f),(.1f,0f),(1f,0f),(10f,0f),(100f,0f),(.1f,1f),(1f,-1f)})
	{
		material.Set("g_vT2AlbedoTint",new Vector4(l,l,l,1));effect.ExposureCompensation=ev;
		var actual=await Render();var value=ExposureValues();
		var expected=Math.Clamp(Exposure(l,ev),.5f,2f);
		rows.Add(new{name=$"exposure-{l}-ev-{ev}",actual=value.x,expected,passed=MathF.Abs(value.x-expected)<.004f});
		rows.Add(new{name=$"meter-{l}-ev-{ev}",actual=value.y,expected=l,passed=MathF.Abs(value.y-l)<MathF.Max(.00001f,l*.003f)});
	}
	effect.ExposureCompensation=0;effect.ForcedExposure=1;material.Set("g_vT2AlbedoTint",Vector4.One);await Render();
	effect.ForcedExposure=0;effect.AdaptationRate=.1f;
	await Render(1 );
	{
		var value=ExposureValues();var targetValue=Exposure(1,0);var expected=1-MathF.Min((1-targetValue)*.1f,1-targetValue);expected=Math.Clamp(expected,.5f,2f);
		rows.Add(new{name="retail-relative-adaptation-step",actual=value.x,expected,passed=MathF.Abs(value.x-expected)<.004f});
	}
	var beforeDuplicate = ExposureValues().x;
	camera.RenderToTexture( target, new Sandbox.Rendering.ViewSetup { EnablePostprocessing=true, EnableUI=false } );
	rows.Add(new{name="duplicate-view-does-not-adapt-twice",passed=MathF.Abs(ExposureValues().x-beforeDuplicate)<.000001f});
	rows.Add(new{name="native-exposure-disabled",passed=!camera.AutoExposure.Enabled});
	var cameraA = camera; var postA = post;
	effect.ForcedExposure = .75f; await Render(); var historyA = History();
	var cameraB = new GameObject( scene, true, "Second independent view" ).AddComponent<CameraComponent>();
	cameraB.WorldTransform = Transform.Zero; cameraB.ZNear = 1; cameraB.ZFar = 1000;
	var postB = typeof( CameraComponent ).GetProperty( "PostProcess", flags ).GetValue( cameraB );
	typeof( CameraComponent ).GetField( "executeCommandListsHook", flags ).SetValue( cameraB,
		(Action<Sandbox.Rendering.Stage, SceneCamera>)((stage, cam) => postStage.Invoke( postB, new object[] { stage } )) );
	camera = cameraB; effect.ForcedExposure = 1.25f; await Render(); var historyB = History();
	rows.Add(new{name="independent-camera-history",passed=!ReferenceEquals(historyA,historyB)&&MathF.Abs(ExposureValues().x-1.25f)<.001f});
	camera = cameraA; post = postA;
	rows.Add(new{name="second-camera-preserves-first-exposure",passed=MathF.Abs(ExposureValues().x-.75f)<.001f});
	cameraB.Destroy();scene.GameTick(1.0/60);await Render();
	rows.Add(new{name="destroyed-camera-releases-history",passed=!historyB.IsValid});
	effect.ExposureCompensation = float.NaN; effect.ForcedExposure = float.NaN; effect.MinimumExposure = float.NaN; effect.MaximumExposure = float.NaN;effect.AdaptationRate = 64;
	await Render();var finite=ExposureValues();
	rows.Add(new{name="invalid-settings-remain-finite",passed=float.IsFinite(finite.x)&&finite.x>=.5f&&finite.x<=2});
	var output=System.IO.Path.Combine(exchangeDirectory,"retail-display-results.json");System.IO.File.WriteAllText(output,System.Text.Json.JsonSerializer.Serialize(rows,new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));return new{output,rows};
}
finally { fixture?.Delete(); scene.Destroy(); }
