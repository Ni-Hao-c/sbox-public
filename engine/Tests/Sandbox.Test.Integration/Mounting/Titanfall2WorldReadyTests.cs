namespace MountingTests;

/// <summary>Readiness must follow actual native collision, through the scene's update loop.</summary>
[TestClass]
public class Titanfall2WorldReadyTest
{
	[TestMethod]
	public void ReadyFollowsWorldCollisionLossAndRecovery()
	{
		var scene = new Scene();
		try
		{
			using var scope = scene.Push();
			Game.TypeLibrary.AddAssembly( typeof( Titanfall2WorldReady ).Assembly, false );
			var template = scene.CreateObject();
			template.Components.Create<Titanfall2StaticPropStreamer>().KeepAllCollisions = true;
			// Cloning executes the production OnLoad, including the empty resident-prop preload.
			var root = template.Clone();
			var props = root.Components.Get<Titanfall2StaticPropStreamer>();
			Assert.IsTrue( props.ResidentCollisionsReady );
			var model = Model.Builder.AddCollisionBox( new Vector3( 16 ), Vector3.Zero ).Create();
			var world = root.Components.Create<ModelCollider>();
			world.Static = true;
			world.Model = model;
			var ready = root.Components.Create<Titanfall2WorldReady>();
			ready.Props = props;
			ready.WorldCollision = world;
			scene.GameTick();
			Assert.IsTrue( root.Tags.Has( Titanfall2WorldReady.ReadyTag ), "Native world collision and a completed preload must publish readiness." );
			Assert.IsTrue( scene.Trace.Ray( new Vector3( 0, 0, 50 ), new Vector3( 0, 0, -50 ) ).Run().Hit );

			world.Model = null;
			scene.GameTick();
			Assert.IsFalse( root.Tags.Has( Titanfall2WorldReady.ReadyTag ), "An active collider without native shapes must revoke readiness." );
			Assert.IsFalse( scene.Trace.Ray( new Vector3( 0, 0, 50 ), new Vector3( 0, 0, -50 ) ).Run().Hit );

			world.Model = model;
			scene.GameTick();
			Assert.IsTrue( root.Tags.Has( Titanfall2WorldReady.ReadyTag ) );
			props.Enabled = false;
			scene.GameTick();
			Assert.IsFalse( root.Tags.Has( Titanfall2WorldReady.ReadyTag ) );
			props.Enabled = true;
			scene.GameTick();
			Assert.IsTrue( root.Tags.Has( Titanfall2WorldReady.ReadyTag ) );
			ready.Enabled = false;
			Assert.IsFalse( root.Tags.Has( Titanfall2WorldReady.ReadyTag ) );
		}
		finally
		{
			scene.Destroy();
		}
	}
}
