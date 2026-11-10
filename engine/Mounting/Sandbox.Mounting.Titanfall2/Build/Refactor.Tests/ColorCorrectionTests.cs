using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sandbox;

[TestClass]
public class ColorCorrectionTests
{
	static Dictionary<string, string> Entity() => new( StringComparer.OrdinalIgnoreCase )
	{
		["classname"] = "color_correction",
		["filename"] = "materials/correction/test.raw",
		["origin"] = "10 0 0",
		["minfalloff"] = "10",
		["maxfalloff"] = "30",
		["maxweight"] = "0.5"
	};

	[TestMethod]
	public void RadialWeightUsesAuthoredInnerOuterAndMaximum()
	{
		var definition = new Titanfall2ColorCorrectionDefinition( "titanfall2", Entity() );
		Assert.AreEqual( 0.5f, definition.SpatialWeight( new Vector3( 10, 0, 0 ) ) );
		Assert.AreEqual( 0.25f, definition.SpatialWeight( new Vector3( 30, 0, 0 ) ), 0.00001f );
		Assert.AreEqual( 0f, definition.SpatialWeight( new Vector3( 40, 0, 0 ) ) );
	}

	[TestMethod]
	public void GlobalWeightAboveOneIsPreservedForRelativeLutNormalization()
	{
		var entity = Entity(); entity["minfalloff"] = "-1"; entity["maxweight"] = "2";
		var definition = new Titanfall2ColorCorrectionDefinition( "titanfall2", entity );
		Assert.IsTrue( definition.IsGlobal );
		Assert.AreEqual( 2f, definition.SpatialWeight( new Vector3( 10000, 0, 0 ) ) );
	}

	[TestMethod]
	public void BrushRotationAndConvexPlanesRestrictTheCorrection()
	{
		var entity = Entity(); entity["classname"] = "color_correction_volume";
		entity["origin"] = "100 200 0"; entity["angles"] = "0 90 0";
		entity["*trigger_bounds_mins"] = "-1 -2 -3"; entity["*trigger_bounds_maxs"] = "1 2 3";
		var definition = new Titanfall2ColorCorrectionDefinition( "titanfall2", entity );
		Assert.AreEqual( 0.5f, definition.SpatialWeight( new Vector3( 101.5f, 200, 0 ) ) );
		Assert.AreEqual( 0f, definition.SpatialWeight( new Vector3( 100, 201.5f, 0 ) ) );
		entity["angles"] = "0 0 0"; entity["*trigger_brush_0_plane_0"] = "1 0 0 0";
		definition = new( "titanfall2", entity );
		Assert.AreEqual( 0.5f, definition.SpatialWeight( new Vector3( 99.5f, 200, 0 ) ) );
		Assert.AreEqual( 0f, definition.SpatialWeight( new Vector3( 100.5f, 200, 0 ) ) );
	}

	[TestMethod]
	public void BrushFadeReversesContinuouslyFromItsCurrentWeight()
	{
		var fade = new Titanfall2ColorCorrectionDefinition.FadeState();
		Assert.AreEqual( 0f, fade.Evaluate( 1, 0, 2, 2 ) );
		Assert.AreEqual( 0.5f, fade.Evaluate( 1, 1, 2, 2 ) );
		Assert.AreEqual( 0.5f, fade.Evaluate( 0, 1, 2, 2 ) );
		Assert.AreEqual( 0.25f, fade.Evaluate( 0, 2, 2, 2 ) );
		Assert.AreEqual( 0f, fade.Evaluate( 0, 3, 2, 2 ) );
	}

	[TestMethod]
	public void PartitionMergeEnrichesGeometryWithoutDuplicatingOrMutatingSources()
	{
		var external = Entity(); external["classname"] = "color_correction_volume";
		var embedded = new Dictionary<string, string>( external, StringComparer.OrdinalIgnoreCase )
		{
			["*trigger_bounds_mins"] = "-1 -1 -1",
			["*trigger_bounds_maxs"] = "1 1 1"
		};
		var merged = Titanfall2EnvironmentEntities.Merge( [external], [embedded] );
		Assert.AreEqual( 1, merged.Count );
		Assert.IsTrue( merged[0].ContainsKey( "*trigger_bounds_mins" ) );
		Assert.IsFalse( external.ContainsKey( "*trigger_bounds_mins" ) );
		Assert.AreEqual( 2, Titanfall2EnvironmentEntities.Merge( [external, external], [embedded] ).Count );
	}
}
