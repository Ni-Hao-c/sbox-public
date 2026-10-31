// GPU evidence for native six-face volume ordering and linear sampling.
var scene=new Scene();var objects=new System.Collections.Generic.List<SceneObject>();
using var target=Texture.CreateRenderTarget().WithSize(256,256).Create();
var colours=new[]{new Vector3(.1f,0,0),new Vector3(0,.1f,0),new Vector3(0,0,.1f),new Vector3(.1f,.1f,0),new Vector3(.1f,0,.1f),new Vector3(0,.1f,.1f)};
var data=new byte[2*2*12*8];for(int face=0;face<6;face++)for(int cell=0;cell<8;cell++){var values=new[]{colours[face].x,colours[face].y,colours[face].z,1f};for(int ch=0;ch<4;ch++)System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan((face*8+cell)*8+ch*2,2),BitConverter.HalfToUInt16Bits((Half)values[ch]));}
using var texture=Texture.CreateVolume(2,2,12).WithFormat(ImageFormat.RGBA16161616F).WithData(data).Finish();
try{
 var volume=new GameObject(scene,true,"Native probe diagnostic").AddComponent<LightProbeVolume>();volume.Bounds=new BBox(new Vector3(-1024),new Vector3(1024));volume.Texture=texture;
 var camera=new GameObject(scene,true,"Probe camera").AddComponent<CameraComponent>();camera.WorldTransform=Transform.Zero;camera.EnablePostProcessing=false;camera.ZNear=1;camera.ZFar=2000;camera.BackgroundColor=Color.Black;
 var material=Material.Create("t2-probe-volume-test","shaders/tests/titanfall2_probe_volume.shader");
 var vertices=new Vertex[4];for(int i=0;i<4;i++){vertices[i].Position=new Vector3(128,i==1||i==2?64:-64,i>=2?64:-64);vertices[i].Normal=new Vector3(-1,0,0);vertices[i].Color=Color.White;}
 var mesh=new Mesh("native-probe-test",material);mesh.CreateVertexBuffer(4,vertices);mesh.CreateIndexBuffer(6,new[]{0,2,1,0,3,2});var obj=new SceneObject(scene.SceneWorld,Model.Builder.WithName("native-probe-test").AddMesh(mesh).Create());obj.Flags.NeedsLightProbe=true;objects.Add(obj);
 var rows=new System.Collections.Generic.List<object>();float Linear(float v)=>v<=.04045f?v/12.92f:MathF.Pow((v+.055f)/1.055f,2.4f);
 foreach(var normal in new[]{Vector3.Forward,Vector3.Backward,Vector3.Left,Vector3.Right,Vector3.Up,Vector3.Down,new Vector3(1,1,1).Normal}){obj.Attributes.Set("T2ProbeNormal",normal);for(int f=0;f<4;f++){await Task.Delay(40,cancellationToken);camera.RenderToTexture(target);}using var bitmap=target.GetBitmap();var pixel=bitmap.GetPixel(128,128);var actual=new Vector3(Linear(pixel.r),Linear(pixel.g),Linear(pixel.b));var expected=colours[normal.x>=0?0:3]*(normal.x*normal.x)+colours[normal.y>=0?1:4]*(normal.y*normal.y)+colours[normal.z>=0?2:5]*(normal.z*normal.z);rows.Add(new{normal=normal.ToString(),rgb=actual.ToString(),expected=expected.ToString(),passed=actual.Distance(expected)<.004f});}
 var output=System.IO.Path.Combine(exchangeDirectory,"native-volume-results.json");System.IO.File.WriteAllText(output,System.Text.Json.JsonSerializer.Serialize(rows,new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));return new{output,rows};
}finally{foreach(var obj in objects)obj.Delete();scene.Destroy();}


