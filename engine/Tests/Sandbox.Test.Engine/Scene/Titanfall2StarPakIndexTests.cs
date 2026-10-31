using System;
using System.IO;
using System.Reflection;

namespace SceneTests;

[TestClass]
[DoNotParallelize]
public class Titanfall2StarPakIndexTests
{
	static readonly Type IndexType = typeof( Titanfall2Mount ).Assembly.GetType( "StarPakIndex", throwOnError: true );
	string _directory;
	string FilePath => Path.Combine( _directory, "test.starpak" );

	[TestInitialize]
	public void CreateDirectory()
	{
		_directory = Path.Combine( Path.GetTempPath(), $"tf2-starpak-tests-{Guid.NewGuid():N}" );
		Directory.CreateDirectory( _directory );
	}

	[TestCleanup]
	public void Cleanup()
	{
		IndexType.GetMethod( "Clear", BindingFlags.NonPublic | BindingFlags.Static ).Invoke( null, null );
		Directory.Delete( _directory, recursive: true );
	}

	void Write( params (long Offset, long Size)[] entries )
	{
		using var stream = File.Create( FilePath );
		using var writer = new BinaryWriter( stream );
		stream.SetLength( 8192 );
		stream.Position = 8192;
		foreach ( var (offset, size) in entries )
		{
			writer.Write( offset );
			writer.Write( size );
		}
		writer.Write( (ulong)entries.Length );
	}

	static bool EntrySize( string path, long offset, out long size, out string error )
	{
		object[] arguments = [path, offset, 0L, null];
		var result = (bool)IndexType.GetMethod( "TryGetEntrySize" ).Invoke( null, arguments );
		size = (long)arguments[2];
		error = (string)arguments[3];
		return result;
	}

	[DataTestMethod]
	[DataRow( 4096L, 4096L )]
	[DataRow( 8191L, 1L )]
	public void EntryMayEndExactlyAtTheIndex( long offset, long size )
	{
		Write( (offset, size) );
		Assert.IsTrue( EntrySize( FilePath, offset, out var actual, out var error ), error );
		Assert.AreEqual( size, actual );
		Assert.IsNull( error );
		Assert.IsTrue( EntrySize( FilePath, offset, out actual, out error ), error );
		Assert.AreEqual( size, actual, "The cached lookup must preserve the validated size." );
	}

	[DataTestMethod]
	[DataRow( 4096L, 4097L )]
	[DataRow( 8192L, 1L )]
	[DataRow( 8193L, 1L )]
	[DataRow( 4096L, long.MaxValue )]
	[DataRow( long.MaxValue, 1L )]
	[DataRow( -1L, 1L )]
	[DataRow( 4096L, 0L )]
	[DataRow( 4096L, -1L )]
	public void InvalidEntryRejectsTheWholeIndex( long offset, long size )
	{
		Write( (offset, size), (0, 1) );
		Assert.IsFalse( EntrySize( FilePath, 0, out var actual, out var error ), "A valid sibling must not publish a corrupt table." );
		Assert.AreEqual( 0L, actual );
		Assert.IsFalse( string.IsNullOrEmpty( error ) );
		// Replacing the rejected file must work immediately: a failed parse is never cached.
		Write( (0, 1) );
		Assert.IsTrue( EntrySize( FilePath, 0, out actual, out error ), error );
		Assert.AreEqual( 1L, actual );
	}

	[DataTestMethod]
	[DataRow( 4096L )]
	[DataRow( 1L )]
	public void DuplicateOffsetsAreRejectedInsteadOfSilentlyOverwriting( long secondSize )
	{
		Write( (4096, 4096), (4096, secondSize) );
		Assert.IsFalse( EntrySize( FilePath, 4096, out var size, out var error ) );
		Assert.AreEqual( 0L, size );
		Assert.IsFalse( string.IsNullOrEmpty( error ) );
	}

	[DataTestMethod]
	[DataRow( false )]
	[DataRow( true )]
	public void MissingFooterOrImpossibleCountReturnsAnError( bool hasFooter )
	{
		File.WriteAllBytes( FilePath, hasFooter ? BitConverter.GetBytes( ulong.MaxValue ) : [1, 2, 3] );
		Assert.IsFalse( EntrySize( FilePath, 4096, out var size, out var error ) );
		Assert.AreEqual( 0L, size );
		Assert.IsFalse( string.IsNullOrEmpty( error ) );
	}

	[TestMethod]
	public void EmptyIndexHasNoEntries()
	{
		Write();
		Assert.IsFalse( EntrySize( FilePath, 4096, out var size, out var error ) );
		Assert.AreEqual( 0L, size );
		StringAssert.Contains( error, "no stream entry" );
	}
}
