# NVIDIA `VK_EXT_descriptor_heap` acceleration-structure device-loss repro

Minimal Vulkan 1.4 repro for a typed acceleration-structure load from
`ResourceHeapEXT`. On NVIDIA 610.74, submitting the ray query causes
`VK_ERROR_DEVICE_LOST`.

The shader is hand-authored SPIR-V and does not depend on Slang or another
shader compiler.

## Requirements

- CMake 3.24 or newer
- Vulkan SDK 1.4 headers and loader
- A Vulkan 1.4 device supporting `VK_EXT_descriptor_heap`, acceleration
  structures, ray queries, buffer device addresses, and untyped pointers

## Build and run

```powershell
cmake -S . -B build
cmake --build build --config Release
.\build\vulkan_descriptor_heap_ray_query_repro.exe
```

With a multi-configuration generator, the executable may be under
`build\Release`.

The run submits GPU work and may reset an affected device. On the known failing
configuration it prints:

```text
GPU: NVIDIA GeForce RTX 4070 Ti SUPER, API 1.4.341
Driver: NVIDIA (610.74)
Dispatching descriptor-heap AS ray query...
REPRODUCED: VK_ERROR_DEVICE_LOST
```

A conforming implementation should complete the query, report the triangle hit,
and exit with code 0. The affected driver exits with code 2 after device loss.

## Repro path

1. Build a one-triangle BLAS and identity-instance TLAS.
2. Obtain the TLAS address with `vkGetAccelerationStructureDeviceAddressKHR`.
3. Write a `VK_DESCRIPTOR_TYPE_ACCELERATION_STRUCTURE_KHR` descriptor with
   `vkWriteResourceDescriptorsEXT`.
4. Bind the descriptor memory with `vkCmdBindResourceHeapEXT`.
5. Load the descriptor as `OpTypeAccelerationStructureKHR` from
   `ResourceHeapEXT` and use it in an inline ray query.

The relevant shader instructions are:

```text
%as = OpTypeAccelerationStructureKHR
%asSize = OpConstantSizeOfEXT %uint %as
OpDecorateId %asArray ArrayStrideIdEXT %asSize
%scenePointer = OpUntypedAccessChainKHR ... %resourceHeap %handle
%scene = OpLoad %as %scenePointer
OpRayQueryInitializeKHR %query %scene ...
```

`ray_query.spvasm` is the complete shader source. The checked-in binary can be
recreated and validated with:

```powershell
spirv-as --target-env vulkan1.4 .\ray_query.spvasm -o .\ray_query.spv
spirv-val --target-env vulkan1.4 .\ray_query.spv
```

Known failing configuration:

```text
GPU:                  NVIDIA GeForce RTX 4070 Ti SUPER
Vendor / device:      0x10de / 0x2705
Driver:               NVIDIA 610.74
Device API:           Vulkan 1.4.341
VK_EXT_descriptor_heap revision: 1
```