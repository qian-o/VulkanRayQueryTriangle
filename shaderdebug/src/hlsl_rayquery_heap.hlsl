// HLSL probe: RayQuery (inline RT) + VK_EXT_descriptor_heap, mirroring the Slang
// RayQueryTriangle.slang so the emitted SPIR-V can be diffed against the Slang output.
//
// Constants live in a set0/binding0 constant buffer whose address the runtime supplies
// through vkCmdPushDataEXT (via DescriptorSetAndBindingMappingEXT). The Scene/Output
// handles index the resource descriptor heap through ResourceDescriptorHeap[].
struct Constants
{
    uint Width;
    uint Height;
    uint Scene;
    uint Output;
};

ConstantBuffer<Constants> constants : register(b0, space0);

[numthreads(16, 16, 1)]
void CSMain(uint3 dispatchThreadID : SV_DispatchThreadID)
{
    uint2 pixel = dispatchThreadID.xy;

    if (pixel.x >= constants.Width || pixel.y >= constants.Height)
    {
        return;
    }

    float2 uv = (float2(pixel) + 0.5) / float2(constants.Width, constants.Height);
    float2 ndc = uv * 2.0 - 1.0;
    ndc.y = -ndc.y;

    RayDesc ray;
    ray.Origin = float3(ndc, -1.0);
    ray.Direction = float3(0.0, 0.0, 1.0);
    ray.TMin = 0.001;
    ray.TMax = 100000.0;

    RaytracingAccelerationStructure scene = ResourceDescriptorHeap[constants.Scene];

    RayQuery<RAY_FLAG_NONE> query;
    query.TraceRayInline(scene, RAY_FLAG_NONE, 0xFF, ray);

    while (query.Proceed())
    {
    }

    float3 color;
    if (query.CommittedStatus() != COMMITTED_NOTHING)
    {
        float2 bary = query.CommittedTriangleBarycentrics();
        color = float3(1.0 - bary.x - bary.y, bary.x, bary.y);
    }
    else
    {
        color = float3(0.05, 0.05, 0.08);
    }

    RWTexture2D<float4> output = ResourceDescriptorHeap[constants.Output];
    output[pixel] = float4(color, 1.0);
}
