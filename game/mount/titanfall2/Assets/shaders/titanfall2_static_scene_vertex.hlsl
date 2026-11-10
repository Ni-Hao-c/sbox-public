#ifndef T2_STATIC_SCENE_VERTEX_HLSL
#define T2_STATIC_SCENE_VERTEX_HLSL

#if D_T2_STATIC_SCENE
struct T2StaticTransform
{
	float4 row0;
	float4 row1;
	float4 row2;
	float alpha;
	uint tint;
	uint vertexCacheOffset;
	uint blendWeightCount;
};
StructuredBuffer<T2StaticTransform> g_T2StaticTransforms < Attribute( "T2StaticTransforms" ); >;
StructuredBuffer<uint> g_T2StaticVisibleIds < Attribute( "T2StaticVisibleIds" ); >;

struct T2StaticCandidate
{
	float4 sphere;
	float4 originAndDistance;
	uint sourceId;
	uint firstBucket;
	uint castShadows;
	uint reserved;
	uint4 pvs;
};
struct T2StaticBucket
{
	uint firstVisible;
	uint capacity;
};
StructuredBuffer<T2StaticCandidate> g_T2StaticCandidates < Attribute( "T2StaticCandidates" ); >;
StructuredBuffer<T2StaticBucket> g_T2StaticBuckets < Attribute( "T2StaticBuckets" ); >;
StructuredBuffer<uint> g_T2StaticCandidateIndices < Attribute( "T2StaticCandidateIndices" ); >;
int g_nT2StaticConservative < Attribute( "T2StaticConservative" ); >;
float3 g_vT2StaticPolicyCamera < Attribute( "T2StaticPolicyCamera" ); >;
float3 g_vT2StaticLodCamera < Attribute( "T2StaticLodCamera" ); >;
float3 g_vT2StaticLodDistances < Attribute( "T2StaticLodDistances" ); >;
int g_nT2StaticPvs0 < Attribute( "T2StaticPvs0" ); >;
int g_nT2StaticPvs1 < Attribute( "T2StaticPvs1" ); >;
int g_nT2StaticPvs2 < Attribute( "T2StaticPvs2" ); >;
int g_nT2StaticPvs3 < Attribute( "T2StaticPvs3" ); >;

bool T2StaticConservativeVisible( uint sourceId, uint visibleBase )
{
	T2StaticCandidate c = g_T2StaticCandidates[g_T2StaticCandidateIndices[sourceId]];
	uint4 pvs = uint4( g_nT2StaticPvs0, g_nT2StaticPvs1, g_nT2StaticPvs2, g_nT2StaticPvs3 );
	if ( any( c.pvs ) && any( pvs ) && !any( c.pvs & pvs ) ) return false;
	float3 delta = c.originAndDistance.xyz - g_vT2StaticPolicyCamera;
	if ( dot( delta, delta ) > c.originAndDistance.w * c.originAndDistance.w ) return false;
	float distance = length( c.originAndDistance.xyz - g_vT2StaticLodCamera );
	uint lod = distance >= g_vT2StaticLodDistances.z ? 3 : distance >= g_vT2StaticLodDistances.y ? 2 : distance >= g_vT2StaticLodDistances.x ? 1 : 0;
	return g_T2StaticBuckets[c.firstBucket + lod].firstVisible == visibleBase;
}

PixelInput T2ProcessStaticVertex( VertexInput v, out uint sourceId )
{
	sourceId = g_T2StaticVisibleIds[v.nT2VisibleBase + v.nT2InstanceIndex];
	T2StaticTransform data = g_T2StaticTransforms[sourceId];
	float3x4 transform = float3x4( data.row0, data.row1, data.row2 );
	PixelInput i = (PixelInput)0;
	float3 normal;
	float4 tangent;
	VS_DecodeObjectSpaceNormalAndTangent( v, normal, tangent );
	i.vPositionWs = mul( transform, float4( v.vPositionOs, 1 ) );
	float sign;
	i.vNormalWs = TransformNormalInverseTranspose( normal, (float3x3)transform, sign );
#if PS_INPUT_HAS_TANGENT_BASIS
	float3 worldTangent = mul( transform, float4( tangent.xyz, 0 ) );
	i.vTangentUWs = normalize( worldTangent - i.vNormalWs * dot( worldTangent, i.vNormalWs ) );
	i.vTangentVWs = cross( i.vNormalWs, i.vTangentUWs ) * tangent.w * sign;
#endif
	i.vPositionPs = Position3WsToPs( i.vPositionWs );
	i.vTextureCoords = float4( v.vTexCoord, v.vTexCoord2 );
	i.vVertexColor = float4( 1, 1, 1, 1 );
	// Overflow recordings share read-only lists for every LOD. Reject whole instances
	// in the vertex stage, so an intentionally empty distant LOD stays empty.
	[branch]
	if ( g_nT2StaticConservative != 0 )
	{
		if ( !T2StaticConservativeVisible( sourceId, v.nT2VisibleBase ) )
			i.vPositionPs = float4( 2, 2, 2, 1 );
	}
	return i;
}
#endif
#endif
