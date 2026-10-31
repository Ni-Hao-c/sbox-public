// Read the native prepass target, rather than invoking the material's forward path.
var scene=new Scene();var rows=new System.Collections.Generic.List<object>();SceneObject obj=null;
using var target=Texture.CreateRenderTarget().WithSize(128,128).Create();
float Linear(float x)=>x<=.04045f?x/12.92f:MathF.Pow((x+.055f)/1.055f,2.4f);
try
{
	var camera=new GameObject(scene,true,"Depth normals camera").AddComponent<CameraComponent>();camera.WorldTransform=Transform.Zero;camera.EnablePostProcessing=true;camera.ZNear=1;camera.ZFar=1000;
	scene.RenderAttributes.Set("NeedsDepthNormals",true);
	var command=new Sandbox.Rendering.CommandList("Read actual normals G-buffer");command.Attributes.SetValue("D_MSAA_NORMALS",Sandbox.Rendering.RenderValue.MsaaCombo);command.Blit(Material.FromShader("shaders/tests/titanfall2_gbuffer_read.shader"));camera.AddCommandList(command,Sandbox.Rendering.Stage.AfterPostProcess);
	var fog=new GameObject(scene,true,"Strong red fog").AddComponent<Titanfall2HeightFog>();
	var entity=new System.Collections.Generic.Dictionary<string,string>{{"fogdensity","1"},{"foghalfdisttop","10"},{"foghalfdistbottom","10"},{"fogdistoffset","0"},{"fogztop","1000"},{"fogzbottom","0"},{"fogcolor","255 0 0"},{"fogcolorstrength","1"}};
	typeof(Titanfall2HeightFog).GetMethod("Configure",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).Invoke(fog,new object[]{entity});
	async Task<Vector4> Render()
	{
		for(var f=0;f<4;f++){scene.GameTick(1.0/60);await Task.Delay(40,cancellationToken);camera.RenderToTexture(target,new Sandbox.Rendering.ViewSetup{EnablePostprocessing=true,EnableUI=false});}
		using var bitmap=target.GetBitmap();var p=bitmap.GetPixel(64,64);return new Vector4(Linear(p.r),Linear(p.g),Linear(p.b),p.a);
	}
	var empty=await Render();
	foreach(var shader in new[]{"default","cutout","world_lightmapped","vista","vista_cutout","decal","vmt_unlit_two_texture_opaque","vmt_unlit_two_texture_cube"})
	{
		var material=Material.Create("native-depth-fixture-"+shader,"shaders/titanfall2_"+shader+".shader");
		foreach(var slot in new[]{"g_tAlbedo","g_tOpacity","g_tAO","g_tGloss","g_tEmissive"})material.Set(slot,Texture.White);
		foreach(var slot in new[]{"g_tSpecular","g_tLightmapSkyA","g_tLightmapSkyB"})material.Set(slot,Texture.Black);
		material.Set("g_flHasNormalMap",0f);material.Set("g_flT2SpecularWorkflow",1f);material.Set("g_flT2GlossScale",.5f);material.Set("g_vT2AlbedoTint",new Vector4(.2f,0,0,1));material.Set("g_vT2EmissiveTint",new Vector4(2,.1f,0,1));
		var vertices=new Vertex[4];for(var i=0;i<4;i++){vertices[i].Position=new Vector3(128,i==1||i==2?80:-80,i>=2?80:-80);vertices[i].Normal=Vector3.Backward;vertices[i].Color=Color.White;vertices[i].TexCoord1=new Vector4(.5f,.5f,0,0);}
		var mesh=new Mesh("depth-fixture",material);mesh.CreateVertexBuffer(4,vertices);mesh.CreateIndexBuffer(6,new[]{0,2,1,0,3,2});obj=new SceneObject(scene.SceneWorld,Model.Builder.WithName("depth-fixture").AddMesh(mesh).Create());obj.Flags.CastShadows=false;
		foreach(var unlit in new[]{false,true})foreach(var useFog in new[]{false,true})
		{
			material.Set("g_flT2Unlit",unlit?1f:0f);fog.Enabled=useFog;var actual=await Render();var rgb=new Vector3(actual.x,actual.y,actual.z);
			var expectedAlpha=shader=="default"||shader=="world_lightmapped"?.5f:1f;
			rows.Add(new{shader,unlit,useFog,actual=actual.ToString(),passed=rgb.Distance(new Vector3(0,.5f,.5f))<.015f&&MathF.Abs(actual.w-expectedAlpha)<.01f});
		}
		var rotation=new Angles(0,20,0).ToRotation();obj.Transform=new Transform(Vector3.Zero,rotation);
		material.Set("g_flT2SpecularWorkflow",0f);material.Set("g_flT2GlossScale",.25f);
		var tilted=await Render();var expectedNormal=(rotation*Vector3.Backward+Vector3.One)*.5f;
		var expectedRoughness=shader=="default"||shader=="world_lightmapped"?.75f:1f;
		rows.Add(new{shader,name="tilted-normal-and-roughness",actual=tilted.ToString(),expected=expectedNormal.ToString(),passed=new Vector3(tilted.x,tilted.y,tilted.z).Distance(expectedNormal)<.015f&&MathF.Abs(tilted.w-expectedRoughness)<.01f});
		if(shader.Contains("cutout")||shader=="decal"){material.Set("g_tOpacity",Texture.Black);var clipped=await Render();rows.Add(new{shader,name="alpha-clip-preserved",actual=clipped.ToString(),passed=new Vector3(clipped.x,clipped.y,clipped.z).Distance(new Vector3(empty.x,empty.y,empty.z))<.01f});}
		obj.Delete();obj=null;
	}
	var output=System.IO.Path.Combine(exchangeDirectory,"depth-normals-results.json");System.IO.File.WriteAllText(output,System.Text.Json.JsonSerializer.Serialize(rows,new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));return new{output,rows};
}
finally{obj?.Delete();scene.Destroy();}

