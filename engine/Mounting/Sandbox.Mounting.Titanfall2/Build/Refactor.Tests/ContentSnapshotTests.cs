using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Text;
using ResourceType = Sandbox.Mounting.ResourceType;

[TestClass]
public class ContentSnapshotTests
{
	sealed class Fixture : IDisposable
	{
		internal readonly string Root = Path.Combine( Path.GetTempPath(), "tf2-content-" + Guid.NewGuid().ToString( "N" ) );
		internal string Write( string path, string text )
		{
			var full = Path.Combine( Root, path );
			Directory.CreateDirectory( Path.GetDirectoryName( full )! ); File.WriteAllText( full, text ); return full;
		}
		internal void Mod( string directory, string name, int priority = 0, string version = "1.0.0" ) =>
			Write( directory + "/mod.json", $$"""{ "Name": "{{name}}", "LoadPriority": {{priority}}, "Version": "{{version}}" }""" );
		public void Dispose() { if ( Directory.Exists( Root ) ) Directory.Delete( Root, recursive: true ); }
	}

	[DataTestMethod]
	[DataRow( "models\\a/./b/../x.mdl", "models/a/x.mdl" )]
	[DataRow( "Materials//WALL.vmt", "materials/wall.vmt" )]
	public void PathsHaveOneCanonicalIdentity( string path, string expected )
	{
		Assert.IsTrue( Titanfall2ContentSnapshot.TryNormalize( path, out var normalized ) ); Assert.AreEqual( expected, normalized );
	}

	[DataTestMethod]
	[DataRow( "../a" )]
	[DataRow( "a/../../b" )]
	[DataRow( "/absolute" )]
	[DataRow( "C:\\host" )]
	[DataRow( "\\\\server\\file" )]
	[DataRow( "a\0b" )]
	public void UnsafePathsAreRejected( string path ) => Assert.IsFalse( Titanfall2ContentSnapshot.TryNormalize( path, out _ ) );

	[TestMethod]
	public void HighestPriorityWinsForPrimaryCompanionAndDependencyWithCandidatesRetained()
	{
		using var fixture = new Fixture();
		fixture.Mod( "mods/low", "Low", 1 ); fixture.Mod( "mods/high", "High", 5 );
		fixture.Write( "mods/low/mod/models/a.mdl", "base-model" );
		fixture.Write( "mods/low/mod/models/a.mdl.vvd", "base-companion" );
		fixture.Write( "mods/low/mod/materials/a.vmt", "base-material" );
		fixture.Write( "mods/high/mod/models/a.mdl.vvd", "mod-companion" );
		fixture.Write( "mods/high/mod/materials/a.vmt", "mod-material" );
		var snapshot = Titanfall2ContentDiscovery.Build( fixture.Root, fixture.Root );
		var source = snapshot.GetSource( "MODELS/a.mdl" );
		Assert.IsNotNull( source );
		Assert.IsTrue( ((ITitanfall2SiblingAssetSource)source).TryReadSibling( ".vvd", out var companion, out _ ) );
		Assert.AreEqual( "mod-companion", Encoding.UTF8.GetString( companion ) );
		Assert.IsTrue( ((ITitanfall2PathAssetSource)source).TryReadPath( "materials/a.vmt", out var material, out _ ) );
		Assert.AreEqual( "mod-material", Encoding.UTF8.GetString( material ) );
		Assert.AreEqual( 2, snapshot.Explain( "materials/a.vmt" ).Count );
		Assert.IsTrue( snapshot.Explain( "materials/a.vmt" )[0].SourceId.Contains( "High" ) );
		Assert.ThrowsException<NotSupportedException>( () => ((IList<Titanfall2ContentEntry>)snapshot.Entries).Clear() );
	}

	[TestMethod]
	public void OldSourceRetainsSnapshotAndChangedWinnerDoesNotFallBack()
	{
		using var fixture = new Fixture();
		fixture.Mod( "mods/low", "Low", 0 ); fixture.Mod( "mods/high", "High", 1 );
		fixture.Write( "mods/low/mod/a.txt", "base" );
		var path = fixture.Write( "mods/high/mod/a.txt", "high" );
		var first = Titanfall2ContentDiscovery.Build( fixture.Root, fixture.Root );
		var old = first.GetSource( "a.txt" );
		var time = File.GetLastWriteTimeUtc( path );
		File.WriteAllText( path, "edit" ); File.SetLastWriteTimeUtc( path, time );
		Assert.IsFalse( old.TryReadAllBytes( out _, out var error ) );
		StringAssert.Contains( error, "changed" );
		fixture.Write( "mods/high/mod/new.txt", "new" );
		Assert.IsNull( first.GetSource( "new.txt" ) );
		var second = Titanfall2ContentDiscovery.Build( fixture.Root, fixture.Root );
		Assert.AreNotEqual( first.Revision, second.Revision );
		Assert.IsTrue( second.GetSource( "a.txt" ).TryReadAllBytes( out var bytes, out _ ) );
		Assert.AreEqual( "edit", Encoding.UTF8.GetString( bytes ) );
	}

	[TestMethod]
	public void MainAndLegacyEnabledFormatsSelectExactVersionsWithoutWritingConfiguration()
	{
		using var fixture = new Fixture();
		fixture.Mod( "mods/a", "A" ); fixture.Mod( "mods/b", "B", version: "2.0.0" ); fixture.Mod( "mods/c", "C" );
		var path = fixture.Write( "enabledmods.json", "{ // comments allowed\n\"Version\":1,\"A\":{\"1.0.0\":false},\"B\":{\"1.0.0\":false},\"C\":false,}" );
		var before = File.ReadAllText( path );
		var profile = new Titanfall2NorthstarProfile( fixture.Root, null, [] );
		CollectionAssert.AreEqual( new[] { "B" }, profile.Mods.Select( mod => mod.Name ).ToArray() );
		Assert.AreEqual( before, File.ReadAllText( path ) );
	}

	[TestMethod]
	public void MissingModManifestIsDiagnosedAndEnabledConfigurationIsOptional()
	{
		using var fixture = new Fixture();
		fixture.Write( "mods/taskinoz.ApexFiringRange/mod/maps/mp_apex_firing_range.bsp", "map" );
		var missingManifest = new Titanfall2NorthstarProfile( fixture.Root, null, [] );
		Assert.AreEqual( 0, missingManifest.Mods.Count );
		Assert.IsTrue( missingManifest.Diagnostics.Any( diagnostic => diagnostic.Contains( "taskinoz.ApexFiringRange" ) && diagnostic.Contains( "mod.json is missing" ) ) );
		Assert.IsFalse( File.Exists( Path.Combine( fixture.Root, "enabledmods.json" ) ) );
		fixture.Mod( "mods/taskinoz.ApexFiringRange", "Apex Firing Range" );
		var withoutEnabledConfig = new Titanfall2NorthstarProfile( fixture.Root, null, [] );
		Assert.AreEqual( "Apex Firing Range", withoutEnabledConfig.Mods.Single().Name );
		Assert.IsFalse( File.Exists( Path.Combine( fixture.Root, "enabledmods.json" ) ) );
		fixture.Write( "enabledmods.json", "{\"Unrelated Mod\":false}" );
		var withoutEnabledEntry = new Titanfall2NorthstarProfile( fixture.Root, null, [] );
		Assert.AreEqual( "Apex Firing Range", withoutEnabledEntry.Mods.Single().Name );
	}

	[TestMethod]
	public void PackageLayoutsRemoteFirstLoadAndDuplicateVersionsMatchMain()
	{
		using var fixture = new Fixture();
		fixture.Mod( "mods/classic", "Classic" );
		fixture.Mod( "packages/Author-Pack-1.0.0/mods/pack", "Package" );
		fixture.Mod( "runtime/remote/mods/workshop", "Remote" );
		fixture.Mod( "runtime/remote/mods/Author-Remote-1.0.0/mods/remote", "RemotePackage" );
		fixture.Mod( "mods/duplicate1", "Duplicate", version: "1.0.0" );
		fixture.Mod( "mods/duplicate2", "Duplicate", version: "2.0.0" );
		var profile = new Titanfall2NorthstarProfile( fixture.Root, null, [] );
		CollectionAssert.AreEquivalent( new[] { "Classic", "Package" }, profile.Mods.Select( mod => mod.Name ).ToArray() );
		Assert.IsTrue( profile.Diagnostics.Any( text => text.Contains( "multiple enabled versions" ) ) );
	}

	[TestMethod]
	public void RpakRulesUsePreloadPostloadClosureAliasesAndFixedMapRegex()
	{
		using var fixture = new Fixture(); fixture.Mod( "mods/a", "A" );
		foreach ( var name in new[] { "pre", "post", "chain", "map", "alias", "unused" } ) fixture.Write( $"mods/a/paks/{name}.rpak", "fixture" );
		fixture.Write( "mods/a/paks/rpak.json", """
			{"Preload":{"pre.rpak":true},"Postload":{"post.rpak":"common.rpak","chain.rpak":"post.rpak"},
			"Aliases":{"alias-source.rpak":"alias.rpak"},"map.rpak":"mp_.*"}
			""" );
		var profile = new Titanfall2NorthstarProfile( fixture.Root, "mp_angel_city", ["common.rpak", "alias-source.rpak"] );
		CollectionAssert.AreEquivalent( new[] { "pre.rpak", "post.rpak", "chain.rpak", "map.rpak", "alias.rpak" }, profile.Mods[0].Rpaks.Select( Path.GetFileName ).ToArray() );
		var withoutMap = new Titanfall2NorthstarProfile( fixture.Root, null, ["common.rpak", "alias-source.rpak"] );
		Assert.IsFalse( withoutMap.Mods[0].Rpaks.Any( path => path.EndsWith( "map.rpak" ) ) );
	}

	[TestMethod]
	public void NewDependencyAndPriorityChangesInvalidateRevision()
	{
		using var fixture = new Fixture(); fixture.Mod( "mods/a", "A", 1 ); fixture.Mod( "mods/b", "B", 2 );
		fixture.Write( "mods/a/mod/a.txt", "a" ); fixture.Write( "mods/b/mod/a.txt", "b" );
		var before = Titanfall2ContentDiscovery.Build( fixture.Root, fixture.Root );
		fixture.Mod( "mods/a", "A", 3 );
		var reordered = Titanfall2ContentDiscovery.Build( fixture.Root, fixture.Root );
		Assert.AreNotEqual( before.Revision, reordered.Revision );
		Assert.IsTrue( reordered.Explain( "a.txt" )[0].SourceId.Contains( ":A/" ) );
		fixture.Write( "mods/a/mod/missing-before.txt", "dependency" );
		Assert.AreNotEqual( reordered.Revision, Titanfall2ContentDiscovery.Build( fixture.Root, fixture.Root ).Revision );
	}

	static void WriteVpk( string path, string content )
	{
		Directory.CreateDirectory( Path.GetDirectoryName( path )! );
		using var tree = new MemoryStream();
		using ( var writer = new BinaryWriter( tree, Encoding.UTF8, leaveOpen: true ) )
		{
			writer.Write( Encoding.ASCII.GetBytes( "txt\0 \0test\0" ) );
			writer.Write( 0u ); var bytes = Encoding.UTF8.GetBytes( content );
			writer.Write( (ushort)bytes.Length ); writer.Write( ushort.MaxValue ); writer.Write( bytes );
			writer.Write( new byte[3] );
		}
		using var file = File.Create( path ); using var header = new BinaryWriter( file );
		header.Write( 0x55AA1234u ); header.Write( 196610u ); header.Write( (uint)tree.Length ); header.Write( 0u ); header.Write( tree.ToArray() );
	}

	[TestMethod]
	public void LooseModOverridesRetailArchiveAndArchiveSourceCannotChangeAfterPublication()
	{
		using var fixture = new Fixture(); fixture.Mod( "mods/a", "A" );
		var archive = Path.Combine( fixture.Root, "vpk/englishclient_test_dir.vpk" );
		WriteVpk( archive, "retail" );
		var original = Titanfall2ContentDiscovery.Build( fixture.Root );
		var source = original.GetSource( "test.txt" );
		Assert.IsTrue( source.TryReadAllBytes( out var bytes, out var error ), error );
		Assert.AreEqual( "retail", Encoding.UTF8.GetString( bytes ) );
		fixture.Write( "mods/a/mod/test.txt", "override" );
		var modified = Titanfall2ContentDiscovery.Build( fixture.Root, fixture.Root );
		Assert.AreEqual( 2, modified.Explain( "test.txt" ).Count );
		Assert.IsTrue( modified.GetSource( "test.txt" ).TryReadAllBytes( out bytes, out error ), error );
		Assert.AreEqual( "override", Encoding.UTF8.GetString( bytes ) );
		WriteVpk( archive, "changed-archive" );
		Assert.IsFalse( source.TryReadAllBytes( out _, out error ) );
		StringAssert.Contains( error, "changed" );
	}

	[TestMethod]
	public void ModVpkPreloadAndMapSelectionDoNotLeakInactiveArchiveMembers()
	{
		using var fixture = new Fixture(); fixture.Mod( "mods/a", "A" );
		WriteVpk( Path.Combine( fixture.Root, "mods/a/vpk/englishclient_mp_angel_city.bsp.pak000_dir.vpk" ), "map" );
		fixture.Write( "mods/a/vpk/vpk.json", "{\"Preload\":{}}" );
		Assert.IsNull( Titanfall2ContentDiscovery.Build( fixture.Root, fixture.Root ).GetSource( "test.txt" ) );
		var snapshot = Titanfall2ContentDiscovery.Build( fixture.Root, fixture.Root, "mp_angel_city" );
		Assert.IsTrue( snapshot.GetSource( "test.txt" ).TryReadAllBytes( out var bytes, out var error ), error );
		Assert.AreEqual( "map", Encoding.UTF8.GetString( bytes ) );
	}

	[TestMethod]
	public void MapOnlyVpkPreloadsWithoutASelectedMapAndAcceptsBspSuffixedKeys()
	{
		using var fixture = new Fixture(); fixture.Mod( "mods/a", "A" );
		fixture.Write( "mods/a/vpk/vpk.json", "{\"MapOnly\":{\"client_mp_nexus.bsp\":\"mp_nexus\"}}" );
		WriteVpk( Path.Combine( fixture.Root, "mods/a/vpk/englishclient_mp_nexus.bsp.pak000_dir.vpk" ), "map" );
		var snapshot = Titanfall2ContentDiscovery.Build( fixture.Root, fixture.Root );
		Assert.IsTrue( snapshot.GetSource( "test.txt" ).TryReadAllBytes( out var bytes, out var error ), error );
		Assert.AreEqual( "map", Encoding.UTF8.GetString( bytes ) );
		Assert.IsTrue( snapshot.Diagnostics.Any( text => text.Contains( "map-only VPK client_mp_nexus" ) ) );
	}

	[TestMethod]
	public void MapOnlyPackageStaysDeferredForADifferentSelectedMap()
	{
		using var fixture = new Fixture(); fixture.Mod( "mods/a", "A" );
		fixture.Write( "mods/a/vpk/vpk.json", "{\"MapOnly\":{\"client_mp_nexus\":\"mp_nexus\"}}" );
		WriteVpk( Path.Combine( fixture.Root, "mods/a/vpk/englishclient_mp_nexus.bsp.pak000_dir.vpk" ), "map" );
		Assert.IsNull( Titanfall2ContentDiscovery.Build( fixture.Root, fixture.Root, "mp_angel_city" ).GetSource( "test.txt" ) );
		Assert.IsNotNull( Titanfall2ContentDiscovery.Build( fixture.Root, fixture.Root, "mp_nexus" ).GetSource( "test.txt" ) );
	}

	[TestMethod]
	public void PreloadSectionAcceptsNorthstarBspSuffixedPackageKeys()
	{
		using var fixture = new Fixture(); fixture.Mod( "mods/a", "A" );
		fixture.Write( "mods/a/vpk/vpk.json", "{\"Preload\":{\"client_mp_nexus.bsp\":true}}" );
		WriteVpk( Path.Combine( fixture.Root, "mods/a/vpk/englishclient_mp_nexus.bsp.pak000_dir.vpk" ), "map" );
		Assert.IsNotNull( Titanfall2ContentDiscovery.Build( fixture.Root, fixture.Root ).GetSource( "test.txt" ) );
	}

	[TestMethod]
	public void MapOnlyDeclarationSelectsTheSameMapRpakWithoutASelectedMap()
	{
		using var fixture = new Fixture(); fixture.Mod( "mods/a", "A" );
		fixture.Write( "mods/a/vpk/vpk.json", "{\"MapOnly\":{\"client_mp_nexus.bsp\":\"mp_nexus\"}}" );
		fixture.Write( "mods/a/paks/mp_nexus.rpak", "fixture" );
		fixture.Write( "mods/a/paks/rpak.json", "{\"mp_nexus.rpak\":\"^mp_nexus$\"}" );
		var withoutMap = new Titanfall2NorthstarProfile( fixture.Root, null, [] );
		CollectionAssert.AreEqual( new[] { "mp_nexus.rpak" }, withoutMap.Mods[0].Rpaks.Select( Path.GetFileName ).ToArray() );
		var otherMap = new Titanfall2NorthstarProfile( fixture.Root, "mp_angel_city", [] );
		Assert.IsFalse( otherMap.Mods[0].Rpaks.Any( path => path.EndsWith( "mp_nexus.rpak" ) ) );
	}

	[TestMethod]
	public void AliasMayRedirectToRetailPackageAndCannotEscapePackageDirectory()
	{
		using var fixture = new Fixture(); fixture.Mod( "mods/a", "A" );
		fixture.Write( "mods/a/paks/rpak.json", "{\"Aliases\":{\"mp_a.rpak\":\"sp_a.rpak\"}}" );
		var profile = new Titanfall2NorthstarProfile( fixture.Root, null, ["mp_a.rpak", "sp_a.rpak"] );
		Assert.AreEqual( "sp_a.rpak", profile.Aliases["mp_a.rpak"] );
		fixture.Write( "mods/a/paks/rpak.json", "{\"Aliases\":{\"mp_a.rpak\":\"../escape.rpak\"}}" );
		Assert.ThrowsException<InvalidDataException>( () => new Titanfall2NorthstarProfile( fixture.Root, null, ["mp_a.rpak"] ) );
	}

	[TestMethod]
	public void CatalogPublishesOnlyLoadableAssetsAndEveryModelUsesTheSnapshot()
	{
		using var fixture = new Fixture(); fixture.Mod( "mods/a", "A" );
		fixture.Write( "mods/a/mod/models/a.mdl", "model" );
		fixture.Write( "mods/a/mod/models/a.mdl.vvd", "companion" );
		fixture.Write( "mods/a/mod/materials/a.vmt", "material" );
		fixture.Write( "mods/a/mod/materials/a.vtf", "texture" );
		fixture.Write( "mods/a/mod/sound/a.wav", "sound" );
		fixture.Write( "mods/a/mod/scripts/metadata.txt", "metadata" );
		var snapshot = Titanfall2ContentDiscovery.Build( fixture.Root, fixture.Root );
		var catalog = new Titanfall2Mount().PrepareContentCatalog( new HashSet<string>( StringComparer.OrdinalIgnoreCase ), snapshot );
		Assert.AreEqual( 6, catalog.Count );
		Assert.IsTrue( catalog.Any( asset => asset.Type == ResourceType.Material && asset.Path == "a" && asset.Loader is LegacyMaterialLoader ) );
		Assert.AreEqual( 2, catalog.Count( asset => asset.Type == ResourceType.Model ) );
		Assert.IsFalse( catalog.Any( asset => asset.Path.EndsWith( ".vvd" ) || asset.Path.EndsWith( "metadata.txt" ) ) );
		Assert.IsNotNull( snapshot.GetSource( "scripts/metadata.txt" ) );
	}

	[TestMethod]
	public void CustomMapPakIsSelectedByRequestedNameWithoutDeprecatedConfig()
	{
		using var fixture = new Fixture(); fixture.Mod( "mods/a", "A" );
		fixture.Write( "mods/a/paks/mp_custom.rpak", "fixture" );
		var profile = new Titanfall2NorthstarProfile( fixture.Root, "mp_custom", ["common.rpak"] );
		Assert.AreEqual( "mp_custom.rpak", Path.GetFileName( profile.Mods[0].Rpaks.Single() ) );
	}

	[TestMethod]
	public void MapPatchPakAndItsPostloadDependencyAreSelectedWithoutPreload()
	{
		using var fixture = new Fixture(); fixture.Mod( "mods/a", "A" );
		foreach ( var name in new[] { "mp_custom", "mp_custom_patch", "patch_art", "mp_other_patch" } )
			fixture.Write( $"mods/a/paks/{name}.rpak", "fixture" );
		fixture.Write( "mods/a/paks/rpak.json", "{\"Postload\":{\"patch_art.rpak\":\"mp_custom_patch.rpak\"}}" );
		var profile = new Titanfall2NorthstarProfile( fixture.Root, "mp_custom", ["common.rpak"] );
		CollectionAssert.AreEquivalent( new[] { "mp_custom.rpak", "mp_custom_patch.rpak", "patch_art.rpak" },
			profile.Mods.Single().Rpaks.Select( Path.GetFileName ).ToArray() );
		var retail = new Titanfall2NorthstarProfile( fixture.Root, "mp_custom", ["mp_custom(01).rpak", "mp_custom_patch.rpak"] );
		CollectionAssert.AreEqual( new[] { "patch_art.rpak" }, retail.Mods.Single().Rpaks.Select( Path.GetFileName ).ToArray() );
		Assert.AreEqual( 0, new Titanfall2NorthstarProfile( fixture.Root, null, [] ).Mods.Single().Rpaks.Count );
	}

	static string WriteStreamingRpak( Fixture fixture )
	{
		var path = fixture.Write( "r2/paks/Win64/shared.rpak", "" );
		var reference = Encoding.UTF8.GetBytes( "stream/shared.starpak\0" );
		var bytes = new byte[0x58 + reference.Length];
		System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian( bytes, 0x6B615052 );
		System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian( bytes.AsSpan( 4 ), 7 );
		System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian( bytes.AsSpan( 0x38 ), (ushort)reference.Length );
		reference.CopyTo( bytes, 0x58 );
		File.WriteAllBytes( path, bytes );
		return path;
	}

	[TestMethod]
	public void ResolvedNestedStreamingDependencyChangesTheSnapshotRevision()
	{
		using var fixture = new Fixture();
		WriteStreamingRpak( fixture );
		var stream = fixture.Write( "r2/paks/Win64/stream/shared.starpak", "first" );
		var before = Titanfall2ContentDiscovery.Build( fixture.Root );
		Assert.IsFalse( before.Diagnostics.Any( text => text.Contains( "Cannot index" ) ) );
		File.WriteAllText( stream, "replacement" );
		Assert.AreNotEqual( before.Revision, Titanfall2ContentDiscovery.Build( fixture.Root ).Revision,
			"Every resolved STARPAK contributes to cache identity, including files below the flat package directory." );
	}

	[TestMethod]
	public void MissingStreamingDependencyIsPinnedAndItsArrivalChangesTheNextSnapshot()
	{
		using var fixture = new Fixture();
		var package = WriteStreamingRpak( fixture );
		Assert.IsTrue( RpakArchive.TryOpen( package, out var archive, out var error ), error );
		archive.FreezeSources();
		var before = Titanfall2ContentDiscovery.Build( fixture.Root );
		Assert.IsNull( archive.GetStreamingFilePath( 0 ) );
		fixture.Write( "r2/paks/Win64/stream/shared.starpak", "new dependency" );
		Assert.IsNull( archive.GetStreamingFilePath( 0 ), "An existing session must not silently adopt a new dependency." );
		Assert.AreNotEqual( before.Revision, Titanfall2ContentDiscovery.Build( fixture.Root ).Revision );
	}

	[TestMethod]
	public async Task ConcurrentReadersKeepTheirOriginalResolutionContext()
	{
		using var fixture = new Fixture(); fixture.Mod( "mods/a", "A" );
		fixture.Write( "mods/a/mod/a.txt", "original" );
		var first = Titanfall2ContentDiscovery.Build( fixture.Root, fixture.Root );
		var source = first.GetSource( "a.txt" );
		fixture.Mod( "mods/b", "B", 10 ); fixture.Write( "mods/b/mod/a.txt", "replacement" );
		var second = Titanfall2ContentDiscovery.Build( fixture.Root, fixture.Root );
		var readers = Enumerable.Range( 0, 16 ).Select( _ => Task.Run( () =>
		{
			Assert.IsTrue( ((ITitanfall2PathAssetSource)source).TryReadPath( "a.txt", out var bytes, out var error ), error );
			Assert.AreEqual( "original", Encoding.UTF8.GetString( bytes ) );
		} ) );
		await Task.WhenAll( readers );
		Assert.IsTrue( second.GetSource( "a.txt" ).TryReadAllBytes( out var newer, out _ ) );
		Assert.AreEqual( "replacement", Encoding.UTF8.GetString( newer ) );
	}

	[TestMethod]
	public void LooseFilesystemOverridePrecedesHigherPriorityArchiveLikeNorthstarHook()
	{
		using var fixture = new Fixture(); fixture.Mod( "mods/loose", "Loose", 0 ); fixture.Mod( "mods/archive", "Archive", 100 );
		fixture.Write( "mods/loose/mod/test.txt", "loose" );
		WriteVpk( Path.Combine( fixture.Root, "mods/archive/vpk/englishclient_test.bsp.pak000_dir.vpk" ), "archive" );
		var snapshot = Titanfall2ContentDiscovery.Build( fixture.Root, fixture.Root );
		Assert.IsTrue( snapshot.GetSource( "test.txt" ).TryReadAllBytes( out var bytes, out var error ), error );
		Assert.AreEqual( "loose", Encoding.UTF8.GetString( bytes ) );
		Assert.AreEqual( 2, snapshot.Explain( "test.txt" ).Count );
	}

	[TestMethod]
	public void Utf8BomManifestIsAcceptedAndUnknownStateFormatFailsClearly()
	{
		using var fixture = new Fixture(); fixture.Mod( "mods/a", "A" );
		var path = Path.Combine( fixture.Root, "mods/a/mod.json" );
		File.WriteAllText( path, "{\"Name\":\"A\"}", new UTF8Encoding( encoderShouldEmitUTF8Identifier: true ) );
		Assert.AreEqual( 1, new Titanfall2NorthstarProfile( fixture.Root, null, [] ).Mods.Count );
		fixture.Write( "enabledmods.json", "{\"Version\":2}" );
		Assert.ThrowsException<InvalidDataException>( () => new Titanfall2NorthstarProfile( fixture.Root, null, [] ) );
	}

	[TestMethod]
	public void FixedMapCatalogDoesNotPublishOtherMaps()
	{
		using var fixture = new Fixture(); fixture.Mod( "mods/a", "A" );
		fixture.Write( "mods/a/mod/maps/mp_other.bsp", "not parsed because this map is outside the selected context" );
		var snapshot = Titanfall2ContentDiscovery.Build( fixture.Root, fixture.Root, "mp_selected" );
		var catalog = new Titanfall2Mount().PrepareContentCatalog( new HashSet<string>( StringComparer.OrdinalIgnoreCase ), snapshot );
		Assert.IsFalse( catalog.Any( asset => asset.Type == ResourceType.Scene ) );
		Assert.IsNotNull( snapshot.GetSource( "maps/mp_other.bsp" ) );
	}

	[TestMethod]
	[TestCategory( "LocalContentInventory" )]
	public void InspectExplicitlyRequestedInstalledContent()
	{
		var game = Environment.GetEnvironmentVariable( "TF2_CONTENT_TEST_GAME" );
		if ( string.IsNullOrWhiteSpace( game ) ) Assert.Inconclusive( "Set TF2_CONTENT_TEST_GAME to run the read-only local inventory." );
		var timer = System.Diagnostics.Stopwatch.StartNew();
		var snapshot = Titanfall2ContentDiscovery.Build( game, Environment.GetEnvironmentVariable( "TF2_CONTENT_TEST_PROFILE" ), "mp_angel_city" );
		Assert.IsNotNull( snapshot.GetSource( "maps/mp_angel_city.bsp" ) );
		Assert.IsTrue( snapshot.Winners.Any( entry => entry.Kind == "txtr" ) );
		Assert.IsTrue( snapshot.Winners.Any( entry => entry.Kind == "matl" ) );
		Assert.IsTrue( snapshot.Winners.Any( entry => entry.Kind == "miles" ) );
		foreach ( var entry in snapshot.Winners.Where( entry => entry.Kind == "file" && entry.SourceId.StartsWith( "northstar:" ) && entry.Path.EndsWith( ".mdl", StringComparison.Ordinal ) ).Take( 3 ) )
		{
			Assert.IsTrue( snapshot.GetSource( entry.Path ).TryReadAllBytes( out var model, out var error ), error );
			Assert.IsTrue( model.Length > 4 );
			Assert.AreEqual( "IDST", Encoding.ASCII.GetString( model, 0, 4 ), entry.Path );
		}
		var texture = snapshot.Winners.FirstOrDefault( entry => entry.Kind == "txtr" && entry.SourceId.StartsWith( "northstar:" ) );
		if ( texture is not null )
		{
			var packed = new RpakTextureAssetSource( texture.Archive, texture.Asset );
			Assert.IsTrue( packed.TryReadTexture( out var decoded, out var error ), error );
			Assert.IsTrue( decoded.Data.Length > 0 );
		}
		var json = System.Text.Json.JsonSerializer.Serialize( new
		{
			GameRoot = game,
			Profile = Environment.GetEnvironmentVariable( "TF2_CONTENT_TEST_PROFILE" ),
			snapshot.SelectedMap,
			ModuleVersionId = typeof( Titanfall2Mount ).Assembly.ManifestModule.ModuleVersionId,
			ModuleLocation = typeof( Titanfall2Mount ).Assembly.Location,
			snapshot.Revision,
			Entries = snapshot.Entries.Count,
			Winners = snapshot.Winners.Count,
			Seconds = timer.Elapsed.TotalSeconds,
			Kinds = snapshot.Winners.GroupBy( entry => entry.Kind ).ToDictionary( group => group.Key, group => group.Count() ),
			ModSources = snapshot.Winners.Where( entry => entry.SourceId.StartsWith( "northstar:" ) ).GroupBy( entry => entry.SourceId ).ToDictionary( group => group.Key, group => group.Count() ),
			snapshot.Diagnostics
		}, new System.Text.Json.JsonSerializerOptions { WriteIndented = true } );
		var report = Environment.GetEnvironmentVariable( "TF2_CONTENT_TEST_REPORT" );
		if ( report is not null ) File.WriteAllText( report, json );
		Console.WriteLine( json );
	}
}
