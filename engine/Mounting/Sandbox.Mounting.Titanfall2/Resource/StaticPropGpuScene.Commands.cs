using Sandbox.Rendering;

sealed partial class Titanfall2StaticPropGpuScene
{
	static readonly ProfilingSampler ResetProfile = new( "T2 GPU reset" );
	static readonly ProfilingSampler CullProfile = new( "T2 GPU cull and LOD" );
	static readonly ProfilingSampler ArgsProfile = new( "T2 GPU args" );
	static readonly ProfilingSampler InputProfile = new( "T2 GPU input barriers" );
	static readonly ProfilingSampler OutputProfile = new( "T2 GPU output barriers" );
	static readonly string[] ProfilePassNames = ["DepthPrepass", "Opaque", "Shadow", "Other"];

	internal static int ProfilePassIndex( SceneLayerType layer ) => layer switch
	{
		SceneLayerType.DepthPrepass => 0,
		SceneLayerType.Opaque => 1,
		SceneLayerType.Shadow => 2,
		_ => 3
	};

	// All lists, marker names and dispatch delegates are built once per fixed view slot.
	// Slot is a recording slot, not a native camera ID. The parent engine GPU scope still
	// identifies the actual view/cascade; no camera-position heuristic is used for reuse.
	void RecordProfiledCommands( ViewOutput output, Layout layout, int slot )
	{
		// Capture counts, never Layout: the command lists must not retain CPU geometry arrays.
		var bucketCount = layout.Buckets.Length;
		var candidateCount = layout.Candidates.Length;
		var drawCount = layout.Draws.Length;
		var cull = new CommandList();
		using ( cull.ProfileScope( InputProfile ) )
			Titanfall2GpuSceneCompatibility.Record( cull, () => BeginCullOutput( output ) );
		using ( cull.ProfileScope( ResetProfile ) )
		{
			cull.ResourceBarrierTransition( output.Counts, ResourceState.UnorderedAccess );
			cull.ResourceBarrierTransition( _shadowStates, ResourceState.UnorderedAccess );
			cull.UavBarrier( _shadowStates );
			Titanfall2GpuSceneCompatibility.Record( cull, () => DispatchStage( output, 0, bucketCount ) );
			cull.UavBarrier( output.Counts );
		}
		using ( cull.ProfileScope( CullProfile ) )
		{
			Titanfall2GpuSceneCompatibility.Record( cull, () => DispatchStage( output, 1, candidateCount ) );
			cull.UavBarrier( _shadowStates );
			cull.UavBarrier( output.Counts );
		}
		using ( cull.ProfileScope( ArgsProfile ) )
		{
			Titanfall2GpuSceneCompatibility.Record( cull, () => DispatchStage( output, 2, drawCount ) );
		}
		using ( cull.ProfileScope( OutputProfile ) )
		{
			cull.ResourceBarrierTransition( output.Visible, ResourceState.UnorderedAccess, ResourceState.NonPixelShaderResource );
			cull.ResourceBarrierTransition( output.Arguments, ResourceState.UnorderedAccess, ResourceState.IndirectArgument );
		}
		for ( var pass = 0; pass < ProfilePassNames.Length; ++pass )
		{
			var name = $"T2 GPU {ProfilePassNames[pass]} slot {slot}";
			output.CullPasses[pass] = new CommandList( $"{name} visibility" );
			output.CullPasses[pass].InsertList( cull );
			output.DrawPasses[pass] = new CommandList( $"{name} draw" );
			using ( output.DrawPasses[pass].ProfileScope( DrawProfile ) )
				output.DrawPasses[pass].InsertList( output.Commands );
		}
	}

	static readonly ProfilingSampler DrawProfile = new( "Titanfall2StaticSceneMDI" );
	static void BeginCullOutput( ViewOutput output )
	{
		Graphics.ResourceBarrierTransition( output.Visible, output.Used ? ResourceState.NonPixelShaderResource : ResourceState.Common, ResourceState.UnorderedAccess );
		Graphics.ResourceBarrierTransition( output.Arguments, output.Used ? ResourceState.IndirectArgument : ResourceState.Common, ResourceState.UnorderedAccess );
	}

	static void DispatchStage( ViewOutput output, int stage, int threads )
	{
		output.Attributes.Set( "T2Stage", stage );
		output.Compute.DispatchWithAttributes( output.Attributes, threads, 1, 1 );
	}
}
