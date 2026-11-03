using System;
using System.Buffers.Binary;
using System.Collections.Generic;
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
	// TryRead preserves on-disk IDs. The BSP reader remaps the lump to full
	// WORLD_LIGHTS indices before distributing probes to static and dynamic props.
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

	/// <summary>
	/// Probe light IDs omit sky-ambient records, unlike the full WORLD_LIGHTS table used
	/// by the renderer. Normalize a private copy once, before either prop lookup reads it.
	/// Keep sun slots and every other source slot; the real-time lightmap page table has
	/// a different mapping and must not be used here.
	/// </summary>
	internal static byte[] RemapLocalLightIndices( ReadOnlySpan<byte> probes, IReadOnlyList<Titanfall2WorldLight> lights )
	{
		// Lump overrides may be reused by another import. Never remap their source IDs in place.
		var result = probes.ToArray();
		var sourceIndices = new List<ushort>( lights.Count );
		for ( var sourceIndex = 0; sourceIndex < lights.Count; sourceIndex++ )
		{
			if ( lights[sourceIndex].Type == Titanfall2WorldLight.SkyAmbientType ) continue;
			sourceIndices.Add( sourceIndex < ushort.MaxValue ? (ushort)sourceIndex : ushort.MaxValue );
		}

		for ( var offset = 0; probes.Length - offset >= RecordBytes; offset += RecordBytes )
		{
			for ( var channel = 0; channel < 4; channel++ )
			{
				var field = result.AsSpan( offset + 36 + channel * 2, 2 );
				var index = BinaryPrimitives.ReadUInt16LittleEndian( field );
				var sourceIndex = index != ushort.MaxValue && index < sourceIndices.Count
					? sourceIndices[index] : ushort.MaxValue;
				BinaryPrimitives.WriteUInt16LittleEndian( field, sourceIndex );
			}
		}
		return result;
	}

	static NumericsVector4 ReadCoefficients( ReadOnlySpan<byte> bytes ) => new(
		BinaryPrimitives.ReadInt16LittleEndian( bytes ) * CoefficientScale,
		BinaryPrimitives.ReadInt16LittleEndian( bytes[2..] ) * CoefficientScale,
		BinaryPrimitives.ReadInt16LittleEndian( bytes[4..] ) * CoefficientScale,
		BinaryPrimitives.ReadInt16LittleEndian( bytes[6..] ) * CoefficientScale );
}
