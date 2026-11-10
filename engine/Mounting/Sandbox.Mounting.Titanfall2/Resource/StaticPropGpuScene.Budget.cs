using System.Runtime.InteropServices;
using Sandbox.Rendering;
using Titanfall2;

sealed partial class Titanfall2StaticPropGpuScene
{
	const int ConservativeOutputCount = 2;
	internal readonly record struct BufferBudget( long StaticBytes, long OutputBytes, int ViewSlots )
	{
		public long TotalBytes => checked(StaticBytes + OutputBytes * (ViewSlots + ConservativeOutputCount));
	}

	internal static BufferBudget GetBufferBudget( Layout layout, int viewSlots )
	{
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero( viewSlots );
		checked
		{
			var permanent = (long)layout.Vertices.Length * Marshal.SizeOf<Vertex>() + (long)layout.Indices.Length * sizeof( uint )
				+ (long)layout.Candidates.Length * Marshal.SizeOf<Candidate>() + (long)layout.Buckets.Length * Marshal.SizeOf<Bucket>()
				+ (long)layout.Draws.Length * Marshal.SizeOf<Draw>() + (long)layout.Transforms.Length * Marshal.SizeOf<GpuInstanceTransform>()
				+ (long)layout.Probes.Length * Marshal.SizeOf<Titanfall2ProbeData>() + (long)layout.Probes.Length * (sizeof( int ) + sizeof( uint ) * 2)
				+ (long)(layout.MaterialData?.Length ?? 0) * Marshal.SizeOf<Titanfall2GpuMaterialData>();
			var output = (long)layout.VisibleCapacity * sizeof( uint ) + (long)layout.Buckets.Length * sizeof( uint )
				+ (long)layout.Draws.Length * Marshal.SizeOf<GpuBuffer.IndirectDrawIndexedArguments>();
			return new( permanent, output, viewSlots );
		}
	}

	internal sealed record ConservativeOutput( uint[] Counts, uint[] Visible, GpuBuffer.IndirectDrawIndexedArguments[] Arguments );

	/// <summary>
	/// Read-only output for excess recordings. The vertex shader rejects the wrong LOD,
	/// distance and PVS for this view. Separate camera/shadow variants preserve
	/// authored shadow eligibility without writing or reusing an in-flight UAV.
	/// </summary>
	internal static ConservativeOutput BuildConservativeOutput( Layout layout, bool shadow )
	{
		var counts = new uint[layout.Buckets.Length];
		var visible = new uint[layout.VisibleCapacity];
		foreach ( var candidate in layout.Candidates )
		{
			if ( shadow && candidate.CastShadows == 0 ) continue;
			for ( var lod = 0u; lod < 4; ++lod )
			{
				var index = candidate.FirstBucket + lod;
				var bucket = layout.Buckets[index];
				var offset = counts[index]++;
				if ( offset >= bucket.Capacity ) throw new InvalidDataException( "Conservative GPU output exceeds its bucket capacity." );
				visible[checked((int)(bucket.FirstVisible + offset))] = candidate.SourceId;
			}
		}
		var arguments = new GpuBuffer.IndirectDrawIndexedArguments[layout.Draws.Length];
		for ( var i = 0; i < arguments.Length; ++i )
		{
			var draw = layout.Draws[i];
			arguments[i] = new()
			{
				IndexCount = draw.IndexCount,
				InstanceCount = counts[draw.Bucket],
				FirstIndex = draw.FirstIndex,
				BaseVertex = draw.BaseVertex,
				FirstInstance = 0
			};
		}
		return new( counts, visible, arguments );
	}
}
