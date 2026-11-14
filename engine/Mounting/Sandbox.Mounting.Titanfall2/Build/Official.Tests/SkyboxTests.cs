using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanfall2;

[TestClass]
public class SkyboxTests
{
	static Dictionary<string, string> Camera( string name, string origin ) => new( StringComparer.OrdinalIgnoreCase )
	{
		["classname"] = "sky_camera",
		["targetname"] = name,
		["origin"] = origin,
		["skyscale"] = "1000",
		["scale"] = "1"
	};

	[TestMethod]
	public void OffsetLevelVistaIsSelectedInsteadOfTheExactPivotIntroPlanet()
	{
		var level = new Titanfall2BspReader.StaticPropInstance
		{
			ModelPath = "models/vistas/eden_se.mdl",
			Origin = new( -5688, 7112, -4696 )
		};
		var intro = new Titanfall2BspReader.StaticPropInstance
		{
			ModelPath = "models/vistas/planet_blue_sun.mdl",
			Origin = new( 11680, -8896, -5120 )
		};
		var result = MapLoader.SelectSkybox( new() { StaticProps = [intro, level] },
			[Camera( "skybox_cam_intro", "11680 -8896 -5120" ), Camera( "skybox_cam_level", "-5688 7112 -4544" )] );
		Assert.AreEqual( "skybox_cam_level", result.Name );
		Assert.AreSame( level, result.Models.Single() );
		Assert.AreEqual( 1000f, result.Scale );
		Assert.AreEqual( -4544f, result.Origin.z );
		Assert.AreEqual( -4696f, result.Models[0].Origin.Z, "Do not snap the authored model pivot to its camera." );
	}

	[TestMethod]
	public void LevelGroupKeepsBothVistaLayersAndExcludesTheIntro()
	{
		var props = new[] { "angel_city_se", "angel_city_scrolling_uv" }.Select( name =>
			new Titanfall2BspReader.StaticPropInstance { ModelPath = $"models/vistas/{name}.mdl", Origin = new( 13568, -5376, -4864 ) } ).ToArray();
		var intro = new Titanfall2BspReader.StaticPropInstance { ModelPath = "models/vistas/planet_blue_sun.mdl", Origin = new( 12032, -12032, -6400 ) };
		var result = MapLoader.SelectSkybox( new() { StaticProps = [.. props, intro] },
			[Camera( "skybox_cam_level", "13568 -5376 -4864" ), Camera( "skybox_cam_intro", "12032 -12032 -6400" )] );
		CollectionAssert.AreEquivalent( props, result.Models );
		Assert.AreEqual( 1000f, result.Scale );
	}

	[TestMethod]
	public void MissingEnvironmentUsesTheNonPlanetVistaCluster()
	{
		var level = new Titanfall2BspReader.StaticPropInstance { ModelPath = "models/vistas/eden_se.mdl", Origin = new( -5688, 7112, -4696 ) };
		var intro = new Titanfall2BspReader.StaticPropInstance { ModelPath = "models/vistas/planet_blue_sun.mdl", Origin = new( 11680, -8896, -5120 ) };
		Assert.AreSame( level, MapLoader.SelectSkybox( new() { StaticProps = [intro, level] }, [] ).Models.Single() );
		Assert.IsNull( MapLoader.SelectSkybox( new(), [] ) );
	}

	[DataTestMethod]
	[DataRow( "mp_eden", "models/vistas/eden_se.mdl", 1 )]
	[DataRow( "mp_angel_city", "models/vistas/angel_city_se.mdl", 2 )]
	public void InstalledRetailMapSelectsItsLevelPanorama( string map, string expectedModel, int count )
	{
		var root = Environment.GetEnvironmentVariable( "TF2_SKY_TEST_GAME" );
		if ( string.IsNullOrEmpty( root ) ) Assert.Inconclusive( "Set TF2_SKY_TEST_GAME for read-only retail verification." );
		Assert.IsTrue( VpkArchive.TryOpen( Path.Combine( root, "vpk", $"englishclient_{map}.bsp.pak000_dir.vpk" ), out var archive, out var error ), error );
		Assert.IsTrue( archive.TryReadAllBytes( $"maps/{map}.bsp", out var bytes, out error ), error );
		Assert.IsTrue( Titanfall2BspReader.TryRead( bytes, map, new Dictionary<int, byte[]>(), false, false, false, out var parsed, out error ), error );
		var entities = new List<Dictionary<string, string>>( parsed.Entities );
		foreach ( var entry in archive.Entries.Where( entry => entry.Path.EndsWith( "_env.ent", StringComparison.OrdinalIgnoreCase ) ) )
		{
			Assert.IsTrue( archive.TryReadAllBytes( entry, out var entityBytes, out error ), error );
			entities.AddRange( Titanfall2EntityText.Parse( entityBytes ) );
		}
		var selected = MapLoader.SelectSkybox( parsed, entities );
		Assert.AreEqual( "skybox_cam_level", selected.Name );
		Assert.AreEqual( 1000f, selected.Scale );
		Assert.AreEqual( count, selected.Models.Length );
		Assert.IsTrue( selected.Models.Any( model => model.ModelPath == expectedModel ) );
		Assert.IsFalse( selected.Models.Any( model => model.ModelPath.Contains( "planet_", StringComparison.OrdinalIgnoreCase ) ) );
	}
}
