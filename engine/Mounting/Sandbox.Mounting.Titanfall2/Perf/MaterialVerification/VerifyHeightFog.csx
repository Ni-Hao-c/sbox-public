var rows=new System.Collections.Generic.List<object>();
var scene=new Scene();
using var target=Texture.CreateRenderTarget().WithSize(128,128).Create();
SceneObject fixture=null;
var flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.NonPublic;
var configure=typeof(Titanfall2HeightFog).GetMethod("Configure",flags);
float Linear(float v)=>v<=.04045f?v/12.92f:MathF.Pow((v+.055f)/1.055f,2.4f);
try {
 var camera=new GameObject(scene,true,"Height fog camera").AddComponent<CameraComponent>();camera.EnablePostProcessing=false;camera.BackgroundColor=Color.Black;camera.ZNear=1;camera.ZFar=10000;
 var fog=new GameObject(scene,true,"Height fog").AddComponent<Titanfall2HeightFog>();
 System.Collections.Generic.Dictionary<string,string> Entity(float bottomHalf=100,float topHalf=400,float opacity=1,float offset=0,float angle=0,float directionalStrength=0)=>new(){["classname"]="env_fog_controller",["fogenable"]="1",["fogzbottom"]="0",["fogztop"]="1000",["foghalfdistbottom"]=bottomHalf.ToString(System.Globalization.CultureInfo.InvariantCulture),["foghalfdisttop"]=topHalf.ToString(System.Globalization.CultureInfo.InvariantCulture),["fogdensity"]=opacity.ToString(System.Globalization.CultureInfo.InvariantCulture),["fogdistoffset"]=offset.ToString(System.Globalization.CultureInfo.InvariantCulture),["fogcolor"]="0 255 0",["fogcolorstrength"]="0.5",["fogdircolor"]="0 0 255",["fogdircolorstrength"]=directionalStrength.ToString(System.Globalization.CultureInfo.InvariantCulture),["fogangles"]="0 "+angle.ToString(System.Globalization.CultureInfo.InvariantCulture)+" 0",["fogdirhalfangle"]="90"};
 var cases=new[]{("below",-100f,100f,100f,400f,1f,0f,0f,0f), ("bottom",0f,100f,100f,400f,1f,0f,0f,0f), ("middle",500f,100f,100f,400f,1f,0f,0f,0f), ("top",1000f,100f,100f,400f,1f,0f,0f,0f), ("above",1200f,100f,100f,400f,1f,0f,0f,0f), ("maximum-opacity",-100f,1000f,100f,100f,.3f,0f,0f,0f), ("distance-offset",-100f,100f,100f,100f,1f,50f,0f,0f), ("inside-offset",-100f,100f,100f,100f,1f,200f,0f,0f), ("direction-front",-100f,100f,100f,100f,.5f,0f,0f,.5f), ("direction-side",-100f,100f,100f,100f,.5f,0f,90f,.5f), ("direction-back",-100f,100f,100f,100f,.5f,0f,180f,.5f), ("direction-diagonal",-100f,100f,100f,100f,.5f,0f,45f,.5f), ("disabled",-100f,100f,100f,100f,0f,0f,0f,.5f)};
 foreach(var shader in new[]{"default","cutout","translucent","alpha","additive","vista"}) foreach(var (name,z,distance,bottomHalf,topHalf,maximum,offset,angle,directionalStrength) in cases) {
  var cameraPosition=new Vector3(0,0,z);camera.WorldTransform=new Transform(cameraPosition);
  configure.Invoke(fog,new object[]{Entity(bottomHalf,topHalf,maximum,offset,angle,directionalStrength)});
  var material=Material.Create("height-fog-"+shader,"shaders/titanfall2_"+shader+".shader");material.Set("g_flT2Unlit",1f);material.Set("g_tAlbedo",Texture.White);material.Set("g_vT2AlbedoTint",new Vector4(1,0,0,1));material.Set("g_tOpacity",Texture.White);material.Set("g_tEmissive",Texture.Black);material.Set("g_tAO",Texture.White);material.Set("g_tSpecular",Texture.Black);material.Set("g_tGloss",Texture.Black);
  var transparent=shader=="translucent"||shader=="alpha"||shader=="additive";material.Set("g_flT2MaterialOpacity",transparent ? .5f : 1f);
  var vertices=new Vertex[4];for(var i=0;i<4;i++){vertices[i].Position=new Vector3(distance,i==1||i==2?distance:-distance,z+(i>=2?distance:-distance));vertices[i].Normal=Vector3.Backward;vertices[i].Color=Color.White;}
  var mesh=new Mesh("height-fog",material);mesh.CreateVertexBuffer(4,vertices);mesh.CreateIndexBuffer(6,new[]{0,2,1,0,3,2});fixture=new SceneObject(scene.SceneWorld,Model.Builder.WithName("height-fog").AddMesh(mesh).Create());fixture.Flags.CastShadows=false;
  scene.GameTick(1.0/60);for(var frame=0;frame<4;frame++){await Task.Delay(20,cancellationToken);typeof(Titanfall2HeightFog).GetMethod("Apply",flags).Invoke(fog,null);camera.RenderToTexture(target);}
  using var bmp=target.GetBitmap();var p=bmp.GetPixel(64,64);var actual=new Vector3(Linear(p.r),Linear(p.g),Linear(p.b));
  var density=1f/bottomHalf+(1f/topHalf-1f/bottomHalf)*Math.Clamp(z/1000f,0,1);var amount=(1-MathF.Pow(2,-density*MathF.Max(0,distance-offset)))*maximum;
  var cone=MathF.Pow(MathF.Max(0,-MathF.Cos(angle*MathF.PI/180)),2);var fogColor=new Vector3(0,.5f,directionalStrength*cone);
  var expected=Vector3.Lerp(new Vector3(1,0,0),shader=="additive"?Vector3.Zero:fogColor,amount)*(transparent ? .5f : 1f);
  rows.Add(new{name=shader+"-"+name,actual=actual.ToString(),expected=expected.ToString(),passed=actual.Distance(expected)<.012f});fixture.Delete();fixture=null;
 }
 var type=typeof(Titanfall2HeightFog).Assembly.GetType("Titanfall2FogParameters");rows.Add(new{name="constant-buffer-layout-80-bytes",passed=System.Runtime.InteropServices.Marshal.SizeOf(type)==80});
 var output=System.IO.Path.Combine(exchangeDirectory,"height-fog-results.json");System.IO.File.WriteAllText(output,System.Text.Json.JsonSerializer.Serialize(rows,new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));return new{output,rows};
}finally{fixture?.Delete();scene.Destroy();}


