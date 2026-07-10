# NVIDIA driver issue draft

> Suggested destination: NVIDIA Vulkan driver feedback / developer support.
> Submit this repository URL as the reproduction. The repository root is the
> complete, standalone C++ project.

## Title

`VK_EXT_descriptor_heap`: opaque acceleration-structure descriptor causes display blackout and VK_ERROR_DEVICE_LOST in ray query on RTX 3050 Laptop GPU (610.74)

## Summary

A minimal headless Vulkan C++ program builds one triangle BLAS and TLAS, writes
the TLAS as an opaque `VK_DESCRIPTOR_TYPE_ACCELERATION_STRUCTURE_KHR` descriptor
with `vkWriteResourceDescriptorsEXT`, loads it from `ResourceHeapEXT` as
`OpTypeAccelerationStructureKHR`, and performs one inline ray query in a
`1x1x1` compute dispatch.

On an NVIDIA GeForce RTX 3050 Laptop GPU with driver 610.74, submission of this
dispatch blacks out the display and returns `VK_ERROR_DEVICE_LOST`. A previous
run caused a hard GPU/desktop hang that required a system restart. Pipeline
creation and acceleration-structure builds complete before the failure.

The shader source enters the descriptor heap through the exact Slang type under
test:

```hlsl
DescriptorHandle<RaytracingAccelerationStructure> Scene;
RaytracingAccelerationStructure scene = constants.Scene;
```

## Environment

| Component | Version |
|---|---|
| GPU | NVIDIA GeForce RTX 3050 Laptop GPU |
| NVIDIA driver | 610.74 |
| OS | Windows x64 |
| Vulkan runtime/API reported by the affected system | 1.4.341 |
| Vulkan SDK used to build/validate | 1.4.350 |
| Host | C++20, CMake 3.24+, Vulkan loader only |
| Shader module | SPIR-V 1.6, precompiled with Slang 2026.13 |

The application requires and enables:

- `VK_EXT_descriptor_heap`
- `VK_KHR_acceleration_structure`
- `VK_KHR_deferred_host_operations`
- `VK_KHR_ray_query`
- `VK_KHR_shader_untyped_pointers`
- `VK_EXT_device_fault` when available

The host requests Vulkan 1.4 and enables the core
`VkPhysicalDeviceVulkan14Features::maintenance5` feature. It does not require
the promoted `VK_KHR_maintenance5` extension name.

## Reproduction

The repository root contains the complete source, CMake project, precompiled
SPIR-V, and SPIR-V disassembly.

1. Install a C++20 compiler, CMake 3.24 or newer, and a Vulkan 1.4 SDK.
2. Configure and build from the repository root:

    ```sh
    cmake -S . -B build
    cmake --build build --config Release
    ```

3. Save other work, then run the generated
  `vulkan_descriptor_heap_ray_query_repro` executable. With Visual Studio it is
  normally located at
  `build/Release/vulkan_descriptor_heap_ray_query_repro.exe`.

Warning: step 3 can hang the GPU and desktop hard enough to require a system
restart on the affected system. Building the executable and running
`spirv-val` do not submit the reproducing workload.

The program prints the selected GPU and driver, all relevant descriptor sizes,
the descriptor offset and handle, and the TLAS address before dispatch.

## Expected behavior

The single inline ray query completes and the fence wait returns `VK_SUCCESS`.
There is no output image; successful completion is sufficient.

## Observed behavior

The display blacks out during the dispatch. After the display recovers, the
exact opaque-descriptor reproduction prints:

```text
Dispatching descriptor-heap AS ray query...
REPRODUCED: VK_ERROR_DEVICE_LOST
Device fault: 1 address info(s), description=''
  fault[0] type=4, reported=0xd00bf8540, precision=0x10
```

Vulkan SDK 1.4.350 defines address type `4` as
`VK_DEVICE_FAULT_ADDRESS_TYPE_INSTRUCTION_POINTER_UNKNOWN_EXT`. The reported
address is included as driver diagnostic data; the reproduction does not assume
that it is an application resource address.

## Descriptor encoding and heap index

The host obtains the TLAS address from
`vkGetAccelerationStructureDeviceAddressKHR` and asks the driver to encode the
opaque descriptor:

```cpp
VkDeviceAddressRangeEXT addressRange{app.tlas.address, app.tlas.size};

VkResourceDescriptorInfoEXT descriptor{
    VK_STRUCTURE_TYPE_RESOURCE_DESCRIPTOR_INFO_EXT};
descriptor.type = VK_DESCRIPTOR_TYPE_ACCELERATION_STRUCTURE_KHR;
descriptor.data.pAddressRange = &addressRange;

VkHostAddressRangeEXT target{sceneSlot, asDescriptorSize};
vkWriteResourceDescriptorsEXT(device, 1, &descriptor, &target);
```

The nonzero range covers the TLAS storage allocation. The Vulkan specification
states that an acceleration-structure descriptor does not use the range size;
a valid nonzero range may be supplied to validate the application's range
assumptions.

The descriptor is placed after the resource heap's reserved range. The host
mirrors the shader's `OpConstantSizeOfEXT` array stride as follows:

```cpp
asStride   = alignUp(bufferDescriptorSize, bufferDescriptorAlignment);
asOffset   = alignUp(minResourceHeapReservedRange, asStride);
sceneHandle = asOffset / asStride;
```

On the affected device:

```text
vkGetPhysicalDeviceDescriptorSizeEXT(ACCELERATION_STRUCTURE_KHR) = 8 bytes
bufferDescriptorSize / shader-visible AS array stride             = 16 bytes
```

The 8-byte opaque descriptor payload and the 16-byte shader array stride are
therefore intentionally different. The constant buffer contains the computed
nonzero `sceneHandle`; this reproduction does not access heap index zero by
accident.

## Relevant SPIR-V

The checked-in module passes:

```sh
spirv-val --target-env vulkan1.4 ray_query_heap.spv
```

It uses an opaque acceleration-structure runtime array and direct opaque load:

```text
OpCapability RayQueryKHR
OpCapability UntypedPointersKHR
OpCapability DescriptorHeapEXT
OpExtension "SPV_KHR_ray_query"
OpExtension "SPV_KHR_untyped_pointers"
OpExtension "SPV_EXT_descriptor_heap"

%asType   = OpTypeAccelerationStructureKHR
%asStride = OpConstantSizeOfEXT %uint %asType
%asArray  = OpTypeRuntimeArray %asType
OpDecorateId %asArray ArrayStrideIdEXT %asStride
OpDecorate %resourceHeap BuiltIn ResourceHeapEXT

%pointer = OpUntypedAccessChainKHR %ptrUniformConstant %asArray %resourceHeap %sceneHandle
%scene   = OpLoad %asType %pointer
OpRayQueryInitializeKHR %query %scene ...
```

There is no raw `uint64_t` acceleration-structure load and no
`OpConvertUToAccelerationStructureKHR` in this module.

## Command path

The compute pipeline is created with
`VK_PIPELINE_CREATE_2_DESCRIPTOR_HEAP_BIT_EXT`. The command buffer:

1. Binds the resource and sampler heaps with `vkCmdBindResourceHeapEXT` and
   `vkCmdBindSamplerHeapEXT`.
2. Supplies the constants-buffer device address through `vkCmdPushDataEXT` and
   a `VK_DESCRIPTOR_MAPPING_SOURCE_PUSH_ADDRESS_EXT` mapping.
3. Calls `vkCmdDispatch(1, 1, 1)`.
4. Submits once and waits on one fence.

The constants buffer is not the acceleration-structure descriptor. It contains
only the integer heap handle used to index `ResourceHeapEXT`.

## What has been verified

- The standalone C++ Release build succeeds with the Vulkan SDK.
- The checked-in SPIR-V passes `spirv-val --target-env vulkan1.4`.
- The shader directly loads an opaque `OpTypeAccelerationStructureKHR` value.
- The descriptor bytes are produced by `vkWriteResourceDescriptorsEXT`, not by
  writing a raw TLAS address into the heap.
- The descriptor target size is the exact size reported for
  `VK_DESCRIPTOR_TYPE_ACCELERATION_STRUCTURE_KHR`.
- The heap handle is derived from the aligned buffer descriptor stride used by
  `OpConstantSizeOfEXT`, not from the exact 8-byte descriptor payload size.
- BLAS/TLAS builds and compute-pipeline creation complete before dispatch.
- The exact opaque-descriptor dispatch returns `VK_ERROR_DEVICE_LOST` and
  reports one `VK_DEVICE_FAULT_ADDRESS_TYPE_INSTRUCTION_POINTER_UNKNOWN_EXT`
  entry through `VK_EXT_device_fault`.

## Request

Please confirm whether this opaque acceleration-structure descriptor access is
expected to work with `VK_EXT_descriptor_heap` on this driver, and investigate
the display reset and device loss during the first ray-query dispatch. Any
recommended additional NVIDIA diagnostic capture that does not require
repeatedly triggering the failure would also be useful.
