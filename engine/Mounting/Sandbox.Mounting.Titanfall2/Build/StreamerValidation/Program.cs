using Sandbox;
using Sandbox.Mounting;
using Sandbox.Rendering;
using NativeEngine;
using System.Reflection;
using System.Text.Json;
using Titanfall2;

// Optional native lifecycle fixture. Run in its own process with FACEPUNCH_ENGINE
// pointing at the same installation used by SboxEngineRoot. Never deploys a DLL.
static class Program
{
	const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
	static readonly List<object> Results = [];
	sealed record ConfigSnapshot( string Path, byte[] Bytes, DateTime LastWriteUtc );

	static ConfigSnapshot[] SaveConfig()
	{
		var engine = Environment.GetEnvironmentVariable( "FACEPUNCH_ENGINE" ) ?? throw new InvalidOperationException( "Set FACEPUNCH_ENGINE." );
		return new[] { "core/cfg/video.txt", "config/video.json" }.Select( relative =>
		{
			var path = Path.Combine( engine, relative );
			return new ConfigSnapshot( path, File.Exists( path ) ? File.ReadAllBytes( path ) : null, File.GetLastWriteTimeUtc( path ) );
		} ).ToArray();
	}

	static void RestoreConfig( ConfigSnapshot[] snapshots )
	{
		foreach ( var snapshot in snapshots )
		{
			if ( snapshot.Bytes is null ) { File.Delete( snapshot.Path ); continue; }
			File.WriteAllBytes( snapshot.Path, snapshot.Bytes );
			File.SetLastWriteTimeUtc( snapshot.Path, snapshot.LastWriteUtc );
		}
	}

	sealed class InstalledGame( string directory ) : ISteamIntegration
	{
		public string GetAppDirectory( long id ) => id == 1237970 ? directory : "";
		public bool IsAppInstalled( long id ) => id == 1237970;
		public bool IsAppOwned( long id ) => id == 1237970;
		public bool IsDlcInstalled( long id ) => false;
	}

	static void Check( bool pass, string message )
	{
		if ( !pass ) throw new InvalidOperationException( message );
	}

	static Titanfall2StaticPropRenderResources Resources( Titanfall2StaticPropStreamer component ) =>
		(Titanfall2StaticPropRenderResources)typeof( Titanfall2StaticPropStreamer ).GetField( "_renderResources", PrivateInstance )!.GetValue( component )!;

	static void Main( string[] args )
	{
		var report = Path.GetFullPath( args[0] );
		var config = SaveConfig();
		try
		{
			var app = new TestAppSystem();
			app.Init();
			var module = typeof( Titanfall2Mount ).Assembly;
			foreach ( var other in AppDomain.CurrentDomain.GetAssemblies().Where( a => a != module && a.GetName().Name == module.GetName().Name ) )
				Game.TypeLibrary.RemoveAssembly( other );
			Game.TypeLibrary.AddAssembly( module, false );
			CheckDataOnlyAwakeAndClone();
			CheckRenderOwnerLifecycle();
			CheckRejectedLoadingTransaction();
			if ( args.Length > 1 ) CheckMapReload( args[1], args.Length > 2 ? args[2] : "sp_boomtown_start" );
			var commandType = typeof( CommandList );
			Results.Add( new
			{
				OfficialApi = typeof( Scene ).Assembly.Location,
				PlaybackPublic = commandType.GetMethod( "ExecuteOnRenderThread", PrivateInstance )?.IsPublic ?? false,
				RecorderPublic = commandType.GetMethod( "AddAction", PrivateInstance )?.IsPublic ?? false,
				ClutterBatchPublic = typeof( Scene ).Assembly.GetType( "Sandbox.Clutter.ClutterBatchSceneObject" )?.IsPublic ?? false
			} );
			File.WriteAllText( report, JsonSerializer.Serialize( new { success = true, mvid = module.ManifestModule.ModuleVersionId, Results }, new JsonSerializerOptions { WriteIndented = true } ) );
			Console.WriteLine( "STATIC_PROP_LIFECYCLE_PASS" );
			RestoreConfig( config );
			EngineGlobal.Plat_ExitProcess( 0 );
		}
		catch ( Exception exception )
		{
			File.WriteAllText( report, JsonSerializer.Serialize( new { success = false, error = exception.ToString(), Results }, new JsonSerializerOptions { WriteIndented = true } ) );
			Console.Error.WriteLine( exception );
			RestoreConfig( config );
			EngineGlobal.Plat_ExitProcess( 1 );
		}
	}

	static void CheckDataOnlyAwakeAndClone()
	{
		var scene = new Scene();
		try
		{
			using var scope = scene.Push();
			var template = scene.CreateObject().AddComponent<Titanfall2StaticPropStreamer>();
			var payload = Titanfall2StaticPropSceneData.Compile( [new() { ModelPath = "models/test.mdl" }], null, null, null );
			template.ConfigureCompiled( "titanfall2", "test", Vector3.Zero, payload, Color.White );
			template.EncodedLocalLights = Convert.ToBase64String( new byte[80] );
			typeof( Titanfall2StaticPropStreamer ).GetMethod( "InitializeCells", PrivateInstance )!.Invoke( template, null );
			var owner = Resources( template );
			Check( !owner.IsPrepared && owner.Lights is null && owner.GpuScene is null, "Data-only restoration allocated render resources." );
			Results.Add( new { Test = "DataOnlyAwake", Snapshot = template.GetCompiledSceneStatistics() } );

			var empty = scene.CreateObject().AddComponent<Titanfall2StaticPropStreamer>();
			empty.KeepAllCollisions = true;
			empty.ConfigureCompiled( "titanfall2", "empty", Vector3.Zero, Titanfall2StaticPropSceneData.Compile( [], null, null, null ), Color.White );
			var clone = empty.GameObject.Clone().GetComponent<Titanfall2StaticPropStreamer>();
			Check( clone.PreparationComplete && clone.ResidentCollisionsReady && clone.ResourcesPrepared, "Empty clone lost readiness semantics." );
			Check( !ReferenceEquals( Resources( empty ), Resources( clone ) ), "Scene clone shared render ownership." );
			empty.GameObject.Destroy();
			Check( clone.ResourcesPrepared && Resources( clone ).GpuScene is null, "Destroying template affected clone." );
			Results.Add( new { Test = "IndependentClone", Snapshot = clone.GetCompiledSceneStatistics() } );
		}
		finally { scene.Destroy(); }
	}

	static void CheckRenderOwnerLifecycle()
	{
		var encoded = Convert.ToBase64String( new byte[80] );
		using var first = new Titanfall2StaticPropRenderResources();
		using var second = new Titanfall2StaticPropRenderResources();
		first.Prepare( encoded );
		second.Prepare( encoded );
		var scene = new Scene();
		try
		{
			var renderObject = new SceneObject( scene.SceneWorld, Model.Cube );
			first.Track( renderObject );
			Check( first.NativeObjectCount == 1 && renderObject.IsValid(), "Native render ownership was not registered." );
			first.Dispose();
			Check( first.NativeObjectCount == 0 && !renderObject.IsValid(), "Render owner did not delete its native object." );
		}
		finally { scene.Destroy(); }
		// A second pair covers repeated preparation independently of the native-object teardown.
		using var prepared = new Titanfall2StaticPropRenderResources();
		prepared.Prepare( encoded );
		Check( prepared.IsPrepared && second.IsPrepared, "Render preparation did not finish." );
		if ( Graphics.IsAvailable )
		{
			Check( prepared.Lights is not null && second.Lights is not null && !ReferenceEquals( prepared.Lights, second.Lights ), "Scenes shared local-light buffers." );
			var lights = prepared.Lights;
			prepared.Prepare( encoded );
			Check( ReferenceEquals( lights, prepared.Lights ), "Repeated preparation uploaded lights twice." );
		}
		else Check( prepared.Lights is null && second.Lights is null, "Headless preparation allocated light buffers." );
		first.Dispose();
		first.Dispose();
		Check( !first.IsPrepared && first.Lights is null && first.GpuScene is null && second.IsPrepared, "Render teardown leaked or affected another scene." );
		try { first.Prepare( encoded ); throw new InvalidOperationException( "Disposed owner accepted preparation." ); }
		catch ( ObjectDisposedException ) { }
		Results.Add( new { Test = "RenderOwnerLifetime", Graphics.IsAvailable } );
	}

	static void CheckMapReload( string gameDirectory, string map )
	{
		using var host = new MountHost( new Configuration { SteamIntegration = new InstalledGame( gameDirectory ) } );
		typeof( Sandbox.Mounting.Directory ).GetField( "_system", BindingFlags.Static | BindingFlags.NonPublic )!.SetValue( null, host );
		host.Initialize( typeof( Titanfall2Mount ) );
		host.Mount( "titanfall2" ).GetAwaiter().GetResult();
		var mount = (Titanfall2Mount)host.GetSource( "titanfall2" );
		var loader = mount.GetByPath( $"mount://titanfall2/maps/{map}.bsp.world.scene" );
		var file = (SceneFile)loader.GetOrCreate().GetAwaiter().GetResult();
		int? instances = null, collisions = null;
		for ( var cycle = 0; cycle < 2; ++cycle )
		{
			var scene = new Scene();
			Titanfall2StaticPropRenderResources owner = null;
			try
			{
				using var scope = scene.Push();
				var options = new SceneLoadOptions { ShowLoadingScreen = false };
				options.SetScene( file );
				Check( scene.Load( options ), "Map scene load failed." );
				var props = scene.GetComponentsInChildren<Titanfall2StaticPropStreamer>().Single();
				owner = Resources( props );
				var loadTimer = System.Diagnostics.Stopwatch.StartNew();
				while ( !props.PreparationComplete && loadTimer.Elapsed.TotalMinutes < 10 )
				{
					EngineGlobal.SourceEnginePanelAppFrame();
					scene.GameTick( 1.0 / 60 );
					EngineLoop.RunAsyncTasks();
					EngineLoop.DrainFrameEndDisposables();
				}
				Results.Add( new { Test = "MapLoadingOutcome", cycle, Snapshot = props.GetCompiledSceneStatistics() } );
				Check( props.ResourcesPrepared && props.ResidentCollisionsReady, "Loaded map is not ready." );
				Check( owner.GpuScene is not null && owner.GpuScene.InstanceCount > 0, "Loaded scene has no GPU props." );
				Check( instances is null || instances == owner.GpuScene.InstanceCount, "Reload lost GPU instances." );
				Check( collisions is null || collisions == props.ResidentCollisionCount, "Reload changed collision count." );
				instances = owner.GpuScene.InstanceCount;
				collisions = props.ResidentCollisionCount;
				props.Enabled = false;
				Check( !owner.GpuScene.RenderingEnabled, "Disabled component still renders." );
				props.Enabled = true;
				Check( owner.GpuScene.RenderingEnabled, "Enabled component did not restore rendering." );
				var camera = scene.CreateObject().AddComponent<CameraComponent>();
				camera.CustomSize = new( 640, 360 );
				camera.ZFar = 50000;
				camera.WorldPosition = map == "sp_boomtown_start" ? new( 12027.547f, -8760.884f, 9795.224f ) : new( 2284, -1766, 210 );
				camera.WorldRotation = map == "sp_boomtown_start" ? new Angles( -16.276112f, -487.74136f, 0 ) : new Angles( -3, 76, 0 );
				using var target = Texture.CreateRenderTarget().WithSize( 640, 360 ).Create( "static-prop-lifecycle" );
				for ( var frame = 0; frame < 4; ++frame )
				{
					EngineGlobal.SourceEnginePanelAppFrame();
					scene.GameTick( 1.0 / 60 );
					scene.PreRender();
					Check( camera.RenderToTexture( target, new ViewSetup { EnablePostprocessing = true, EnableUI = false } ), "Map render failed." );
					g_pRenderDevice.Present( IntPtr.Zero );
					g_pRenderDevice.ForceFlushGPU( IntPtr.Zero );
					EngineLoop.DrainFrameEndDisposables();
					RenderTarget.EndOfFrame();
					EngineLoop.RunAsyncTasks();
				}
				Results.Add( new { Test = "MapLoadRenderReload", cycle, Snapshot = props.GetCompiledSceneStatistics() } );
			}
			finally { scene.Destroy(); }
			Check( owner is not null && !owner.IsPrepared && owner.GpuScene is null && owner.Lights is null && owner.NativeObjectCount == 0,
				"Scene destruction retained render resources." );
		}
	}

	static void CheckRejectedLoadingTransaction()
	{
		// Non-rendering scenes deliberately do not read or allocate the render light table.
		if ( !Graphics.IsAvailable ) return;
		var scene = new Scene();
		try
		{
			using var scope = scene.Push();
			var props = scene.CreateObject().AddComponent<Titanfall2StaticPropStreamer>();
			props.KeepAllCollisions = true;
			props.ConfigureCompiled( "titanfall2", "invalid-lights", Vector3.Zero,
				Titanfall2StaticPropSceneData.Compile( [new() { ModelPath = "models/test.mdl" }], null, null, null ), Color.White );
			props.EncodedLocalLights = "invalid!";
			var method = typeof( Titanfall2StaticPropStreamer ).GetMethod( "OnLoad", PrivateInstance, [typeof( LoadingContext )] )!;
			try
			{
				((Task)method.Invoke( props, [new LoadingContext()] )!).GetAwaiter().GetResult();
				throw new InvalidOperationException( "Invalid light data did not fail the loading transaction." );
			}
			catch ( FormatException ) { }
			var owner = Resources( props );
			Check( !props.ResourcesPrepared && !props.ResidentCollisionsReady && props.ResidentCollisionCount == 0,
				"Failed loading transaction published readiness." );
			Check( !owner.IsPrepared && owner.GpuScene is null && owner.Lights is null && owner.NativeObjectCount == 0,
				"Failed loading transaction retained render resources." );
			Results.Add( new { Test = "RejectedLoadingTransaction", Snapshot = props.GetCompiledSceneStatistics() } );
		}
		finally { scene.Destroy(); }
	}
}
