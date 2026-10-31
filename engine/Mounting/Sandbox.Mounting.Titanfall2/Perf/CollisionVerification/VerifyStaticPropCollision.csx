using System.Text;
using System.Text.Json;
using Titanfall2;

// Compile the actual BSP reader into a small managed-only check. No engine,
// installed game, NuGet packages or graphics driver is required.
const int headerBytes = 16 + 128 * 16;
var cases = (from solid in new byte[] { 0, 1, 2, 6, 7 }
             from reserved in new uint[] { 0, 0x12345678, uint.MaxValue }
             from remove in new uint[] { 0, 0x006B0000, uint.MaxValue }
             select (solid, reserved, remove)).ToArray();
using var game = new MemoryStream();
using (var writer = new BinaryWriter(game, Encoding.ASCII, true))
{
    writer.Write(1); writer.Write(Encoding.ASCII.GetBytes("prps"));
    writer.Write((ushort)0); writer.Write((ushort)13);
    writer.Write(headerBytes + 20); writer.Write(4 + 128 + 12 + cases.Length * 64);
    writer.Write(1);
    var name = new byte[128]; Encoding.ASCII.GetBytes("models/test/decorative_or_solid.mdl").CopyTo(name, 0);
    writer.Write(name); writer.Write(cases.Length); writer.Write(0); writer.Write(0);
    foreach (var item in cases)
    {
        for (var i = 0; i < 6; ++i) writer.Write(i + 0.25f);
        writer.Write(1.5f); writer.Write((ushort)0); writer.Write(item.solid); writer.Write((byte)0);
        writer.Write((ushort)3); writer.Write((ushort)7); writer.Write(2f);
        writer.Write(10f); writer.Write(20f); writer.Write(30f);
        writer.Write(new byte[] { 17, 34, 51, 68 });
        writer.Write(item.reserved); writer.Write(item.remove);
    }
}
var bytes = new byte[headerBytes + game.Length];
Encoding.ASCII.GetBytes("rBSP").CopyTo(bytes, 0);
BitConverter.GetBytes(37).CopyTo(bytes, 4);
BitConverter.GetBytes(127).CopyTo(bytes, 12);
BitConverter.GetBytes(headerBytes).CopyTo(bytes, 16 + 35 * 16);
BitConverter.GetBytes((int)game.Length).CopyTo(bytes, 20 + 35 * 16);
game.ToArray().CopyTo(bytes, headerBytes);
if (!Titanfall2BspReader.TryRead(bytes, "collision_fixture", null, false, false, false, out var bsp, out var error))
    throw new Exception(error);
if (bsp.StaticProps.Count != cases.Length) throw new Exception("Lost static-prop records.");
for (var i = 0; i < cases.Length; ++i)
{
    var expected = cases[i]; var prop = bsp.StaticProps[i];
    if (prop.IsCollidable != (expected.solid != 0)
        || prop.SolidType != expected.solid || prop.ReservedCollisionData != expected.reserved
        || prop.CollisionFlagsRemove != expected.remove || prop.SourceIndex != i
        || prop.DiffuseModulationR != 17 || prop.DiffuseModulationG != 34
        || prop.DiffuseModulationB != 51 || prop.DiffuseModulationA != 68
        || prop.Skin != 3 || prop.Cubemap != 7 || prop.Scale != 1.5f)
        throw new Exception($"v13 collision or adjacent metadata regressed at {i}.");
    // The map cache stores metadata as JSON, omitting computed properties.
    var options = new JsonSerializerOptions { IncludeFields = true, IgnoreReadOnlyProperties = true };
    var restored = JsonSerializer.Deserialize<Titanfall2BspReader.StaticPropInstance>(JsonSerializer.Serialize(prop, options), options);
    if (restored.IsCollidable != prop.IsCollidable || restored.ReservedCollisionData != expected.reserved
        || restored.CollisionFlagsRemove != expected.remove)
        throw new Exception($"Cached collision metadata regressed at {i}.");
}
Console.WriteLine($"PASS: {cases.Length} v13 binary fixtures and {cases.Length} cache metadata round trips.");
