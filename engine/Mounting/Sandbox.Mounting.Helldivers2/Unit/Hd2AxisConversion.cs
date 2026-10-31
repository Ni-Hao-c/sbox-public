namespace Sandbox.Mounting.Helldivers2;

/// <summary>
/// The space conversion between the game and the engine, applied once as decoded positions and
/// normals are copied into the engine vertex rather than in a second pass over the finished buffer.
///
/// MEASURED 2026-10-18 against the retail install, raw vertex positions decoded straight out of the
/// GPU stream (the header AABBs agree with them exactly):
/// <list type="bullet">
/// <item>the game's unit meshes are already <b>Z-up</b>, the same as the engine: a pumpjack tower
/// measures 3.073 along X by 7.259 along Y by 18.801 along Z (its height on Z), an automaton
/// soldier mesh 2.073 / 1.467 / 2.618 (its height on Z), and ground props sit on the z = 0 plane
/// with x and y centred on it (an illuminate canister spans x -0.295..0.296, y -0.296..0.293,
/// z -0.001..1.046). So <see cref="ToZUp"/> is normally <b>not</b> wanted here: it lays models on
/// their back. It stays available because it is what an older measurement asked for, and it is the
/// same rotation the reference implementation uses for its Z-up to Y-up glTF matrix
/// (<c>stingray.ToGLTFMatrix</c>).</item>
/// <item>the game stores <b>metres</b> while the engine's unit is one <b>inch</b>: the same pumpjack
/// is 18.8 m tall and a grenade launcher 0.715 m long, so positions need a uniform
/// <see cref="MetresToInches"/> scale.</item>
/// </list>
/// </summary>
internal static class Hd2AxisConversion
{
	/// <summary>Metres to inches, the scale from the game's units to the engine's.</summary>
	public const float MetresToInches = 39.3701f;

	/// <summary>
	/// Rotates a vector by the Y-up to Z-up rotation: ( x, y, z ) becomes ( x, z, -y ). The
	/// determinant is +1, so this is a pure rotation: handedness is preserved, front faces keep
	/// their winding and no index needs reversing. Texture coordinates, which live in their own
	/// space, are not affected.
	/// </summary>
	public static Vector3 ToZUp( Vector3 value ) => new( value.x, value.z, -value.y );

	/// <summary>
	/// Applies the rotation and the uniform scale a build asked for, in the order the engine sees
	/// them. A uniform scale is rotation invariant, so the two commute; only positions go through
	/// this, since a uniform scale leaves a normalized direction alone.
	/// </summary>
	public static Vector3 ToEngine( Vector3 position, bool convertToZUp, float unitScale ) =>
		( convertToZUp ? ToZUp( position ) : position ) * unitScale;

	/// <summary>Normalizes a vector, returning zero for a degenerate one instead of a NaN vector.</summary>
	public static Vector3 Normalize( Vector3 value )
	{
		var length = value.Length;
		return length > 1e-12f ? value / length : Vector3.Zero;
	}
}
