using System.Runtime.InteropServices;
using Sandbox.Mounting.PerfKit;
using Titanfall2;

/// <summary>GPU layout shared with T2LightProbeData in titanfall2_probe_data.hlsl.</summary>
[StructLayout( LayoutKind.Sequential )]
public struct Titanfall2ProbeData
{
	public Vector4 Red, Green, Blue, SkyDirectionSunVisibility, Metadata;

	public static Titanfall2ProbeData FromSource( Titanfall2LightProbe source )
	{
		if ( source is null ) return default;
		static Vector4 Convert( System.Numerics.Vector4 value ) => new( value.X, value.Y, value.Z, value.W );
		return new Titanfall2ProbeData
		{
			Red = Convert( source.Red ),
			Green = Convert( source.Green ),
			Blue = Convert( source.Blue ),
			SkyDirectionSunVisibility = Convert( source.SkyDirectionSunVisibility ),
			// Use the reserved 12 bytes without changing the 80-byte GPU stride.
			Metadata = new Vector4( 1, BitConverter.UInt32BitsToSingle( source.PackedLightWeights ),
				BitConverter.UInt32BitsToSingle( (uint)source.LightIndex0 | (uint)source.LightIndex1 << 16 ),
				BitConverter.UInt32BitsToSingle( (uint)source.LightIndex2 | (uint)source.LightIndex3 << 16 ) )
		};
	}

	public void Apply( SceneObject target, Color skyColor )
	{
		target.Attributes.Set( "T2ProbeMode", Metadata.x > 0 ? 1 : 0 );
		target.Attributes.Set( "T2ProbeRed", Red );
		target.Attributes.Set( "T2ProbeGreen", Green );
		target.Attributes.Set( "T2ProbeBlue", Blue );
		target.Attributes.Set( "T2ProbeSky", SkyDirectionSunVisibility );
		target.Attributes.Set( "T2ProbeWeights", BitConverter.SingleToInt32Bits( Metadata.y ) );
		target.Attributes.Set( "T2ProbeLightIds01", BitConverter.SingleToInt32Bits( Metadata.z ) );
		target.Attributes.Set( "T2ProbeLightIds23", BitConverter.SingleToInt32Bits( Metadata.w ) );
		target.Attributes.Set( "T2ProbeSkyColor", new Vector3( skyColor.r, skyColor.g, skyColor.b ) );
	}
}

/// <summary>
/// A batch without probe data. Graphics.Attributes is the render context's shared block, and a
/// probe batch drawn earlier in the same layer leaves T2ProbeMode=2 plus its own probe/index
/// tables in it; without an explicit reset this batch would shade with another batch's probes
/// (and index past its own instance count). Mode 0 is the shader's per-object/tint path.
/// </summary>
public sealed class Titanfall2PlainInstanceBatch : MountedInstanceBatch
{
	static readonly StringToken ProbeModeAttribute = "T2ProbeMode";
	static readonly StringToken LocalLightCountAttribute = "T2LocalLightCount";

	public Titanfall2PlainInstanceBatch( SceneWorld world, Model model, Transform[] transforms, MountedInstanceBatchOptions options = null )
		: base( world, model, transforms, options ) { }

	protected override void DrawInstances( Span<Transform> transforms, ReadOnlySpan<int> sourceIndices, int lodLevel )
	{
		Graphics.Attributes.Set( ProbeModeAttribute, 0 );
		Graphics.Attributes.Set( LocalLightCountAttribute, 0 );
		base.DrawInstances( transforms, sourceIndices, lodLevel );
	}
}

/// <summary>
/// A permanent coefficient table plus source-index tables for LOD/cull subsets.
/// LOD/cull reordering changes indices, never averages or reassigns lighting.
/// </summary>
public sealed class Titanfall2ProbeInstanceBatch : MountedInstanceBatch
{
	/// <summary>
	/// One uploaded subset. A subset only changes when the camera moves an instance across a
	/// LOD band or the cull radius, so the same few index lists are drawn frame after frame.
	/// Uploading them on every draw cost a GPU copy plus a barrier each time: GPU time rose in
	/// step with the upload count (9.7 ms at ~60 uploads/frame, 13.5 ms at ~350 on
	/// mp_angel_city), so each distinct list is uploaded once into its own buffer and reused.
	/// </summary>
	sealed class IndexTable( int capacity )
	{
		public readonly GpuBuffer<int> Buffer = new( capacity );
		public readonly int[] Indices = new int[capacity];
		public int Count;
		public long LastUse;
	}

	const int IndexTableCacheSize = 8;
	readonly GpuBuffer<Titanfall2ProbeData> _probes;
	readonly GpuBuffer<int> _identityIndices;
	// Created lazily: most batches only ever draw the identity order.
	readonly IndexTable[] _indexTables = new IndexTable[IndexTableCacheSize];
	readonly object _indexTableLock = new();
	readonly int _instanceCapacity;
	long _indexTableClock;
	bool _deleted;
	readonly Vector3 _skyColor;
	readonly Titanfall2LocalLightBuffer _localLights;
	static long _indexUploads, _identityReuses;
	static long _depthDraws;
	static readonly StringToken ProbeModeAttribute = "T2ProbeMode";
	static readonly StringToken ProbesAttribute = "T2LightProbes";
	static readonly StringToken ProbeIndicesAttribute = "T2ProbeIndices";
	static readonly StringToken ProbeSkyColorAttribute = "T2ProbeSkyColor";
	static readonly StringToken LocalLightCountAttribute = "T2LocalLightCount";
	/// <summary>Opt in for depth shaders which consume coverage/normals, but no lighting probes.</summary>
	public bool SkipDepthProbeBindings { get; set; }
	public static long SkippedDepthDraws => System.Threading.Interlocked.Read( ref _depthDraws );
	public static (long Uploads, long Reuses) GetIndexStatistics() =>
		(System.Threading.Interlocked.Read( ref _indexUploads ), System.Threading.Interlocked.Read( ref _identityReuses ));

	public Titanfall2ProbeInstanceBatch( SceneWorld world, Model model, Transform[] transforms,
		Titanfall2ProbeData[] probes, Color skyColor, MountedInstanceBatchOptions options = null, Titanfall2LocalLightBuffer localLights = null )
		: base( world, model, Validate( transforms, probes ), options )
	{
		_skyColor = new Vector3( skyColor.r, skyColor.g, skyColor.b );
		_instanceCapacity = probes.Length;
		_localLights = localLights;
		try
		{
			_probes = new GpuBuffer<Titanfall2ProbeData>( probes.Length );
			_probes.SetData( probes.AsSpan() );
			// Most cell/model batches draw every instance in source order in both the
			// depth and forward passes. Upload this immutable table once, rather than
			// synchronously rewriting a GPU buffer for every object in every pass.
			_identityIndices = new GpuBuffer<int>( probes.Length );
			var identity = new int[probes.Length];
			for ( var i = 0; i < identity.Length; ++i ) identity[i] = i;
			_identityIndices.SetData( identity.AsSpan() );
		}
		catch
		{
			Delete();
			throw;
		}
	}

	static Transform[] Validate( Transform[] transforms, Titanfall2ProbeData[] probes )
	{
		if ( transforms is null || probes is null || transforms.Length != probes.Length )
			throw new ArgumentException( "Each instance must have its own probe record.", nameof( probes ) );
		return transforms;
	}

	protected override void DrawInstances( Span<Transform> transforms, ReadOnlySpan<int> sourceIndices, int lodLevel )
	{
		// Production depth shaders return normals/coverage before reading lighting.
		// Keep shadow bindings until the shadow path has its own GPU acceptance test.
		var layer = Graphics.LayerType;
		if ( SkipDepthProbeBindings && layer == SceneLayerType.DepthPrepass )
		{
			System.Threading.Interlocked.Increment( ref _depthDraws );
			base.DrawInstances( transforms, sourceIndices, lodLevel );
			return;
		}
		var identity = true;
		for ( var i = 0; identity && i < sourceIndices.Length; ++i ) identity = sourceIndices[i] == i;
		GpuBuffer<int> indices;
		if ( identity )
		{
			indices = _identityIndices;
			System.Threading.Interlocked.Increment( ref _identityReuses );
		}
		else
		{
			indices = GetIndexTable( sourceIndices );
			if ( indices is null ) return;
		}
		Graphics.Attributes.Set( ProbeModeAttribute, 2 );
		Graphics.Attributes.Set( ProbesAttribute, _probes );
		Graphics.Attributes.Set( ProbeIndicesAttribute, indices );
		Graphics.Attributes.Set( ProbeSkyColorAttribute, _skyColor );
		Graphics.Attributes.Set( LocalLightCountAttribute, 0 );
		_localLights?.Apply( Graphics.Attributes );
		base.DrawInstances( transforms, sourceIndices, lodLevel );
	}

	/// <summary>
	/// Returns a buffer holding exactly <paramref name="sourceIndices"/>, uploading only when no
	/// cached table matches. Views may record on several threads, hence the lock; rewriting an
	/// evicted table is safe because SetData is recorded in order with the draws that use it.
	/// </summary>
	GpuBuffer<int> GetIndexTable( ReadOnlySpan<int> sourceIndices )
	{
		lock ( _indexTableLock )
		{
			if ( _deleted || sourceIndices.Length > _instanceCapacity ) return null;
			var clock = ++_indexTableClock;
			var slot = 0;
			var oldest = long.MaxValue;
			for ( var i = 0; i < _indexTables.Length; ++i )
			{
				var table = _indexTables[i];
				if ( table is null )
				{
					if ( oldest != long.MinValue ) { slot = i; oldest = long.MinValue; }
					continue;
				}
				if ( table.Count == sourceIndices.Length && table.Indices.AsSpan( 0, table.Count ).SequenceEqual( sourceIndices ) )
				{
					table.LastUse = clock;
					System.Threading.Interlocked.Increment( ref _identityReuses );
					return table.Buffer;
				}
				if ( table.LastUse < oldest ) { slot = i; oldest = table.LastUse; }
			}

			var target = _indexTables[slot] ??= new IndexTable( _instanceCapacity );
			sourceIndices.CopyTo( target.Indices );
			target.Count = sourceIndices.Length;
			target.LastUse = clock;
			target.Buffer.SetData( sourceIndices );
			System.Threading.Interlocked.Increment( ref _indexUploads );
			return target.Buffer;
		}
	}

	public override void Delete()
	{
		base.Delete();
		lock ( _indexTableLock )
		{
			_deleted = true;
			for ( var i = 0; i < _indexTables.Length; ++i )
			{
				_indexTables[i]?.Buffer.Dispose();
				_indexTables[i] = null;
			}
		}
		_identityIndices?.Dispose();
		_probes?.Dispose();
	}
}
