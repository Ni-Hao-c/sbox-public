using Sandbox.Rendering;
using Titanfall2;

sealed partial class Titanfall2StaticPropGpuScene
{
	readonly GpuBuffer<Vertex> _vertices;
	readonly GpuBuffer<uint> _indices;
	readonly GpuBuffer<Candidate> _candidates;
	readonly GpuBuffer<Bucket> _buckets;
	readonly GpuBuffer<Draw> _draws;
	readonly GpuBuffer<GpuInstanceTransform> _transforms;
	readonly GpuBuffer<Titanfall2ProbeData> _probes;
	readonly GpuBuffer<int> _sourceIds;
	readonly GpuBuffer<uint> _sourceCandidateIndices;
	readonly GpuBuffer<uint> _shadowStates;
	readonly GpuBuffer<Titanfall2GpuMaterialData> _materialData;
	readonly Material[] _sourceMaterials;
	Vector3 _policyCamera;
	readonly MaterialGroup[] _groups;
	readonly Titanfall2GpuViewPool<ViewOutput> _outputs;
	readonly ViewOutput[] _conservativeOutputs = new ViewOutput[ConservativeOutputCount];
	readonly BufferBudget _bufferBudget;
	readonly Func<Vector3, Titanfall2BspReader.VisibilityCellMask> _resolvePvs;
	readonly Titanfall2LocalLightBuffer _lights;
	readonly Vector3 _sky;
	bool _deleted;
	readonly object _renderGate = new();
	long _recordings, _mdiCalls;
	public int InstanceCount { get; }
	public int MaterialGroups => _groups.Length;
	public object Statistics => new
	{
		InstanceCount,
		MaterialGroups,
		SimpleSurfaceGroups = _groups.Count( group => group.SimpleSurface ),
		MaterialIdentities = _sourceMaterials.Length,
		DrawRecords = _draws.ElementCount,
		GeometryVertices = _vertices.ElementCount,
		GeometryIndices = _indices.ElementCount,
		Recordings = System.Threading.Interlocked.Read( ref _recordings ),
		MdiCalls = System.Threading.Interlocked.Read( ref _mdiCalls ),
		RuntimeTransformUploadBytes = 0,
		CpuInstanceLodTests = 0,
		ViewSlots = _outputs.Capacity,
		ViewSlotHighWaterMark = _outputs.HighWaterMark,
		ConservativeRecordings = _outputs.OverflowCount,
		StaticBufferBytes = _bufferBudget.StaticBytes,
		ViewOutputBytes = _bufferBudget.OutputBytes,
		TotalBufferBytes = _bufferBudget.TotalBytes
	};

	sealed class ViewOutput : IDisposable
	{
		public readonly GpuBuffer<uint> Counts, Visible;
		public readonly GpuBuffer<GpuBuffer.IndirectDrawIndexedArguments> Arguments;
		public readonly RenderAttributes Attributes = new();
		public readonly ComputeShader Compute = new( "shaders/titanfall2_static_scene_cs.shader" );
		public readonly CommandList Commands = new( "Titanfall2StaticSceneMDI" );
		public readonly CommandList[] CullPasses = new CommandList[4];
		public readonly CommandList[] DrawPasses = new CommandList[4];
		public bool Used;
		public ViewOutput( int buckets, int visible, int draws )
		{
			try
			{
				Counts = new( buckets ); Visible = new( visible );
				Arguments = new( draws, GpuBuffer.UsageFlags.Structured | GpuBuffer.UsageFlags.IndirectDrawArguments );
				Attributes.Set( "T2Counts", Counts ); Attributes.Set( "T2VisibleIds", Visible ); Attributes.Set( "T2Arguments", Arguments );
			}
			catch { Dispose(); throw; }
		}
		public void Dispose() { Counts?.Dispose(); Visible?.Dispose(); Arguments?.Dispose(); }
	}

	public Titanfall2StaticPropGpuScene( SceneWorld world, Layout layout, Color sky, Titanfall2LocalLightBuffer lights,
		Func<Vector3, Titanfall2BspReader.VisibilityCellMask> resolvePvs ) : base( world )
	{
		try
		{
			_bufferBudget = ValidateBufferBudget( layout );
			var shader = Shader.Load( "shaders/titanfall2_static_scene_cs.shader" );
			if ( shader is null || !shader.ResourcePath.EndsWith( "titanfall2_static_scene_cs.shader", StringComparison.OrdinalIgnoreCase ) )
				throw new InvalidDataException( "The static scene compute shader is unavailable." );
			_vertices = Upload( layout.Vertices, GpuBuffer.UsageFlags.Vertex );
			_indices = Upload( layout.Indices, GpuBuffer.UsageFlags.Index );
			_candidates = Upload( layout.Candidates ); _buckets = Upload( layout.Buckets ); _draws = Upload( layout.Draws );
			_transforms = Upload( layout.Transforms ); _probes = Upload( layout.Probes );
			_sourceIds = Upload( Enumerable.Range( 0, layout.Probes.Length ).ToArray() );
			var sourceCandidateIndices = new uint[layout.Probes.Length];
			for ( var i = 0; i < layout.Candidates.Length; ++i ) sourceCandidateIndices[layout.Candidates[i].SourceId] = (uint)i;
			_sourceCandidateIndices = Upload( sourceCandidateIndices );
			_shadowStates = Upload( Enumerable.Repeat( 1u, layout.Probes.Length ).ToArray() );
			_sourceMaterials = layout.SourceMaterials ?? layout.Materials;
			if ( layout.MaterialData is { Length: > 0 } ) _materialData = Upload( layout.MaterialData );
			_groups = layout.Materials.Select( ( material, index ) => new MaterialGroup( material, layout.FirstDraws[index], layout.DrawCounts[index],
				layout.MaterialTables?[index] ?? false, layout.SimpleSurfaces?[index] ?? false ) ).ToArray();
			var slot = 0;
			_outputs = new( () => CreateViewOutput( layout, slot++ ), _bufferBudget.ViewSlots );
			for ( var i = 0; i < _conservativeOutputs.Length; ++i )
			{
				var data = BuildConservativeOutput( layout, shadow: i != 0 );
				var output = CreateViewOutput( layout, slot++ );
				_conservativeOutputs[i] = output;
				output.Counts.SetData( data.Counts );
				output.Visible.SetData( data.Visible );
				output.Arguments.SetData( data.Arguments );
			}
			_resolvePvs = resolvePvs; _lights = lights; _sky = new Vector3( sky.r, sky.g, sky.b );
			InstanceCount = layout.Candidates.Length;
			Bounds = layout.Bounds;
			Batchable = false;
			Flags.IsOpaque = true; Flags.IsTranslucent = false; Flags.IsStatic = true;
			Flags.CastShadows = layout.Candidates.Any( candidate => candidate.CastShadows != 0 );
			Flags.WantsPrePass = true; Flags.NeedsLightProbe = false; Flags.NeedsEnvironmentMap = false; Flags.IncludeInCubemap = false;
		}
		catch { Delete(); throw; }
	}

	ViewOutput CreateViewOutput( Layout layout, int slot )
	{
		var output = new ViewOutput( layout.Buckets.Length, layout.VisibleCapacity, layout.Draws.Length );
		try
		{
			foreach ( var group in _groups )
			{
				output.Commands.Attributes.SetCombo( "D_T2_MATERIAL_TABLE", group.Table ? 1 : 0 );
				output.Commands.Attributes.SetCombo( "D_T2_SIMPLE_SURFACE", group.SimpleSurface ? 1 : 0 );
				output.Commands.DrawIndexedInstancedIndirect( _vertices, _indices, group.Material,
					output.Arguments, group.FirstDraw, null, Graphics.PrimitiveType.Triangles, group.DrawCount, 20 );
			}
			RecordProfiledCommands( output, layout, slot );
			return output;
		}
		catch { output.Dispose(); throw; }
	}

	static GpuBuffer<T> Upload<T>( T[] data, GpuBuffer.UsageFlags usage = GpuBuffer.UsageFlags.Structured ) where T : unmanaged
	{
		var buffer = new GpuBuffer<T>( data.Length, usage );
		try { buffer.SetData( data ); return buffer; }
		catch { buffer.Dispose(); throw; }
	}

	public override void RenderSceneObject()
	{
		lock ( _renderGate )
		{
			if ( _deleted ) return;
			using var scope = Sandbox.Diagnostics.Performance.Scope( "Titanfall2 GPU record" );
			RenderCurrentView();
		}
	}

	// One anchor per update, shared by shadow cascades and secondary cameras. No instance scan.
	public void UpdatePolicyCamera( Vector3 position )
	{
		lock ( _renderGate ) _policyCamera = position;
	}

	void RenderCurrentView()
	{
		var culled = _outputs.TryAcquire( Application.FrameCount, out var output );
		if ( !culled ) output = _conservativeOutputs[Graphics.LayerType == SceneLayerType.Shadow ? 1 : 0];
		using ( Sandbox.Diagnostics.Performance.Scope( "Titanfall2 GPU setup" ) ) PrepareView( output, culled );
		if ( culled )
		{
			using ( Sandbox.Diagnostics.Performance.Scope( "Titanfall2 GPU dispatch" ) ) CullView( output );
		}
		else if ( !output.Used )
		{
			Graphics.ResourceBarrierTransition( output.Visible, ResourceState.NonPixelShaderResource );
			Graphics.ResourceBarrierTransition( output.Arguments, ResourceState.IndirectArgument );
			output.Used = true;
		}
		using ( Sandbox.Diagnostics.Performance.Scope( "Titanfall2 GPU MDI" ) ) DrawView( output, !culled );
	}

	void PrepareView( ViewOutput output, bool culled )
	{
		if ( !output.Used )
		{
			Graphics.ResourceBarrierTransition( _vertices, ResourceState.VertexOrIndexBuffer );
			Graphics.ResourceBarrierTransition( _indices, ResourceState.VertexOrIndexBuffer );
			Graphics.ResourceBarrierTransition( _candidates, ResourceState.NonPixelShaderResource );
			Graphics.ResourceBarrierTransition( _buckets, ResourceState.NonPixelShaderResource );
			Graphics.ResourceBarrierTransition( _draws, ResourceState.NonPixelShaderResource );
			Graphics.ResourceBarrierTransition( _transforms, ResourceState.NonPixelShaderResource );
			Graphics.ResourceBarrierTransition( _probes, ResourceState.PixelShaderResource );
			Graphics.ResourceBarrierTransition( _sourceIds, ResourceState.PixelShaderResource );
			Graphics.ResourceBarrierTransition( _sourceCandidateIndices, ResourceState.NonPixelShaderResource );
			if ( _materialData is not null ) Graphics.ResourceBarrierTransition( _materialData, ResourceState.PixelShaderResource );
		}
		if ( !culled ) return;
		var attributes = output.Attributes;
		attributes.Set( "T2Candidates", _candidates ); attributes.Set( "T2Buckets", _buckets ); attributes.Set( "T2Draws", _draws );
		attributes.Set( "T2CandidateCount", InstanceCount ); attributes.Set( "T2BucketCount", _buckets.ElementCount ); attributes.Set( "T2DrawCount", _draws.ElementCount );
		var shadow = Graphics.LayerType == SceneLayerType.Shadow;
		attributes.Set( "T2ShadowPass", shadow ? 1 : 0 );
		attributes.Set( "T2ShadowStates", _shadowStates );
		attributes.Set( "T2ShadowAnchor", _policyCamera );
		attributes.Set( "T2ShadowDistance", Titanfall2StreamingSettings.PropShadowDistance );
		var camera = shadow ? _policyCamera : Graphics.CameraPosition;
		attributes.Set( "T2PolicyCamera", camera );
		attributes.Set( "T2LodCamera", new Vector3( MathF.Floor( camera.x / 32 ) * 32, MathF.Floor( camera.y / 32 ) * 32, MathF.Floor( camera.z / 32 ) * 32 ) );
		attributes.Set( "T2LodDistances", new Vector3( ModelLoader.GetLodSwitchDistance( 1 ), ModelLoader.GetLodSwitchDistance( 2 ), ModelLoader.GetLodSwitchDistance( 3 ) ) );
		var pvs = shadow ? default : _resolvePvs( camera );
		attributes.Set( "T2Pvs0", unchecked((int)pvs.Low) ); attributes.Set( "T2Pvs1", unchecked((int)(pvs.Low >> 32)) );
		attributes.Set( "T2Pvs2", unchecked((int)pvs.High) ); attributes.Set( "T2Pvs3", unchecked((int)(pvs.High >> 32)) );
		static Vector4 PlaneVector( Plane plane ) => new( plane.Normal, plane.Distance );
		var frustum = Graphics.Frustum;
		attributes.Set( "T2Plane0", PlaneVector( frustum.LeftPlane ) ); attributes.Set( "T2Plane1", PlaneVector( frustum.RightPlane ) );
		attributes.Set( "T2Plane2", PlaneVector( frustum.TopPlane ) ); attributes.Set( "T2Plane3", PlaneVector( frustum.BottomPlane ) );
		attributes.Set( "T2Plane4", PlaneVector( frustum.NearPlane ) ); attributes.Set( "T2Plane5", PlaneVector( frustum.FarPlane ) );
	}

	void CullView( ViewOutput output )
	{
		// Separate output per recording prevents concurrent views from overwriting one another.
		// Reuse in later frames is GPU-only and ordered behind its previous consumers.
		Titanfall2GpuSceneCompatibility.Execute( output.CullPasses[ProfilePassIndex( Graphics.LayerType )] );
		output.Used = true;
	}

	void DrawView( ViewOutput output, bool conservative )
	{
		var drawAttributes = Graphics.Attributes;
		drawAttributes.SetCombo( "D_T2_STATIC_SCENE", 1 );
		drawAttributes.Set( "T2StaticConservative", conservative ? 1 : 0 );
		drawAttributes.Set( "T2StaticCandidates", _candidates );
		drawAttributes.Set( "T2StaticBuckets", _buckets );
		drawAttributes.Set( "T2StaticCandidateIndices", _sourceCandidateIndices );
		if ( conservative )
		{
			var shadow = Graphics.LayerType == SceneLayerType.Shadow;
			var camera = shadow ? _policyCamera : Graphics.CameraPosition;
			var pvs = shadow ? default : _resolvePvs( camera );
			drawAttributes.Set( "T2StaticPolicyCamera", camera );
			drawAttributes.Set( "T2StaticLodCamera", new Vector3( MathF.Floor( camera.x / 32 ) * 32, MathF.Floor( camera.y / 32 ) * 32, MathF.Floor( camera.z / 32 ) * 32 ) );
			drawAttributes.Set( "T2StaticLodDistances", new Vector3( ModelLoader.GetLodSwitchDistance( 1 ), ModelLoader.GetLodSwitchDistance( 2 ), ModelLoader.GetLodSwitchDistance( 3 ) ) );
			drawAttributes.Set( "T2StaticPvs0", unchecked((int)pvs.Low) ); drawAttributes.Set( "T2StaticPvs1", unchecked((int)(pvs.Low >> 32)) );
			drawAttributes.Set( "T2StaticPvs2", unchecked((int)pvs.High) ); drawAttributes.Set( "T2StaticPvs3", unchecked((int)(pvs.High >> 32)) );
		}
		drawAttributes.Set( "T2StaticTransforms", _transforms ); drawAttributes.Set( "T2StaticVisibleIds", output.Visible );
		if ( _materialData is not null ) drawAttributes.Set( "T2GpuMaterials", _materialData );
		drawAttributes.Set( "T2LightProbes", _probes ); drawAttributes.Set( "T2ProbeIndices", _sourceIds );
		drawAttributes.Set( "T2ProbeMode", 2 ); drawAttributes.Set( "T2ProbeSkyColor", _sky );
		drawAttributes.Set( "T2LocalLightCount", 0 ); _lights?.Apply( drawAttributes );
		try
		{
			Titanfall2GpuSceneCompatibility.Execute( output.DrawPasses[ProfilePassIndex( Graphics.LayerType )] );
		}
		finally { drawAttributes.SetCombo( "D_T2_STATIC_SCENE", 0 ); drawAttributes.SetCombo( "D_T2_MATERIAL_TABLE", 0 ); drawAttributes.SetCombo( "D_T2_SIMPLE_SURFACE", 0 ); drawAttributes.Set( "T2ProbeMode", 0 ); drawAttributes.Set( "T2StaticConservative", 0 ); }
		System.Threading.Interlocked.Increment( ref _recordings );
		System.Threading.Interlocked.Add( ref _mdiCalls, _groups.Length );
	}

	public new void Delete()
	{
		lock ( _renderGate )
		{
			if ( _deleted ) return;
			_deleted = true;
			base.Delete();
			_outputs?.Dispose(); _vertices?.Dispose(); _indices?.Dispose(); _candidates?.Dispose(); _buckets?.Dispose();
			foreach ( var output in _conservativeOutputs ) output?.Dispose();
			_draws?.Dispose(); _transforms?.Dispose(); _probes?.Dispose(); _sourceIds?.Dispose(); _shadowStates?.Dispose();
			_materialData?.Dispose();
			_sourceCandidateIndices?.Dispose();
		}
	}
}
