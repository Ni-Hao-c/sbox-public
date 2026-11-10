using System;
using System.Runtime.InteropServices;
using Titanfall2.Formats;

namespace MountingTests;

[TestClass, DoNotParallelize]
public class Titanfall2CollisionOutcomeValidation
{
	sealed class Source( byte[] bytes, bool failFirst = false ) : ITitanfall2AssetSource
	{
		public int Reads { get; private set; }
		public string Description => "collision-outcome-fixture";
		public bool TryReadAllBytes( out byte[] data, out string error )
		{
			Reads++;
			data = failFirst && Reads == 1 ? null : bytes;
			error = data is null ? "temporary read failure" : null;
			return data is not null;
		}
	}

	static byte[] EmptyMdl()
	{
		var header = new Mdl53StudioHeader { Id = 0x54534449, Version = 53, Length = Marshal.SizeOf<Mdl53StudioHeader>(), Contents = 1 };
		return MemoryMarshal.AsBytes( MemoryMarshal.CreateReadOnlySpan( ref header, 1 ) ).ToArray();
	}

	[TestMethod]
	public void CompletedEmptyImportIsCachedWithoutInventingCollisionOrSuccess()
	{
		var source = new Source( EmptyMdl() );
		var loader = new ModelLoader( source, variantPath: "collision-empty-fixture" );
		var first = loader.GetCollisionVariant( 0, 0 );
		Assert.AreEqual( Titanfall2CollisionModelStatus.Empty, first.Status );
		Assert.IsNull( first.Model );
		Assert.AreEqual( 1u, first.ModelContents );
		Assert.AreEqual( first, loader.GetCollisionVariant( 0, 0 ) );
		Assert.AreEqual( 1, source.Reads, "A completed empty import must not be parsed on every instance." );
	}

	[TestMethod]
	public void ReadFailureCanRecoverWithoutBeingCachedAsEmpty()
	{
		var source = new Source( EmptyMdl(), failFirst: true );
		var loader = new ModelLoader( source, variantPath: "collision-retry-fixture" );
		Assert.AreEqual( Titanfall2CollisionModelStatus.Unavailable, loader.GetCollisionVariant( 0, 0 ).Status );
		Assert.AreEqual( Titanfall2CollisionModelStatus.Empty, loader.GetCollisionVariant( 0, 0 ).Status );
		Assert.AreEqual( 2, source.Reads );
	}

	[TestMethod]
	public void CorruptMdlDoesNotBecomeACompletedEmptyImport()
	{
		var source = new Source( [1, 2, 3] );
		var loader = new ModelLoader( source, variantPath: "collision-corrupt-fixture" );
		Assert.AreEqual( Titanfall2CollisionModelStatus.Unavailable, loader.GetCollisionVariant( 0, 0 ).Status );
		Assert.AreEqual( Titanfall2CollisionModelStatus.Unavailable, loader.GetCollisionVariant( 0, 0 ).Status );
		Assert.AreEqual( 2, source.Reads );
	}

	[TestMethod]
	public void CorruptEmbeddedPhysicsDoesNotBecomeACachedEmptyImport()
	{
		var bytes = EmptyMdl();
		var header = MemoryMarshal.Read<Mdl53StudioHeader>( bytes );
		header.PhysicsOffset = bytes.Length;
		header.PhysicsSize = 128; // Claims bytes beyond the actual file.
		MemoryMarshal.Write( bytes, in header );
		var source = new Source( bytes );
		var loader = new ModelLoader( source, variantPath: "collision-corrupt-physics-fixture" );
		Assert.AreEqual( Titanfall2CollisionModelStatus.Unavailable, loader.GetCollisionVariant( 0, 0 ).Status );
		Assert.AreEqual( Titanfall2CollisionModelStatus.Unavailable, loader.GetCollisionVariant( 0, 0 ).Status );
		Assert.AreEqual( 2, source.Reads );
	}
}
