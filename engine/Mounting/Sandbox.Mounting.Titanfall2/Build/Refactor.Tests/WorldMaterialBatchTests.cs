using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sandbox;

[TestClass]
public class WorldMaterialBatchTests
{
	[TestMethod]
	public void RetailRockDetailNormalsRemainSampledOnTheirOwnUvChannel()
	{
		var descriptor = new Titanfall2MaterialDescriptor
		{
			SourceKind = Titanfall2MaterialSourceKind.Rpak,
			ShaderSetName = "uberDetDtnUV2000011Samp222222_skn"
		};
		var resources = new[] { "albedoTexture", "normalTexture", "glossTexture", "specTexture", "detailTexture", "detailNormalTexture" };
		var registers = new[] { 0, 1, 2, 3, 14, 15 };
		for ( var i = 0; i < resources.Length; ++i )
			descriptor.TextureBindings.Add( new( "rock/texture", (ulong)i + 1, registers[i], registers[i], resources[i],
				Titanfall2MaterialDescriptor.InferTextureSemantic( resources[i], "rock/texture", i == 0 ), true ) );
		var detail = descriptor.TextureBindings[^1];
		Assert.AreEqual( Titanfall2TextureSemantic.DetailNormal, detail.Semantic );
		Assert.IsTrue( descriptor.UsesTexture( detail ), "Detail normals must not be discarded after reflection." );
		Assert.AreEqual( 1, descriptor.GetTextureUvChannel( detail ) );
		Assert.AreEqual( 0, descriptor.GetTextureUvChannel( descriptor.TextureBindings[0] ) );
	}

	[TestMethod]
	public void GeneratedMaterialKeysStayIndependentAndReleaseWithTheMapBuild()
	{
		var first = new Material[1]; var second = new Material[2];
		var firstKey = Titanfall2WorldMaterialBatch.Register( first );
		var secondKey = Titanfall2WorldMaterialBatch.Register( second );
		try
		{
			Assert.AreNotEqual( firstKey, secondKey );
			Assert.AreSame( first, Titanfall2WorldMaterialBatch.Resolve( firstKey ) );
			Assert.AreSame( second, Titanfall2WorldMaterialBatch.Resolve( secondKey ) );
			using ( var build = new WorldBuildResult( [], [], [], [], [], null, [], WorldMaterialSourceKey: firstKey ) ) { }
			Assert.AreEqual( 0, Titanfall2WorldMaterialBatch.Resolve( firstKey ).Length );
			Assert.AreSame( second, Titanfall2WorldMaterialBatch.Resolve( secondKey ) );
		}
		finally { Titanfall2WorldMaterialBatch.Release( firstKey ); Titanfall2WorldMaterialBatch.Release( secondKey ); }
	}

	[TestMethod]
	public void MaterialTableVertexPreservesGeometryAndDoesNotExpandTheReferenceVertex()
	{
		var source = new TitanfallMapVertex
		{
			Position = new( -3251.25f, 875.5f, 0.03125f ),
			Normal = new( 0.3f, 0.4f, 0.5f ),
			TexCoord = new( -123.125f, 456.25f ),
			LightmapTexCoord = new( 0.99999f, 0.00001f ),
			Color = new Color32( 41, 82, 123, 164 )
		};
		var actual = TitanfallWorldMaterialVertex.From( source, 16777216 );
		Assert.AreEqual( source.Position, actual.Position ); Assert.AreEqual( source.Normal, actual.Normal );
		Assert.AreEqual( source.TexCoord, actual.TexCoord ); Assert.AreEqual( source.LightmapTexCoord, actual.LightmapTexCoord );
		Assert.AreEqual( source.Color, actual.Color ); Assert.AreEqual( 16777216f, actual.MaterialId );
		Assert.AreEqual( 44, System.Runtime.InteropServices.Marshal.SizeOf<TitanfallMapVertex>() );
		Assert.ThrowsException<ArgumentOutOfRangeException>( () => TitanfallWorldMaterialVertex.From( source, 16777217 ) );
		Assert.ThrowsException<ArgumentOutOfRangeException>( () => TitanfallWorldMaterialVertex.From( source, -1 ) );
	}
}
