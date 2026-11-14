using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanfall2;
using static Titanfall2StaticPropSceneData;

[TestClass]
public class StaticPropSceneDataTests
{
	[TestMethod]
	public void CompilePreservesStableIdsSkinsCollisionAndFadeAfterFilteringEmptyPaths()
	{
		Titanfall2BspReader.StaticPropInstance[] source =
		[
			new() { ModelPath = "models/rock.mdl", Origin = new( -1, 600, 2 ), Skin = 2, SourceIndex = 91,
				SolidType = 6, ReservedCollisionData = 123, CollisionFlagsRemove = 4, Scale = 2 },
			new() { ModelPath = "" },
			new() { ModelPath = "models/rock.mdl", Origin = new( 700, -1, 3 ), Skin = 3, SourceIndex = 7, Scale = 1 }
		];
		var payload = Compile( source, null, null, [1500, 9999, 2500] );
		var decoded = ReadCompiledScene( payload.Props );
		Assert.IsTrue( decoded.FromCompiledPayload );
		Assert.AreEqual( 2, decoded.Props.Length );
		CollectionAssert.AreEqual( new[] { 0, 1 }, decoded.Props.Select( x => x.InstanceId ).ToArray() );
		CollectionAssert.AreEqual( new[] { 91, 7 }, decoded.Props.Select( x => x.SourceIndex ).ToArray() );
		CollectionAssert.AreEqual( new[] { 2, 3 }, decoded.Props.Select( x => x.Skin ).ToArray() );
		CollectionAssert.AreEqual( new[] { 1500f, 2500f }, decoded.Props.Select( x => x.FadeDistance ).ToArray() );
		Assert.IsTrue( decoded.Props[0].Collidable );
		Assert.AreEqual( (byte)6, decoded.Props[0].SolidType );
		Assert.AreEqual( 123u, decoded.Props[0].ReservedCollisionData );
		Assert.AreEqual( 4u, decoded.Props[0].CollisionFlagsRemove );
		Assert.AreEqual( 2, decoded.Layout.Models.Length );
		CollectionAssert.AreEquivalent( new[] { 0, 1 }, decoded.Layout.CellInstances );
		Assert.IsNull( payload.LocalLights );
	}

	[TestMethod]
	public void PayloadCompilationIsDeterministicAndDecodesIntoIndependentSceneData()
	{
		Titanfall2BspReader.StaticPropInstance[] source = [new() { ModelPath = "models/rock.mdl", Scale = 1 }];
		var first = Compile( source, null, null, null );
		Assert.AreEqual( first, Compile( source, null, null, null ) );
		var a = ReadCompiledScene( first.Props );
		var b = ReadCompiledScene( first.Props );
		Assert.AreNotSame( a.Props, b.Props );
		Assert.AreNotSame( a.Layout.CellInstances, b.Layout.CellInstances );
		a.Props[0] = a.Props[0] with { InstanceId = 99 };
		Assert.AreEqual( 0, b.Props[0].InstanceId );
	}

	[TestMethod]
	public void CorruptCompiledPayloadCannotBecomeAnEmptySuccessfulScene()
	{
		var payload = Compile( [new() { ModelPath = "models/rock.mdl", Scale = 1 }], null, null, null );
		var bytes = Convert.FromBase64String( payload.Props );
		bytes[^1] ^= 1;
		Assert.ThrowsException<InvalidDataException>( () => ReadCompiledScene( Convert.ToBase64String( bytes ) ) );
		Assert.ThrowsException<EndOfStreamException>( () => ReadCompiledScene( Convert.ToBase64String( bytes[..20] ) ) );
	}

	[TestMethod]
	public void EmptyCompiledMapRemainsAValidDataOnlyPayload()
	{
		var payload = Compile( [], null, null, null );
		var decoded = ReadCompiledScene( payload.Props );
		Assert.AreEqual( 0, decoded.Props.Length );
		Assert.AreEqual( 0, decoded.Layout.Cells.Length );
		Assert.IsTrue( decoded.FromCompiledPayload );
		Assert.IsNull( ReadVisibility( payload.Visibility, out var visibility ) );
		Assert.IsNull( visibility );
	}
}
