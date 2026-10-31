// Independently verify cavity's specular-only meaning in both material paths.
var scene=new Scene();var rows=new System.Collections.Generic.List<object>();SceneObject obj=null;
using var target=Texture.CreateRenderTarget().WithSize(128,128).Create();
using var half=Texture.Create(1,1).WithData(new byte[]{128,128,128,255}).Finish();
var cubeData=new byte[4*4*6*8];for(var i=0;i<4*4*6;i++)for(var c=0;c<4;c++)System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(cubeData.AsSpan(i*8+c*2,2),BitConverter.HalfToUInt16Bits((Half)(c==3?1f:.5f)));
using var cube=Texture.CreateCube(4,4,ImageFormat.RGBA16161616F).WithData(cubeData).Finish();
float Linear(float x)=>x<=.04045f?x/12.92f:MathF.Pow((x+.055f)/1.055f,2.4f);
try
{
	var camera=new GameObject(scene,true,"Cavity GPU camera").AddComponent<CameraComponent>();camera.WorldTransform=Transform.Zero;camera.EnablePostProcessing=false;camera.ZNear=1;camera.ZFar=1000;camera.BackgroundColor=Color.Black;
	var sun=new GameObject(scene,true,"Cavity direct light").AddComponent<DirectionalLight>();sun.WorldRotation=Rotation.Identity;sun.LightColor=new Color(.2f);sun.SkyColor=Color.Black;sun.Shadows=false;
	var probeRoot=new GameObject(scene,false,"Cavity reflection fixture");probeRoot.WorldPosition=new Vector3(128,0,0);var probe=probeRoot.AddComponent<EnvmapProbe>();probe.Mode=EnvmapProbe.EnvmapProbeMode.CustomTexture;probe.Texture=cube;probe.Bounds=new BBox(new Vector3(-4096),new Vector3(4096));probe.Feathering=64;probe.Projection=SceneCubemap.ProjectionMode.Sphere;
	foreach(var shader in new[]{"default","world_lightmapped"})
	{
		probeRoot.Enabled=false;sun.SkyColor=Color.Black;
		var material=Material.Create("cavity-fixture-"+shader,"shaders/titanfall2_"+shader+".shader");
		foreach(var slot in new[]{"g_tAlbedo","g_tSpecular","g_tAO","g_tOpacity","g_tGloss","g_tEmissive"})material.Set(slot,Texture.White);
		foreach(var slot in new[]{"g_tLightmapSkyA","g_tLightmapSkyB"})material.Set(slot,Texture.Black);
		material.Set("g_flHasNormalMap",0f);material.Set("g_flT2SpecularWorkflow",1f);material.Set("g_flT2GlossScale",.5f);material.Set("g_flT2EmissiveStrength",1f);
		material.Set("g_vT2AlbedoTint",new Vector4(.1f,.2f,.3f,1));material.Set("g_vT2EmissiveTint",new Vector4(.01f,.02f,.03f,1));material.Set("g_vT2LightmapSkyColor",Vector3.Zero);
		var vertices=new Vertex[4];for(var i=0;i<4;i++){vertices[i].Position=new Vector3(128,i==1||i==2?80:-80,i>=2?80:-80);vertices[i].Normal=Vector3.Backward;vertices[i].Color=Color.White;vertices[i].TexCoord1=new Vector4(.5f,.5f,0,0);}
		var mesh=new Mesh("cavity-fixture",material);mesh.CreateVertexBuffer(4,vertices);mesh.CreateIndexBuffer(6,new[]{0,2,1,0,3,2});obj=new SceneObject(scene.SceneWorld,Model.Builder.WithName("cavity-fixture").AddMesh(mesh).Create());obj.Flags.CastShadows=false;
		var samples=new System.Collections.Generic.Dictionary<string,Vector3>();
		foreach(var test in new[]{"white","black","half","missing","diffuse-white","diffuse-black","emission-white","emission-black"})
		{
			material.Set("g_flT2HasCavity",test=="missing"?0f:1f);material.Set("g_tCavity",test=="half"?half:test.Contains("black")||test=="missing"?Texture.Black:Texture.White);
			material.Set("g_vT2SpecularTint",test.StartsWith("diffuse")?new Vector4(0,0,0,1):new Vector4(.2f,.2f,.2f,1));sun.LightColor=test.StartsWith("emission")?Color.Black:new Color(.2f);
			for(var f=0;f<4;f++){await Task.Delay(35,cancellationToken);camera.RenderToTexture(target);}
			using var bitmap=target.GetBitmap();var p=bitmap.GetPixel(64,64);samples[test]=new Vector3(Linear(p.r),Linear(p.g),Linear(p.b));
		}
		var white=samples["white"];var black=samples["black"];var expectedHalf=black+(white-black)*(128f/255);
		rows.Add(new{shader,name="black-cavity-removes-direct-reflection",white=white.ToString(),black=black.ToString(),passed=white.x-black.x>.01f&&black.x>.005f});
		rows.Add(new{shader,name="half-cavity-linearly-modulates-reflection",actual=samples["half"].ToString(),expected=expectedHalf.ToString(),passed=samples["half"].Distance(expectedHalf)<.006f});
		rows.Add(new{shader,name="missing-cavity-is-neutral",passed=samples["missing"].Distance(white)<.003f});
		rows.Add(new{shader,name="diffuse-unaffected",passed=samples["diffuse-white"].Distance(samples["diffuse-black"])<.003f});
		rows.Add(new{shader,name="emission-unaffected",passed=samples["emission-white"].Distance(samples["emission-black"])<.003f&&samples["emission-white"].z>.02f});
		probeRoot.Enabled=true;sun.LightColor=Color.Black;sun.SkyColor=new Color(.1f);
		foreach(var test in new[]{"indirect-white","indirect-black","indirect-half"})
		{
			material.Set("g_flT2HasCavity",1f);material.Set("g_tCavity",test.EndsWith("black")?Texture.Black:test.EndsWith("half")?half:Texture.White);
			for(var f=0;f<6;f++){await Task.Delay(35,cancellationToken);camera.RenderToTexture(target);}
			using var bitmap=target.GetBitmap();var p=bitmap.GetPixel(64,64);samples[test]=new Vector3(Linear(p.r),Linear(p.g),Linear(p.b));
		}
		var indirectWhite=samples["indirect-white"];var indirectBlack=samples["indirect-black"];
		rows.Add(new{shader,name="cavity-modulates-environment-reflection",white=indirectWhite.ToString(),black=indirectBlack.ToString(),passed=indirectWhite.Distance(indirectBlack)>.003f&&samples["indirect-half"].Distance(indirectBlack+(indirectWhite-indirectBlack)*(128f/255))<.006f});
		obj.Delete();obj=null;
	}
	var flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.NonPublic;
	var host=Sandbox.Mounting.Directory.Get("titanfall2");var actualMaterial=Material.Load("mount://titanfall2/models/vehicles_r2/aircraft/goblin_dropship/goblin_dropship_1_big.vmat");
	var descriptors=(System.Collections.IDictionary)host.GetType().GetField("_materialDescriptors",flags).GetValue(host);var descriptor=descriptors["models/vehicles_r2/aircraft/goblin_dropship/goblin_dropship_1_big"];
	var bindings=((System.Collections.IEnumerable)descriptor.GetType().GetProperty("TextureBindings").GetValue(descriptor)).Cast<object>().ToArray();string Semantic(object b)=>b.GetType().GetProperty("Semantic").GetValue(b).ToString();
	rows.Add(new{name="real-goblin-ao-and-cavity-stay-distinct",passed=bindings.Count(b=>Semantic(b)=="AmbientOcclusion")==1&&bindings.Count(b=>Semantic(b)=="Cavity")==1});
	var ao=actualMaterial.GetTexture("g_tAO");var cavity=actualMaterial.GetTexture("g_tCavity");rows.Add(new{name="real-goblin-binds-two-valid-textures",passed=ao is not null&&cavity is not null&&ao.IsValid&&cavity.IsValid&&!ReferenceEquals(ao,cavity)&&actualMaterial.GetVector4("g_flT2HasCavity").x==1});
	var output=System.IO.Path.Combine(exchangeDirectory,"cavity-results.json");System.IO.File.WriteAllText(output,System.Text.Json.JsonSerializer.Serialize(rows,new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));return new{output,rows};
}
finally{obj?.Delete();scene.Destroy();}

