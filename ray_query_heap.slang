struct Constants
{
    DescriptorHandle<RaytracingAccelerationStructure> Scene;
};

[[vk::binding(0, 0)]] ConstantBuffer<Constants> constants;

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
}