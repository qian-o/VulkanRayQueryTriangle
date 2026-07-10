struct Constants
{
    DescriptorHandle<RaytracingAccelerationStructure> Scene;
};

[[vk::binding(0, 0)]] ConstantBuffer<Constants> constants;
[[vk::binding(1, 0)]] RWStructuredBuffer<uint> result;

[shader("compute")]
[numthreads(1, 1, 1)]
void CSMain()
{
    RaytracingAccelerationStructure scene = constants.Scene;

    RayDesc ray;
    ray.Origin = float3(0.0, 0.0, -1.0);
    ray.Direction = float3(0.0, 0.0, 1.0);
    ray.TMin = 0.001;
    ray.TMax = 10.0;

    RayQuery<RAY_FLAG_NONE> query;
    query.TraceRayInline(scene, RAY_FLAG_NONE, 0xFF, ray);
    while (query.Proceed())
    {
    }

    result[0] = query.CommittedStatus() == COMMITTED_TRIANGLE_HIT ? 1 : 0;
}
