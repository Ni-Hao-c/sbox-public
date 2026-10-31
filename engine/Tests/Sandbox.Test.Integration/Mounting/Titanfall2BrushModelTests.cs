using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;

namespace MountingTests;

/// <summary>Real native model/physics checks, without starting or modifying an editor.</summary>
[TestClass]
public class Titanfall2BrushModelTest
{
	[DataTestMethod]
	[DataRow( false )]
	[DataRow( true )]
	public void IndependentBrushKeepsOpeningAndMovesItsCollisions( bool tricoll )
	{
		var file = Path.Combine( Path.GetTempPath(), $"tf2-brush-{Guid.NewGuid():N}.bsp" );
		Scene scene = null;
		IDisposable source = null;
		try
		{
			File.WriteAllBytes( file, Fixture( tricoll ) );
			var assembly = typeof( Titanfall2Mount ).Assembly;
			var assetSource = Activator.CreateInstance( assembly.GetType( "LooseAssetSource", true ), file );
			var sourceType = assembly.GetType( "Titanfall2BrushModelSource", true );
			source = (IDisposable)Activator.CreateInstance( sourceType, [assetSource, null] );
			var model = (Model)sourceType.GetMethod( "Build", BindingFlags.NonPublic | BindingFlags.Instance )
				.Invoke( source, [new Titanfall2Mount(), $"titanfall2/test/brush/{Guid.NewGuid():N}", 1] );
			Assert.IsTrue( model.IsValid && !model.IsError );
			Assert.AreEqual( tricoll ? 1 : 2, model.Physics.Parts.Sum( part => part.Hulls.Count ) );
			Assert.AreEqual( tricoll ? 1 : 0, model.Physics.Parts.Sum( part => part.Meshes.Count ) );
			var data = model.GetData<Titanfall2BrushModelData>();
			Assert.AreEqual( 1, data.ModelIndex ); Assert.AreEqual( 2, data.Parts.Length );
			Assert.AreEqual( 0x00EB0280u, data.Parts[0].Contents );
			var groups = ((uint Contents, Model Model)[])sourceType.GetMethod( "BuildCollisionGroups", BindingFlags.NonPublic | BindingFlags.Instance )
				.Invoke( source, [$"titanfall2/test/brush/{Guid.NewGuid():N}", 1] );
			Assert.AreEqual( data.Parts.Select( part => part.Contents ).Distinct().Count(), groups.Length );
			Assert.AreEqual( 0x00EB0280u, groups[0].Contents );
			Assert.AreEqual( model.Physics.Parts.Sum( part => part.Hulls.Count + part.Meshes.Count ),
				groups.Sum( group => group.Model.Physics.Parts.Sum( part => part.Hulls.Count + part.Meshes.Count ) ) );

			scene = new Scene();
			using var scope = scene.Push();
			var go = scene.CreateObject();
			var collider = go.AddComponent<ModelCollider>(); collider.Static = false; collider.Model = model;
			Assert.AreEqual( 2, collider.Shapes.Count );
			Assert.IsTrue( scene.Trace.Ray( new Vector3( 120, 0, 0 ), new Vector3( 80, 0, 0 ) ).Run().Hit,
				"The second source primitive must be queryable before it moves." );
			if ( tricoll ) Assert.IsFalse( scene.Trace.Ray( new Vector3( 80, 0, 0 ), new Vector3( 120, 0, 0 ) ).Run().Hit,
				"The original clockwise tricoll must remain single sided after conversion." );
			Assert.IsTrue( scene.Trace.Ray( new Vector3( -20, 0, 0 ), new Vector3( 20, 0, 0 ) ).Run().Hit );
			Assert.IsFalse( scene.Trace.Ray( new Vector3( 50, -20, 0 ), new Vector3( 50, 20, 0 ) ).Run().Hit,
				"Separate source brushes must leave the opening between them empty." );
			go.WorldPosition = new Vector3( 1000, 500, 0 );
			go.WorldRotation = Rotation.FromYaw( 90 );
			// Non-static colliders are keyframed by the engine's next physics step.
			for ( var tick = 0; tick < 3; tick++ ) scene.GameTick( 0.02 );
			Assert.IsFalse( scene.Trace.Ray( new Vector3( -20, 0, 0 ), new Vector3( 20, 0, 0 ) ).Run().Hit,
				"Moving the game object must not leave collision at the old position." );
			Assert.IsTrue( scene.Trace.Ray( new Vector3( 980, 500, 0 ), new Vector3( 1020, 500, 0 ) ).Run().Hit );
			Assert.IsTrue( scene.Trace.Ray( go.WorldTransform.PointToWorld( new Vector3( 120, 0, 0 ) ),
				go.WorldTransform.PointToWorld( new Vector3( 80, 0, 0 ) ) ).Run().Hit,
				"The original front-facing ray must follow the second primitive's transform." );
			Assert.IsFalse( scene.Trace.Ray( new Vector3( 980, 550, 0 ), new Vector3( 1020, 550, 0 ) ).Run().Hit );
		}
		finally
		{
			scene?.Destroy();
			source?.Dispose();
			File.Delete( file );
		}
	}

	static byte[] Fixture( bool tricoll )
	{
		var lumps = new Dictionary<int, byte[]>
		{
			[14] = new byte[64],
			[85] = new byte[28],
			[86] = new byte[12],
			[87] = new byte[16],
			[91] = new byte[4],
			[92] = new byte[64]
		};
		Int( lumps[85], 12, 1 ); Int( lumps[85], 16, 1 );
		// World owns zero sets; model 1 owns both source convex brushes.
		BinaryPrimitives.WriteUInt16LittleEndian( lumps[86].AsSpan( 10 ), 2 );
		Int( lumps[91], 0, 0x00EB0280 );
		for ( var i = 0; i < 2; i++ )
		{
			BinaryPrimitives.WriteUInt16LittleEndian( lumps[87].AsSpan( i * 8 + 2 ), 1 );
			Int( lumps[87], i * 8 + 4, i << 8 );
			Float( lumps[92], i * 32, i * 100 );
			for ( var axis = 0; axis < 3; axis++ ) Float( lumps[92], i * 32 + 16 + axis * 4, 10 );
		}
		if ( tricoll )
		{
			Int( lumps[87], 12, 0x40000000 );
			lumps[3] = new byte[36]; lumps[69] = new byte[44]; lumps[66] = new byte[4];
			// Retail render/tricoll order is clockwise relative to its front normal.
			// This face points +X after import (raw cross product -X), just as the
			// matched retail triangles do; a counter-clockwise fixture tests its back.
			var points = new[] { new Vector3( 100, 10, -10 ), new Vector3( 100, -10, -10 ), new Vector3( 100, 0, 10 ) };
			for ( var i = 0; i < points.Length; i++ )
			{
				Float( lumps[3], i * 12, points[i].x ); Float( lumps[3], i * 12 + 4, points[i].y ); Float( lumps[3], i * 12 + 8, points[i].z );
			}
			BinaryPrimitives.WriteUInt16LittleEndian( lumps[69].AsSpan( 6 ), 3 );
			BinaryPrimitives.WriteUInt16LittleEndian( lumps[69].AsSpan( 8 ), 1 );
			Int( lumps[66], 0, (1 << 10) | (2 << 17) );
		}
		var bytes = new byte[16 + 128 * 16 + lumps.Sum( pair => pair.Value.Length )];
		Encoding.ASCII.GetBytes( "rBSP" ).CopyTo( bytes, 0 ); Int( bytes, 4, 37 ); Int( bytes, 12, 127 );
		var offset = 16 + 128 * 16;
		foreach ( var (id, data) in lumps )
		{
			Int( bytes, 16 + id * 16, offset ); Int( bytes, 20 + id * 16, data.Length );
			data.CopyTo( bytes, offset ); offset += data.Length;
		}
		return bytes;
	}
	static void Int( byte[] bytes, int offset, int value ) => BinaryPrimitives.WriteInt32LittleEndian( bytes.AsSpan( offset ), value );
	static void Float( byte[] bytes, int offset, float value ) => Int( bytes, offset, BitConverter.SingleToInt32Bits( value ) );
}
