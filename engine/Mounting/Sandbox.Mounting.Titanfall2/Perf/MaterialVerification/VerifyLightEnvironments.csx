var rows=new System.Collections.Generic.List<object>();
await Sandbox.Mounting.Directory.Mount("titanfall2");
var host=Sandbox.Mounting.Directory.Get("titanfall2");
var sourceFlags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Public;
System.Collections.Generic.Dictionary<string,string>[] Entities(string map)
{
	var loader=host.GetByPath("mount://titanfall2/maps/"+map+".bsp.scene");
	var source=loader?.GetType().GetField("_environmentEntitySource",sourceFlags)?.GetValue(loader);
	var args=new object[]{null,null};
	if(source is null||!(bool)source.GetType().GetMethod("TryReadAllBytes",sourceFlags).Invoke(source,args))throw new Exception("Missing source environment: "+map);
	return Titanfall2.Titanfall2EntityText.Parse((byte[])args[0]).ToArray();
}
var war=Entities("mp_wargames");var angel=Entities("mp_angel_city");var beacon=Entities("sp_beacon");
int Selected(System.Collections.Generic.Dictionary<string,string>[] entities,System.Numerics.Vector3 position)=>Titanfall2.Titanfall2LightEnvironment.Index(Titanfall2.Titanfall2LightEnvironment.Select(entities,position));
rows.Add(new{name="wargames-default-excludes-sky-volume",passed=Titanfall2.Titanfall2LightEnvironment.Index(Titanfall2.Titanfall2LightEnvironment.Default(war))==1});
rows.Add(new{name="wargames-main-camera-dark-environment",passed=Selected(war,new System.Numerics.Vector3(900,2700,450))==1});
rows.Add(new{name="wargames-sky-camera-bright-environment",passed=Selected(war,new System.Numerics.Vector3(-6184,-3624,-6519))==0});
rows.Add(new{name="angel-default-preserves-daylight",passed=Selected(angel,new System.Numerics.Vector3(0,0,100))==0});
rows.Add(new{name="angel-sky-volume-selects-index-one",passed=Selected(angel,new System.Numerics.Vector3(12928,1031,-5504))==1});
rows.Add(new{name="single-beacon-environment-preserved",passed=Selected(beacon,new System.Numerics.Vector3(0,0,100))==0});
var custom=new System.Collections.Generic.Dictionary<string,string>{{"origin","0 0 0"},{"*trigger_bounds_mins","-2 -2 -2"},{"*trigger_bounds_maxs","2 2 2"},{"*trigger_brush_0_plane_0","1 1 0 1"}};
rows.Add(new{name="convex-plane-rejects-box-corner",passed=!Titanfall2.Titanfall2LightEnvironment.Contains(custom,new System.Numerics.Vector3(1,1,0))&&Titanfall2.Titanfall2LightEnvironment.Contains(custom,System.Numerics.Vector3.Zero)});
custom["*trigger_brush_1_plane_0"]="-1 -1 0 -1";
rows.Add(new{name="multiple-brushes-form-union",passed=Titanfall2.Titanfall2LightEnvironment.Contains(custom,new System.Numerics.Vector3(1,1,0))});
custom.Remove("*trigger_brush_0_plane_0");custom.Remove("*trigger_brush_1_plane_0");custom["*trigger_bounds_mins"]="-2 -.5 -1";custom["*trigger_bounds_maxs"]="2 .5 1";custom["angles"]="0 90 0";
rows.Add(new{name="rotated-volume-tests-local-bounds",passed=Titanfall2.Titanfall2LightEnvironment.Contains(custom,new System.Numerics.Vector3(0,1,0))&&!Titanfall2.Titanfall2LightEnvironment.Contains(custom,new System.Numerics.Vector3(1,0,0))});
custom["*trigger_brush_0_plane_0"]="NaN 0 0 1";
rows.Add(new{name="malformed-plane-is-not-global-volume",passed=!Titanfall2.Titanfall2LightEnvironment.Contains(custom,System.Numerics.Vector3.Zero)});
var actualVolume=war.Single(e=>e.TryGetValue("classname",out var name)&&name=="light_environment_volume");var volume=new Titanfall2.Titanfall2LightEnvironment.Volume(actualVolume);var inside=new System.Numerics.Vector3(-6184,-3624,-6519);
for(var i=0;i<100;i++)volume.Contains(inside);var before=GC.GetAllocatedBytesForCurrentThread();var count=0;for(var i=0;i<10000;i++)if(volume.Contains(inside))count++;var bytes=GC.GetAllocatedBytesForCurrentThread()-before;
rows.Add(new{name="moving-camera-volume-check-allocates-nothing",bytes,passed=bytes==0&&count==10000});
var scene=new Scene();
try
{
	var root=new GameObject(scene,true,"Authored environment fixture");var sun=root.AddComponent<DirectionalLight>();var controller=root.AddComponent<Titanfall2EnvironmentController>();
	var states=new[]{new[]{0f,3,3,3,-.33544245f,.5255672f,-.7818296f,.2f,.2f,.2f},new[]{1f,.05311749f,.08345605f,.09803922f,.19303574f,-.2837238f,-.93927526f,.039215688f,.039215688f,.039215688f}};
	var flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Public;
	typeof(Titanfall2EnvironmentController).GetMethod("Configure",flags).Invoke(controller,new object[]{war,states});
	rows.Add(new{name="controller-applies-authored-default",passed=controller.CurrentEnvironmentIndex==1&&MathF.Abs(sun.LightColor.r-.05311749f)<.00001f});
	var camera=new GameObject(scene,true,"Environment selection camera").AddComponent<CameraComponent>();camera.WorldPosition=new Vector3(-6184,-3624,-6519);scene.GameTick(1.0/60);
	rows.Add(new{name="controller-enters-sky-region",current=controller.CurrentEnvironmentIndex,camera=scene.Camera?.WorldPosition.ToString(),active=controller.Active,passed=controller.CurrentEnvironmentIndex==0&&MathF.Abs(sun.LightColor.r-3)<.00001f});
	camera.WorldPosition=new Vector3(900,2700,450);scene.GameTick(1.0/60);
	rows.Add(new{name="controller-leaves-sky-region",passed=controller.CurrentEnvironmentIndex==1&&MathF.Abs(sun.SkyColor.r-.039215688f)<.00001f});
	controller.Enabled=false;scene.GameTick(1.0/60);controller.Enabled=true;scene.GameTick(1.0/60);
	rows.Add(new{name="reenable-restores-view-attributes",passed=scene.RenderAttributes.GetInt("T2EnvironmentEnabled")==1});
	var encodedEntities=controller.EncodedEntities;var encodedStates=controller.EncodedStates;root.Destroy();scene.GameTick(1.0/60);
	var restoredRoot=new GameObject(scene,true,"Recreated serialized environment");var restoredSun=restoredRoot.AddComponent<DirectionalLight>();var restored=restoredRoot.AddComponent<Titanfall2EnvironmentController>();restored.EncodedEntities=encodedEntities;restored.EncodedStates=encodedStates;typeof(Titanfall2EnvironmentController).GetMethod("Restore",flags).Invoke(restored,null);scene.GameTick(1.0/60);
	rows.Add(new{name="serialized-state-restores-after-original-destroyed",passed=restored.CurrentEnvironmentIndex==1&&MathF.Abs(restoredSun.LightColor.b-.09803922f)<.00001f});
	restored.Enabled=false;scene.GameTick(1.0/60);
	rows.Add(new{name="disable-clears-environment-override",passed=scene.RenderAttributes.GetInt("T2EnvironmentEnabled")==0});
}
finally{scene.Destroy();}
var output=System.IO.Path.Combine(exchangeDirectory,"light-environment-results.json");System.IO.File.WriteAllText(output,System.Text.Json.JsonSerializer.Serialize(rows,new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));return new{output,rows};

