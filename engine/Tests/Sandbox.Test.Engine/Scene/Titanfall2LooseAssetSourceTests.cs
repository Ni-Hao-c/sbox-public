using System;
using System.IO;
using System.Reflection;

namespace SceneTests;

[TestClass]
public class Titanfall2LooseAssetSourceTests
{
	string _directory;
	string _root;
	object _source;
	static readonly Type Source = typeof( Titanfall2Mount ).Assembly.GetType( "LooseAssetSource", true );

	[TestInitialize]
	public void CreateFiles()
	{
		_directory = Path.Combine( Path.GetTempPath(), $"tf2-loose-{Guid.NewGuid():N}" );
		_root = Path.Combine( _directory, "game" );
		Directory.CreateDirectory( Path.Combine( _root, "models", "props" ) );
		_source = Activator.CreateInstance( Source, Path.Combine( _root, "models", "props", "test.mdl" ) );
		File.WriteAllBytes( Path.Combine( _directory, "outside.bin" ), [1, 2, 3] );
	}

	[TestCleanup]
	public void Cleanup() => Directory.Delete( _directory, true );

	bool Read( string path, out byte[] bytes, out string error )
	{
		object[] args = [path, null, null];
		var result = (bool)Source.GetMethod( "TryReadPath" ).Invoke( _source, args );
		bytes = (byte[])args[1]; error = (string)args[2];
		return result;
	}

	[DataTestMethod]
	[DataRow( "../outside.bin" )]
	[DataRow( "..\\outside.bin" )]
	[DataRow( "models/../../outside.bin" )]
	[DataRow( "models\\..\\..\\outside.bin" )]
	public void ParentPathsCannotReadOutsideTheSourceRoot( string path )
	{
		Assert.IsFalse( Read( path, out var bytes, out var error ) );
		Assert.IsNull( bytes );
		Assert.IsFalse( string.IsNullOrEmpty( error ) );
	}

	[TestMethod]
	public void AbsolutePathIsRejected()
	{
		Assert.IsFalse( Read( Path.Combine( _directory, "outside.bin" ), out var bytes, out var error ) );
		Assert.IsNull( bytes );
		Assert.IsFalse( string.IsNullOrEmpty( error ) );
	}

	[DataTestMethod]
	[DataRow( "models/props/animation.rseq" )]
	[DataRow( "models\\props\\animation.rseq" )]
	[DataRow( "animation.rseq" )]
	public void GameRelativeAndAdjacentDependenciesStillResolve( string path )
	{
		File.WriteAllBytes( Path.Combine( _root, "models", "props", "animation.rseq" ), [4, 5, 6] );
		Assert.IsTrue( Read( path, out var bytes, out var error ), error );
		CollectionAssert.AreEqual( new byte[] { 4, 5, 6 }, bytes );
	}

	[TestMethod]
	public void InvalidPathReturnsFailureInsteadOfThrowing()
	{
		Assert.IsFalse( Read( "invalid\0name", out var bytes, out var error ) );
		Assert.IsNull( bytes );
		Assert.IsFalse( string.IsNullOrEmpty( error ) );
	}
}
