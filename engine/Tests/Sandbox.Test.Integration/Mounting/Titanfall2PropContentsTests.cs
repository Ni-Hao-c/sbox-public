using System;
using System.Collections;
using System.Reflection;
using Titanfall2;

namespace MountingTests;

// Model creation and native collider traces require the booted integration tier.
[TestClass]
[DoNotParallelize]
public class Titanfall2PropContentsTest
{
	static readonly BindingFlags PrivateStatic = BindingFlags.NonPublic | BindingFlags.Static;
	Scene _scene;
	static object[] Decode( byte[] payload ) => ((IEnumerable)typeof( Titanfall2StaticPropStreamer )
		.GetMethod( "Decode", PrivateStatic ).Invoke( null, [Convert.ToBase64String( payload )] )).Cast<object>().ToArray();
	static T Field<T>( object record, string name ) => (T)record.GetType().GetProperty( name ).GetValue( record );
	[DataTestMethod]
	[DataRow( false )]
	[DataRow( true )]
	public void ZeroContentsSkipsOnlyKnownInstances( bool legacy )
	{
		var scene = _scene = new Scene();
		using var scope = scene.Push();
		Game.TypeLibrary.AddAssembly( typeof( Titanfall2StaticPropStreamer ).Assembly, false );
		var streamer = scene.CreateObject().Components.Create<Titanfall2StaticPropStreamer>();
		streamer.KeepAllCollisions = true;
		var records = new[] { new Titanfall2BspReader.StaticPropInstance { ModelPath = "models/test_missing_contents.mdl",
			SourceIndex = legacy ? -1 : 7, SolidType = 6, Scale = 1, CollisionFlagsRemove = uint.MaxValue } };
		var encoded = (string)typeof( Titanfall2StaticPropStreamer ).GetMethod( "Encode", PrivateStatic )
			.Invoke( null, [records, new Titanfall2BspReader.BspVisibility()] );
		var source = Decode( Convert.FromBase64String( encoded ) ).Single();
		var privateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
		// Exercise the real fallback branch without asking the managed-only test host
		// to mount a retail game or wait on a resource-loader main-thread callback.
		streamer.MountIdent = "fixture";
		((IDictionary)typeof( Titanfall2StaticPropStreamer ).GetField( "_modelFailureRetryAfter", privateInstance ).GetValue( streamer ))
			.Add( "mount://fixture/models/test_missing_contents.mdl.vmdl", double.MaxValue );
		var type = typeof( Titanfall2StaticPropStreamer ).GetNestedType( "PropInstance", BindingFlags.NonPublic );
		var instance = Activator.CreateInstance( type, [source, null, 1f, true, false, false] );
		var model = Model.Builder.AddCollisionHull( new BBox( -Vector3.One, Vector3.One ).Corners.ToArray() )
			.WithData( new Titanfall2ModelCollisionData { ModelContents = 1 } ).Create();
		try
		{
			var ensure = typeof( Titanfall2StaticPropStreamer ).GetMethod( "EnsureCollisionState", privateInstance );
			Assert.IsTrue( (bool)ensure.Invoke( streamer, [instance, Vector3.Zero, model] ) );
			Assert.AreEqual( !legacy, Field<bool>( instance, "CollisionFiltered" ) );
			Assert.AreEqual( legacy, Field<GameObject>( instance, "CollisionObject" ).IsValid() );
			Assert.IsFalse( (bool)ensure.Invoke( streamer, [instance, Vector3.Zero, model] ), "Repeated checks must not create bodies or recount exclusions." );
			Assert.AreEqual( legacy ? 0 : 1, (int)typeof( Titanfall2StaticPropStreamer ).GetField( "_collisionFilteredProps", privateInstance ).GetValue( streamer ) );
		}
		finally { scene.Destroy(); _scene = null; }
	}

}
