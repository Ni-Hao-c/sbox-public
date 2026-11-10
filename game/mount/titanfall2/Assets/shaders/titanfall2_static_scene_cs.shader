HEADER
{
	Description = "Titanfall 2 unified static visibility and LOD";
}
MODES
{
	Default();
}
COMMON
{
	#include "system.fxc"
	#include "common.fxc"
}
CS
{
	struct T2Candidate
	{
		float4 sphere;
		float4 originAndDistance;
		uint sourceId;
		uint firstBucket;
		uint castShadows;
		uint reserved;
		uint4 pvs;
	};
	struct T2Bucket
	{
		uint firstVisible;
		uint capacity;
	};
	struct T2Draw
	{
		uint indexCount;
		uint firstIndex;
		uint bucket;
		int baseVertex;
	};
	struct T2Args
	{
		uint indexCount;
		uint instanceCount;
		uint firstIndex;
		int baseVertex;
		uint firstInstance;
	};
	StructuredBuffer<T2Candidate> Candidates < Attribute( "T2Candidates" ); >;
	StructuredBuffer<T2Bucket> Buckets < Attribute( "T2Buckets" ); >;
	StructuredBuffer<T2Draw> Draws < Attribute( "T2Draws" ); >;
	RWStructuredBuffer<uint> Counts < Attribute( "T2Counts" ); >;
	RWStructuredBuffer<uint> VisibleIds < Attribute( "T2VisibleIds" ); >;
	RWStructuredBuffer<T2Args> Arguments < Attribute( "T2Arguments" ); >;
	RWStructuredBuffer<uint> ShadowStates < Attribute( "T2ShadowStates" ); >;
	float3 ShadowAnchor < Attribute( "T2ShadowAnchor" ); >;
	float ShadowDistance < Attribute( "T2ShadowDistance" ); >;
	int Stage < Attribute( "T2Stage" ); >;
	int CandidateCount < Attribute( "T2CandidateCount" ); >;
	int BucketCount < Attribute( "T2BucketCount" ); >;
	int DrawCount < Attribute( "T2DrawCount" ); >;
	int ShadowPass < Attribute( "T2ShadowPass" ); >;
	float3 PolicyCamera < Attribute( "T2PolicyCamera" ); >;
	float3 LodCamera < Attribute( "T2LodCamera" ); >;
	float3 LodDistances < Attribute( "T2LodDistances" ); >;
	int Pvs0 < Attribute( "T2Pvs0" ); >;
	int Pvs1 < Attribute( "T2Pvs1" ); >;
	int Pvs2 < Attribute( "T2Pvs2" ); >;
	int Pvs3 < Attribute( "T2Pvs3" ); >;
	float4 Plane0 < Attribute( "T2Plane0" ); >;
	float4 Plane1 < Attribute( "T2Plane1" ); >;
	float4 Plane2 < Attribute( "T2Plane2" ); >;
	float4 Plane3 < Attribute( "T2Plane3" ); >;
	float4 Plane4 < Attribute( "T2Plane4" ); >;
	float4 Plane5 < Attribute( "T2Plane5" ); >;
	bool Outside( float4 plane, float4 sphere ) { return dot( plane.xyz, sphere.xyz ) - plane.w < -sphere.w; }
	[numthreads( 64, 1, 1 )]
	void MainCs( uint3 id : SV_DispatchThreadID )
	{
		uint n = id.x;
		if ( Stage == 0 ) { if ( n < (uint)BucketCount ) Counts[n] = 0; return; }
		if ( Stage == 2 )
		{
			if ( n >= (uint)DrawCount ) return;
			T2Draw draw = Draws[n];
			T2Args args;
			args.indexCount = draw.indexCount; args.instanceCount = Counts[draw.bucket];
			args.firstIndex = draw.firstIndex; args.baseVertex = draw.baseVertex; args.firstInstance = 0;
			Arguments[n] = args;
			return;
		}
		if ( n >= (uint)CandidateCount ) return;
		T2Candidate c = Candidates[n];
		if ( ShadowPass != 0 && c.castShadows == 0 ) return;
		if ( ShadowPass != 0 && ShadowDistance > 0 )
		{
			// Static per-source hysteresis stays on GPU. Every view uses the same main-camera
			// anchor; secondary cameras and cascade origins cannot change the distance policy.
			uint bucket = uint( length( c.originAndDistance.xyz - ShadowAnchor ) / 256.0 );
			uint disableBucket = uint( ShadowDistance / 256.0 );
			uint restoreBucket = uint( max( 0.0, floor( ShadowDistance * 0.5 / 256.0 ) - 1.0 ) );
			uint casting = ShadowStates[c.sourceId];
			casting = casting != 0 ? uint( bucket < disableBucket ) : uint( bucket <= restoreBucket );
			ShadowStates[c.sourceId] = casting;
			if ( casting == 0 ) return;
		}
		uint4 pvs = uint4( Pvs0, Pvs1, Pvs2, Pvs3 );
		if ( ShadowPass == 0 && any( c.pvs ) && any( pvs ) && !any( c.pvs & pvs ) ) return;
		float3 delta = c.originAndDistance.xyz - PolicyCamera;
		if ( dot( delta, delta ) > c.originAndDistance.w * c.originAndDistance.w ) return;
		if ( Outside( Plane0, c.sphere ) || Outside( Plane1, c.sphere ) || Outside( Plane2, c.sphere )
			|| Outside( Plane3, c.sphere ) || Outside( Plane4, c.sphere ) || Outside( Plane5, c.sphere ) ) return;
		float distance = length( c.originAndDistance.xyz - LodCamera );
		uint lod = distance >= LodDistances.z ? 3 : distance >= LodDistances.y ? 2 : distance >= LodDistances.x ? 1 : 0;
		uint bucketIndex = c.firstBucket + lod;
		T2Bucket bucket = Buckets[bucketIndex];
		uint slot;
		InterlockedAdd( Counts[bucketIndex], 1, slot );
		// Capacity equals this model's full instance count at every LOD. Each candidate
		// writes exactly once, so exceeding it is a compiler defect, never normal truncation.
		VisibleIds[bucket.firstVisible + slot] = c.sourceId;
	}
}
