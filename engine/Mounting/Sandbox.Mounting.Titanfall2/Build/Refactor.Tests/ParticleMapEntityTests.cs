using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sandbox;
using Titanfall2;

[TestClass]
public class ParticleMapEntityTests
{
	static List<Dictionary<string, string>> Rows( string text ) => Titanfall2EntityText.Parse( text );
	const string Effect = "{\"classname\" \"info_particle_system\" \"effect_name\" \"smoke\" \"origin\" \"1 2 3\"}";

	[TestMethod]
	public void MainLumpEffectsAndPartitionEffectsAreBothRetained()
	{
		var result = Titanfall2ParticleMapEntities.Read( Rows( Effect ), Rows( Effect.Replace( "smoke", "fire" ) ) );
		CollectionAssert.AreEqual( new[] { "smoke", "fire" }, result.Entities.Select( e => e.EffectName ).ToArray() );
	}

	[TestMethod]
	public void DuplicatePartitionsDoNotDoubleEmitButAuthoredDuplicatesSurvive()
	{
		var result = Titanfall2ParticleMapEntities.Read( Rows( Effect + Effect ), Rows( Effect ) );
		Assert.AreEqual( 2, result.Entities.Count );
	}

	[TestMethod]
	public void BrushParentTransformsEffectAndControlPointIntoWorldSpace()
	{
		var result = Titanfall2ParticleMapEntities.Read( Rows( """
			{"classname" "info_particle_system" "effect_name" "beam" "origin" "10 0 0" "parentBrushModel" "*3" "cpoint1" "end"}
			{"classname" "info_target" "targetname" "end" "origin" "20 0 0" "parentBrushModel" "*3"}
			""" ), Rows( """
			{"classname" "func_brush" "model" "*3" "origin" "100 200 300" "angles" "0 90 0"}
			""" ) );
		var effect = result.Entities.Single();
		Assert.IsTrue( effect.Position.Distance( new Vector3( 100, 210, 300 ) ) < 0.001f );
		Assert.IsTrue( effect.ControlPoints.Single().Position.Distance( new Vector3( 100, 220, 300 ) ) < 0.001f );
		Assert.AreEqual( 0, result.UnresolvedParents );
	}

	[TestMethod]
	public void MissingParentDoesNotPlaceLocalCoordinatesInTheWorld()
	{
		var result = Titanfall2ParticleMapEntities.Read( Rows( Effect.Replace( "}", "\"parentBrushModel\" \"*3\"}" ) ) );
		Assert.AreEqual( 0, result.Entities.Count );
		Assert.AreEqual( 1, result.UnresolvedParents );
	}

	[TestMethod]
	public void EntityWarmupSurvivesSerializationAndIconScaleIsIgnored()
	{
		var result = Titanfall2ParticleMapEntities.Read( Rows( Effect.Replace( "}",
			"\"warmup_time\" \"40\" \"warmup_and_pause\" \"true\" \"scale\" \"20\"}" ) ) );
		var effect = Titanfall2ParticleStreamer.Decode( Titanfall2ParticleStreamer.Encode( result.Entities ) ).Single();
		Assert.AreEqual( 40f, effect.WarmupTime );
		Assert.IsTrue( effect.WarmupAndPause );
		Assert.AreEqual( 1f, effect.Scale );
		Assert.AreEqual( new Vector3( 1, 2, 3 ), effect.Position );
	}

	[TestMethod]
	public void LegacySerializedEffectsDefaultToNoEntityWarmup()
	{
		using var stream = new MemoryStream();
		using ( var writer = new BinaryWriter( stream, System.Text.Encoding.UTF8, true ) )
		{
			writer.Write( 0x58465054 ); writer.Write( 3 ); writer.Write( 1 );
			writer.Write( "smoke" ); writer.Write( "" );
			for ( var i = 0; i < 6; i++ ) writer.Write( 0f );
			writer.Write( 1f ); writer.Write( 0 );
		}
		var effect = Titanfall2ParticleStreamer.Decode( Convert.ToBase64String( stream.ToArray() ) ).Single();
		Assert.AreEqual( 0f, effect.WarmupTime );
		Assert.IsFalse( effect.WarmupAndPause );
	}

	[TestMethod]
	public void InactiveEffectsStayInactiveAndSkyboxEffectsAreExplicitlyDeferred()
	{
		var result = Titanfall2ParticleMapEntities.Read( Rows(
			Effect.Replace( "}", "\"start_active\" \" false \"}" )
			+ Effect.Replace( "}", "\"in_skybox\" \"True\"}" ) ) );
		Assert.AreEqual( 0, result.Entities.Count );
		Assert.AreEqual( 1, result.SkyboxCount );
	}

	[TestMethod]
	public void NonFinitePositionsAreRejectedAndUnresolvedControlPointsAreCounted()
	{
		var result = Titanfall2ParticleMapEntities.Read( Rows( Effect.Replace( "1 2 3", "NaN 2 3" )
			+ Effect.Replace( "}", "\"cpoint1\" \"missing\"}" ) ) );
		Assert.AreEqual( 1, result.Entities.Count );
		Assert.AreEqual( 1, result.UnresolvedControlPoints );
	}
}
