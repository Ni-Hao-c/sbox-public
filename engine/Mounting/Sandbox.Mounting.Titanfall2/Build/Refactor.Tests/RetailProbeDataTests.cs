using System.Buffers.Binary;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanfall2;
using NumericsVector3 = System.Numerics.Vector3;

/// <summary>Optional local fixtures extracted from an owned TF2 installation; no game data is distributed.</summary>
[TestClass]
public class RetailProbeDataTests
{
	[TestMethod]
	public void RetailBeaconUsesLocalBrushCoordinatesAndFortySecondEntityWarmup()
	{
		var root = Environment.GetEnvironmentVariable( "TF2_PROBE_AUDIT_ROOT" );
		if ( string.IsNullOrWhiteSpace( root ) ) Assert.Inconclusive( "Set TF2_PROBE_AUDIT_ROOT to extracted local retail fixtures." );
		var folder = Path.Combine( root!, "sp_beacon" );
		var sources = Directory.GetFiles( folder, "*.ent" ).Select( file =>
			(IReadOnlyList<Dictionary<string, string>>)Titanfall2EntityText.Parse( File.ReadAllBytes( file ) ) ).ToArray();
		Assert.AreEqual( 14, sources.SelectMany( source => source ).Count( row => row.ContainsKey( "parentBrushModel" )
			&& row.GetValueOrDefault( "classname" ) == "info_particle_system" ) );
		var result = Titanfall2ParticleMapEntities.Read( sources );
		var light = result.Entities.First( entity => entity.TargetName == "info_particle_system_1" && entity.EffectName == "warning_light_red" );
		Assert.IsTrue( light.Position.Distance( new Vector3( 3140.91f, -2002.61f, 4529.39f ) ) < 0.01f );
		Assert.AreEqual( 40f, result.Entities.Single( entity => entity.EffectName == "env_smoke_plume_XLG_dark" ).WarmupTime );
		Assert.AreEqual( 0, result.UnresolvedParents );
	}

	[DataTestMethod]
	[DataRow( "sp_timeshift_spoke02" )]
	[DataRow( "mp_angel_city" )]
	[DataRow( "sp_beacon" )]
	[DataRow( "sp_crashsite" )]
	[DataRow( "mp_coliseum" )]
	[DataRow( "sp_training" )]
	[DataRow( "sp_boomtown" )]
	[DataRow( "sp_skyway_v1" )]
	[DataRow( "mp_drydock" )]
	[DataRow( "sp_tday" )]
	public void RetailLeafRangesPartitionReferencesAndProductionQueriesMatch( string map )
	{
		var root = Environment.GetEnvironmentVariable( "TF2_PROBE_AUDIT_ROOT" );
		if ( string.IsNullOrWhiteSpace( root ) ) Assert.Inconclusive( "Set TF2_PROBE_AUDIT_ROOT to extracted local retail fixtures." );
		var folder = Path.Combine( root!, map );
		var probes = File.ReadAllBytes( Path.Combine( folder, "probes.bin" ) );
		var tree = File.ReadAllBytes( Path.Combine( folder, "tree.bin" ) );
		var references = File.ReadAllBytes( Path.Combine( folder, "references.bin" ) );
		var ownership = new int[references.Length / 20];
		for ( var i = 0; i < tree.Length; i += 8 )
		{
			var tag = BinaryPrimitives.ReadUInt32LittleEndian( tree.AsSpan( i ) );
			if ( (tag & 3) != 3 ) continue;
			var first = tag >> 2;
			var count = BinaryPrimitives.ReadUInt32LittleEndian( tree.AsSpan( i + 4 ) );
			Assert.IsTrue( count is > 0 and <= 4, map );
			Assert.IsTrue( (long)first + count <= ownership.Length, map );
			for ( var r = first; r < first + count; r++ ) ownership[r]++;
		}
		Assert.IsTrue( ownership.All( count => count == 1 ), $"{map}: every reference must belong to exactly one leaf." );
		var lookup = Titanfall2LightProbeLookup.TryCreate( probes, tree, references );
		Assert.IsNotNull( lookup );
		using var json = JsonDocument.Parse( File.ReadAllText( Path.Combine( folder, "queries.json" ) ) );
		foreach ( var query in json.RootElement.EnumerateArray() )
		{
			var p = query.GetProperty( "position" );
			var actual = lookup.Find( new NumericsVector3( p[0].GetSingle(), p[1].GetSingle(), p[2].GetSingle() ) );
			var id = query.GetProperty( "probe" ).GetInt32();
			Assert.IsTrue( Titanfall2LightProbe.TryRead( probes.AsSpan( id * 48, 48 ), out var expected ) );
			Assert.IsNotNull( actual, map );
			Assert.AreEqual( expected.Red, actual.Red, map );
			Assert.AreEqual( expected.Green, actual.Green, map );
			Assert.AreEqual( expected.Blue, actual.Blue, map );
			Assert.AreEqual( expected.SkyDirectionSunVisibility, actual.SkyDirectionSunVisibility, map );
			Assert.AreEqual( expected.PackedLightWeights, actual.PackedLightWeights, map );
			Assert.AreEqual( expected.LightIndex0, actual.LightIndex0, map );
			Assert.AreEqual( expected.LightIndex1, actual.LightIndex1, map );
			Assert.AreEqual( expected.LightIndex2, actual.LightIndex2, map );
			Assert.AreEqual( expected.LightIndex3, actual.LightIndex3, map );
		}
	}
}
