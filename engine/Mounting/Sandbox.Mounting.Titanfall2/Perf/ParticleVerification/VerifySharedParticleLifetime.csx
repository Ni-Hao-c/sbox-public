// Dedicated test editor only: this check refreshes the Titanfall 2 mount.
// It uses real animated retail textures and two isolated scene consumers.
var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
await Sandbox.Mounting.Directory.Mount( "titanfall2" );
var mount = Sandbox.Mounting.Directory.Get( "titanfall2" );
if ( mount?.IsMounted != true ) throw new Exception( "Titanfall 2 must be installed and mounted." );
mount.GetType().GetMethod( "EnsureRuntimeTypesRegistered", flags ).Invoke( mount, null );
var method = mount.GetType().GetMethod( "TryGetLegacyParticleSprite", flags );
var framesField = mount.GetType().GetField( "_legacyParticleTextureFrames", flags );
var spritesField = mount.GetType().GetField( "_legacyParticleSprites", flags );
Sprite Load( string name )
{
	var arguments = new object[] { name, 12f, null, null };
	if ( !(bool)method.Invoke( mount, arguments ) ) throw new Exception( $"{name}: {arguments[3]}" );
	return (Sprite)arguments[2];
}
Texture[] Frames( Sprite sprite ) => sprite.Animations.SelectMany( a => a.Frames ).Select( f => f.Texture ).Distinct().ToArray();
var rows = new System.Collections.Generic.List<object>();
void Check( string name, bool passed ) => rows.Add( new { name, passed } );
var source = "particle/muzzleflash/flash_core";
var sprite = Load( source );
var textures = Frames( sprite );
if ( textures.Length < 2 ) throw new Exception( "The fixture must exercise the shared multi-frame GPU cache." );
Check( "real-animated-retail-sheet-is-cached", textures.Length > 1 && ((System.Collections.IDictionary)framesField.GetValue( mount )).Count > 0 );
Check( "source-textures-are-valid", textures.All( t => t is { IsValid: true, IsError: false } ) );
Scene MakeScene( string name, Sprite shared )
{
	var scene = new Scene { Name = name };
	using ( scene.Push() )
	{
		var go = new GameObject( scene, true, "Shared particle consumer" );
		go.AddComponent<Titanfall2SceneResourceOwner>().MountIdent = "titanfall2";
		go.AddComponent<ParticleEffect>();
		go.AddComponent<ParticleSpriteRenderer>().Sprite = shared;
	}
	return scene;
}
var first = MakeScene( "Lifetime first", sprite );
var second = MakeScene( "Lifetime second", sprite );
try
{
	Check( "both-consumers-share-the-same-sprite", ReferenceEquals( first.GetAllComponents<ParticleSpriteRenderer>().Single().Sprite,
		second.GetAllComponents<ParticleSpriteRenderer>().Single().Sprite ) );
	first.Destroy();
	Check( "destroying-one-scene-keeps-mount-cache", ((System.Collections.IDictionary)framesField.GetValue( mount )).Count > 0 );
	Check( "surviving-scene-keeps-valid-frames", Frames( second.GetAllComponents<ParticleSpriteRenderer>().Single().Sprite )
		.All( t => t is { IsValid: true, IsError: false } ) );
	cancellationToken.ThrowIfCancellationRequested();
	await mount.RefreshInternal();
	Check( "refresh-completes", mount.IsMounted );
	Check( "refresh-clears-old-mount-frame-cache", ((System.Collections.IDictionary)framesField.GetValue( mount )).Count == 0 );
	Check( "refresh-does-not-dispose-old-consumer-frames", textures.All( t => t is { IsValid: true, IsError: false } ) );
	var replacement = Load( source );
	Check( "remounted-source-builds-new-sprite", !ReferenceEquals( replacement, sprite ) && Frames( replacement ).Length > 1 );
	var count = ((System.Collections.IDictionary)spritesField.GetValue( mount )).Count;
	second.Destroy();
	Check( "old-scene-destruction-keeps-new-generation-cache", count > 0 && ((System.Collections.IDictionary)spritesField.GetValue( mount )).Count == count );
	Check( "new-generation-textures-stay-valid", Frames( replacement ).All( t => t is { IsValid: true, IsError: false } ) );
	GC.Collect();
	GC.WaitForPendingFinalizers();
	Check( "live-sprite-survives-gc-after-refresh", Frames( replacement ).All( t => t is { IsValid: true, IsError: false } ) );
	GC.KeepAlive( replacement );
}
finally
{
	first.Destroy();
	second.Destroy();
}
var output = System.IO.Path.Combine( exchangeDirectory, "shared-particle-lifetime.json" );
System.IO.File.WriteAllText( output, System.Text.Json.JsonSerializer.Serialize( rows, new System.Text.Json.JsonSerializerOptions { WriteIndented = true } ) );
return new { output, rows };
