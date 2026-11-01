using System;
using Titanfall2;

namespace SceneTests;

/// <summary>
/// Covers the shared, reference counted rBSP parse and the disposal of a world build.
/// </summary>
[TestClass]
[DoNotParallelize]
public class Titanfall2MapImportTests
{
	sealed class CountingDisposable : IDisposable
	{
		public int Disposals;
		public void Dispose() => Disposals++;
	}

	static Titanfall2Mount CreateMount() => new();

	[TestMethod]
	public void TwoBuildsOfTheSameMapShareOneParse()
	{
		var mount = CreateMount();
		var parses = 0;
		using var first = mount.BeginMapImport( "map_a", Parse );
		using ( var second = mount.BeginMapImport( "map_a", Parse ) )
		{
			Assert.AreSame( first.Parse, second.Parse, "The full and the pure-world scene must share one parse." );
			Assert.IsNull( first.Error );
			Assert.IsNull( second.Error );
			Assert.AreEqual( 1, parses, "A shared import must read and decode the BSP once." );
			Assert.AreEqual( 1, mount.MapImportCount );
		}
		Assert.AreEqual( 1, mount.MapImportCount, "A remaining user keeps the parse alive." );

		(Titanfall2BspReader.ParsedBsp, string) Parse()
		{
			parses++;
			return (new Titanfall2BspReader.ParsedBsp(), null);
		}
	}

	[TestMethod]
	public void LastUserFreesTheParseAndTheNextBuildReadsAgain()
	{
		var mount = CreateMount();
		var parses = 0;
		var first = mount.BeginMapImport( "map_a", Parse );
		var second = mount.BeginMapImport( "map_a", Parse );
		Assert.IsNotNull( first.Parsed );
		first.Dispose();
		second.Dispose();
		Assert.AreEqual( 0, mount.MapImportCount, "The last user must free the shared parse." );
		using var reopened = mount.BeginMapImport( "map_a", Parse );
		Assert.IsNotNull( reopened.Parsed );
		Assert.AreEqual( 2, parses, "A freed parse must be read again rather than returning stale data." );

		(Titanfall2BspReader.ParsedBsp, string) Parse()
		{
			parses++;
			return (new Titanfall2BspReader.ParsedBsp(), null);
		}
	}

	[TestMethod]
	public void DifferentMapsDoNotShareAParse()
	{
		var mount = CreateMount();
		using var first = mount.BeginMapImport( "map_a", () => (new Titanfall2BspReader.ParsedBsp(), null) );
		using var second = mount.BeginMapImport( "map_b", () => (new Titanfall2BspReader.ParsedBsp(), null) );
		Assert.AreNotSame( first.Parse, second.Parse );
		Assert.AreEqual( 2, mount.MapImportCount );
	}

	[TestMethod]
	public void AFailedParseIsNotCachedAndCanBeRetried()
	{
		var mount = CreateMount();
		var attempts = 0;
		using ( var failed = mount.BeginMapImport( "map_a", Parse ) )
		{
			Assert.AreEqual( "unreadable", failed.Error );
			Assert.IsNull( failed.Parsed );
			// Lazy<T> caches a failure forever, so the entry has to be dropped instead.
			using var retry = mount.BeginMapImport( "map_a", Parse );
			Assert.AreNotSame( failed.Parse, retry.Parse );
			Assert.IsNull( retry.Error );
			Assert.IsNotNull( retry.Parsed );
		}
		Assert.AreEqual( 2, attempts );
		Assert.AreEqual( 0, mount.MapImportCount );

		(Titanfall2BspReader.ParsedBsp, string) Parse()
		{
			attempts++;
			return attempts == 1 ? (null, "unreadable") : (new Titanfall2BspReader.ParsedBsp(), null);
		}
	}

	[TestMethod]
	public void DisposingAWorldBuildReleasesItsTexturesExactlyOnce()
	{
		using var owned = new CountingDisposable();
		var build = new WorldBuildResult( [], [], [], [], [], null, [owned] );
		build.Dispose();
		build.Dispose();
		Assert.AreEqual( 1, owned.Disposals, "A discarded world build must release its lightmap pages, and only once." );
	}
}
