using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace SceneTests;

[TestClass]
[DoNotParallelize]
public class Titanfall2AudioLifecycleTests : SceneTest
{
	const BindingFlags Instance = BindingFlags.NonPublic | BindingFlags.Instance;
	Scene _scene;
	Titanfall2Mount _mount;

	[TestInitialize]
	public void CreateScene()
	{
		_scene = new Scene();
		_mount = new Titanfall2Mount();
		// Zone ownership does not require playback. A non-native resource sentinel
		// exercises the real resolver cache without starting an audio device.
		var cache = (Dictionary<string, SoundFile>)typeof( Titanfall2Mount ).GetField( "_soundFileCache", Instance ).GetValue( _mount );
		cache["sound/test|loop"] = (SoundFile)RuntimeHelpers.GetUninitializedObject( typeof( SoundFile ) );
	}

	[TestCleanup]
	public void Cleanup() => _scene?.Destroy();

	Titanfall2MapAudio CreateAudio()
	{
		var audio = _scene.CreateObject().AddComponent<Titanfall2MapAudio>();
		typeof( Titanfall2MapAudio ).GetField( "_mount", Instance ).SetValue( audio, _mount );
		Configure( audio );
		return audio;
	}

	void Configure( Titanfall2MapAudio audio ) => typeof( Titanfall2MapAudio ).GetMethod( "Configure", Instance ).Invoke( audio,
		[_mount, "test", Array.Empty<Titanfall2MapAudio.Emitter>(),
			new[] { new Titanfall2MapAudio.SoundscapeZone( "test", Vector3.Zero, 512 ) }, 0.4f, 2] );

	[TestMethod]
	public void ReconfigureReplacesOwnedZones()
	{
		using var scope = _scene.Push();
		var audio = CreateAudio();
		var first = audio.GameObject.Children.Single();
		Configure( audio );
		Assert.IsFalse( first.IsValid );
		Assert.AreEqual( 1, audio.GameObject.Children.Count );
		Assert.AreEqual( 1, audio.ZoneCount );
	}

	[TestMethod]
	public void RemovingComponentReclaimsReparentedZoneAndPreservesHostChildren()
	{
		using var scope = _scene.Push();
		var audio = CreateAudio();
		var zone = audio.GameObject.Children.Single();
		zone.SetParent( _scene.CreateObject() );
		var host = audio.GameObject;
		var unrelated = new GameObject( host, true, "unrelated" );
		unrelated.Flags |= GameObjectFlags.NotSaved;
		audio.Destroy();
		Assert.IsFalse( zone.IsValid );
		Assert.IsTrue( host.IsValid );
		Assert.IsTrue( unrelated.IsValid );
	}

	[TestMethod]
	public void DisablingAudioAlsoDisablesItsZoneTriggers()
	{
		using var scope = _scene.Push();
		var audio = CreateAudio();
		var zone = audio.GameObject.Children.Single();
		audio.Enabled = false;
		Assert.IsFalse( zone.Enabled );
		audio.Enabled = true;
		Assert.IsTrue( zone.Enabled );
		Assert.AreEqual( 1, audio.ZoneCount );
	}

	[TestMethod]
	public void GeneratedZonesAreNotSerializedOrDuplicatedAsAuthoredChildren()
	{
		using var scope = _scene.Push();
		var audio = CreateAudio();
		var zone = audio.GameObject.Children.Single();
		Assert.IsTrue( zone.Flags.HasFlag( GameObjectFlags.NotSaved ) );
		// The engine uses NotSaved for both serialization and cloning.
		Assert.AreEqual( 0, audio.GameObject.Serialize()["Children"]?.AsArray().Count ?? 0 );
	}

	[TestMethod]
	public void InvalidZonePayloadCannotPublishEarlierValidZones()
	{
		using var scope = _scene.Push();
		using var stream = new MemoryStream();
		using var writer = new BinaryWriter( stream );
		writer.Write( 0x41504654 );
		writer.Write( 1 );
		writer.Write( 2 );
		writer.Write( "test" );
		for ( var i = 0; i < 3; ++i ) writer.Write( 0f );
		writer.Write( 512f );
		// The second declared zone is missing.
		var audio = _scene.CreateObject().AddComponent<Titanfall2MapAudio>();
		typeof( Titanfall2MapAudio ).GetField( "_mount", Instance ).SetValue( audio, _mount );
		audio.EncodedZones = Convert.ToBase64String( stream.ToArray() );
		typeof( Titanfall2MapAudio ).GetField( "_initialized", Instance ).SetValue( audio, false );
		_scene.GameTick();
		Assert.AreEqual( 0, audio.ZoneCount );
		Assert.AreEqual( 0, audio.GameObject.Children.Count );
	}
}
