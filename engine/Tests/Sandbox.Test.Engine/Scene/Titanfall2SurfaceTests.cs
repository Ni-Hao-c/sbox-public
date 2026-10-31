using System;
using System.Reflection;

namespace SceneTests;

/// <summary>
/// Verifies the source-surface-name normalization used to map Titanfall 2 surface properties
/// onto engine <see cref="Surface"/> resources. Unknown names must pass through unchanged so
/// <see cref="Surface.FindByName"/> gets a chance to resolve them before the default fallback.
/// </summary>
[TestClass]
public class Titanfall2SurfaceTests
{
	static readonly Type SurfaceType = typeof( Titanfall2Map ).Assembly.GetType( "Titanfall2Surface", throwOnError: true );

	static string NormalizeName( string name )
	{
		var method = SurfaceType.GetMethod( "NormalizeName", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public );
		Assert.IsNotNull( method, "Titanfall2Surface.NormalizeName was not found." );
		return (string)method.Invoke( null, [name] );
	}

	[DataTestMethod]
	[DataRow( null, "default" )]
	[DataRow( "", "default" )]
	[DataRow( "   ", "default" )]
	[DataRow( "Metal", "metal" )]
	[DataRow( "metal_panel", "metal" )]
	[DataRow( "metal.sheet", "metal.sheet" )]
	[DataRow( "surfaceproperties/concrete", "dirt" )]
	[DataRow( "wood.surface", "wood" )]
	[DataRow( "cloth", "fabric" )]
	[DataRow( "unknown_surface", "unknown_surface" )]
	public void NormalizesSourceSurfaceNames( string input, string expected )
	{
		Assert.AreEqual( expected, NormalizeName( input ) );
	}
}
