using System.Runtime.InteropServices;

namespace Sandbox.Mounting.Helldivers2;

/// <summary>
/// The engine vertex a Helldivers 2 mesh is baked into. The attributes drive the engine's vertex
/// layout, so the field order here is the byte order the GPU sees; only the position, normal and
/// first UV set are carried, which is all the mount builds today.
/// </summary>
[StructLayout( LayoutKind.Sequential )]
internal struct Hd2Vertex
{
	[VertexLayout.Position] public Vector3 Position;
	[VertexLayout.Normal] public Vector3 Normal;
	[VertexLayout.TexCoord] public Vector2 TexCoord;
}
