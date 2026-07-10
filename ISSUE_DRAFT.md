# Slang issue draft

> Submit this first to https://github.com/shader-slang/slang/issues.
> Before submission, commit and push the current reproduction and replace the
> commit placeholder below. No additional GPU dispatch is needed for this
> compiler/specification question.

## Title

`DescriptorHandle<RaytracingAccelerationStructure>` loads `ResourceHeapEXT` as `uint64_t` instead of `OpTypeAccelerationStructureKHR`

## Summary

Slang 2026.13 lowers a
`DescriptorHandle<RaytracingAccelerationStructure>` access through
`ResourceHeapEXT` to an 8-byte integer load followed by
`OpConvertUToAccelerationStructureKHR`:

```text
OpDecorate %_runtimearr_ulong ArrayStride 8
OpDecorate %slang_resourceHeap BuiltIn ResourceHeapEXT
%38 = OpUntypedAccessChainKHR ... %_runtimearr_ulong ...
%40 = OpLoad %ulong %38
%42 = OpConvertUToAccelerationStructureKHR %41 %40
```

The host writes a `VK_DESCRIPTOR_TYPE_ACCELERATION_STRUCTURE_KHR` descriptor
with `vkWriteResourceDescriptorsEXT` after querying its size with
`vkGetPhysicalDeviceDescriptorSizeEXT`.

`SPV_EXT_descriptor_heap` defines a descriptor as an opaque object with a
descriptor type such as `OpTypeAccelerationStructureKHR`; it does not list an
integer type as an acceleration-structure descriptor type. The Vulkan
Descriptor Heap Interface says resources retrieved from a heap must have
descriptors matching the declared resource type. Its Heap Resource Type
Correspondence table maps
`VK_DESCRIPTOR_TYPE_ACCELERATION_STRUCTURE_KHR` in `ResourceHeapEXT` to
`OpTypeAccelerationStructureKHR`, not to a 64-bit integer type:

- https://docs.vulkan.org/spec/latest/chapters/interfaces.html#interfaces-resources-descriptorheap
- https://docs.vulkan.org/spec/latest/chapters/interfaces.html#interfaces-resources-heap-type-correspondence
- https://github.khronos.org/SPIRV-Registry/extensions/EXT/SPV_EXT_descriptor_heap.html

The Vulkan text also says that reading a typed resource from a heap consumes
the implementation-reported descriptor size. The emitted integer array instead
has a fixed 8-byte stride and load width. On the tested implementation,
`vkGetPhysicalDeviceDescriptorSizeEXT` returns 8 for this descriptor type, but
the payload is still opaque and that size is not a portable representation
guarantee.

`VUID-RuntimeSpirv-Result-11350` separately defines the heap alignment
requirement for an `OpLoad` with result type
`OpTypeAccelerationStructureKHR`:

- https://docs.vulkan.org/spec/latest/appendices/spirvenv.html#VUID-RuntimeSpirv-Result-11350

I could not find an exception that permits loading an acceleration-structure
descriptor from `ResourceHeapEXT` as `%ulong` and converting it afterward.
`spirv-val --target-env vulkan1.4` accepts the module, but that does not check
the descriptor type supplied by the Vulkan application at runtime.

Vulkan does allow a 64-bit acceleration-structure address stored as ordinary
data to be consumed by `OpConvertUToAccelerationStructureKHR`; the relevant
[`VK_EXT_mutable_descriptor_type` proposal](https://github.com/KhronosGroup/Vulkan-Docs/blob/main/proposals/VK_EXT_mutable_descriptor_type.adoc)
explicitly says that no descriptor is required for that path. This
reproduction instead writes an API-generated acceleration-structure descriptor.
I therefore do not see a specification guarantee that the opaque descriptor
payload can be reinterpreted as the raw input address.

## Reproduction

Repository:

https://github.com/qian-o/VulkanRayQueryTriangle

Reproduction commit:

https://github.com/qian-o/VulkanRayQueryTriangle/commit/c5f99e0f6b76c5785ad61f4a75651f50d888d99d

Minimal shader source:

```hlsl
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
```

Environment:

```text
Slang:       2026.13
SPIRV-Tools: v2026.2 v2026.2.rc2-1-g2ec8457a
Vulkan SDK:  1.4.350
OS:          Windows 11
GPU:         NVIDIA GeForce RTX 3050 Laptop GPU
Driver:      NVIDIA 610.74 (32.0.16.1074)
```

Generate and inspect the module:

```powershell
$slangc = "C:\path\to\Slang\2026.13\bin\slangc.exe"
& $slangc .\ray_query_heap.slang `
  -target spirv -profile sm_6_6 -entry CSMain -stage compute `
  -capability spvDescriptorHeapEXT -capability spirv_1_6 `
  -fvk-use-entrypoint-name -o .\ray_query_heap.spv
spirv-val --target-env vulkan1.4 .\ray_query_heap.spv
spirv-dis .\ray_query_heap.spv -o .\ray_query_heap.spvasm
```

SPIR-V SHA-256:

```text
93EBAE20651A003F9B41A72D7CF30E1E26067086A996DA56A861CECC9CE5ADFC
```

The C++ host can be built and its complete workload recorded without queue
submission:

```powershell
cmake -S . -B build
cmake --build build --config Release
.\build\Release\vulkan_descriptor_heap_ray_query_repro.exe --validate-only
```

This path uses `vkWriteResourceDescriptorsEXT`, creates the compute pipeline
with `VK_PIPELINE_CREATE_2_DESCRIPTOR_HEAP_BIT_EXT`, binds the resource heap,
and passes core and synchronization validation with `queue submissions=0` on
the tested system.

## Expected

For a `VK_DESCRIPTOR_TYPE_ACCELERATION_STRUCTURE_KHR` descriptor retrieved
directly from `ResourceHeapEXT`, I expected the heap load to produce an
`OpTypeAccelerationStructureKHR` value in accordance with Vulkan's Heap
Resource Type Correspondence table. Its array stride can be expressed using
`OpConstantSizeOfEXT(OpTypeAccelerationStructureKHR)` and
`ArrayStrideIdEXT`, allowing the implementation-defined descriptor size and
alignment to be respected.

Alternatively, if the emitted `%ulong` load plus
`OpConvertUToAccelerationStructureKHR` is intentionally valid in the Vulkan
environment for `DescriptorHandle<RaytracingAccelerationStructure>`, please
identify the specification rule that permits an API-generated descriptor to be
read as raw address data. If the intended contract is instead that applications
store raw 64-bit addresses in heap data without calling the descriptor writer,
please document that host-side requirement explicitly.

## Related Slang work

- https://github.com/shader-slang/slang/issues/10671
- https://github.com/shader-slang/slang/pull/11209
- https://github.com/shader-slang/slang/issues/11231
- https://github.com/shader-slang/slang/pull/11494

Those changes appear to establish the current conversion and 8-byte stride as
intentional behavior. This follow-up is specifically about compatibility with
Vulkan's descriptor-heap resource type correspondence and opaque descriptor
representation.

## Request

Please confirm whether this lowering is valid for Vulkan. If it is not, please
emit an acceleration-structure-typed heap load and add a regression test for
the Vulkan descriptor type correspondence. If it is intended to be valid,
please clarify whether the host must provide an API-generated descriptor or
ordinary raw address data, and identify the specification basis so the
question can be cross-filed with Khronos rather than reported prematurely as a
driver defect.