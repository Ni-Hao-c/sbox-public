using System.Reflection;
using Sandbox.Rendering;

namespace MountingTests;

[TestClass, DoNotParallelize]
public class Titanfall2NativeTonemappingValidation
{
	[TestMethod]
	public void MapDisplayCreatesNativeLinearBaselineAndOwnsItsLifetime()
	{
		var scene = new Scene();
		try
		{
			using var scope = scene.Push();
			var owner = new GameObject();
			var display = Titanfall2Tonemapping.CreateMapDisplay( owner, 0.75f );
			var native = owner.Components.Get<Tonemapping>();
			Assert.AreEqual( Tonemapping.TonemappingMode.Linear, native.Mode );
			Assert.IsFalse( native.AutoExposureEnabled );
			Assert.AreEqual( 0.75f, display.ExposureCompensation );
			owner.Destroy(); scene.GameTick( 0 );
			Assert.AreEqual( 0, scene.GetAllComponents<Tonemapping>().Count() );
			Assert.AreEqual( 0, scene.GetAllComponents<Titanfall2Tonemapping>().Count() );
		}
		finally { scene.Destroy(); }
	}

	[TestMethod]
	[DataRow( false )]
	[DataRow( true )]
	public void NativeEffectSelectionUsesBaselineAndPreservesExplicitCameraOverride( bool cameraOverride )
	{
		if ( !Graphics.IsAvailable ) Assert.Inconclusive( "Requires SBOX_TEST_GRAPHICS=1." );
		var scene = new Scene();
		try
		{
			using var scope = scene.Push();
			var cameraObject = new GameObject();
			var camera = cameraObject.AddComponent<CameraComponent>();
			var host = cameraObject.AddComponent<Tonemapping>();
			host.Mode = Tonemapping.TonemappingMode.AgX;
			host.Enabled = cameraOverride;
			var owner = new GameObject();
			var volume = owner.AddComponent<PostProcessVolume>();
			volume.SceneVolume = new Sandbox.Volumes.SceneVolume { Type = Sandbox.Volumes.SceneVolume.VolumeTypes.Infinite };
			volume.Priority = -100;
			var display = Titanfall2Tonemapping.CreateMapDisplay( owner, 0 );
			display.BloomStrength = 0;
			var baseline = owner.Components.Get<Tonemapping>();
			var build = typeof( PostProcessSystem ).GetMethod( "UpdateCamera", BindingFlags.Instance | BindingFlags.NonPublic );
			build.Invoke( scene.GetSystem<PostProcessSystem>(), [camera] );
			var selected = cameraOverride ? host : baseline;
			var attributes = (RenderAttributes)typeof( BasePostProcess ).GetField( "Attributes", BindingFlags.Instance | BindingFlags.NonPublic ).GetValue( selected );
			var expected = cameraOverride ? Tonemapping.TonemappingMode.AgX : Tonemapping.TonemappingMode.Linear;
			Assert.AreEqual( (int)expected, attributes.GetComboInt( "D_TONEMAPPING" ) );
			Assert.IsFalse( camera.AutoExposure.Enabled );
			var layers = camera.PostProcess.Layers.Values.SelectMany( value => value ).ToArray();
			Assert.AreEqual( 1, layers.Count( layer => layer.Name == "Tonemapping" ) );
			Assert.AreEqual( 1, layers.Count( layer => layer.Name == "Titanfall2Display" ) );
			Assert.AreEqual( Tonemapping.TonemappingMode.AgX, host.Mode );
			Assert.IsTrue( host.AutoExposureEnabled );
		}
		finally { scene.Destroy(); }
	}
}
