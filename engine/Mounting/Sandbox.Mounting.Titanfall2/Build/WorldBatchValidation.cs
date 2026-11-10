using System;
using System.Collections.Generic;
using NativeEngine;
using Sandbox.Mounting.PerfKit;
using Sandbox.Rendering;

namespace MountingTests;

[TestClass, DoNotParallelize]
public class Titanfall2WorldBatchValidation
{
	[TestMethod]
	public void MaterialBatchesRetainEveryFragmentAndSplitAtNativeLimit()
	{
		var materials = Enumerable.Range( 0, 4 ).Select( i => Material.Create( $"t2-world-plan-{i}", "shaders/titanfall2_world_lightmapped.shader" ) ).ToArray();
		var keys = new[] { new WorldMaterialKey( "a", 0 ), new WorldMaterialKey( "b", 0 ), new WorldMaterialKey( "c", 1 ), new WorldMaterialKey( "animated", 0 ) };
		var cells = new Dictionary<WorldRenderCell, Dictionary<WorldMaterialKey, MapMeshGroup>>();
		for ( var i = 0; i <= AggregateRenderer.MaxFragments; ++i )
		{
			var entries = new Dictionary<WorldMaterialKey, MapMeshGroup>();
			foreach ( var key in i == 0 ? keys : keys.Take( 1 ) )
			{
				var group = new MapMeshGroup();
				group.Vertices.AddRange( Enumerable.Range( 0, 3 ).Select( n => new TitanfallMapVertex { Position = new( i, n, 0 ), Color = Color.White } ) );
				group.Indices.AddRange( new[] { 0, 1, 2 } ); group.Bounds = BBox.FromPositionAndSize( new Vector3( i, 0, 0 ), 4 );
				entries.Add( key, group );
			}
			cells.Add( new( i, 0, 0 ), entries );
		}
		try
		{
			var result = Titanfall2WorldMaterialBatch.Build( cells, keys.Zip( materials ).ToDictionary( pair => pair.First, pair => pair.Second ),
				key => key.MaterialName != "animated", "t2-world-plan" );
			Assert.AreEqual( 3, result.Owned.Sources.Length ); Assert.AreEqual( 2, result.Owned.Tables.Length );
			Assert.AreEqual( AggregateRenderer.MaxFragments + 4, result.Aggregates.Sum( aggregate => aggregate.Fragments.Count ) );
			Assert.IsTrue( result.Aggregates.All( aggregate => aggregate.Fragments.Count <= AggregateRenderer.MaxFragments ) );
			Assert.AreEqual( 4, result.Aggregates.Count );
			Assert.AreEqual( 1, result.Keys.Count( key => key.MaterialName == "animated" ) );
			Assert.AreEqual( 1, result.Keys.Count( key => key.LightmapPage == 1 ) );
			foreach ( var material in result.Owned.Tables ) material.Destroy();
		}
		finally { foreach ( var material in materials ) material.Destroy(); }
	}

	[TestMethod]
	[DataRow( false )]
	[DataRow( true )]
	public void NativeAggregateMaterialTablePreservesColourAndDepthNormals( bool layered )
	{
		if ( !Graphics.IsAvailable ) Assert.Inconclusive( "Requires SBOX_TEST_GRAPHICS=1." );
		Sandbox.SceneRenderer.EngineHooks.Install();
		RenderPipeline.WarmUp();
		var managed = ManagedSceneRendering.Enabled;
		var scene = new Scene();
		using var scope = scene.Push();
		using var colour = Texture.Create( 2, 2 ).WithData( new byte[] { 220, 60, 40, 255, 50, 180, 220, 255, 220, 60, 40, 255, 50, 180, 220, 255 } ).Finish();
		using var normal = Texture.Create( 1, 1 ).WithData( new byte[] { 160, 100, 245, 255 } ).Finish();
		using var sky = Texture.Create( 1, 1 ).WithData( new byte[] { 160, 150, 140, 128 } ).Finish();
		using var direction = Texture.Create( 1, 1 ).WithData( new byte[] { 128, 128, 255, 255 } ).Finish();
		var first = Material.Create( "t2-world-table-first", "shaders/titanfall2_world_lightmapped.shader" );
		first.Set( "g_tAlbedo", colour ); first.Set( "g_tAO", Texture.White );
		first.Set( "g_tGloss", Texture.White ); first.Set( "g_tSpecular", Texture.Black ); first.Set( "g_tEmissive", Texture.Black );
		first.Set( "g_tNormal", normal ); first.Set( "g_flHasNormalMap", layered ? 1 : 0 );
		first.Set( "g_flT2GlossScale", 0.6f );
		first.Set( "g_tLightmapSkyA", sky ); first.Set( "g_tLightmapSkyB", direction );
		first.Set( "g_flT2HasSunLightmap", 0 );
		first.Set( "g_tAlbedo2", Texture.White ); first.Set( "g_tNormal2", normal ); first.Set( "g_tLayerBlend", Texture.White );
		first.Set( "g_tGloss2", Texture.Black ); first.Set( "g_tSpecular2", Texture.Black );
		first.Set( "g_flT2Layered", layered ? 1 : 0 ); first.Set( "g_flHasNormalMap2", layered ? 1 : 0 );
		var second = first.CreateCopy( "t2-world-table-second" );
		second.Set( "g_vT2AlbedoTint", new Vector4( 0.3f, 0.8f, 0.5f, 1 ) );
		second.Set( "g_flT2GlossScale", 0.2f );
		var tableMaterial = first.CreateCopy( "t2-world-table-merged" );
		var data = new[] { Titanfall2GpuMaterialData.Capture( first ), Titanfall2GpuMaterialData.Capture( second ) };
		using var buffer = new GpuBuffer<Titanfall2GpuMaterialData>( data.Length );
		buffer.SetData( data );
		tableMaterial.SetFeature( "F_T2_WORLD_MATERIAL_TABLE", 1 );
		var ordinary = new GameObject( true, "ordinary" );
		var merged = new GameObject( true, "merged" );
		var parts = new[] { Part( -12, 0 ), Part( 12, 1 ) };
		for ( var i = 0; i < parts.Length; ++i )
		{
			var part = parts[i];
			var aggregate = MountedAggregateBuilder.CreateAggregate( i == 0 ? first : second,
				MountedAggregateBuilder.PrepareGeometry( new[] { part }, 0, 1 ), $"t2-world-ordinary-{i}" );
			MountedAggregateBuilder.Attach( ordinary, $"part-{i}", aggregate, false );
		}
		var combined = MountedAggregateBuilder.CreateAggregate( tableMaterial,
			MountedAggregateBuilder.PrepareGeometry( parts, 0, parts.Length ), "t2-world-table-combined" );
		MountedAggregateBuilder.Attach( merged, "merged-parts", combined, false );
		using var camera = new SceneCamera( "T2 world table validation" )
		{
			World = scene.SceneWorld, Position = new Vector3( -55, 0, 0 ), Rotation = Rotation.Identity,
			ZNear = 1, ZFar = 500, BackgroundColor = Color.Black, EnablePostProcessing = false
		};
		camera.Attributes.Set( "NeedsDepthNormals", true );
		camera.Attributes.Set( "T2WorldMaterials", buffer );
		using var target = Texture.CreateRenderTarget().WithSize( 96, 96 ).WithFormat( ImageFormat.RGBA16161616F ).Create();
		var readNormals = Material.FromShader( "shaders/tests/titanfall2_gbuffer_read.shader" );
		var commands = new CommandList( "T2 world table normals" );
		commands.Attributes.SetCombo( "D_MSAA_NORMALS", 0 ); commands.Blit( readNormals );
		var normals = false;
		camera.OnRenderStageHook = ( stage, _ ) => { if ( normals && stage == Stage.BeforePostProcess ) Titanfall2GpuSceneCompatibility.Execute( commands ); };
		try
		{
			ManagedSceneRendering.Enabled = false;
			var errors = new List<float>();
			foreach ( var depth in new[] { false, true } )
			{
				normals = depth;
				var before = Capture( false ); var after = Capture( true );
				Assert.IsTrue( before.Count( v => v > 0.03f ) > 100, "Reference geometry must be visible." );
				Assert.IsTrue( after.All( float.IsFinite ) );
				var error = before.Zip( after, ( a, b ) => MathF.Abs( a - b ) ).Average();
				Console.WriteLine( $"World material table layered={layered}, normals={depth}, mean error={error}" );
				var output = Environment.GetEnvironmentVariable( "T2_WORLD_TABLE_EVIDENCE" );
				if ( !string.IsNullOrEmpty( output ) )
				{
					System.IO.Directory.CreateDirectory( output );
					System.IO.File.WriteAllText( System.IO.Path.Combine( output, $"pixels-{layered}-{depth}.json" ),
						System.Text.Json.JsonSerializer.Serialize( new { before, after, data }, new System.Text.Json.JsonSerializerOptions { IncludeFields = true } ) );
				}
				errors.Add( error );
			}
			normals = false;
			var original = Capture( true );
			data[1].g_vT2AlbedoTint = new Vector4( 0.9f, 0.1f, 0.1f, 1 ); buffer.SetData( data );
			var changed = Capture( true );
			var control = changed.Zip( original, ( a, b ) => MathF.Abs( a - b ) ).ToArray();
			Console.WriteLine( $"World material row 1 positive control: mean={control.Average()}, max={control.Max()}" );
			Assert.IsTrue( control.Average() > 0.0001f && control.Max() > 0.05f, "Changing only table row 1 must actually affect pixels." );
			Assert.IsTrue( errors.All( error => error < 0.002f ), $"Native aggregate material table changed pixels: {string.Join( ", ", errors )}" );
		}
		finally { scene.Destroy(); first.Destroy(); second.Destroy(); tableMaterial.Destroy(); ManagedSceneRendering.Enabled = managed; }

		MountedGeometryPart<TitanfallWorldMaterialVertex> Part( float y, uint id )
		{
			var points = new[] { new Vector3( 0, y - 9, -10 ), new Vector3( 0, y, 10 ), new Vector3( 0, y + 9, -10 ) };
			var vertices = points.Select( ( p, i ) => new TitanfallWorldMaterialVertex
			{
				Position = p, Normal = -Vector3.Forward, TexCoord = i == 0 ? Vector2.Zero : i == 1 ? new Vector2( 0.5f, 1 ) : new Vector2( 1, 0 ),
				LightmapTexCoord = new Vector2( 0.5f ), Color = Color.White, MaterialId = id
			} ).ToList();
			return new( vertices, [0, 1, 2, 2, 1, 0], BBox.FromPositionAndSize( new Vector3( 0, y, 0 ), 22 ) );
		}

		float[] Capture( bool useTable )
		{
			ordinary.Enabled = !useTable; merged.Enabled = useTable;
			for ( var i = 0; i < 16; ++i )
			{
				System.Threading.Thread.Sleep( 20 ); EngineGlobal.SourceEnginePanelAppFrame(); scene.GameTick( 0 ); scene.PreRender();
				camera.RenderToTexture( target, null, default ); g_pRenderDevice.Present( IntPtr.Zero ); g_pRenderDevice.ForceFlushGPU( IntPtr.Zero );
				EngineLoop.DrainFrameEndDisposables(); RenderTarget.EndOfFrame(); EngineLoop.RunAsyncTasks();
			}
			var pixels = new ushort[96 * 96 * 4]; target.GetPixels<ushort>( (0, 0, 96, 96), 0, 0, pixels, ImageFormat.RGBA16161616F );
			return pixels.Select( v => (float)BitConverter.UInt16BitsToHalf( v ) ).ToArray();
		}
	}
}
