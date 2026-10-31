var flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.NonPublic;
var mount=Sandbox.Mounting.Directory.Get("titanfall2");mount.GetType().GetMethod("IndexParticleFiles",flags).Invoke(mount,new object[]{512,5000f});
var factory=mount.GetType().Assembly.GetType("Titanfall2ParticleFactory").GetMethod("TryCreate",flags);
var rows=new System.Collections.Generic.List<object>();
var scene=new Scene();
try {
foreach(var (name,minimum,depth,persistent) in new[]{("P_cloudthin_beacon",900f,60f,false),("P_cloud_thickCover_beacon",5900f,512f,true),("P_cloudCumulus_beacon",1400f,512f,true),("P_cloud_thickCover_SM_beacon",2900f,512f,true),("P_cloudthin_fast_beacon",490f,128f,false),("P_cloudground_beacon",240f,512f,false)}) {
var args=new object[]{name,null};if(!(bool)mount.GetType().GetMethod("TryGetParticleDefinition",flags).Invoke(mount,args))throw new Exception(name);
var root=new GameObject(scene,true,name);root.AddComponent<Titanfall2ParticleControlPoints>();var create=new object[]{root,mount,args[1],0,null};var created=(bool)factory.Invoke(null,create);
var effects=root.GetComponentsInChildren<ParticleEffect>().ToArray();foreach(var e in effects)e.Step(.1f);
var primary=effects.Single(e=>e.GameObject.Name==name);var particles=primary.Particles.ToArray();var max=particles.Length==0?0:particles.Max(p=>p.Size.x);
var renderers=new[]{primary.GameObject.GetComponent<ParticleSpriteRenderer>()}.Where(r=>r!=null).ToArray();
rows.Add(new{name=name+"-authored-radius",max,passed=created&&max>=minimum});
rows.Add(new{name=name+"-material-depth-feather",passed=renderers.Length>0&&renderers.Any(r=>MathF.Abs(r.DepthFeather-depth)<.01f)});
if((int)args[1].GetType().GetProperty("OrientationType",flags).GetValue(args[1])==2)
rows.Add(new{name=name+"-horizontal-world-z",passed=renderers.Length==1&&renderers[0].Alignment==ParticleSpriteRenderer.BillboardAlignment.Particle&&particles.Length>0&&particles.All(p=>MathF.Abs(p.Angles.Forward.z)>.999f)});
if(persistent){foreach(var e in effects)e.Step(12f);var kept=primary.Particles.ToArray();rows.Add(new{name=name+"-persistent-after-nominal-lifetime",particles=kept.Length,passed=kept.Length>0&&kept.Max(p=>p.Size.x)>=minimum});}
root.Destroy();
}
var output=System.IO.Path.Combine(exchangeDirectory,"cloud-runtime-results.json");System.IO.File.WriteAllText(output,System.Text.Json.JsonSerializer.Serialize(rows,new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));return new{output,rows};
}finally{scene.Destroy();}


