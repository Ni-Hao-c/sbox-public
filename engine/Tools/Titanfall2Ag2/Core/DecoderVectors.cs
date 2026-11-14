using System.Runtime.InteropServices;

// The shared mount readers use the engine's global Vector types for binary fields.
// This standalone assembly supplies only their wire layout; no engine DLL is loaded.
[StructLayout( LayoutKind.Sequential, Pack = 4 )]
public struct Vector3( float x, float y, float z )
{
	public float x = x;
	public float y = y;
	public float z = z;
	public readonly float X => x;
	public readonly float Y => y;
	public readonly float Z => z;
	public static Vector3 Zero => new( 0, 0, 0 );
	public static Vector3 One => new( 1, 1, 1 );
	public static implicit operator System.Numerics.Vector3( Vector3 v ) => new( v.x, v.y, v.z );
	public static implicit operator Vector3( System.Numerics.Vector3 v ) => new( v.X, v.Y, v.Z );
}

[StructLayout( LayoutKind.Sequential, Pack = 4 )]
public struct Vector2( float x, float y )
{
	public float x = x;
	public float y = y;
}
