using System.Runtime.InteropServices;
using Titanfall2;

/// <summary>Map-local light table. Indices retain the complete WORLD_LIGHTS order.</summary>
[StructLayout( LayoutKind.Sequential )]
public struct Titanfall2LocalLightData
{
	public Vector4 PositionRadius, ColorSpecular, DirectionOuter, ConeFalloff, Shape;

	public static Titanfall2LocalLightData FromSource( Titanfall2WorldLight light )
	{
		// Other source light types are handled by the sky/sun path. Retain their
		// empty slots: compacting this table would silently change prop probe IDs.
		if ( light.Type is not (1 or 2) || !float.IsFinite( light.Radius ) || light.Radius <= 0
			|| !Finite( light.Origin ) || !Finite( light.Intensity ) || !Finite( light.Direction )
			|| !float.IsFinite( light.LinearAttenuation ) || !float.IsFinite( light.QuadraticAttenuation )
			|| !float.IsFinite( light.InnerConeCosine ) || !float.IsFinite( light.OuterConeCosine )
			|| !float.IsFinite( light.SpecularIntensity ) || !float.IsFinite( light.ConeExponent ) ) return default;
		var direction = light.Direction.LengthSquared() > 0.000001f
			? System.Numerics.Vector3.Normalize( light.Direction ) : System.Numerics.Vector3.UnitZ;
		return new Titanfall2LocalLightData
		{
			PositionRadius = new Vector4( light.Origin.X, light.Origin.Y, light.Origin.Z, light.Radius ),
			ColorSpecular = new Vector4( MathF.Max( 0, light.Intensity.X ), MathF.Max( 0, light.Intensity.Y ),
				MathF.Max( 0, light.Intensity.Z ), MathF.Max( 0, light.SpecularIntensity ) ),
			DirectionOuter = new Vector4( direction.X, direction.Y, direction.Z, light.OuterConeCosine ),
			ConeFalloff = new Vector4( light.InnerConeCosine, light.LinearAttenuation, light.QuadraticAttenuation,
				((light.Flags & 32) != 0 ? 1 : 0) + (light.Type == 2 ? 2 : 0) ),
			Shape = new Vector4( float.IsFinite( light.EmitterRadius ) ? MathF.Max( 0, light.EmitterRadius ) : 0,
				Math.Clamp( light.ConeExponent, 0, 1 ), 0, 0 )
		};
	}

	static bool Finite( System.Numerics.Vector3 v ) => float.IsFinite( v.X ) && float.IsFinite( v.Y ) && float.IsFinite( v.Z );
}

/// <summary>One shared immutable table per map owner, not one native light per lamp.</summary>
public sealed class Titanfall2LocalLightBuffer : IDisposable
{
	readonly GpuBuffer<Titanfall2LocalLightData> _buffer;
	public int Count { get; }

	public Titanfall2LocalLightBuffer( IReadOnlyList<Titanfall2WorldLight> lights )
		: this( lights.Select( Titanfall2LocalLightData.FromSource ).ToArray() ) { }

	Titanfall2LocalLightBuffer( Titanfall2LocalLightData[] lights )
	{
		if ( lights.Length > ushort.MaxValue ) throw new InvalidDataException( "Too many compiled map lights." );
		Count = lights.Length;
		if ( Count == 0 ) return;
		_buffer = new GpuBuffer<Titanfall2LocalLightData>( Count );
		try { _buffer.SetData( lights.AsSpan() ); }
		catch { _buffer.Dispose(); throw; }
	}

	// The mount DLL has no compiler rewrite of string literals into tokens; the string
	// overloads hash the name on every call, and Apply runs once per instanced draw.
	static readonly StringToken CountAttribute = "T2LocalLightCount";
	static readonly StringToken LightsAttribute = "T2LocalLights";

	public void Apply( RenderAttributes attributes )
	{
		attributes.Set( CountAttribute, Count );
		if ( _buffer is not null ) attributes.Set( LightsAttribute, _buffer );
	}

	public static string Encode( IReadOnlyList<Titanfall2WorldLight> lights )
	{
		if ( lights is null || lights.Count == 0 ) return null;
		return Convert.ToBase64String( MemoryMarshal.AsBytes( lights.Select( Titanfall2LocalLightData.FromSource ).ToArray().AsSpan() ) );
	}

	public static Titanfall2LocalLightBuffer Restore( string encoded )
	{
		if ( string.IsNullOrEmpty( encoded ) ) return null;
		var bytes = Convert.FromBase64String( encoded );
		if ( bytes.Length % 80 != 0 || bytes.Length / 80 > ushort.MaxValue ) throw new InvalidDataException( "Invalid compiled light table." );
		return new Titanfall2LocalLightBuffer( MemoryMarshal.Cast<byte, Titanfall2LocalLightData>( bytes ).ToArray() );
	}

	public void Dispose() => _buffer?.Dispose();
}
