using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Reflection;
using System.Threading;

[TestClass]
public class LoaderCompatibilityTests
{
	sealed class Loader : Titanfall2ResourceLoader
	{
		public int Loads;
		public TaskCompletionSource<object> Pending;
		public bool Release() => ReleaseLoaderResult();
		protected override Task<object> LoadAsync()
		{
			Loads++;
			return Pending?.Task ?? Task.FromResult<object>( new object() );
		}
	}

	[TestMethod]
	public async Task ReleaseRebuildsTheNextRequestWithoutDestroyingThePreviousResult()
	{
		var loader = new Loader();
		var first = await loader.GetOrCreate();
		Assert.AreSame( first, await loader.GetOrCreate() );
		Assert.AreEqual( 1, loader.Loads );
		Assert.IsTrue( loader.Release() );
		Assert.IsFalse( loader.Release() );
		Assert.AreNotSame( first, await loader.GetOrCreate() );
		Assert.AreEqual( 2, loader.Loads );
	}

	[TestMethod]
	public async Task InFlightLoadSurvivesReleaseAndCanBeReleasedAfterCompletion()
	{
		var loader = new Loader { Pending = new() };
		var pending = loader.GetOrCreate();
		Assert.IsTrue( Titanfall2OfficialLoaderCache.IsLoading( loader ) );
		Assert.IsFalse( loader.Release() );
		var result = new object();
		loader.Pending.SetResult( result );
		Assert.AreSame( result, await pending );
		Assert.IsFalse( Titanfall2OfficialLoaderCache.IsLoading( loader ) );
		Assert.AreSame( result, await loader.GetOrCreate() );
		Assert.IsTrue( loader.Release() );
		loader.Pending = null;
		Assert.AreNotSame( result, await loader.GetOrCreate() );
	}

	[TestMethod]
	public async Task ReleaseDoesNotBlockOrClearCacheWhileAnotherThreadOwnsTheLoader()
	{
		var loader = new Loader();
		var first = await loader.GetOrCreate();
		var gate = (Lock)typeof( Sandbox.Mounting.ResourceLoader ).GetField( "_lock", BindingFlags.Instance | BindingFlags.NonPublic )!.GetValue( loader )!;
		using var entered = new ManualResetEventSlim();
		using var finish = new ManualResetEventSlim();
		var worker = Task.Run( () =>
		{
			using ( gate.EnterScope() )
			{
				entered.Set();
				if ( !finish.Wait( TimeSpan.FromSeconds( 10 ) ) ) throw new TimeoutException();
			}
		} );
		try
		{
			Assert.IsTrue( entered.Wait( TimeSpan.FromSeconds( 10 ) ) );
			Assert.IsTrue( Titanfall2OfficialLoaderCache.IsLoading( loader ) );
			Assert.IsFalse( loader.Release() );
		}
		finally
		{
			finish.Set();
			await worker;
		}
		Assert.AreSame( first, await loader.GetOrCreate() );
		Assert.IsTrue( loader.Release() );
	}
}
