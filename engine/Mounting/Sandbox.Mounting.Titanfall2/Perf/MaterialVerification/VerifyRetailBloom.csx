// Real GPU core-bloom checks. Keep fixtures isolated from map lighting/LUT/fog.
var rows = new System.Collections.Generic.List<object>();
var shader = new ComputeShader( "shaders/titanfall2_bloom_cs.shader" );
Texture Pixels( int width, int height, Vector4[] values ) => Texture.Create( width, height, ImageFormat.RGBA32323232F )
	.WithData( System.Runtime.InteropServices.MemoryMarshal.AsBytes( values.AsSpan() ).ToArray() ).Finish();
Vector4[] Read( Texture texture, int w, int h )
{
	var values = new Vector4[w*h]; texture.GetPixels<Vector4>( (0,0,w,h), 0, 0, values, ImageFormat.RGBA32323232F ); return values;
}
using ( var input = Pixels( 8, 8, Enumerable.Repeat( new Vector4( .5f, 2, 4, 1 ), 64 ).ToArray() ) )
using ( var output = Texture.Create( 2, 2, ImageFormat.RGBA32323232F ).WithUAVBinding().Finish() )
{
	shader.Attributes.SetCombo( "D_BLOOM_STAGE", 0 ); shader.Attributes.Set( "T2BloomInput", input );
	shader.Attributes.Set( "T2BloomOutput", output ); shader.Attributes.Set( "T2BloomParameters", new Vector2( 1, 2 ) ); shader.Dispatch( 2, 2, 1 );
	var data=Read(output,2,2);
	rows.Add(new{name="per-channel-cutoff-before-power",passed=data.All(p=>new Vector3(p.x,p.y,p.z).Distance(new Vector3(0,1,9))<.0001f)});
	rows.Add(new{name="bloom-has-no-opacity",passed=data.All(p=>p.w==0)});
}
using ( var input = Pixels( 7, 3, Enumerable.Repeat( new Vector4( .25f, .5f, 2, 1 ), 21 ).ToArray() ) )
using ( var output = Texture.Create( 7, 3, ImageFormat.RGBA32323232F ).WithUAVBinding().Finish() )
{
	foreach(var stage in new[]{1,2})
	{
		shader.Attributes.SetCombo("D_BLOOM_STAGE",stage);shader.Attributes.Set("T2BloomInput",input);shader.Attributes.Set("T2BloomOutput",output);shader.Dispatch(7,3,1);
		rows.Add(new{name="retail-kernel-preserves-constant-odd-size-"+stage,passed=Read(output,7,3).All(p=>new Vector3(p.x,p.y,p.z).Distance(new Vector3(.25f,.5f,2))<.0001f)});
	}
}
var flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Public;
var scene=new Scene(); var fixtures=new System.Collections.Generic.List<SceneObject>();
using var target=Texture.CreateRenderTarget().WithSize(129,65).Create();
try
{
	var camera=new GameObject(scene,true,"Bloom GPU camera").AddComponent<CameraComponent>();camera.WorldTransform=Transform.Zero;camera.BackgroundColor=Color.Black;camera.ZNear=1;camera.ZFar=1000;
	var post=typeof(CameraComponent).GetProperty("PostProcess",flags).GetValue(camera);var stageMethod=post.GetType().GetMethod("OnRenderStage");
	typeof(CameraComponent).GetField("executeCommandListsHook",flags).SetValue(camera,(Action<Sandbox.Rendering.Stage,SceneCamera>)((stage,cam)=>stageMethod.Invoke(post,new object[]{stage})));
	var root=new GameObject(scene,true,"Bloom GPU volume");var volume=root.AddComponent<PostProcessVolume>();volume.SceneVolume=new Sandbox.Volumes.SceneVolume{Type=Sandbox.Volumes.SceneVolume.VolumeTypes.Infinite};
	var effect=root.AddComponent<Titanfall2Tonemapping>();effect.ForcedExposure=1;effect.BloomStrength=1;effect.BloomCutoff=0;
	Material Quad(string name,float x,float radius,Vector3 color)
	{
		var m=Material.Create(name,"shaders/titanfall2_default.shader");m.Set("g_flT2Unlit",1f);m.Set("g_tAlbedo",Texture.White);m.Set("g_tEmissive",Texture.Black);m.Set("g_tAO",Texture.White);m.Set("g_tOpacity",Texture.White);m.Set("g_vT2AlbedoTint",new Vector4(color.x,color.y,color.z,1));
		var vertices=new Vertex[4];for(var i=0;i<4;i++){vertices[i].Position=new Vector3(x,i==1||i==2?radius:-radius,i>=2?radius:-radius);vertices[i].Normal=Vector3.Backward;vertices[i].Color=Color.White;}
		var mesh=new Mesh(name,m);mesh.CreateVertexBuffer(4,vertices);mesh.CreateIndexBuffer(6,new[]{0,2,1,0,3,2});var obj=new SceneObject(scene.SceneWorld,Model.Builder.WithName(name).AddMesh(mesh).Create());obj.Flags.CastShadows=false;fixtures.Add(obj);return m;
	}
	var background=Quad("bloom-background",128,256,new Vector3(.25f));
	async Task<Bitmap> Render()
	{
		for(var f=0;f<3;f++){scene.GameTick(1.0/60);typeof(PostProcessSystem).GetMethod("UpdateCamera",flags).Invoke(scene.GetSystem<PostProcessSystem>(),new object[]{camera});await Task.Delay(40,cancellationToken);camera.RenderToTexture(target,new Sandbox.Rendering.ViewSetup{EnablePostprocessing=true,EnableUI=false});}
		return target.GetBitmap();
	}
	float Curve(float x)=>x*(10*x+.3f)/(x*(10*x+.5f)+1.5f);
	float Linear(float x)=>x<=.04045f?x/12.92f:MathF.Pow((x+.055f)/1.055f,2.4f);
	Vector4 History(){var states=(System.Collections.IDictionary)typeof(Titanfall2Tonemapping).GetField("_cameras",flags).GetValue(effect);var state=states[camera];return Read((Texture)state.GetType().GetField("Exposure",flags).GetValue(state),1,1)[0];}
	using(var image=await Render())rows.Add(new{name="bloom-composited-before-display",passed=MathF.Abs(Linear(image.GetPixel(64,32).r)-Curve(.375f))<.01f});
	effect.BloomStrength=.15f;
	using(var image=await Render())rows.Add(new{name="reduced-gain-remains-linear-before-display",passed=MathF.Abs(Linear(image.GetPixel(64,32).r)-Curve(.26875f))<.01f});
	effect.BloomCutoff=1;
	using(var image=await Render())rows.Add(new{name="ordinary-surface-below-cutoff-does-not-glow",passed=MathF.Abs(Linear(image.GetPixel(64,32).r)-Curve(.25f))<.01f});
	var meterWithBloom=History().y;effect.BloomStrength=0;
	using(var image=await Render())rows.Add(new{name="zero-strength-bypasses-bloom",passed=MathF.Abs(Linear(image.GetPixel(64,32).r)-Curve(.25f))<.01f});
	rows.Add(new{name="bloom-does-not-feed-meter",passed=MathF.Abs(History().y-meterWithBloom)<.00001f});
	background.Set("g_vT2AlbedoTint",new Vector4(0,0,0,1));Quad("bloom-blue-emitter",127,10,new Vector3(0,0,16));
	using var noGlow=await Render();effect.BloomStrength=1;effect.BloomCutoff=0;using var glow=await Render();
	var near=noGlow.GetPixel(75,32);var halo=glow.GetPixel(75,32);var far=glow.GetPixel(120,32);
	rows.Add(new{name="bright-blue-spreads-to-neighbor",actual=halo.b,baseline=near.b,passed=halo.b>near.b+.015f&&halo.b>halo.r+.015f});
	rows.Add(new{name="far-black-remains-black",passed=far.r<.01f&&far.g<.01f&&far.b<.01f});
	var path=System.IO.Path.Combine(exchangeDirectory,"bloom-impulse.png");System.IO.File.WriteAllBytes(path,glow.ToPng());
	effect.BloomStrength=float.NaN;using(var image=await Render())rows.Add(new{name="invalid-strength-bypasses-cleanly",passed=MathF.Abs(image.GetPixel(75,32).b-near.b)<.01f});
	var output=System.IO.Path.Combine(exchangeDirectory,"retail-bloom-results.json");System.IO.File.WriteAllText(output,System.Text.Json.JsonSerializer.Serialize(rows,new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));return new{output,path,rows};
}
finally{foreach(var fixture in fixtures)fixture.Delete();scene.Destroy();}
