using System.Buffers.Binary;
using NumericsVector3 = System.Numerics.Vector3;

namespace Titanfall2;

/// <summary>A compiled R2 WORLD_LIGHTS record, with linear HDR intensity.</summary>
public sealed class Titanfall2WorldLight
{
	public const int SunType = 3;
	public const int SkyAmbientType = 5;
	public const uint RealTimeFlag = 8;

	public NumericsVector3 Origin { get; init; }
	public NumericsVector3 Intensity { get; init; }
	public NumericsVector3 Direction { get; init; }
	public int Type { get; init; }
	public uint Flags { get; init; }
	public float InnerConeCosine { get; init; }
	public float OuterConeCosine { get; init; }
	public float ConeExponent { get; init; }
	public float Radius { get; init; }
	public float ConstantAttenuation { get; init; }
	public float LinearAttenuation { get; init; }
	public float QuadraticAttenuation { get; init; }
	public float EmitterRadius { get; init; }
	public float HighlightSize { get; init; }
	public float SpecularIntensity { get; init; } = 1;
	public bool IsRealTime => (Flags & RealTimeFlag) != 0;

	public static IReadOnlyList<Titanfall2WorldLight> Read( ReadOnlySpan<byte> bytes, int version )
	{
		var stride = version switch { 1 => 100, 2 => 108, 3 => 112, _ => 0 };
		if ( stride == 0 || bytes.Length % stride != 0 ) return Array.Empty<Titanfall2WorldLight>();
		var lights = new Titanfall2WorldLight[bytes.Length / stride];
		for ( var index = 0; index < lights.Length; ++index )
		{
			var record = bytes.Slice( index * stride, stride );
			lights[index] = new Titanfall2WorldLight
			{
				Origin = ReadVector( record ),
				Intensity = ReadVector( record[12..] ),
				Direction = ReadVector( record[24..] ),
				Type = BinaryPrimitives.ReadInt32LittleEndian( record[52..] ),
				Flags = BinaryPrimitives.ReadUInt32LittleEndian( record[88..] ),
				InnerConeCosine = ReadSingle( record[60..] ),
				OuterConeCosine = ReadSingle( record[64..] ),
				ConeExponent = ReadSingle( record[68..] ),
				Radius = ReadSingle( record[72..] ),
				ConstantAttenuation = ReadSingle( record[76..] ),
				LinearAttenuation = ReadSingle( record[80..] ),
				QuadraticAttenuation = ReadSingle( record[84..] ),
				EmitterRadius = stride >= 108 ? ReadSingle( record[100..] ) : 0,
				HighlightSize = stride >= 108 ? ReadSingle( record[104..] ) : 0,
				SpecularIntensity = stride >= 112 ? ReadSingle( record[108..] ) : 1
			};
		}
		return lights;
	}

	/// <summary>
	/// Precompute which of each texel's four visibility channels belongs to a sun.
	/// Keep channel selection point sampled in the shader: interpolating selected
	/// visibility on the CPU would mix different light IDs at chart boundaries.
	/// </summary>
	public static byte[] CreateSunChannels( ReadOnlySpan<byte> packedIds, IReadOnlyList<short> lightPages,
		IReadOnlyList<Titanfall2WorldLight> lights )
	{
		if ( packedIds.Length == 0 || packedIds.Length % 4 != 0 || lightPages.Count == 0 || lightPages.Count % 63 != 0 ) return [];
		// Retail compacts WORLD_LIGHTS to the real-time list before resolving RTL
		// page indices. Prop probes instead retain indices into the full source list.
		var runtimeLights = lights.Where( light => light.IsRealTime ).ToArray();
		if ( runtimeLights.Length == 0 ) return [];
		var channels = new byte[packedIds.Length / 4];
		for ( var pixel = 0; pixel < channels.Length; ++pixel )
		{
			var packed = BinaryPrimitives.ReadUInt32LittleEndian( packedIds[(pixel * 4)..] );
			var page = (int)(packed >> 24);
			for ( var channel = 0; channel < 4; ++channel )
			{
				var localIndex = (int)((packed >> (channel * 6)) & 63);
				if ( localIndex == 63 ) continue;
				var pageIndex = page * 63 + localIndex;
				if ( pageIndex >= lightPages.Count ) return [];
				var lightIndex = lightPages[pageIndex];
				if ( lightIndex < 0 || lightIndex >= runtimeLights.Length ) return [];
				if ( runtimeLights[lightIndex].Type == SunType )
				{
					channels[pixel] = (byte)(1 << channel);
					break;
				}
			}
		}
		return channels;
	}

	static NumericsVector3 ReadVector( ReadOnlySpan<byte> bytes ) => new( ReadSingle( bytes ), ReadSingle( bytes[4..] ), ReadSingle( bytes[8..] ) );
	static float ReadSingle( ReadOnlySpan<byte> bytes ) => BitConverter.Int32BitsToSingle( BinaryPrimitives.ReadInt32LittleEndian( bytes ) );
}
