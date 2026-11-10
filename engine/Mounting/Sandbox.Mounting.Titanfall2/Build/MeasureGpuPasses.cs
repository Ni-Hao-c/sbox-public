// Read-only scene capture for sbox-cli run_csharp; output defaults to its exchange directory.
// Samples the current camera and settings; only temporary profiler state and GPU markers change.
var root=Environment.GetEnvironmentVariable("T2_GPU_PASS_EVIDENCE") ?? exchangeDirectory;
System.IO.Directory.CreateDirectory(root);
var scene=Game.ActiveScene;
var props=scene.GetAllComponents<Titanfall2StaticPropStreamer>().Single();
if(!props.PreparationComplete || !props.ResidentCollisionsReady)throw new Exception("Map is not ready.");
var instanceFlags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.NonPublic;
var staticFlags=System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.NonPublic;
var gpu=(SceneObject)props.GetType().GetField("_gpuScene",instanceFlags).GetValue(props);
var gate=gpu.GetType().GetField("_renderGate",instanceFlags).GetValue(gpu);
var vertices=(GpuBuffer)gpu.GetType().GetField("_vertices",instanceFlags).GetValue(gpu);
var indices=(GpuBuffer)gpu.GetType().GetField("_indices",instanceFlags).GetValue(gpu);
var groups=((Array)gpu.GetType().GetField("_groups",instanceFlags).GetValue(gpu)).Cast<object>().ToArray();
var pool=gpu.GetType().GetField("_outputs",instanceFlags).GetValue(gpu);
var slots=((Array)pool.GetType().GetField("_slots",instanceFlags).GetValue(pool)).Cast<object>()
    .Concat(((Array)gpu.GetType().GetField("_conservativeOutputs",instanceFlags).GetValue(gpu)).Cast<object>()).ToArray();
var indirect=typeof(Sandbox.Rendering.CommandList).GetMethods().Single(m=>m.Name=="DrawIndexedInstancedIndirect" && m.IsGenericMethodDefinition && m.GetParameters().Length==9)
    .MakeGenericMethod(vertices.GetType().GetGenericArguments()[0]);
var samplers=groups.Select(g=>new Sandbox.Rendering.ProfilingSampler(((Material)g.GetType().GetProperty("Material").GetValue(g)).Shader.ResourcePath.Contains("cutout")?"T2 cutout group":"T2 opaque group")).ToArray();
void ConfigureGroups(bool instrument)
{
    lock(gate)
    {
        foreach(var output in slots)
        {
            var command=(Sandbox.Rendering.CommandList)output.GetType().GetField("Commands",instanceFlags).GetValue(output);
            var arguments=(GpuBuffer)output.GetType().GetField("Arguments",instanceFlags).GetValue(output);
            command.Reset();
            for(var n=0;n<groups.Length;++n)
            {
                var group=groups[n];var gt=group.GetType();
                command.Attributes.SetCombo("D_T2_MATERIAL_TABLE",(bool)gt.GetProperty("Table").GetValue(group)?1:0);
                command.Attributes.SetCombo("D_T2_SIMPLE_SURFACE",(bool)gt.GetProperty("SimpleSurface").GetValue(group)?1:0);
                var marker=instrument?command.ProfileScope(samplers[n]):default;
                try { indirect.Invoke(command,new object[]{vertices,indices,gt.GetProperty("Material").GetValue(group),arguments,
                    (uint)gt.GetProperty("FirstDraw").GetValue(group),null,Graphics.PrimitiveType.Triangles,(uint)gt.GetProperty("DrawCount").GetValue(group),20u}); }
                finally {marker.Dispose();}
            }
        }
    }
}
var camera=scene.Camera;
var dev=typeof(Scene).Assembly.GetType("Sandbox.Diagnostics.DevTools",true);
var collectors=(System.Collections.Generic.Dictionary<string,double>)dev.GetField("_collectUntil",staticFlags).GetValue(null);
var hadCollector=collectors.TryGetValue("rendering",out var previousUntil);
var profiler=Sandbox.Diagnostics.GpuProfilerStats.Enabled;
var settingsType=typeof(Scene).Assembly.GetType("Sandbox.Engine.Settings.RenderSettings",true);
var settings=(Sandbox.Engine.Settings.RenderSettings)settingsType.GetField("Instance",staticFlags).GetValue(null);
var history=dev.GetProperty("Frames",staticFlags).GetValue(null);
var ht=history.GetType();
var number=ht.GetMethod("Number");
var pt=typeof(Sandbox.Diagnostics.GpuProfilerStats);
var gpuFrame=pt.GetProperty("FrameId",staticFlags);
var rowCount=pt.GetProperty("RowCount",staticFlags);
var getRow=pt.GetMethod("GetRow",staticFlags);
var jsonOptions=new System.Text.Json.JsonSerializerOptions{IncludeFields=true};
var results=new System.Collections.Generic.List<object>();
try
{
    ConfigureGroups(true);
    dev.GetMethod("CollectFor",staticFlags).Invoke(null,new object[]{"rendering",60f});
    Sandbox.Diagnostics.GpuProfilerStats.Enabled=true;
    var categories=(Array)dev.GetProperty("Categories",staticFlags).GetValue(null);
    var names=categories.Cast<object>().Select(x=>(string)x.GetType().GetProperty("Name").GetValue(x)).ToArray();
    var renderIndex=Array.IndexOf(names,"Render");var physicsIndex=Array.IndexOf(names,"Physics");
    var view=0;var mode="capture";
    await Task.Delay(3000,cancellationToken);
    var start=(long)number.Invoke(history,new object[]{0});
    var timelines=new System.Collections.Generic.List<System.Text.Json.JsonElement>();
    var observed=new System.Collections.Generic.List<object>();
    var seen=new System.Collections.Generic.HashSet<string>();
    var allFocused=true;
    var captureDeadline=DateTime.UtcNow.AddSeconds(20);
    while(timelines.Count<20 || (long)number.Invoke(history,new object[]{0})-start<260)
    {
        await Task.Delay(100,cancellationToken);
        allFocused &= Application.IsFocused;
        if(DateTime.UtcNow>captureDeadline)throw new Exception("No completed GPU timings within capture deadline.");
        var id=Convert.ToString(gpuFrame.GetValue(null));
        if(id=="0" || !seen.Add(id))continue;
        var count=(int)rowCount.GetValue(null);
        if(count==0)continue;
        var passRows=Enumerable.Range(0,count).Select(n=>getRow.Invoke(null,new object[]{n})).ToArray();
        timelines.Add(System.Text.Json.JsonSerializer.SerializeToElement(new{FrameId=id,Rows=passRows},jsonOptions));
        var f=Sandbox.Diagnostics.FrameStats.Current;
        observed.Add(new{Frame=Application.FrameCount,Focused=Application.IsFocused,f.DrawCalls,f.TrianglesRendered,f.MaterialChanges,
            f.AggregateIndirectSubmits,f.AggregateIndirectFragments,Gpu=Sandbox.Diagnostics.PerformanceStats.GpuFrametime});
        if(timelines.Count>100)throw new Exception("Frame history did not advance.");
    }
    var snapshot=ht.GetMethod("Clone").Invoke(history,null);
    var rows=new System.Collections.Generic.List<double[]>();
    for(var age=239;age>=0;--age)
    {
        double Read(string m)=>Convert.ToDouble(ht.GetMethod(m).Invoke(snapshot,new object[]{age}));
        double Category(int n)=>Convert.ToDouble(ht.GetMethod("CategoryMs").Invoke(snapshot,new object[]{age,n}));
        rows.Add(new double[]{Read("Number"),Read("FrameMs"),Read("GpuMs"),Read("IdleMs"),Category(renderIndex),Category(physicsIndex)});
    }
    var item=new{View=view,Mode=mode,Position=camera.WorldPosition.ToString(),Rotation=camera.WorldRotation.ToString(),camera.FieldOfView,
        Focus=allFocused,settings.RenderScale,settings.VSync,settings.MaxFrameRate,settings.MaxFrameRateInactive,Timelines=timelines,Observed=observed,ConsecutiveSamples=rows};
    results.Add(item);
    System.IO.File.WriteAllText(root+"/passes.json",System.Text.Json.JsonSerializer.Serialize(results));
    System.IO.File.WriteAllText(root+"/progress.txt",$"view={view},mode={mode}");
    return new{Segments=results.Count};
}
finally
{
    try { ConfigureGroups(false); }
    finally
    {
        if(hadCollector)collectors["rendering"]=previousUntil;else collectors.Remove("rendering");
        Sandbox.Diagnostics.GpuProfilerStats.Enabled=profiler;
        System.IO.File.WriteAllText(root+"/restored.json",System.Text.Json.JsonSerializer.Serialize(new{
            Shadows=gpu.Flags.CastShadows,Prepass=gpu.Flags.WantsPrePass,settings.RenderScale,
            CameraEnabled=camera.Enabled,Profiler=Sandbox.Diagnostics.GpuProfilerStats.Enabled}));
    }
}
