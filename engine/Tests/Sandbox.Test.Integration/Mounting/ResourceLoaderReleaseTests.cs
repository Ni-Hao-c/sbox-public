using Sandbox.Mounting;

namespace MountingTests;

[TestClass]
public class ResourceLoaderReleaseTests
{
	sealed class CountingLoader : Sandbox.Mounting.ResourceLoader
	{
		public int Loads { get; private set; }

		protected override object Load()
		{
			Loads++;
			return new object();
		}

		public bool Release() => ReleaseCachedResult();
	}

	[TestMethod]
	public async Task ReleasedResultIsLoadedAgain()
	{
		var loader = new CountingLoader();
		Assert.IsFalse( loader.Release(), "Nothing is cached before the first load" );

		var first = await loader.GetOrCreate();
		Assert.AreSame( first, await loader.GetOrCreate(), "A cached result is returned until it is released" );
		Assert.AreEqual( 1, loader.Loads );

		Assert.IsTrue( loader.Release() );
		var second = await loader.GetOrCreate();
		Assert.AreNotSame( first, second, "The next request after a release loads a new result" );
		Assert.AreEqual( 2, loader.Loads );
	}
}
