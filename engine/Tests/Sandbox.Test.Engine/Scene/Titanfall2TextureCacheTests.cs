using System;
using System.IO;
using System.Reflection;

namespace SceneTests;

[TestClass]
[DoNotParallelize]
public class Titanfall2TextureCacheTests
{
	static readonly Assembly Mount = typeof( Titanfall2Mount ).Assembly;
	static readonly Type Cache = Mount.GetType( "Titanfall2ProcessedCache", true );
	static readonly Type Textures = Mount.GetType( "Titanfall2TextureCache", true );
	const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic;
	string _directory;
	string _previousPath;

	[TestInitialize]
	public void CreateCache()
	{
		_previousPath = Environment.GetEnvironmentVariable( "SBOX_TITANFALL2_CACHE_PATH" );
		_directory = Path.Combine( Path.GetTempPath(), $"tf2-texture-cache-{Guid.NewGuid():N}" );
		Directory.CreateDirectory( _directory );
		Environment.SetEnvironmentVariable( "SBOX_TITANFALL2_CACHE_PATH", _directory );
	}

	[TestCleanup]
	public void Cleanup()
	{
		Environment.SetEnvironmentVariable( "SBOX_TITANFALL2_CACHE_PATH", _previousPath );
		Directory.Delete( _directory, true );
	}

	bool Read() => (bool)Textures.GetMethod( "TryRead", Static ).Invoke( null, new object[] { "test", null } );

	void Write( int width, int height, ImageFormat format, int mips, int surfaces, bool cube, int length )
	{
		Action<BinaryWriter> write = writer =>
		{
			writer.Write( width ); writer.Write( height ); writer.Write( (int)format );
			writer.Write( mips ); writer.Write( surfaces ); writer.Write( cube );
			writer.Write( length ); writer.Write( new byte[length] );
		};
		Cache.GetMethod( "Write", Static ).Invoke( null, ["texture", "test", write] );
	}

	[DataTestMethod]
	[DataRow( ImageFormat.DXT1, 56 )]
	[DataRow( ImageFormat.ATI1N, 56 )]
	[DataRow( ImageFormat.BC7, 112 )]
	[DataRow( ImageFormat.RGBA8888, 340 )]
	[DataRow( ImageFormat.I8, 85 )]
	[DataRow( ImageFormat.A8, 85 )]
	public void ValidCompleteMipChainsAreAccepted( ImageFormat format, int length )
	{
		Write( 8, 8, format, 4, 1, false, length );
		Assert.IsTrue( Read() );
	}

	[DataTestMethod]
	[DataRow( 55 )]
	[DataRow( 57 )]
	[DataRow( 1 )]
	[DataRow( 32 )]
	public void WrongDataLengthIsAMissAndCanBeRebuilt( int length )
	{
		Write( 8, 8, ImageFormat.DXT1, 4, 1, false, length );
		Assert.IsFalse( Read(), "A damaged cache must fall back to the importer before GPU creation." );
		Write( 8, 8, ImageFormat.DXT1, 4, 1, false, 56 );
		Assert.IsTrue( Read() );
	}

	[DataTestMethod]
	[DataRow( 2, false, 112 )]
	[DataRow( 6, true, 336 )]
	[DataRow( 12, true, 672 )]
	public void EverySurfaceContributesItsMipBytes( int surfaces, bool cube, int length )
	{
		Write( 8, 8, ImageFormat.DXT1, 4, surfaces, cube, length );
		Assert.IsTrue( Read() );
		Write( 8, 8, ImageFormat.DXT1, 4, surfaces, cube, length - 8 );
		Assert.IsFalse( Read() );
	}

	[TestMethod]
	public void NonSquareCubeIsRejected()
	{
		Write( 8, 4, ImageFormat.DXT1, 1, 6, true, 96 );
		Assert.IsFalse( Read() );
	}

	[TestMethod]
	public void ImpossibleMipCountIsRejected()
	{
		Write( 4, 4, ImageFormat.DXT1, 4, 1, false, 32 );
		Assert.IsFalse( Read() );
	}

	[TestMethod]
	public void UnproducedImageFormatIsRejected()
	{
		Write( 8, 8, ImageFormat.RGB888, 1, 1, false, 192 );
		Assert.IsFalse( Read(), "RPAK caches may only contain formats emitted by the RPAK decoder." );
	}
}
