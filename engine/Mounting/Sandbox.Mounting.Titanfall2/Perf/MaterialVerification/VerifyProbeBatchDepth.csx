// Verify the actual native depth/normal target for the instanced probe path.
var scene = new Scene();
Titanfall2ProbeInstanceBatch batch = null;
var rows = new System.Collections.Generic.List<object>();
using var target = Texture.CreateRenderTarget().WithSize(384, 384).Create();
float Linear(float x) => x <= .04045f ? x / 12.92f : MathF.Pow((x + .055f) / 1.055f, 2.4f);
try
{
    var camera = new GameObject(scene, true, "Instanced depth camera").Components.Create<CameraComponent>();
    camera.FieldOfView = 90;
    camera.ZNear = 1;
    camera.ZFar = 2000;
    camera.EnablePostProcessing = true;
    scene.RenderAttributes.Set("NeedsDepthNormals", true);
    var command = new Sandbox.Rendering.CommandList("Read instanced native normals");
    command.Attributes.SetValue("D_MSAA_NORMALS", Sandbox.Rendering.RenderValue.MsaaCombo);
    command.Blit(Material.FromShader("shaders/tests/titanfall2_gbuffer_read.shader"));
    camera.AddCommandList(command, Sandbox.Rendering.Stage.AfterPostProcess);
    var transforms = new[]
    {
        new Transform(new Vector3(256, -80, 0), Rotation.FromYaw(-15)),
        new Transform(new Vector3(256, 0, 0), Rotation.Identity),
        new Transform(new Vector3(256, 80, 0), Rotation.FromYaw(15))
    };
    async Task<Vector4[]> Render()
    {
        for (var frame = 0; frame < 4; ++frame)
        {
            scene.GameTick(1.0 / 60);
            await Task.Delay(40, cancellationToken);
            camera.RenderToTexture(target, new Sandbox.Rendering.ViewSetup { EnablePostprocessing = true, EnableUI = false });
        }
        using var bitmap = target.GetBitmap();
        return transforms.Select(t =>
        {
            var p = bitmap.GetPixel((int)((.5f - t.Position.y / 512f) * 384), 192);
            return new Vector4(Linear(p.r), Linear(p.g), Linear(p.b), p.a);
        }).ToArray();
    }
    var empty = await Render();
    foreach (var shader in new[] { "default", "cutout" })
    {
        var material = Material.Create("probe-depth-" + shader, "shaders/titanfall2_" + shader + ".shader");
        foreach (var slot in new[] { "g_tAlbedo", "g_tOpacity", "g_tAO", "g_tGloss", "g_tEmissive" }) material.Set(slot, Texture.White);
        material.Set("g_tSpecular", Texture.Black);
        material.Set("g_flHasNormalMap", 0f);
        material.Set("g_flT2GlossScale", .5f);
        material.Set("g_flT2SpecularWorkflow", 1f);
        material.Set("g_vT2EmissiveTint", new Vector4(4, 0, 0, 1));
        var vertices = new Vertex[4];
        for (var i = 0; i < 4; ++i)
        {
            vertices[i].Position = new Vector3(0, i == 1 || i == 2 ? 24 : -24, i >= 2 ? 24 : -24);
            vertices[i].Normal = Vector3.Backward;
            vertices[i].Color = Color.White;
        }
        var mesh = new Mesh("probe-depth", material);
        mesh.CreateVertexBuffer(4, vertices);
        mesh.CreateIndexBuffer(6, new[] { 0, 2, 1, 0, 3, 2 });
        var model = Model.Builder.WithName("probe-depth-" + shader).AddMesh(mesh).Create();
        var probes = Enumerable.Range(0, 3).Select(i => new Titanfall2ProbeData
        {
            Red = new Vector4(0, 0, 0, i == 0 ? 2 : 0),
            Green = new Vector4(0, 0, 0, i == 1 ? 2 : 0),
            Blue = new Vector4(0, 0, 0, i == 2 ? 2 : 0),
            Metadata = new Vector4(1, 0, 0, 0)
        }).ToArray();
        batch = new Titanfall2ProbeInstanceBatch(scene.SceneWorld, model, transforms, probes, Color.Black,
            new Sandbox.Mounting.PerfKit.MountedInstanceBatchOptions { CastShadows = false });
        foreach (var skip in new[] { false, true })
        {
            batch.SkipDepthProbeBindings = skip;
            material.Set("g_tOpacity", Texture.White);
            var actual = await Render();
            for (var i = 0; i < actual.Length; ++i)
            {
                var expected = (transforms[i].Rotation * Vector3.Backward + Vector3.One) * .5f;
                rows.Add(new { shader, skip, instance = i, test = "normal-and-roughness", actual = actual[i].ToString(),
                    passed = new Vector3(actual[i].x, actual[i].y, actual[i].z).Distance(expected) < .015f
                    && MathF.Abs(actual[i].w - (shader == "cutout" ? 1f : .5f)) < .01f });
            }
            if (shader == "cutout")
            {
                material.Set("g_tOpacity", Texture.Black);
                var clipped = await Render();
                for (var i = 0; i < clipped.Length; ++i)
                    rows.Add(new { shader, skip, instance = i, test = "alpha-clip", passed = (clipped[i] - empty[i]).Length < .02f });
            }
        }
        batch.Delete();
        batch = null;
    }
    var output = System.IO.Path.Combine(exchangeDirectory, "probe-batch-depth-results.json");
    System.IO.File.WriteAllText(output, System.Text.Json.JsonSerializer.Serialize(rows, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    return new { output, rows };
}
finally { batch?.Delete(); scene.Destroy(); }
