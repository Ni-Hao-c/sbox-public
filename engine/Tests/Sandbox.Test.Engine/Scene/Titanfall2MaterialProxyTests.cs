using System;
using System.Collections;
using System.Reflection;
using System.Text;

namespace SceneTests;

[TestClass]
public class Titanfall2MaterialProxyTests
{
	static readonly Assembly MountAssembly = typeof( Titanfall2Mount ).Assembly;

	static object ParseScroll( string parameters )
		=> ParseProxy( "TextureScroll", "texturescrollvar \"$basetexturetransform\" " + parameters );

	static object ParseProxy( string type, string parameters )
	{
		var source = "UnlitTwoTexture { Proxies { " + type + " { " + parameters + " } } }";
		object[] arguments = [Encoding.UTF8.GetBytes( source ), null, null];
		Assert.IsTrue( (bool)MountAssembly.GetType( "Titanfall2VmtReader" ).GetMethod( "TryRead", BindingFlags.Static | BindingFlags.NonPublic ).Invoke( null, arguments ), (string)arguments[2] );
		var definition = arguments[1];
		return ((IEnumerable)definition.GetType().GetMethod( "GetRuntimeExpressions", BindingFlags.Instance | BindingFlags.NonPublic )
			.Invoke( definition, null )).Cast<object>().Single();
	}

	[DataTestMethod]
	[DataRow( "texturescale .25", 0.25f )]
	[DataRow( "texturescale 2", 2f )]
	[DataRow( "texturescale .8", 0.8f )]
	[DataRow( "texturescale 0", 0f )]
	[DataRow( "", 1f )]
	public void ScrollPreservesAuthoredScaleAndDefault( string parameters, float expected )
	{
		var expression = ParseScroll( parameters );
		var values = (Vector4)expression.GetType().GetProperty( "Parameters" ).GetValue( expression );
		Assert.AreEqual( expected, values.z );
	}

	[TestMethod]
	public void ScrollPreservesVariableScaleInput()
	{
		var expression = ParseScroll( "texturescale \"$scrollscale\"" );
		Assert.AreEqual( "$scrollscale", expression.GetType().GetProperty( "SourceC" ).GetValue( expression ) );
	}

	[DataTestMethod]
	[DataRow( "", 1f )]
	[DataRow( "texturescrollrate 0", 0f )]
	public void ScrollRateUsesSourceDefaultAndPreservesExplicitZero( string parameters, float expected )
	{
		var expression = ParseScroll( parameters );
		var values = (Vector4)expression.GetType().GetProperty( "Parameters" ).GetValue( expression );
		Assert.AreEqual( expected, values.x );
	}

	[DataTestMethod]
	[DataRow( 0f )]
	[DataRow( -4f )]
	[DataRow( 0.00001f )]
	public void SinePreservesAuthoredPeriod( float period )
	{
		var expression = ParseProxy( "Sine", "resultvar \"$alpha\" sineperiod " + period.ToString( System.Globalization.CultureInfo.InvariantCulture ) );
		var values = (Vector4)expression.GetType().GetProperty( "Parameters" ).GetValue( expression );
		Assert.AreEqual( period, values.z );
	}

	[DataTestMethod]
	[DataRow( 4f, 1f, 0f, 0f )]
	[DataRow( 4f, 1f, 2f, 1f )]
	[DataRow( 4f, -1f, 0f, 1f )]
	[DataRow( 0f, 0f, 0.25f, 1f )]
	[DataRow( -4f, 0f, 1f, 0f )]
	[DataRow( 0.00001f, 0f, 0.0000025f, 1f )]
	public void SineUsesSourceDelayAndPeriodSemantics( float period, float offset, float time, float expected )
	{
		var registry = MountAssembly.GetType( "Titanfall2MaterialAnimationRegistry" );
		var actual = (float)registry.GetMethod( "EvaluateSine", BindingFlags.Static | BindingFlags.NonPublic )
			.Invoke( null, [new Vector4( 0f, 1f, period, offset ), time] );
		Assert.AreEqual( expected, actual, 0.0001f );
	}
}
