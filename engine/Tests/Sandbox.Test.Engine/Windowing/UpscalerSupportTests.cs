using NativeEngine;
using Sandbox.Engine;
using Sandbox.Engine.Settings;
using System;
using System.Reflection;
using System.Runtime.InteropServices;

namespace EngineTests;

[TestClass, DoNotParallelize]
public unsafe class UpscalerSupportTests
{
	static int queryCount;

	[DataTestMethod]
	[DataRow( "NVIDIA GeForce RTX 4060 Laptop GPU", true )]
	[DataRow( "NVIDIA RTX A6000", true )]
	[DataRow( "nvidia geforce rtx 5090", true )]
	[DataRow( "NVIDIA GeForce GTX 1080", false )]
	[DataRow( "AMD Radeon RX 7900 XTX", false )]
	[DataRow( "Intel Arc A770 Graphics", false )]
	[DataRow( "unset", false )]
	[DataRow( null, false )]
	public void SettingsUseCachedHardwareWithoutInitializingNgx( string gpuName, bool dlssSupported )
	{
		var query = RenderDeviceManager.__N.Glue_RndrDvcMngr_IsUpscalerSupported;
		var gpu = typeof( SystemInfo ).GetProperty( nameof( SystemInfo.Gpu ), BindingFlags.Public | BindingFlags.Static );
		var originalGpu = SystemInfo.Gpu;
		try
		{
			RenderDeviceManager.__N.Glue_RndrDvcMngr_IsUpscalerSupported = &Query;
			gpu.SetValue( null, gpuName );
			queryCount = 0;
			Assert.AreEqual( dlssSupported, RenderSettings.IsUpscalerModeSupported( UpscalerMode.DLSS ) );
			foreach ( var mode in new[] { UpscalerMode.Off, UpscalerMode.Stretch, UpscalerMode.FSR1, UpscalerMode.FSR3 } )
				Assert.IsTrue( RenderSettings.IsUpscalerModeSupported( mode ) );
			Assert.AreEqual( 0, queryCount, "Opening settings must not enter NGX's blocking capability query." );
		}
		finally
		{
			gpu.SetValue( null, originalGpu );
			RenderDeviceManager.__N.Glue_RndrDvcMngr_IsUpscalerSupported = query;
		}
	}

	[UnmanagedCallersOnly]
	static int Query( int type )
	{
		queryCount++;
		return 0;
	}
}
