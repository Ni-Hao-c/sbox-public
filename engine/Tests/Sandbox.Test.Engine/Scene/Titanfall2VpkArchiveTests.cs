using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace SceneTests;

[TestClass]
public class Titanfall2VpkArchiveTests
{
	static readonly Type ArchiveType = typeof( Titanfall2Mount ).Assembly.GetType( "VpkArchive", throwOnError: true );
	string _directory;
	string DirectoryPath => Path.Combine( _directory, "englishclient_test_dir.vpk" );

	[TestInitialize]
	public void CreateDirectory()
	{
		_directory = Path.Combine( Path.GetTempPath(), $"tf2-vpk-tests-{Guid.NewGuid():N}" );
		Directory.CreateDirectory( _directory );
	}

	[TestCleanup]
	public void DeleteDirectory() => Directory.Delete( _directory, recursive: true );

	static bool Open( string path, out object archive, out string error )
	{
		object[] arguments = [path, null, null];
		var success = (bool)ArchiveType.GetMethod( "TryOpen" ).Invoke( null, arguments );
		archive = arguments[1];
		error = (string)arguments[2];
		return success;
	}

	static bool Read( object archive, string path, out byte[] bytes, out string error )
	{
		object[] arguments = [path, null, null];
		var method = ArchiveType.GetMethod( "TryReadAllBytes", [typeof( string ), typeof( byte[] ).MakeByRefType(), typeof( string ).MakeByRefType()] );
		var success = (bool)method.Invoke( archive, arguments );
		bytes = (byte[])arguments[1];
		error = (string)arguments[2];
		return success;
	}

	sealed class Fixture
	{
		public byte[] Tree { get; }
		public Dictionary<string, int> Cuts { get; } = new();
		readonly byte[] _data;

		public Fixture( byte[] preload = null, byte[] data = null, params (ushort Index, ulong Offset, ulong Size)[] chunks )
		{
			preload ??= [];
			_data = data ?? [];
			using var tree = new MemoryStream();
			using var writer = new BinaryWriter( tree, Encoding.ASCII, leaveOpen: true );
			void Cut( string name ) => Cuts[name] = (int)tree.Position - 1;
			writer.Write( Encoding.ASCII.GetBytes( "txt\0" ) ); Cut( "extension" );
			writer.Write( Encoding.ASCII.GetBytes( " \0" ) ); Cut( "folder" );
			writer.Write( Encoding.ASCII.GetBytes( "test\0" ) ); Cut( "name" );
			writer.Write( 0u ); Cut( "crc" );
			writer.Write( (ushort)preload.Length ); Cut( "preload-count" );
			foreach ( var chunk in chunks )
			{
				writer.Write( chunk.Index ); Cut( "chunk-index" );
				writer.Write( (ushort)1 ); writer.Write( 0u );
				writer.Write( chunk.Offset ); writer.Write( chunk.Size ); writer.Write( chunk.Size ); Cut( "chunk-record" );
			}
			writer.Write( ushort.MaxValue ); Cut( "chunk-terminator" );
			writer.Write( preload ); Cut( "preload" );
			writer.Write( (byte)0 ); Cut( "file-list" );
			writer.Write( (byte)0 ); Cut( "folder-list" );
			writer.Write( (byte)0 ); Cut( "extension-list" );
			Tree = tree.ToArray();
		}

		public byte[] Bytes( int? treeSize = null, uint? dataSize = null )
		{
			using var file = new MemoryStream();
			using var writer = new BinaryWriter( file );
			writer.Write( 0x55AA1234u ); writer.Write( 196610u );
			writer.Write( (uint)(treeSize ?? Tree.Length) ); writer.Write( dataSize ?? (uint)_data.Length );
			writer.Write( Tree ); writer.Write( _data );
			return file.ToArray();
		}
	}

	object OpenFixture( Fixture fixture )
	{
		File.WriteAllBytes( DirectoryPath, fixture.Bytes() );
		Assert.IsTrue( Open( DirectoryPath, out var archive, out var error ), error );
		Assert.IsNull( error );
		return archive;
	}

	[DataTestMethod]
	[DataRow( "extension" )]
	[DataRow( "folder" )]
	[DataRow( "name" )]
	[DataRow( "crc" )]
	[DataRow( "preload-count" )]
	[DataRow( "chunk-index" )]
	[DataRow( "chunk-record" )]
	[DataRow( "chunk-terminator" )]
	[DataRow( "preload" )]
	[DataRow( "file-list" )]
	[DataRow( "folder-list" )]
	[DataRow( "extension-list" )]
	public void TreeCannotReadFollowingBytesToCompleteTruncatedFields( string field )
	{
		var fixture = new Fixture( [1, 2, 3], [9, 9, 9, 9], (0, 0, 4) );
		File.WriteAllBytes( DirectoryPath, fixture.Bytes( fixture.Cuts[field] ) );
		Assert.IsFalse( Open( DirectoryPath, out var archive, out var error ), field );
		Assert.IsNull( archive );
		Assert.IsFalse( string.IsNullOrEmpty( error ) );
	}

	[DataTestMethod]
	[DataRow( "empty" )]
	[DataRow( "past-eof" )]
	[DataRow( "trailing-data" )]
	[DataRow( "truncated-file" )]
	[DataRow( "oversized-data-block" )]
	public void InvalidTreeOrDataExtentCannotPublishArchive( string corruption )
	{
		var fixture = new Fixture( data: [9, 9, 9, 9] );
		var bytes = corruption switch
		{
			"empty" => fixture.Bytes( 0 ),
			"past-eof" => fixture.Bytes( fixture.Tree.Length + 100 ),
			"trailing-data" => fixture.Bytes( fixture.Tree.Length + 1, 0 ),
			"oversized-data-block" => fixture.Bytes( dataSize: 100 ),
			_ => fixture.Bytes()[..^5]
		};
		File.WriteAllBytes( DirectoryPath, bytes );
		Assert.IsFalse( Open( DirectoryPath, out var archive, out var error ), corruption );
		Assert.IsNull( archive );
		Assert.IsFalse( string.IsNullOrEmpty( error ) );
	}

	[TestMethod]
	public void EmptyTreeIsValidWithTerminator()
	{
		var fixture = new Fixture();
		var bytes = fixture.Bytes();
		Array.Resize( ref bytes, 17 );
		BitConverter.GetBytes( 1u ).CopyTo( bytes, 8 );
		bytes[16] = 0;
		File.WriteAllBytes( DirectoryPath, bytes );
		Assert.IsTrue( Open( DirectoryPath, out var archive, out var error ), error );
		Assert.IsFalse( Read( archive, "test.txt", out var data, out error ) );
		Assert.IsNull( data );
	}

	[TestMethod]
	public void PreloadAndMultipleExternalVolumesConcatenateInSourceOrder()
	{
		File.WriteAllBytes( Path.Combine( _directory, "client_test_000.vpk" ), [99, 3, 4] );
		File.WriteAllBytes( Path.Combine( _directory, "client_test_001.vpk" ), [5, 6] );
		var archive = OpenFixture( new Fixture( [1, 2], null, (0, 1, 2), (1, 0, 2) ) );
		Assert.IsTrue( Read( archive, "/TEST.TXT", out var data, out var error ), error );
		CollectionAssert.AreEqual( new byte[] { 1, 2, 3, 4, 5, 6 }, data );
		Assert.IsNull( error );
	}

	[TestMethod]
	public void PreloadOnlyEntryDoesNotRequirePayloadVolume()
	{
		var archive = OpenFixture( new Fixture( [1, 2, 3] ) );
		Assert.IsTrue( Read( archive, "test.txt", out var data, out var error ), error );
		CollectionAssert.AreEqual( new byte[] { 1, 2, 3 }, data );
	}

	[DataTestMethod]
	[DataRow( false )]
	[DataRow( true )]
	public void MissingOrTruncatedExternalVolumeCannotFallBackToDirectoryData( bool truncated )
	{
		if ( truncated ) File.WriteAllBytes( Path.Combine( _directory, "client_test_000.vpk" ), [1, 2] );
		var archive = OpenFixture( new Fixture( null, [9, 9, 9, 9], (0, 0, 4) ) );
		Assert.IsFalse( Read( archive, "test.txt", out var data, out var error ) );
		Assert.IsNull( data );
		StringAssert.Contains( error, "test.txt" );
		StringAssert.Contains( error, "000.vpk" );
	}

	[TestMethod]
	public void NonstandardDirectoryFilenameCannotReadItsOwnHeaderAsExternalPayload()
	{
		var path = Path.Combine( _directory, "test.vpk" );
		File.WriteAllBytes( path, new Fixture( null, null, (0, 0, 4) ).Bytes() );
		Assert.IsTrue( Open( path, out var archive, out var error ), error );
		Assert.IsFalse( Read( archive, "test.txt", out var data, out error ) );
		Assert.IsNull( data );
		StringAssert.Contains( error, "test.vpk" );
	}

	[DataTestMethod]
	[DataRow( false )]
	[DataRow( true )]
	public void EmbeddedIndexSelectsDirectoryDataEvenWhenNumberedVolumeExists( bool competingVolume )
	{
		if ( competingVolume ) File.WriteAllBytes( Path.Combine( _directory, "englishclient_test_32767.vpk" ), [9, 9, 9, 9] );
		var archive = OpenFixture( new Fixture( [1], [99, 2, 3, 4], (0x7FFF, 1, 3) ) );
		Assert.IsTrue( Read( archive, "test.txt", out var data, out var error ), error );
		CollectionAssert.AreEqual( new byte[] { 1, 2, 3, 4 }, data );
	}

	[TestMethod]
	public void EmbeddedChunkCannotReadBeyondDeclaredDataBlock()
	{
		var fixture = new Fixture( null, [1, 2, 3, 4], (0x7FFF, 0, 4) );
		File.WriteAllBytes( DirectoryPath, fixture.Bytes( dataSize: 2 ) );
		Assert.IsTrue( Open( DirectoryPath, out var archive, out var error ), error );
		Assert.IsFalse( Read( archive, "test.txt", out var data, out error ) );
		Assert.IsNull( data );
		Assert.IsFalse( string.IsNullOrEmpty( error ) );
	}
}
