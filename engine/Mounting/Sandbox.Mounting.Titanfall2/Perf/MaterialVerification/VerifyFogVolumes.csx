await Sandbox.Mounting.Directory.Mount("titanfall2");
var host=Sandbox.Mounting.Directory.Get("titanfall2");var flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Public;
string Value(System.Collections.Generic.IReadOnlyDictionary<string,string> e,string key)=>e is not null&&e.TryGetValue(key,out var text)?text:null;
var rows=new System.Collections.Generic.List<object>();var all=new System.Collections.Generic.Dictionary<string,System.Collections.Generic.Dictionary<string,string>[]>();
foreach(var map in new[]{"mp_wargames","sp_beacon","sp_tday","mp_angel_city"})
{
 var loader=host.GetByPath("mount://titanfall2/maps/"+map+".bsp.scene");
 byte[] Bytes(string field){var source=loader.GetType().GetField(field,flags).GetValue(loader);var args=new object[]{null,null};if(!(bool)source.GetType().GetMethod("TryReadAllBytes",flags).Invoke(source,args))throw new Exception(args[1]?.ToString());return(byte[])args[0];}
 var raw=Bytes("_source");if(!Titanfall2.Titanfall2BspReader.TryRead(raw,map,null,false,false,false,out var parsed,out var error))throw new Exception(error);
 var environment=Titanfall2.Titanfall2EntityText.Parse(Bytes("_environmentEntitySource"));var entities=environment.Concat(parsed.Entities).ToArray();all[map]=entities;
 var volumes=parsed.Entities.Where(e=>Value(e,"classname")=="fog_volume").ToArray();
 rows.Add(new{name=map+"-inline-convex-hulls-restored",volumes=volumes.Length,passed=volumes.All(e=>e.ContainsKey("*trigger_bounds_mins")&&e.Keys.Count(k=>k.StartsWith("*trigger_brush_"))>=6)&&parsed.WorldCollision.IsEmpty});
 var selector=new Titanfall2.Titanfall2FogEnvironment(entities);
 foreach(var volume in volumes){var p=Vector3.Parse(volume["origin"]);var actual=selector.Select(new System.Numerics.Vector3(p.x,p.y,p.z));rows.Add(new{name=map+"-"+volume["model"]+"-authored-target",target=volume["target"],actual=Value(actual,"targetname"),passed=Value(actual,"targetname")==volume["target"]});}
}
var war=all["mp_wargames"];var selected=new Titanfall2.Titanfall2FogEnvironment(war);
rows.Add(new{name="wargames-playable-area-selects-main-fog",passed=Value(selected.Select(new System.Numerics.Vector3(900,2700,450)),"targetname")=="env_fog_controller_1"});
var first=selected.Select(new System.Numerics.Vector3(100000));rows.Add(new{name="outside-volumes-uses-first-controller",passed=Value(first,"targetname")=="fog_intro_imc"});
var beacon=new Titanfall2.Titanfall2FogEnvironment(all["sp_beacon"]);rows.Add(new{name="last-master-controller-is-default",passed=Value(beacon.Select(new System.Numerics.Vector3(100000)),"targetname")=="env_fog_controller_1"});
var origin=new System.Numerics.Vector3(900,2700,450);for(var i=0;i<100;i++)selected.Select(origin);var before=GC.GetAllocatedBytesForCurrentThread();for(var i=0;i<10000;i++)selected.Select(origin);var allocation=GC.GetAllocatedBytesForCurrentThread()-before;rows.Add(new{name="camera-selection-no-allocations",allocation,passed=allocation==0});
var synthetic=war.Select(e=>new System.Collections.Generic.Dictionary<string,string>(e,StringComparer.OrdinalIgnoreCase)).ToArray();synthetic.Single(e=>Value(e,"targetname")=="env_fog_controller_1")["fogenable"]="0";
rows.Add(new{name="disabled-selected-fog-is-not-skipped",passed=Value(new Titanfall2.Titanfall2FogEnvironment(synthetic).Select(origin),"fogenable")=="0"});
var missing=war.Where(e=>Value(e,"targetname")!="env_fog_controller_1").ToArray();rows.Add(new{name="unresolved-volume-target-falls-back",passed=Value(new Titanfall2.Titanfall2FogEnvironment(missing).Select(origin),"targetname")=="fog_intro_imc"});
var staticFlags=System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic;
var cache=typeof(Titanfall2HeightFog).Assembly.GetType("Titanfall2MapCache");var previousCache=Environment.GetEnvironmentVariable("SBOX_TITANFALL2_CACHE_PATH");
try
{
 Environment.SetEnvironmentVariable("SBOX_TITANFALL2_CACHE_PATH",System.IO.Path.Combine(exchangeDirectory,"fog-cache"));
 var identity="fog-volumes-"+Guid.NewGuid();var metadata=new Titanfall2.Titanfall2BspReader.ParsedBsp{Entities=war};
 cache.GetMethod("Write",staticFlags).Invoke(null,new object[]{identity,metadata});var read=new object[]{identity,null};
 var hit=(bool)cache.GetMethod("TryRead",staticFlags).Invoke(null,read);var restored=read[1] as Titanfall2.Titanfall2BspReader.ParsedBsp;
 rows.Add(new{name="disk-metadata-roundtrip-preserves-convex-selection",passed=hit&&Value(new Titanfall2.Titanfall2FogEnvironment(restored.Entities).Select(origin),"targetname")=="env_fog_controller_1"});
}
finally{Environment.SetEnvironmentVariable("SBOX_TITANFALL2_CACHE_PATH",previousCache);}
var scene=new Scene();
try
{
 var camera=new GameObject(scene,true,"Fog volume camera").AddComponent<CameraComponent>();camera.WorldPosition=new Vector3(900,2700,450);camera.ComposeView();
 var root=new GameObject(scene,true,"Fog volume controller");var fog=root.AddComponent<Titanfall2HeightFog>();
 typeof(Titanfall2HeightFog).GetMethod("ConfigureEnvironment",flags).Invoke(fog,new object[]{war});
 var pre=typeof(Titanfall2HeightFog).GetMethod("OnPreRender",flags);
 foreach(var (name,p,expected) in new[]{("main",new Vector3(900,2700,450),"env_fog_controller_1"),("imc",new Vector3(-3046,2751.5f,-1515),"fog_intro_imc"),("militia",new Vector3(-1917.5f,2840,-1276),"fog_intro_militia"),("return",new Vector3(900,2700,450),"env_fog_controller_1")}){camera.WorldPosition=p;camera.ComposeView();pre.Invoke(fog,null);rows.Add(new{name="runtime-"+name,actual=fog.SourceName,passed=fog.SourceName==expected});}
 var encoded=fog.EncodedEnvironment;root.Destroy();var restored=new GameObject(scene,true,"Restored fog").AddComponent<Titanfall2HeightFog>();restored.EncodedEnvironment=encoded;pre.Invoke(restored,null);
 rows.Add(new{name="serialized-clone-restores-independent-volume-selection",passed=restored.SourceName=="env_fog_controller_1"&&MathF.Abs(restored.Density.w-.8f)<.0001f});
 restored.Enabled=false;restored.Enabled=true;pre.Invoke(restored,null);rows.Add(new{name="reenable-restores-current-region",passed=restored.SourceName=="env_fog_controller_1"});
}
finally{scene.Destroy();}
var output=System.IO.Path.Combine(exchangeDirectory,"fog-volume-results.json");System.IO.File.WriteAllText(output,System.Text.Json.JsonSerializer.Serialize(rows,new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));return new{output,rows};


