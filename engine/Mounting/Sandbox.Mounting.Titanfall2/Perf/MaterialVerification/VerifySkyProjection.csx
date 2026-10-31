var scene=new Scene();var rows=new System.Collections.Generic.List<object>();SceneObject obj=null;using var target=Texture.CreateRenderTarget().WithSize(128,128).Create();
try{var camera=new GameObject(scene,true,"Sky projection regression").AddComponent<CameraComponent>();camera.WorldTransform=Transform.Zero;camera.EnablePostProcessing=false;camera.ZNear=10;camera.ZFar=1000;camera.BackgroundColor=Color.Black;
foreach(var shader in new[]{"vista","vista_cutout","vista_additive","vista_translucent"})foreach(var fixedProjection in new[]{false,true}){
 var material=Material.Create("sky-projection-"+shader+fixedProjection,"shaders/titanfall2_"+shader+".shader");material.Set("g_tAlbedo",Texture.White);material.Set("g_tOpacity",Texture.White);material.Set("g_vT2AlbedoTint",new Vector4(.25f,.25f,.25f,1));
 var vertices=new Vertex[4];for(int i=0;i<4;i++){vertices[i].Position=new Vector3(2,i==1||i==2?1:-1,i>=2?1:-1);vertices[i].Normal=Vector3.Backward;vertices[i].Color=Color.White;}
 var mesh=new Mesh("tiny-sky-card",material);mesh.CreateVertexBuffer(4,vertices);mesh.CreateIndexBuffer(6,new[]{0,2,1,0,3,2});obj=new SceneObject(scene.SceneWorld,Model.Builder.AddMesh(mesh).Create());obj.Attributes.Set("T2SkyProjectionNear",fixedProjection?.001f:0f);
 for(int f=0;f<3;f++){await Task.Delay(30,cancellationToken);camera.RenderToTexture(target);}using var bitmap=target.GetBitmap();var p=bitmap.GetPixel(64,64);rows.Add(new{shader,fixedProjection,pixel=p.ToString(),passed=fixedProjection?p.r>.3f:p.r<.01f});obj.Delete();obj=null;
}var output=System.IO.Path.Combine(exchangeDirectory,"sky-projection-results.json");System.IO.File.WriteAllText(output,System.Text.Json.JsonSerializer.Serialize(rows));return new{output,rows};}finally{obj?.Delete();scene.Destroy();}

