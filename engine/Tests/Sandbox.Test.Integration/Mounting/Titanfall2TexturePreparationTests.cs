using System;
using System.Buffers.Binary;

namespace MountingTests;

[TestClass, DoNotParallelize]
public class Titanfall2TexturePreparationTests
{
	sealed class VtfSource : ITitanfall2AssetSource
	{
		internal int Reads;
		public string Description => "texture-preparation-runtime-test";
		public bool TryReadAllBytes( out byte[] bytes, out string error )
		{
			Reads++;
			bytes = new byte[80 + 4 * 4 * 4];
			BinaryPrimitives.WriteUInt32LittleEndian( bytes, 0x00465456 );
			BinaryPrimitives.WriteUInt32LittleEndian( bytes.AsSpan( 4 ), 7 );
			BinaryPrimitives.WriteUInt32LittleEndian( bytes.AsSpan( 12 ), 80 );
			BinaryPrimitives.WriteUInt16LittleEndian( bytes.AsSpan( 16 ), 4 );
			BinaryPrimitives.WriteUInt16LittleEndian( bytes.AsSpan( 18 ), 4 );
			BinaryPrimitives.WriteUInt16LittleEndian( bytes.AsSpan( 24 ), 1 );
			bytes[56] = 1; // One RGBA8888 mip.
			bytes.AsSpan( 80 ).Fill( 255 );
			error = null;
			return true;
		}
	}

	[TestMethod]
	public async Task NativeTextureUploadConsumesCrossingPayloadAndCanPrepareAgainAfterRelease()
	{
		if ( !Graphics.IsAvailable ) Assert.Inconclusive( "Requires SBOX_TEST_GRAPHICS=1." );
		var source = new VtfSource();
		var loader = new Titanfall2TextureLoader( source );
		using var pool = new Titanfall2MaterialPrefetch( null, [], 8 );
		Assert.IsFalse( pool.PrepareTexture( loader ) );
		Assert.AreEqual( 144L, pool.RetainedBytes );
		using var texture = (Texture)await loader.GetOrCreate();
		Assert.IsTrue( texture.IsValid() );
		Assert.AreEqual( 4, texture.Width );
		Assert.AreEqual( 4, texture.Height );
		Assert.AreEqual( 1, source.Reads, "Upload must consume preparation without another source read." );
		Assert.AreEqual( 0L, pool.RetainedBytes );
		Assert.IsTrue( loader.ReleaseSceneResource() );
		Assert.IsTrue( texture.IsValid(), "Releasing the loader cache must preserve a live texture owner." );
		Assert.IsFalse( pool.PrepareTexture( loader ) );
		Assert.AreEqual( 2, source.Reads );
		pool.Dispose();
		Assert.AreEqual( 0L, pool.RetainedBytes );
	}
}
