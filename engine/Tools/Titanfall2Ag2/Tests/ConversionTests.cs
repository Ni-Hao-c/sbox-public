using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanfall2Ag2;
using static Titanfall2.Formats.Titanfall2Mdl53AnimationReader;

namespace Titanfall2Ag2Tests;

[TestClass]
public class ConversionTests
{
	[TestMethod]
	public void DecoderVectorLayoutsMatchWireFormat()
	{
		Assert.AreEqual( 12, Marshal.SizeOf<global::Vector3>() );
		Assert.AreEqual( 8, Marshal.SizeOf<global::Vector2>() );
	}

	[TestMethod]
	public void RejectTraversalAndAbsolutePaths()
	{
		foreach ( var path in new[] { "../outside.mdl", "models/../../outside.mdl", "C:/outside.mdl", "/outside.mdl" } )
			Assert.ThrowsException<ArgumentException>( () => ModelSource.Normalize( path ) );
		Assert.AreEqual( "models/test.mdl", ModelSource.Normalize( "mount://titanfall2/models/test.mdl.vmdl" ) );
	}

	[TestMethod]
	public void RejectNonuniformScaleRatherThanSilentlyLosingIt()
	{
		Assert.ThrowsException<NotSupportedException>( () => ModelData.ValidatePose( new( System.Numerics.Vector3.Zero, Quaternion.Identity, new( 1, 2, 1 ) ), "bone" ) );
	}

	[TestMethod]
	public void CacheChecksOutputContentAndMissingFiles()
	{
		var root = Path.Combine( Path.GetTempPath(), "tf2ag2-test-" + Guid.NewGuid().ToString( "N" ) );
		Directory.CreateDirectory( root );
		try
		{
			var file = Path.Combine( root, "test.anim_c" );
			File.WriteAllText( file, "compiled" );
			var manifest = new ConversionManifest( "test", "converter", "compiler", true, "model.vmdl", "model.skel", [],
				[new( "test.anim_c", Converter.Hash( file ) )], [], [], [], 1, 3, 1 );
			File.WriteAllText( Path.Combine( root, "manifest.json" ), JsonSerializer.Serialize( manifest ) );
			Assert.IsNotNull( Converter.ReadValid( root, "test" ) );
			Assert.IsNull( Converter.ReadValid( root, "other" ) );
			File.WriteAllText( file, "tampered" );
			Assert.IsNull( Converter.ReadValid( root, "test" ) );
			File.Delete( file );
			Assert.IsNull( Converter.ReadValid( root, "test" ) );
		}
		finally { Directory.Delete( root, recursive: true ); }
	}

	[TestMethod]
	public void RejectPackageCopyInsideItsOwnCacheEntry()
	{
		var root = Path.Combine( Path.GetTempPath(), "tf2ag2-copy-test" );
		Assert.ThrowsException<ArgumentException>( () => Converter.CopyPackage( new( root, true, null ), Path.Combine( root, "copy" ) ) );
	}

	[TestMethod]
	public async Task OfficialCompilerAcceptsGeneratedSkeletonAndAnimatedDmx()
	{
		var engine = Environment.GetEnvironmentVariable( "TF2AG2_TEST_ENGINE" );
		if ( string.IsNullOrWhiteSpace( engine ) ) Assert.Inconclusive( "Set TF2AG2_TEST_ENGINE to run native compiler validation." );
		var root = Path.Combine( Path.GetTempPath(), "tf2ag2-native-" + Guid.NewGuid().ToString( "N" ) );
		var assets = Path.Combine( root, "Assets" );
		Directory.CreateDirectory( assets );
		var identity = new BonePose( System.Numerics.Vector3.Zero, Quaternion.Identity, System.Numerics.Vector3.One );
		Bone[] bones = [new( "root", -1, identity ), new( "child", 0, identity with { Position = new( 0, 0, 10 ) } )];
		var frames = Enumerable.Range( 0, 31 ).Select( i => new[] { identity, bones[1].Reference with { Rotation = Quaternion.CreateFromAxisAngle( System.Numerics.Vector3.UnitZ, i / 30f ) } } ).ToArray();
		try
		{
			File.WriteAllText( Path.Combine( assets, "skeleton.dmx" ), new DmxWriter().Write( bones ) );
			File.WriteAllText( Path.Combine( assets, "skeleton.skel" ), SourceDocuments.Skeleton( "skeleton.dmx" ) );
			File.WriteAllText( Path.Combine( assets, "motion.dmx" ), new DmxWriter().Write( bones, animation: new( "motion", 30, true, frames ) ) );
			File.WriteAllText( Path.Combine( assets, "motion.anim" ), SourceDocuments.Clip( "motion.dmx", "skeleton.skel", 30 ) );
			File.WriteAllText( Path.Combine( assets, "still.dmx" ), new DmxWriter().Write( bones, animation: new( "still", 30, true, [frames[0]] ) ) );
			File.WriteAllText( Path.Combine( assets, "still.anim" ), SourceDocuments.Clip( "still.dmx", "skeleton.skel", 30 ) );
			var geometry = new Titanfall2.Formats.Titanfall2MeshExtractor.ExtractedLod
			{
				Meshes = [new()
				{
					Vertices = [new() { Position = new( 0, 0, 0 ), Normal = new( 0, 0, 1 ) },
						new() { Position = new( 10, 0, 0 ), Normal = new( 0, 0, 1 ) },
						new() { Position = new( 0, 10, 0 ), Normal = new( 0, 0, 1 ) }],
					Indices = [0, 1, 2]
				}]
			};
			File.WriteAllText( Path.Combine( assets, "model.dmx" ), new DmxWriter().Write( bones, geometry, ["slot.vmat"] ) );
			File.WriteAllText( Path.Combine( assets, "model.vmdl" ), SourceDocuments.Model( "model.dmx", "skeleton.skel" ) );
			File.WriteAllText( Path.Combine( assets, "slot.vmat" ), SourceDocuments.Material );
			var compiler = new OfficialCompiler( engine );
			await compiler.Compile( assets, ["skeleton.skel", "slot.vmat"], Path.Combine( root, "compile.log" ) );
			await compiler.Compile( assets, ["model.vmdl", "motion.anim", "still.anim"], Path.Combine( root, "compile.log" ) );
			Assert.IsTrue( new FileInfo( Path.Combine( assets, "skeleton.skel_c" ) ).Length > 0 );
			Assert.IsTrue( new FileInfo( Path.Combine( assets, "motion.anim_c" ) ).Length > 0 );
			Assert.IsTrue( new FileInfo( Path.Combine( assets, "still.anim_c" ) ).Length > 0 );
			StringAssert.Contains( Encoding.UTF8.GetString( File.ReadAllBytes( Path.Combine( assets, "model.vmdl_c" ) ) ), "skeleton.skel" );
		}
		finally { Directory.Delete( root, recursive: true ); }
	}
}
