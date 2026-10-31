using System;
using System.Reflection;

namespace SceneTests;

[TestClass]
public class Titanfall2SurfaceEncodingTests
{
	[DataTestMethod]
	[DataRow( "Rpak", "uberCutSamp2222_wld", ImageFormat.DXT1, true )]
	[DataRow( "Rpak", "uberSamp2222_wld", ImageFormat.DXT1, true )]
	[DataRow( "Rpak", "uberLyrSamp222222222_wld", ImageFormat.ATI2N, true )]
	[DataRow( "Rpak", "uberAoCav", ImageFormat.RGBA8888, true )]
	[DataRow( "Rpak", "unknown", ImageFormat.ATI2N, true )]
	[DataRow( "Rpak", "unknown", ImageFormat.DXT1, false )]
	[DataRow( "Vmt", "uberCutSamp2222_wld", ImageFormat.DXT1, false )]
	[DataRow( "Vmt", null, ImageFormat.ATI2N, false )]
	public void NormalEncodingFollowsTheSourceShaderRatherThanCompression( string source, string shader, ImageFormat format, bool expected )
	{
		var assembly = typeof( Titanfall2Mount ).Assembly;
		var type = assembly.GetType( "Titanfall2MaterialDescriptor" );
		var descriptor = Activator.CreateInstance( type );
		type.GetProperty( "SourceKind" ).SetValue( descriptor, Enum.Parse( assembly.GetType( "Titanfall2MaterialSourceKind" ), source ) );
		type.GetProperty( "ShaderSetName" ).SetValue( descriptor, shader );
		var actual = (bool)type.GetMethod( "UsesSlopeNormals", BindingFlags.Instance | BindingFlags.NonPublic )
			.Invoke( descriptor, [format] );
		Assert.AreEqual( expected, actual );
	}
}
