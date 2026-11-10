using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sandbox.Engine.Settings;
using System.Buffers.Binary;

[TestClass]
public class GraphicsSettingsTests
{
	[DataTestMethod]
	[DataRow( TextureQuality.Low, 1024 )]
	[DataRow( TextureQuality.Medium, 2048 )]
	[DataRow( TextureQuality.High, 4096 )]
	[DataRow( TextureQuality.Ultra, int.MaxValue )]
	public void PlayerTextureLevelsFollowEngineProfileLimits( TextureQuality quality, int expected )
	{
		var snapshot = Titanfall2GraphicsSettings.Resolve( quality, null );
		Assert.AreEqual( expected, snapshot.TextureMaxDimension );
		Assert.AreEqual( "Game", snapshot.Source );
	}

	[TestMethod]
	public void ExplicitTextureOverrideWinsAndUnavailableSettingsUseFallback()
	{
		Assert.AreEqual( 512, Titanfall2GraphicsSettings.Resolve( TextureQuality.Ultra, 512 ).TextureMaxDimension );
		Assert.AreEqual( "SBOX_TITANFALL2_TEXTURE_MAX", Titanfall2GraphicsSettings.Resolve( TextureQuality.Low, 2048 ).Source );
		Assert.AreEqual( 1024, Titanfall2GraphicsSettings.Resolve( null, null ).TextureMaxDimension );
		Assert.AreEqual( "Fallback", Titanfall2GraphicsSettings.Resolve( null, null ).Source );
	}

	[DataTestMethod]
	[DataRow( "bad", null )]
	[DataRow( "", null )]
	[DataRow( "32", 64 )]
	[DataRow( "65536", 16384 )]
	[DataRow( " 512 ", 512 )]
	public void OptionalOverrideRejectsInvalidNumbersAndClampsBounds( string value, int? expected )
	{
		var name = "TF2_TEST_TEXTURE_OVERRIDE_" + Guid.NewGuid().ToString( "N" );
		try
		{
			Environment.SetEnvironmentVariable( name, value );
			Assert.AreEqual( expected, Titanfall2StreamingSettings.GetOptionalInt( name, 64, 16384 ) );
		}
		finally
		{
			Environment.SetEnvironmentVariable( name, null );
		}
	}

	[DataTestMethod]
	[DataRow( TextureQuality.Low, 1024, 2, 0x22 )]
	[DataRow( TextureQuality.Medium, 2048, 3, 0x33 )]
	[DataRow( TextureQuality.High, 2048, 3, 0x33 )]
	[DataRow( TextureQuality.Ultra, 2048, 3, 0x33 )]
	public void AppliedLimitSelectsTheAuthoredVtfMipWithoutUpscaling( TextureQuality quality, int expectedWidth, int expectedMips, int expectedByte )
	{
		// VTF stores smallest first. Give each mip a different value so a header-only
		// dimension change cannot pass: the selected chain must contain the matching bytes.
		var sizes = new[] { 512 * 512 / 2, 1024 * 1024 / 2, 2048 * 2048 / 2 };
		var bytes = new byte[80 + sizes.Sum()];
		BinaryPrimitives.WriteUInt32LittleEndian( bytes, 0x00465456 );
		BinaryPrimitives.WriteUInt32LittleEndian( bytes.AsSpan( 4 ), 7 );
		BinaryPrimitives.WriteUInt32LittleEndian( bytes.AsSpan( 12 ), 80 );
		BinaryPrimitives.WriteUInt16LittleEndian( bytes.AsSpan( 16 ), 2048 );
		BinaryPrimitives.WriteUInt16LittleEndian( bytes.AsSpan( 18 ), 2048 );
		BinaryPrimitives.WriteUInt16LittleEndian( bytes.AsSpan( 24 ), 1 );
		BinaryPrimitives.WriteInt32LittleEndian( bytes.AsSpan( 52 ), 13 ); // DXT1
		bytes[56] = 3;
		var offset = 80;
		for ( var i = 0; i < sizes.Length; i++ )
		{
			Array.Fill( bytes, (byte)((i + 1) * 0x11), offset, sizes[i] );
			offset += sizes[i];
		}
		var limit = Titanfall2GraphicsSettings.Resolve( quality, null ).TextureMaxDimension;
		Assert.IsTrue( VtfTextureDecoder.TryRead( bytes, false, out var decoded, out var error, limit ), error );
		Assert.AreEqual( expectedWidth, decoded.Width );
		Assert.AreEqual( expectedMips, decoded.MipCount );
		Assert.AreEqual( (byte)expectedByte, decoded.Data[^1] );
	}
}
