using System;

namespace MountingTests;

/// <summary>
/// Tests the native-decal factory that unpacks a Titanfall 2 decal material into the textures a
/// <see cref="DecalDefinition"/> needs. The material is created locally so the test does not
/// need a retail Titanfall 2 installation.
/// </summary>
[TestClass]
public class Titanfall2DecalFactoryTests
{
	Material CreateMaterial( string shader = "shaders/complex.shader" )
	{
		var material = Material.Create( $"integration/decal_{Guid.NewGuid():N}", shader );
		Assert.IsTrue( material.IsValid, "Could not create the test material." );
		return material;
	}

	[TestMethod]
	public void ExtractsColourAndNormalTextures()
	{
		var material = CreateMaterial();
		try
		{
			material.Set( "g_tAlbedo", Texture.White );
			material.Set( "g_tNormal", Texture.White );

			Assert.IsTrue( Titanfall2DecalFactory.TryCreateDefinition( material, out var definition, out var error ), error );
			Assert.IsNotNull( definition );
			// Texture getters return a fresh managed wrapper for the same native texture, so
			// assert the bound textures are present rather than comparing wrapper instances.
			Assert.IsTrue( definition.ColorTexture.IsValid(), "The albedo texture must be bound." );
			Assert.IsTrue( definition.NormalTexture.IsValid(), "The normal texture must be bound." );
			Assert.IsNull( definition.EmissiveTexture, "An unbound emissive map must stay unset." );
		}
		finally
		{
			material.Destroy();
		}
	}

	[TestMethod]
	public void RejectsMaterialsWithoutAColourMap()
	{
		var material = CreateMaterial();
		try
		{
			Assert.IsFalse( Titanfall2DecalFactory.TryCreateDefinition( material, out var definition, out var error ) );
			Assert.IsNull( definition );
			Assert.IsFalse( string.IsNullOrWhiteSpace( error ) );
		}
		finally
		{
			material.Destroy();
		}
	}

	[TestMethod]
	public void RotationPointsIntoTheSurface()
	{
		var rotation = Titanfall2DecalFactory.RotationForNormal( Vector3.Up );
		Assert.IsTrue( rotation.Forward.Dot( -Vector3.Up ) > 0.9f,
			"A decal's forward axis must point into the surface it projects onto." );
	}

	[TestMethod]
	public void RejectsUnknownMaterialPathsWithoutThrowing()
	{
		Assert.IsFalse( Titanfall2DecalFactory.TryCreateDefinition( "materials/does/not/exist", out var definition, out var error ) );
		Assert.IsNull( definition );
		Assert.IsFalse( string.IsNullOrWhiteSpace( error ) );
	}
}
