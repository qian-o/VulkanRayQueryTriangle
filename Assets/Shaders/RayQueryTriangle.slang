// RayQueryTriangle.slang
//
// Minimal compute + RayQuery (inline ray tracing) shader that traces one ray per
// pixel against a TLAS and writes the result into a storage image. Constants are
// delivered like the CornellBox path tracer: a `uniform` struct whose device address
// is supplied via vkCmdPushDataEXT, and whose DescriptorHandle<T> fields index into
// the resource descriptor heap (enabled by -capability spvDescriptorHeapEXT).
//
// The TLAS is read through DescriptorHandle<RaytracingAccelerationStructure>, i.e. the
// real descriptor-heap path this demo exists to validate. With Slang >= v2026.12 the
// spvDescriptorHeapEXT acceleration-structure heap load lowers to a uint64 device-address
// load (runtime array of ulong, ArrayStride 8) followed by OpConvertUToAcceleration-
// StructureKHR (PR #11209 fixed the opaque-AS load; PR #11494 fixed the stride). The
// host writes the raw TLAS device address into the matching heap slot (see
// DescriptorHeap.WriteAccelerationStructure). Earlier releases (<= 2026.8) emitted a
// direct OpLoad of an opaque acceleration structure from the heap, which device-lost on
// NVIDIA; that compiler bug is the root cause this demo isolated.
//
// UPDATE: even with the fixed compiler (verified: correct uint64+OpConvertU SPIR-V) and a
// heap slot verified by CPU read-back to hold the exact TLAS address, NVIDIA still device-
// losts on the first rayQuery.Proceed() when the AS address comes through the descriptor
// heap. The same address routed via push constants works. The TLAS device address is
// therefore pushed in Params.z/w and converted in-shader via OpConvertUToAcceleration-
// StructureKHR; see the RayQueryConstants comment below.

struct RayQueryConstants
{
    // 16-byte header:  x = Width, y = Height, z = TlasAddrLo, w = TlasAddrHi
    private uint4 Params;

    // Kept only to preserve the std140 offset of Output (24). The TLAS is NOT read through
    // this handle: routing the acceleration-structure device address through the
    // spvDescriptorHeapEXT / ResourceHeapEXT untyped-pointer path device-losts on NVIDIA at
    // ray-query traversal even with Slang >= 2026.12 (which emits the correct uint64 load +
    // OpConvertUToAccelerationStructureKHR, verified by disassembly, and with the heap slot
    // holding the exact TLAS address, verified by CPU read-back). The identical address routed
    // through push constants below works, so the fault is specific to the descriptor-heap AS
    // load path, not the address value, the compiler, or the app's AS build.
    private DescriptorHandle<RaytracingAccelerationStructure> Scene;

    DescriptorHandle<RWTexture2D<float4>> Output;

    property uint Width
    {
        get { return Params.x; }
    }

    property uint Height
    {
        get { return Params.y; }
    }

    property uint64_t SceneAddress
    {
        get { return (uint64_t(Params.w) << 32) | uint64_t(Params.z); }
    }
};

uniform RayQueryConstants constants;

[shader("compute")]
[numthreads(16, 16, 1)]
void CSMain(uint3 dispatchThreadID: SV_DispatchThreadID)
{
    uint2 pixel = dispatchThreadID.xy;

    if (pixel.x >= constants.Width || pixel.y >= constants.Height)
    {
        return;
    }

    // Orthographic camera looking down +Z. The triangle sits on the z = 0 plane in
    // world space (see Program.cs), so every pixel shoots a parallel ray from z = -1.
    float2 uv = (float2(pixel) + 0.5) / float2(constants.Width, constants.Height);
    float2 ndc = uv * 2.0 - 1.0;
    ndc.y = -ndc.y;

    RayDesc ray;
    ray.Origin = float3(ndc, -1.0);
    ray.Direction = float3(0.0, 0.0, 1.0);
    ray.TMin = 0.001;
    ray.TMax = 100000.0;

    RayQuery<RAY_FLAG_NONE> query;
    RaytracingAccelerationStructure scene = RaytracingAccelerationStructure(constants.SceneAddress);
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

    (*constants.Output)[pixel] = float4(color, 1.0);
}
