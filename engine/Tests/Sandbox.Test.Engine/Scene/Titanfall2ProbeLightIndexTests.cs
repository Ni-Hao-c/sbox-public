using System;
using System.Buffers.Binary;
using Titanfall2;
using NumericsVector3 = System.Numerics.Vector3;

namespace SceneTests;

[TestClass]
public class Titanfall2ProbeLightIndexTests
{
	static byte[] Probe( params ushort[] indices )
	{
		var bytes = new byte[Titanfall2LightProbe.RecordBytes];
		BinaryPrimitives.WriteInt16LittleEndian( bytes.AsSpan( 6 ), 4096 );
		BinaryPrimitives.WriteUInt32LittleEndian( bytes.AsSpan( 32 ), 0xFF804020 );
		for ( var channel = 0; channel < 4; channel++ )
			BinaryPrimitives.WriteUInt16LittleEndian( bytes.AsSpan( 36 + channel * 2 ), indices[channel] );
		return bytes;
	}

	// mp_rise has two environment pairs; sp_crashsite has three.
	[DataTestMethod]
	[DataRow( 2 )]
	[DataRow( 3 )]
	public void LocalLightsResolvePastTheMapsSkyAmbientRecords( int environments )
	{
		var lights = new Titanfall2WorldLight[environments * 2 + 1];
		for ( var i = 0; i < environments; i++ )
		{
			lights[i] = new() { Type = Titanfall2WorldLight.SkyAmbientType };
			lights[environments + i] = new() { Type = Titanfall2WorldLight.SunType };
		}
		lights[^1] = new() { Type = 1, Origin = new NumericsVector3( 10, 0, 0 ), Radius = 100 };
		var bytes = Probe( (ushort)environments, ushort.MaxValue, (ushort)(environments + 1), 0 );
		var before = (byte[])bytes.Clone();

		var normalized = Titanfall2LightProbe.RemapLocalLightIndices( bytes, lights );
		CollectionAssert.AreEqual( before, bytes, "A second import must not receive already-remapped lump overrides." );
		bytes = normalized;
		Assert.IsTrue( Titanfall2LightProbe.TryRead( bytes, out var probe ) );
		Assert.AreEqual( (ushort)(environments * 2), probe.LightIndex0 );
		Assert.AreEqual( ushort.MaxValue, probe.LightIndex1 );
		Assert.AreEqual( ushort.MaxValue, probe.LightIndex2, "Out-of-range IDs cannot select an unrelated lamp." );
		Assert.AreEqual( (ushort)environments, probe.LightIndex3, "Sun slots must remain in the probe index space." );
		CollectionAssert.AreEqual( before[..36], bytes[..36], "Irradiance, sky visibility and light weights must be unchanged." );
	}

	[TestMethod]
	public void MappingUsesSourceOrderRatherThanAFixedOffsetOrTheRealtimeList()
	{
		Titanfall2WorldLight[] lights =
		[
			new() { Type = 1 },
			new() { Type = Titanfall2WorldLight.SkyAmbientType },
			new() { Type = Titanfall2WorldLight.SunType },
			new() { Type = 2, Flags = Titanfall2WorldLight.RealTimeFlag },
			new() { Type = Titanfall2WorldLight.SkyAmbientType },
			new() { Type = 1 }
		];
		var bytes = Probe( 0, 1, 2, 3 );
		bytes = Titanfall2LightProbe.RemapLocalLightIndices( bytes, lights );
		Assert.IsTrue( Titanfall2LightProbe.TryRead( bytes, out var probe ) );
		CollectionAssert.AreEqual( new ushort[] { 0, 2, 3, 5 },
			new[] { probe.LightIndex0, probe.LightIndex1, probe.LightIndex2, probe.LightIndex3 } );
	}

	[TestMethod]
	public void PositionLookupReadsTheSameNormalizedProbeAsStaticProps()
	{
		var bytes = Probe( 1, ushort.MaxValue, ushort.MaxValue, ushort.MaxValue );
		bytes = Titanfall2LightProbe.RemapLocalLightIndices( bytes,
		[
			new() { Type = Titanfall2WorldLight.SkyAmbientType },
			new() { Type = Titanfall2WorldLight.SunType },
			new() { Type = 1 }
		] );
		var tree = new byte[8];
		BinaryPrimitives.WriteUInt32LittleEndian( tree, 3 );
		BinaryPrimitives.WriteUInt32LittleEndian( tree.AsSpan( 4 ), 1 );
		var lookup = Titanfall2LightProbeLookup.TryCreate( bytes, tree, new byte[20] );
		Assert.IsNotNull( lookup );
		Assert.AreEqual( (ushort)2, lookup.Find( NumericsVector3.Zero ).LightIndex0 );
		Assert.AreEqual( 0.5f, lookup.Find( NumericsVector3.Zero ).Red.W );
	}

	[TestMethod]
	public void MissingLightsDisableAssociationsWithoutChangingProbeLighting()
	{
		var bytes = Probe( 0, 1, 2, ushort.MaxValue );
		bytes = Titanfall2LightProbe.RemapLocalLightIndices( bytes, [] );
		Assert.IsTrue( Titanfall2LightProbe.TryRead( bytes, out var probe ) );
		Assert.AreEqual( 0.5f, probe.Red.W );
		CollectionAssert.AreEqual( new[] { ushort.MaxValue, ushort.MaxValue, ushort.MaxValue, ushort.MaxValue },
			new[] { probe.LightIndex0, probe.LightIndex1, probe.LightIndex2, probe.LightIndex3 } );
	}
}
