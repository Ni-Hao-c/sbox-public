var scene=new Scene();using var target=Texture.CreateRenderTarget().WithSize(128,128).Create();
var flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Public;
var rows=new System.Collections.Generic.List<object>();float Linear(float v)=>v<=.04045f?v/12.92f:MathF.Pow((v+.055f)/1.055f,2.4f);
try {
var camera=new GameObject(scene,true,"Fog sprite camera").AddComponent<CameraComponent>();camera.WorldTransform=Transform.Zero;camera.EnablePostProcessing=false;camera.BackgroundColor=Color.Black;camera.ZNear=1;camera.ZFar=1000;
var fog=new GameObject(scene,true,"Fog").AddComponent<Titanfall2HeightFog>();typeof(Titanfall2HeightFog).GetMethod("Configure",flags).Invoke(fog,new object[]{new System.Collections.Generic.Dictionary<string,string>{["fogcolor"]="0 255 0",["fogcolorstrength"]="0.5",["fogdensity"]="1",["foghalfdistbottom"]="128",["foghalfdisttop"]="128",["fogztop"]="1000",["fogzbottom"]="0"}});
var root=new GameObject(scene,true,"Fog sprite");root.WorldPosition=new Vector3(128,0,0);
var effect=root.AddComponent<ParticleEffect>();effect.ApplyColor=true;effect.Gradient=Color.Red;effect.ApplyShape=true;effect.Scale=32;effect.Lifetime=10;
var renderer=root.AddComponent<ParticleSpriteRenderer>();renderer.Sprite=Sprite.FromTexture(Texture.White);renderer.Lighting=false;renderer.Shadows=false;renderer.DepthFeather=0;renderer.CameraFadeNear=0;renderer.CameraFadeFar=0;
var emitter=root.AddComponent<ParticleSphereEmitter>();emitter.Radius=0;emitter.Velocity=0;emitter.Burst=1;emitter.Rate=0;emitter.Loop=false;emitter.ResetEmitter();effect.Step(.01f);
var update=typeof(SceneSpriteSystem).GetMethod("UpdateSprites",flags);
foreach(var additive in new[]{false,true})foreach(var strength in new[]{0f,.8f,1f}){
renderer.Additive=additive;renderer.FogStrength=strength;
for(var frame=0;frame<4;frame++){await Task.Delay(40,cancellationToken);update.Invoke(scene.GetSystem<SceneSpriteSystem>(),null);typeof(Titanfall2HeightFog).GetMethod("Apply",flags).Invoke(fog,null);camera.RenderToTexture(target);}
using var bmp=target.GetBitmap();var p=bmp.GetPixel(64,64);var actual=new Vector3(Linear(p.r),Linear(p.g),Linear(p.b));var expected=Vector3.Lerp(new Vector3(1,0,0),additive?Vector3.Zero:new Vector3(0,.5f,0),.5f*strength);expected.x=1-.5f*strength;
rows.Add(new{name=$"sprite-additive-{additive}-fog-{strength}",actual=actual.ToString(),expected=expected.ToString(),passed=actual.Distance(expected)<.015f});
}
renderer.Additive=false;renderer.FogStrength=0;effect.Gradient=Color.White;effect.Step(.01f);
using var artwork=Texture.Create(1,1).WithData(new byte[]{188,204,51,255}).Finish();
using var ramp=Texture.Create(2,2).WithData(new byte[]{255,0,0,255,0,255,0,255,0,0,255,255,255,255,255,255}).Finish();
renderer.Sprite=Sprite.FromTexture(artwork);
foreach(var enabled in new[]{true,false}){
renderer.ColorRamp=enabled?ramp:null;
for(var frame=0;frame<4;frame++){await Task.Delay(40,cancellationToken);update.Invoke(scene.GetSystem<SceneSpriteSystem>(),null);typeof(Titanfall2HeightFog).GetMethod("Apply",flags).Invoke(fog,null);camera.RenderToTexture(target);}
using var bmp=target.GetBitmap();var p=bmp.GetPixel(64,64);var actual=new Vector3(Linear(p.r),Linear(p.g),Linear(p.b));
var u=Math.Clamp(Linear(188/255f)*2-.5f,0,1);var v=Math.Clamp(Linear(204/255f)*2-.5f,0,1);var expected=enabled?new Vector3((1-u)*(1-v)+u*v,u,v):new Vector3(Linear(188/255f),Linear(204/255f),Linear(51/255f));
rows.Add(new{name="sprite-color-ramp-"+enabled,actual=actual.ToString(),expected=expected.ToString(),passed=actual.Distance(expected)<.015f});
}
var output=System.IO.Path.Combine(exchangeDirectory,"height-fog-particle-results.json");System.IO.File.WriteAllText(output,System.Text.Json.JsonSerializer.Serialize(rows,new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));return new{output,rows};
}finally{scene.Destroy();}


