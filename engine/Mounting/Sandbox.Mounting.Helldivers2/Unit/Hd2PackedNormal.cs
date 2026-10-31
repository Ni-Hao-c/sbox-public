namespace Sandbox.Mounting.Helldivers2;

/// <summary>
/// Decodes the 10/10/10/2 packed word Helldivers 2 uses for normals: an octahedral normal in the
/// red and green channels, a tangent rotation in blue, and two bits that flip the bitangent.
/// Ported from the reference implementation, which cites the layout as "tangent spaces and
/// diamond encoding".
/// </summary>
internal static class Hd2PackedNormal
{
	/// <summary>Decodes one packed word into its normal, tangent and bitangent.</summary>
	public static void Decode( uint packed, out Vector3 normal, out Vector4 tangent, out Vector3 bitangent )
	{
		var r10 = packed & 0x3ff;
		var g10 = (packed >> 10) & 0x3ff;
		var b10 = (packed >> 20) & 0x3ff;
		var a2 = (packed >> 30) & 0x3;

		normal = DecodeOctahedral(
			r10 * (2f / 1023f) - 1f,
			g10 * (2f / 1023f) - 1f );

		// Blue carries the tangent's rotation over a half turn as two straight lines, so a shader
		// can recover its cosine and minus sine without trigonometry.
		var rotation = b10 * (1f / 1023f);
		float cosine;
		float minusSine;
		if ( rotation < 0.5f )
		{
			cosine = 4f * rotation - 1f;
			minusSine = MathF.Abs( cosine ) - 1f;
		}
		else
		{
			cosine = 3f - 4f * rotation;
			minusSine = 1f - MathF.Abs( cosine );
		}

		var length = MathF.Sqrt( cosine * cosine + minusSine * minusSine );
		if ( length > 0f )
		{
			cosine /= length;
			minusSine /= length;
		}

		var firstBase = MathF.Abs( normal.z ) < MathF.Abs( normal.y );

		// The reference implementation flips the choice for normals pointing straight along x,
		// where the two bases meet. Its comment is that the shader does not need this because it
		// works in float16, so the correction only matters for a float32 decode like this one.
		if ( NearlyEqual( normal.x, -1f ) || NearlyEqual( normal.x, 1f ) ) firstBase = !firstBase;

		Vector3 tangentBase;
		float tangentBasis;
		if ( firstBase )
		{
			tangentBase = Hd2AxisConversion.Normalize( new Vector3( normal.y, -normal.x, 0f ) );
			tangentBasis = 1f;
		}
		else
		{
			tangentBase = Hd2AxisConversion.Normalize( new Vector3( normal.z, 0f, -normal.x ) );
			tangentBasis = -1f;
		}

		// Rotate the base around the normal (Rodrigues, without its last term since the base is
		// already orthogonal to the normal).
		var rotated = tangentBase * cosine + Vector3.Cross( tangentBase, normal ) * minusSine;

		tangent = new Vector4( rotated.x, rotated.y, rotated.z, tangentBasis );
		bitangent = Vector3.Cross( normal, rotated );

		if ( a2 == 3 ) bitangent = -bitangent;
	}

	static Vector3 DecodeOctahedral( float x, float y )
	{
		var z = 1f - MathF.Abs( x ) - MathF.Abs( y );
		if ( z < 0f )
		{
			var correctedX = (1f - MathF.Abs( y )) * Sign( x );
			var correctedY = (1f - MathF.Abs( x )) * Sign( y );
			x = correctedX;
			y = correctedY;
		}

		return Hd2AxisConversion.Normalize( new Vector3( x, y, z ) );
	}

	/// <summary>A sign that treats zero as positive, the way the shader's does.</summary>
	static float Sign( float value ) => value >= 0f ? 1f : -1f;

	static bool NearlyEqual( float value, float target ) => MathF.Abs( value - target ) <= 1e-6f;
}
