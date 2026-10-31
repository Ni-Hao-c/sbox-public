var scene = new Scene();
var rows = new System.Collections.Generic.List<object>();
SceneObject obj = null;
using var target = Texture.CreateRenderTarget().WithSize(128,128).Create();
// BC1 stores the same RG slope normal as BC5: blue is deliberately zero.
using var normalTexture = Texture.Create(4,4,ImageFormat.DXT1).WithData(new byte[]{0,132,0,132,0,0,0,0}).Finish();
float Linear(float x) => x <= .04045f ? x / 12.92f : MathF.Pow((x+.055f)/1.055f,2.4f);
try
{
 await Sandbox.Mounting.Directory.Mount("titanfall2");
 foreach(var path in new[]{"world/metal/metal_grate_roundholes","world/metal/metal_grate_stretch_panels01","world/utility/utility_grate_white_stretched_metal01"})
 {
  var source=Material.Load("mount://titanfall2/"+path+".vmat");
  rows.Add(new{name="retail-normal-binding",path,format=source.GetTexture("g_tNormal").ImageFormat.ToString(),passed=source.GetVector4("g_flT2NormalSlope").x==1});
 }
 var camera = new GameObject(scene,true,"BC1 normal regression").AddComponent<CameraComponent>();
 camera.WorldTransform=Transform.Zero;camera.EnablePostProcessing=true;camera.ZNear=1;camera.ZFar=1000;
 scene.RenderAttributes.Set("NeedsDepthNormals",true);
 var command=new Sandbox.Rendering.CommandList("Read normal encoding G-buffer");command.Attributes.SetValue("D_MSAA_NORMALS",Sandbox.Rendering.RenderValue.MsaaCombo);command.Blit(Material.FromShader("shaders/tests/titanfall2_gbuffer_read.shader"));camera.AddCommandList(command,Sandbox.Rendering.Stage.AfterPostProcess);
 foreach(var shader in new[]{"default","cutout","world_lightmapped"})
 {
  var material=Material.Create("bc1-normal-regression-"+shader,"shaders/titanfall2_"+shader+".shader");
  material.Set("g_tAlbedo",Texture.White);material.Set("g_tOpacity",Texture.White);material.Set("g_tNormal",normalTexture);
  material.Set("g_flHasNormalMap",1f);material.Set("g_flT2NormalSlope",1f);
  var vertices=new Vertex[4];for(int i=0;i<4;i++){vertices[i].Position=new Vector3(128,i==1||i==2?80:-80,i>=2?80:-80);vertices[i].Normal=Vector3.Backward;vertices[i].Color=Color.White;vertices[i].TexCoord0=new Vector4(i==1||i==2?1:0,i>=2?1:0,0,0);}
  var mesh=new Mesh("bc1-normal-fixture",material);mesh.CreateVertexBuffer(4,vertices);mesh.CreateIndexBuffer(6,new[]{0,2,1,0,3,2});obj=new SceneObject(scene.SceneWorld,Model.Builder.AddMesh(mesh).Create());
  for(int f=0;f<4;f++){await Task.Delay(35,cancellationToken);camera.RenderToTexture(target,new Sandbox.Rendering.ViewSetup{EnablePostprocessing=true,EnableUI=false});}
  using var bitmap=target.GetBitmap();var p=bitmap.GetPixel(64,64);var actual=new Vector3(Linear(p.r),Linear(p.g),Linear(p.b));
  var expected=(new Vector3(-1,132f/255*2.867661f-1.439453f,130f/255*2.867661f-1.439453f).Normal+Vector3.One)*.5f;
  rows.Add(new{name="native-bc1-normal",shader,actual=actual.ToString(),expected=expected.ToString(),passed=actual.Distance(expected)<.02f});
  obj.Delete();obj=null;
 }
 var output=System.IO.Path.Combine(exchangeDirectory,"normal-encoding-results.json");System.IO.File.WriteAllText(output,System.Text.Json.JsonSerializer.Serialize(rows));return new{output,rows};
}
finally {obj?.Delete();scene.Destroy();}
