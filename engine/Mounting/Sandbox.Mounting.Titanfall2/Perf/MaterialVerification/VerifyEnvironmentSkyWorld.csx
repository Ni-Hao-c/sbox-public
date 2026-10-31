var scene=new Scene();var rows=new System.Collections.Generic.List<object>();SceneObject obj=null;
using var target=Texture.CreateRenderTarget().WithSize(128,128).Create();
using var direction=Texture.Create(1,1).WithData(new byte[]{0,127,127,255}).Finish();
float Linear(float x)=>x<=.04045f?x/12.92f:MathF.Pow((x+.055f)/1.055f,2.4f);
try
{
	var camera=new GameObject(scene,true,"Environment world GPU camera").AddComponent<CameraComponent>();camera.WorldTransform=Transform.Zero;camera.EnablePostProcessing=false;camera.ZNear=1;camera.ZFar=1000;camera.BackgroundColor=Color.Black;
	var material=Material.Create("environment-world-sky","shaders/titanfall2_world_lightmapped.shader");
	foreach(var slot in new[]{"g_tAlbedo","g_tAO"})material.Set(slot,Texture.White);
	foreach(var slot in new[]{"g_tGloss","g_tSpecular","g_tEmissive","g_tLightmapSkyA"})material.Set(slot,Texture.Black);
	material.Set("g_tLightmapSkyB",direction);material.Set("g_flHasNormalMap",0f);material.Set("g_flT2SpecularWorkflow",1f);material.Set("g_vT2LightmapSkyColor",new Vector3(.02f,.03f,.04f));
	var vertices=new Vertex[4];for(var i=0;i<4;i++){vertices[i].Position=new Vector3(128,i==1||i==2?80:-80,i>=2?80:-80);vertices[i].Normal=Vector3.Backward;vertices[i].Color=Color.White;vertices[i].TexCoord1=new Vector4(.5f,.5f,0,0);}
	var mesh=new Mesh("environment-world-sky",material);mesh.CreateVertexBuffer(4,vertices);mesh.CreateIndexBuffer(6,new[]{0,2,1,0,3,2});obj=new SceneObject(scene.SceneWorld,Model.Builder.WithName("environment-world-sky").AddMesh(mesh).Create());
	foreach(var (name,enabled,color,expected) in new[]{("fallback",0,Vector3.One,new Vector3(.02f,.03f,.04f)),("red",1,new Vector3(.1f,0,0),new Vector3(.1f,0,0)),("blue",1,new Vector3(0,0,.2f),new Vector3(0,0,.2f)),("black",1,Vector3.Zero,Vector3.Zero)})
	{
		scene.RenderAttributes.Set("T2EnvironmentEnabled",enabled);scene.RenderAttributes.Set("T2EnvironmentSkyColor",color);
		for(var f=0;f<4;f++){await Task.Delay(35,cancellationToken);camera.RenderToTexture(target);}
		using var bitmap=target.GetBitmap();var p=bitmap.GetPixel(64,64);var actual=new Vector3(Linear(p.r),Linear(p.g),Linear(p.b));rows.Add(new{name,actual=actual.ToString(),expected=expected.ToString(),passed=actual.Distance(expected)<.004f});
	}
	var output=System.IO.Path.Combine(exchangeDirectory,"environment-sky-world-results.json");System.IO.File.WriteAllText(output,System.Text.Json.JsonSerializer.Serialize(rows,new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));return new{output,rows};
}
finally{obj?.Delete();scene.Destroy();}
