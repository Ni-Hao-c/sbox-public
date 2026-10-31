using System;
using System.Buffers.Binary;
using NumericsVector3 = System.Numerics.Vector3;
using NumericsVector4 = System.Numerics.Vector4;

namespace Titanfall2;

/// <summary>
/// One retail R2 LIGHTPROBES record. Irradiance coefficients are signed Q13,
/// not six RGBE cube faces. Directions are in map/world space.
/// </summary>
public sealed class Titanfall2LightProbe
{
	public const int RecordBytes = 48;
	public const float CoefficientScale = 1f / 8192f;

	public NumericsVector4 Red { get; init; }
	public NumericsVector4 Green { get; init; }
	public NumericsVector4 Blue { get; init; }
	public NumericsVector4 SkyDirectionSunVisibility { get; init; }
	// Keep the local-light association separate. The retail loader adjusts and
	// compacts these indices; that processing does not affect SH irradiance.
	public uint PackedLightWeights { get; init; }
	public ushort LightIndex0 { get; init; }
	public ushort LightIndex1 { get; init; }
	public ushort LightIndex2 { get; init; }
	public ushort LightIndex3 { get; init; }
	public uint Reserved { get; init; }

	/// <summary>Diffuse irradiance for a unit world-space normal, before sky and direct lights.</summary>
	public NumericsVector3 Evaluate( NumericsVector3 normal )
	{
		var direction = new NumericsVector4( normal, 1f );
		return NumericsVector3.Max( NumericsVector3.Zero, new NumericsVector3(
			NumericsVector4.Dot( Red, direction ),
			NumericsVector4.Dot( Green, direction ),
			NumericsVector4.Dot( Blue, direction ) ) );
	}

	/// <summary>Decode a single record without interpreting its local-light indices.</summary>
	public static bool TryRead( ReadOnlySpan<byte> bytes, out Titanfall2LightProbe probe )
	{
		probe = null;
		if ( bytes.Length < RecordBytes ) return false;
		probe = new Titanfall2LightProbe
		{
			Red = ReadCoefficients( bytes ),
			Green = ReadCoefficients( bytes[8..] ),
			Blue = ReadCoefficients( bytes[16..] ),
			SkyDirectionSunVisibility = ReadCoefficients( bytes[24..] ),
			PackedLightWeights = BinaryPrimitives.ReadUInt32LittleEndian( bytes[32..] ),
			LightIndex0 = BinaryPrimitives.ReadUInt16LittleEndian( bytes[36..] ),
			LightIndex1 = BinaryPrimitives.ReadUInt16LittleEndian( bytes[38..] ),
			LightIndex2 = BinaryPrimitives.ReadUInt16LittleEndian( bytes[40..] ),
			LightIndex3 = BinaryPrimitives.ReadUInt16LittleEndian( bytes[42..] ),
			Reserved = BinaryPrimitives.ReadUInt32LittleEndian( bytes[44..] )
		};
		return true;
	}

	static NumericsVector4 ReadCoefficients( ReadOnlySpan<byte> bytes ) => new(
		BinaryPrimitives.ReadInt16LittleEndian( bytes ) * CoefficientScale,
		BinaryPrimitives.ReadInt16LittleEndian( bytes[2..] ) * CoefficientScale,
		BinaryPrimitives.ReadInt16LittleEndian( bytes[4..] ) * CoefficientScale,
		BinaryPrimitives.ReadInt16LittleEndian( bytes[6..] ) * CoefficientScale );
}
