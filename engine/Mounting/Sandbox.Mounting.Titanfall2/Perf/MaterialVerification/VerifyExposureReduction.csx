var rows=new System.Collections.Generic.List<object>();
var shader=new ComputeShader("shaders/titanfall2_exposure_cs.shader");
var pixels=new float[7*3*4];var expected=0f;
for(var i=0;i<21;i++){
var r=.01f+i*.1f;var g=.02f+i*.04f;var b=.03f+i*.2f;
pixels[i*4]=r;pixels[i*4+1]=g;pixels[i*4+2]=b;pixels[i*4+3]=1;
expected+=MathF.Log2(.299f*r+.587f*g+.114f*b);
}
var bytes=System.Runtime.InteropServices.MemoryMarshal.AsBytes(pixels.AsSpan()).ToArray();
using var input=Texture.Create(7,3,ImageFormat.RGBA32323232F).WithData(bytes).Finish();
using var first=Texture.Create(2,1,ImageFormat.RGBA32323232F).WithUAVBinding().Finish();
using var final=Texture.Create(1,1,ImageFormat.RGBA32323232F).WithUAVBinding().Finish();
shader.Attributes.SetCombo("D_STAGE",0);shader.Attributes.Set("T2MeterInput",input);shader.Attributes.Set("T2MeterOutput",first);shader.Dispatch(2,1,1);
var data=new Vector4[2];first.GetPixels<Vector4>((0,0,2,1),0,0,data,ImageFormat.RGBA32323232F);
rows.Add(new{name="odd-view-partial-tiles-retain-pixels",counts=data.Select(p=>p.y).ToArray(),passed=data[0].y==9&&data[1].y==12});
shader.Attributes.SetCombo("D_STAGE",1);shader.Attributes.Set("T2MeterInput",first);shader.Attributes.Set("T2MeterOutput",final);shader.Dispatch(1,1,1);
final.GetPixels<Vector4>((0,0,1,1),0,0,data,ImageFormat.RGBA32323232F);
rows.Add(new{name="weighted-logarithmic-reduction",actual=data[0].x,expected,passed=MathF.Abs(data[0].x-expected)<.0001f&&data[0].y==21});
rows.Add(new{name="geometric-mean-luminance",actual=MathF.Pow(2,data[0].x/data[0].y),expected=MathF.Pow(2,expected/21),passed=MathF.Abs(MathF.Pow(2,data[0].x/data[0].y)-MathF.Pow(2,expected/21))<.0001f});
var output=System.IO.Path.Combine(exchangeDirectory,"exposure-reduction-results.json");System.IO.File.WriteAllText(output,System.Text.Json.JsonSerializer.Serialize(rows,new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));return new{output,rows};
