using System;
using System.Collections;
using System.IO;
using System.Reflection;
using Titanfall2;

namespace SceneTests;

[TestClass]
[DoNotParallelize]
public class Titanfall2MountTests : SceneTest
{
	static readonly BindingFlags PrivateStatic = BindingFlags.NonPublic | BindingFlags.Static;
	Scene _scene;

	[TestCleanup]
	public void DestroyTestScene() => _scene?.Destroy();

	static object[] Decode( byte[] payload ) => ((IEnumerable)typeof( Titanfall2StaticPropStreamer )
		.GetMethod( "Decode", PrivateStatic ).Invoke( null, [Convert.ToBase64String( payload )] )).Cast<object>().ToArray();

	static T Field<T>( object record, string name ) => (T)record.GetType().GetProperty( name ).GetValue( record );

	[DataTestMethod]
	[DataRow( 0u, 0u, 0x00EB0280u )]
	[DataRow( 1u, 0u, 0x00EB0280u )]
	[DataRow( 2u, 0u, 0x00E30240u )]
	[DataRow( 8u, 0u, 0x00A30240u )]
	[DataRow( 0x00080002u, 0u, 0x00E30240u )]
	[DataRow( 0x00400008u, 0u, 0x00A30240u )]
	[DataRow( 1u, 0x00EB0280u, 0u )]
	[DataRow( 1u, 0xFFFFFFFFu, 0u )]
	[DataRow( 0x20u, 0u, 0x20u )]
	[DataRow( 0x80000001u, 0u, 0x80EB0280u )]
	public void StaticPropContentsMatchesRetailExpansionAndRemoval( uint mdl, uint remove, uint expected )
		=> Assert.AreEqual( expected, Titanfall2CollisionSource.StaticPropContents( mdl, remove ) );


	[DataTestMethod]
	[DataRow( "OnUpdate", typeof( Sandbox.Internal.IUpdateSubscriber ) )]
	[DataRow( "OnFixedUpdate", typeof( Sandbox.Internal.IFixedUpdateSubscriber ) )]
	[DataRow( "OnPreRender", typeof( Sandbox.Internal.IPreRenderSubscriber ) )]
	public void MountCallbacksSubscribeToSceneDispatch( string callback, Type subscriber )
	{
		// Mount DLLs are built without the game compiler's interface rewriting pass.
		foreach ( var type in typeof( Titanfall2Mount ).Assembly.GetTypes().Where( t => typeof( Component ).IsAssignableFrom( t ) ) )
		{
			var method = type.GetMethod( callback, BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly );
			if ( method is null || method.GetBaseDefinition().DeclaringType != typeof( Component ) ) continue;
			Assert.IsTrue( subscriber.IsAssignableFrom( type ), $"{type.Name}.{callback} is not subscribed to scene dispatch." );
		}
	}

	[DataTestMethod]
	[DataRow( "mp_angel_city" )]
	[DataRow( "mp_angel_city.bsp" )]
	[DataRow( "maps/mp_angel_city.bsp.scene" )]
	[DataRow( "mount://titanfall2/maps/mp_angel_city.bsp.world.scene" )]
	public void PureWorldPathIsAdditiveAndNormalizesInput( string input )
	{
		var scene = _scene = new Scene();
		using var scope = scene.Push();
		Game.TypeLibrary.AddAssembly( typeof( Titanfall2Map ).Assembly, false );
		var map = scene.CreateObject().Components.Create<Titanfall2Map>();
		map.MapName = input;
		Assert.AreEqual( "mount://titanfall2/maps/mp_angel_city.bsp.scene", map.ScenePath );
		map.PureWorld = true;
		Assert.AreEqual( "mount://titanfall2/maps/mp_angel_city.bsp.world.scene", map.ScenePath );
		Assert.IsTrue( map.SpawnSpectatorCamera ); // Legacy preference survives changing the world mode.
	}

	[TestMethod]
	public void ResidentCollisionPolicySurvivesClone()
	{
		var scene = _scene = new Scene();
		using var scope = scene.Push();
		Game.TypeLibrary.AddAssembly( typeof( Titanfall2StaticPropStreamer ).Assembly, false );
		var go = scene.CreateObject();
		var streamer = go.Components.Create<Titanfall2StaticPropStreamer>();
		Assert.IsFalse( streamer.KeepAllCollisions );
		streamer.KeepAllCollisions = true;
		var clone = go.Clone().Components.Get<Titanfall2StaticPropStreamer>();
		Assert.IsTrue( clone.KeepAllCollisions );
		// The clone runs OnLoad. An empty prop list has no collision work to wait for;
		// world collision is independently required by Titanfall2WorldReady.
		Assert.IsTrue( clone.ResidentCollisionsReady );
		Assert.AreEqual( 0, clone.ResidentCollisionCount );
	}

	[DataTestMethod]
	[DataRow( "base64" )]
	[DataRow( "signature" )]
	[DataRow( "version" )]
	[DataRow( "truncated" )]
	[DataRow( "partial" )]
	public void CorruptResidentPropsCannotReportReady( string corruption )
	{
		var scene = _scene = new Scene();
		using var scope = scene.Push();
		Game.TypeLibrary.AddAssembly( typeof( Titanfall2StaticPropStreamer ).Assembly, false );
		var records = new[]
		{
			new Titanfall2BspReader.StaticPropInstance { ModelPath = "models/test.mdl" },
			new Titanfall2BspReader.StaticPropInstance { ModelPath = "models/test.mdl" }
		};
		var encoded = (string)typeof( Titanfall2StaticPropStreamer ).GetMethod( "Encode", PrivateStatic )
			.Invoke( null, [records, new Titanfall2BspReader.BspVisibility()] );
		var bytes = Convert.FromBase64String( encoded );
		if ( corruption == "signature" ) bytes[0] ^= 0xFF;
		if ( corruption == "version" ) bytes[4] = 0xFF;
		if ( corruption == "truncated" ) Array.Resize( ref bytes, 4 );
		if ( corruption == "partial" ) Array.Resize( ref bytes, bytes.Length - 1 );
		var template = scene.CreateObject().Components.Create<Titanfall2StaticPropStreamer>();
		template.KeepAllCollisions = true;
		template.EncodedProps = corruption == "base64" ? "invalid!" : Convert.ToBase64String( bytes );
		var clone = template.GameObject.Clone().Components.Get<Titanfall2StaticPropStreamer>();
		Assert.IsFalse( clone.ResidentCollisionsReady, "A rejected prop payload must not be treated as an empty successful preload." );
	}

	[TestMethod]
	public void WorldReadyCannotPublishWithoutCollisionDependencies()
	{
		var scene = _scene = new Scene();
		using var scope = scene.Push();
		Game.TypeLibrary.AddAssembly( typeof( Titanfall2WorldReady ).Assembly, false );
		var go = scene.CreateObject();
		var ready = go.Components.Create<Titanfall2WorldReady>();
		go.Tags.Add( Titanfall2WorldReady.ReadyTag );
		scene.GameTick();
		Assert.IsFalse( go.Tags.Has( Titanfall2WorldReady.ReadyTag ) );
		go.Tags.Add( Titanfall2WorldReady.ReadyTag );
		ready.Enabled = false;
		Assert.IsFalse( go.Tags.Has( Titanfall2WorldReady.ReadyTag ) );
	}

	[TestMethod]
	public void WorldReadyClearsInheritedTagBeforeFirstTick()
	{
		var scene = _scene = new Scene();
		using var scope = scene.Push();
		Game.TypeLibrary.AddAssembly( typeof( Titanfall2WorldReady ).Assembly, false );
		var go = scene.CreateObject();
		go.Tags.Add( Titanfall2WorldReady.ReadyTag );
		var ready = go.Components.Create<Titanfall2WorldReady>();
		Assert.IsFalse( go.Tags.Has( Titanfall2WorldReady.ReadyTag ), "An enabled publisher must validate inherited readiness again." );
		ready.Enabled = false;
		go.Tags.Add( Titanfall2WorldReady.ReadyTag );
		ready.Enabled = true;
		Assert.IsFalse( go.Tags.Has( Titanfall2WorldReady.ReadyTag ) );
	}

	[DataTestMethod]
	[DataRow( false )]
	[DataRow( true )]
	public void WorldReadyCloneRevalidatesSerializedTag( bool enabled )
	{
		var scene = _scene = new Scene();
		using var scope = scene.Push();
		Game.TypeLibrary.AddAssembly( typeof( Titanfall2WorldReady ).Assembly, false );
		var go = scene.CreateObject();
		go.Components.Create<Titanfall2WorldReady>().Enabled = enabled;
		go.Tags.Add( Titanfall2WorldReady.ReadyTag );
		var clone = go.Clone();
		Assert.IsFalse( clone.Tags.Has( Titanfall2WorldReady.ReadyTag ), "A serialized tag is not proof that the clone has collision bodies." );
	}

	[TestMethod]
	public void CollisionDiagnosticIdentitySurvivesClone()
	{
		var scene = _scene = new Scene();
		using var scope = scene.Push();
		Game.TypeLibrary.AddAssembly( typeof( Titanfall2CollisionSource ).Assembly, false );
		var go = scene.CreateObject();
		var source = go.Components.Create<Titanfall2CollisionSource>();
		source.MountIdent = "titanfall2";
		source.MapPath = "mount://titanfall2/maps/test.bsp.scene";
		source.ModelPath = "models/test.mdl";
		source.SourceIndex = 12345;
		source.HasInstanceMetadata = true;
		source.SolidType = 6;
		source.ReservedCollisionData = 0xFEDCBA98;
		source.CollisionFlagsRemove = 0xFFFFFFFF;
		source.GeometrySource = "test";
		var restored = go.Clone().Components.Get<Titanfall2CollisionSource>();
		Assert.IsNotNull( restored );
		Assert.AreEqual( source.MapPath, restored.MapPath );
		Assert.AreEqual( source.ModelPath, restored.ModelPath );
		Assert.AreEqual( source.SourceIndex, restored.SourceIndex );
		Assert.AreEqual( source.SolidType, restored.SolidType );
		Assert.AreEqual( source.ReservedCollisionData, restored.ReservedCollisionData );
		Assert.AreEqual( source.CollisionFlagsRemove, restored.CollisionFlagsRemove );
		Assert.IsTrue( restored.HasInstanceMetadata );
		Assert.IsFalse( Field<bool>( Titanfall2CollisionSource.DescribeHit( default ), "Hit" ) );
	}

	[TestMethod]
	public void ShutdownReleasesPackageAndMaterialMetadata()
	{
		var mount = new Titanfall2Mount();
		var dictionaries = new[] { "_modelSources", "_texturePaths", "_shaderSetNames", "_shaderSetSources", "_shaderSources",
			"_materialPaths", "_materialSources", "_legacyMaterialSources", "_legacyTextureSources", "_legacyMaterialDefinitions",
			"_legacyMaterialErrors", "_legacyAnimatedTexturePaths", "_materialMetadata", "_shaderReflections", "_shaderReflectionErrors" }
			.Select( name => (IDictionary)typeof( Titanfall2Mount ).GetField( name, BindingFlags.Instance | BindingFlags.NonPublic ).GetValue( mount ) ).ToArray();
		foreach ( var dictionary in dictionaries )
		{
			var types = dictionary.GetType().GetGenericArguments();
			object key = types[0] == typeof( string ) ? "test" : 1UL;
			var value = types[1].IsValueType ? Activator.CreateInstance( types[1] ) : null;
			dictionary.Add( key, value );
		}
		typeof( Titanfall2Mount ).GetMethod( "Shutdown", BindingFlags.Instance | BindingFlags.NonPublic ).Invoke( mount, null );
		foreach ( var dictionary in dictionaries ) Assert.AreEqual( 0, dictionary.Count );
	}

	[TestMethod]
	public void CollisionMetadataSurvivesSceneSerialization()
	{
		var source = new byte[] { 0, 1, 2, 6, 7 }.Select( ( solid, index ) => new Titanfall2BspReader.StaticPropInstance
		{
			SourceIndex = index + 10,
			ModelPath = "models/test.mdl",
			SolidType = solid,
			ReservedCollisionData = 0xFEDCBA98,
			CollisionFlagsRemove = 0xFFFFFFFF
		} ).ToArray();
		var encoded = (string)typeof( Titanfall2StaticPropStreamer ).GetMethod( "Encode", PrivateStatic )
			.Invoke( null, [source, new Titanfall2BspReader.BspVisibility()] );
		var bytes = Convert.FromBase64String( encoded );
		Assert.AreEqual( 8, BitConverter.ToInt32( bytes, 4 ) );
		var decoded = Decode( bytes );
		Assert.AreEqual( source.Length, decoded.Length );
		for ( var i = 0; i < source.Length; i++ )
		{
			Assert.AreEqual( source[i].SourceIndex, Field<int>( decoded[i], "SourceIndex" ) );
			Assert.AreEqual( source[i].SolidType, Field<byte>( decoded[i], "SolidType" ) );
			Assert.AreEqual( source[i].IsCollidable, Field<bool>( decoded[i], "Collidable" ) );
			Assert.AreEqual( source[i].ReservedCollisionData, Field<uint>( decoded[i], "ReservedCollisionData" ) );
			Assert.AreEqual( source[i].CollisionFlagsRemove, Field<uint>( decoded[i], "CollisionFlagsRemove" ) );
		}
		Array.Resize( ref bytes, bytes.Length - 1 );
		Assert.ThrowsException<EndOfStreamException>( () => Decode( bytes ) );
	}

	[TestMethod]
	public void PropFadeDistanceSurvivesSceneSerialization()
	{
		// Index 1 has no model and is skipped, so the fades must stay aligned with BSP order.
		var source = new[] { "models/a.mdl", "", "models/b.mdl", "models/c.mdl" }
			.Select( ( path, index ) => new Titanfall2BspReader.StaticPropInstance { SourceIndex = index, ModelPath = path } ).ToArray();
		var fades = new[] { 2070f, 999f, 0f, float.NaN };
		var encoded = (string)typeof( Titanfall2StaticPropStreamer ).GetMethod( "EncodeWithFade", PrivateStatic )
			.Invoke( null, [source, new Titanfall2BspReader.BspVisibility(), fades] );
		var decoded = Decode( Convert.FromBase64String( encoded ) );
		CollectionAssert.AreEqual( new[] { 2070f, 0f, 0f }, decoded.Select( prop => Field<float>( prop, "FadeDistance" ) ).ToArray() );
		CollectionAssert.AreEqual( new[] { 0, 2, 3 }, decoded.Select( prop => Field<int>( prop, "SourceIndex" ) ).ToArray() );
	}

	[DataTestMethod]
	[DataRow( 3 )]
	[DataRow( 4 )]
	[DataRow( 5 )]
	public void LegacyPayloadKeepsCollisionWithoutInventingMetadata( int version )
	{
		using var stream = new MemoryStream();
		using ( var writer = new BinaryWriter( stream, System.Text.Encoding.UTF8, true ) )
		{
			writer.Write( 0x32505354 );
			writer.Write( version );
			writer.Write( 1 );
			writer.Write( "models/test.mdl" );
			writer.Write( 1 );
			writer.Write( 0 );
			for ( var i = 0; i < 6; ++i ) writer.Write( 0f );
			writer.Write( 1f );
			writer.Write( true );
			for ( var i = 0; i < 3; ++i ) writer.Write( 1f );
			writer.Write( 0UL );
			writer.Write( 0UL );
			if ( version >= 4 ) writer.Write( false );
		}
		var decoded = Decode( stream.ToArray() ).Single();
		Assert.IsTrue( Field<bool>( decoded, "Collidable" ) );
		Assert.AreEqual( -1, Field<int>( decoded, "SourceIndex" ) );
		Assert.AreEqual( (byte)0, Field<byte>( decoded, "SolidType" ) );
		Assert.AreEqual( 0u, Field<uint>( decoded, "CollisionFlagsRemove" ) );
	}

	static Titanfall2DeferredNavMeshBuilder CreateBuilder( Scene scene )
	{
		Game.TypeLibrary.AddAssembly( typeof( Titanfall2DeferredNavMeshBuilder ).Assembly, false );
		return scene.CreateObject().Components.Create<Titanfall2DeferredNavMeshBuilder>();
	}

	[TestMethod]
	public void DisabledNavigationLeavesHostUntouched()
	{
		var scene = _scene = new Scene();
		using var scope = scene.Push();
		var original = new TagSet( ["host"] );
		scene.NavMesh.IncludedBodies = original;
		scene.NavMesh.DeferGeneration = false;
		scene.NavMesh.IsEnabled = true;
		var builder = CreateBuilder( scene );
		builder.Configure( false, 0 );
		Assert.IsTrue( scene.NavMesh.IsEnabled );
		Assert.IsFalse( scene.NavMesh.DeferGeneration );
		Assert.AreSame( original, scene.NavMesh.IncludedBodies );
	}

	[TestMethod]
	public void NavigationRestoresHostOnlyAfterLastOwnerLeaves()
	{
		var scene = _scene = new Scene();
		using var scope = scene.Push();
		var original = scene.NavMesh.IncludedBodies;
		var first = CreateBuilder( scene );
		var second = CreateBuilder( scene );
		first.Configure( true, 0 );
		second.Configure( true, 0 );
		Assert.IsTrue( scene.NavMesh.IsEnabled );
		first.Enabled = false;
		Assert.IsTrue( scene.NavMesh.IsEnabled );
		Assert.AreNotSame( original, scene.NavMesh.IncludedBodies );
		second.Enabled = false;
		Assert.IsFalse( scene.NavMesh.IsEnabled );
		Assert.IsFalse( scene.NavMesh.DeferGeneration );
		Assert.AreSame( original, scene.NavMesh.IncludedBodies );
		first.Enabled = true;
		first.Configure( false, 0 );
		Assert.AreSame( original, scene.NavMesh.IncludedBodies );
	}

	[TestMethod]
	public void InactiveMapDoesNotAcquireNavigation()
	{
		var scene = _scene = new Scene();
		using var scope = scene.Push();
		var original = scene.NavMesh.IncludedBodies;
		var builder = CreateBuilder( scene );
		builder.GameObject.Enabled = false;
		builder.Configure( true, 0 );
		Assert.IsFalse( scene.NavMesh.IsEnabled );
		Assert.IsFalse( scene.NavMesh.DeferGeneration );
		Assert.AreSame( original, scene.NavMesh.IncludedBodies );
		builder.GameObject.Enabled = true;
		Assert.IsTrue( scene.NavMesh.IsEnabled );
		builder.Configure( false, 0 );
		Assert.AreSame( original, scene.NavMesh.IncludedBodies );
	}

	[TestMethod]
	public void NavigationDoesNotOverwriteLaterHostFilter()
	{
		var scene = _scene = new Scene();
		using var scope = scene.Push();
		var builder = CreateBuilder( scene );
		builder.Configure( true, 0 );
		var changed = new TagSet( ["replacement"] );
		scene.NavMesh.IncludedBodies = changed;
		scene.NavMesh.DeferGeneration = false;
		builder.Configure( false, 0 );
		Assert.AreSame( changed, scene.NavMesh.IncludedBodies );
		Assert.IsFalse( scene.NavMesh.DeferGeneration );
	}

	[TestMethod]
	public void OnDemandNavigationLeavesDisabledHostUntouched()
	{
		var scene = _scene = new Scene();
		using var scope = scene.Push();
		var original = scene.NavMesh.IncludedBodies;
		var builder = CreateBuilder( scene );
		builder.Configure( false, true, 2048f, 0, BBox.FromPositionAndSize( -1000, 1000 ) );
		Assert.IsFalse( scene.NavMesh.IsEnabled );
		Assert.IsFalse( scene.NavMesh.DeferGeneration );
		Assert.AreSame( original, scene.NavMesh.IncludedBodies );
	}

	[TestMethod]
	public void OnDemandNavigationDefersEnabledHostAndRestoresIt()
	{
		var scene = _scene = new Scene();
		using var scope = scene.Push();
		var original = scene.NavMesh.IncludedBodies;
		scene.NavMesh.DeferGeneration = false;
		scene.NavMesh.IsEnabled = true;
		var builder = CreateBuilder( scene );
		builder.Configure( false, true, 2048f, 0, BBox.FromPositionAndSize( -1000, 1000 ) );
		Assert.IsTrue( scene.NavMesh.IsEnabled );
		// The engine's whole-map load pass is skipped and only mounted world collision is used.
		Assert.IsTrue( scene.NavMesh.DeferGeneration );
		Assert.IsTrue( scene.NavMesh.IncludedBodies.Has( Titanfall2DeferredNavMeshBuilder.NavMeshBodyTag ) );
		builder.Enabled = false;
		Assert.IsTrue( scene.NavMesh.IsEnabled );
		Assert.IsFalse( scene.NavMesh.DeferGeneration );
		Assert.AreSame( original, scene.NavMesh.IncludedBodies );
	}
}
