using Sandbox.Engine;
using Sandbox.Engine.Settings;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace MountingTests;

[TestClass, DoNotParallelize]
public class Titanfall2GraphicsSettingsTests
{
	static RenderSettings WithMemoryCookie( CookieContainer cookies )
	{
		var settings = (RenderSettings)RuntimeHelpers.GetUninitializedObject( typeof( RenderSettings ) );
		typeof( RenderSettings ).GetField( "<VideoSettings>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic )
			.SetValue( settings, cookies );
		typeof( RenderSettings ).GetField( "<Config>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic )
			.SetValue( settings, new RenderQualityProfiles() );
		return settings;
	}

	[TestMethod]
	public void ApplyingPlayerSettingsPublishesSnapshotAndDisconnectRemovesListener()
	{
		// Exercise the real Apply/event/save path with a memory cookie. Do not load or
		// overwrite the player's video.json, and do not queue a mode on a live game window.
		var files = new MemoryFileSystem();
		var cookies = new CookieContainer( "tf2-quality-test", true, files );
		var settings = WithMemoryCookie( cookies );
		try
		{
			settings.TextureQuality = TextureQuality.Low;
			Titanfall2GraphicsSettings.Connect( settings );
			var initial = Titanfall2GraphicsSettings.Current;
			Assert.AreEqual( "Low", Titanfall2GraphicsSettings.Current.TextureQuality );
			settings.TextureQuality = TextureQuality.High;
			Assert.AreEqual( "Low", Titanfall2GraphicsSettings.Current.TextureQuality, "Unapplied edits must not change worker policy." );
			settings.Apply();
			Assert.AreEqual( "High", Titanfall2GraphicsSettings.Current.TextureQuality );
			Assert.AreEqual( Titanfall2GraphicsSettings.Resolve( TextureQuality.High,
				Titanfall2StreamingSettings.GetOptionalInt( "SBOX_TITANFALL2_TEXTURE_MAX", 64, 16384 ) ).TextureMaxDimension,
				Titanfall2StreamingSettings.TextureMaxDimension );
			Assert.AreEqual( "Low", initial.TextureQuality, "Published snapshots remain immutable." );
			var applied = Titanfall2GraphicsSettings.Current;
			Titanfall2GraphicsSettings.Disconnect();
			settings.TextureQuality = TextureQuality.Medium;
			settings.Apply();
			Assert.AreSame( applied, Titanfall2GraphicsSettings.Current, "Shutdown must unsubscribe from the settings event." );
			Assert.IsTrue( files.FileExists( "tf2-quality-test.json" ) );
		}
		finally
		{
			Titanfall2GraphicsSettings.Disconnect();
			cookies.Dispose();
			files.Dispose();
			RenderSettings.Instance.Config.SetGroupConVars( "TextureQuality", RenderSettings.Instance.TextureQuality.ToString() );
		}
	}

	[TestMethod]
	[DataRow( ShadowQuality.Low, 1 )]
	[DataRow( ShadowQuality.Medium, 2 )]
	[DataRow( ShadowQuality.High, 3 )]
	[DataRow( ShadowQuality.Ultra, 4 )]
	public void NativePlayerShadowProfileControlsCascadeBudget( ShadowQuality quality, int expectedCascades )
	{
		var files = new MemoryFileSystem();
		var cookies = new CookieContainer( "tf2-shadow-quality-test", true, files );
		var settings = WithMemoryCookie( cookies );
		try
		{
			settings.ShadowQuality = quality;
			Assert.AreEqual( expectedCascades, ConVarSystem.GetInt( "r.shadows.csm.maxcascades", -1, true ) );
			Assert.AreEqual( expectedCascades == 1 ? 0 : 1, ConVarSystem.GetInt( "r.shadows.contact.enabled", -1, true ) );
		}
		finally
		{
			cookies.Dispose();
			files.Dispose();
			RenderSettings.Instance.Config.SetGroupConVars( "ShadowQuality", RenderSettings.Instance.ShadowQuality.ToString() );
		}
	}

	[TestMethod]
	public void MountCanFindTheCurrentEngineSettingsOutsideStandalone()
	{
		try
		{
			Titanfall2GraphicsSettings.Connect();
			Assert.AreEqual( RenderSettings.Instance.TextureQuality.ToString(), Titanfall2GraphicsSettings.Current.TextureQuality );
		}
		finally
		{
			Titanfall2GraphicsSettings.Disconnect();
		}
	}
}
