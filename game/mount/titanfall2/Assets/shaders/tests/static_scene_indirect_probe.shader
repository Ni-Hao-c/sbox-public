HEADER
{
	Description = "Titanfall 2 static scene indirect addressing diagnostic";
}
MODES
{
	Default();
	Forward();
}
COMMON
{
	#include "system.fxc"
	#include "common.fxc"
	struct VS_INPUT
	{
		float3 position : POSITION0 < Semantic( Position ); >;
		uint instance : SV_InstanceID;
	};
	struct ProbePixel
	{
		float4 position : SV_Position;
		nointerpolation float4 color : COLOR0;
	};
}
VS
{
	StructuredBuffer<float4> g_positions < Attribute( "ProbePositions" ); >;
	StructuredBuffer<uint> g_visibleIds < Attribute( "ProbeVisibleIds" ); >;
	StructuredBuffer<uint> g_vertexBuckets < Attribute( "ProbeVertexBuckets" ); >;
	StructuredBuffer<float4> g_transforms < Attribute( "ProbeTransforms" ); >;
	StructuredBuffer<float4> g_probes < Attribute( "ProbeLighting" ); >;

	ProbePixel MainVs( VS_INPUT input )
	{
		// The attribute follows native vertex-buffer addressing, including BaseVertex.
		uint vertex = (uint)input.position.z;
		uint bucketBase = g_vertexBuckets[vertex];
		uint source = g_visibleIds[bucketBase + input.instance];
		ProbePixel result;
		result.position = g_positions[vertex] + float4( g_transforms[source].xy, 0, 0 );
		result.color = g_probes[source];
		return result;
	}
}
PS
{
	RenderState( CullMode, NONE );
	RenderState( DepthEnable, false );
	RenderState( DepthWriteEnable, false );
	float4 MainPs( ProbePixel input ) : SV_Target0
	{
		return input.color;
	}
}
