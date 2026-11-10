using System;
using System.Runtime.InteropServices;

namespace SceneTests;

[TestClass]
public class Titanfall2GpuSceneContractTests
{
	sealed class Slot : IDisposable { public bool Disposed; public void Dispose() => Disposed = true; }

	[TestMethod]
	public void ViewOutputsAreExclusiveWithinFrameAndReusableAfterwards()
	{
		using var pool = new Titanfall2GpuViewPool<Slot>( () => new() );
		var first = pool.Acquire( 100 );
		var second = pool.Acquire( 100 );
		Assert.AreNotSame( first, second );
		Assert.AreSame( first, pool.Acquire( 101 ) );
		Assert.AreSame( second, pool.Acquire( 101 ) );
		pool.Dispose();
		Assert.IsTrue( first.Disposed && second.Disposed );
		Assert.ThrowsException<ObjectDisposedException>( () => pool.Acquire( 102 ) );
	}

	[TestMethod]
	public void StaticSceneGpuStridesMatchShaderAbi()
	{
		Assert.AreEqual( 44, Marshal.SizeOf<Titanfall2StaticPropGpuScene.Vertex>() );
		Assert.AreEqual( 736, Marshal.SizeOf<Titanfall2GpuMaterialData>() );
		Assert.AreEqual( 64, Marshal.SizeOf<Titanfall2StaticPropGpuScene.Candidate>() );
		Assert.AreEqual( 8, Marshal.SizeOf<Titanfall2StaticPropGpuScene.Bucket>() );
		Assert.AreEqual( 16, Marshal.SizeOf<Titanfall2StaticPropGpuScene.Draw>() );
		Assert.AreEqual( 20, Marshal.SizeOf<GpuBuffer.IndirectDrawIndexedArguments>() );
		Assert.IsTrue( Titanfall2GpuSceneCompatibility.Available, "The stock managed command playback contract changed." );
	}
}
