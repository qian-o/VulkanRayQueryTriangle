# NVIDIA driver issue

## Title

`VK_EXT_descriptor_heap`: acceleration-structure address used by ray query causes device loss on RTX 3050 Laptop GPU (610.74)

## Summary

A minimal Vulkan 1.4 program builds one triangle BLAS and TLAS, writes the
TLAS device address into `ResourceHeapEXT`, and performs one inline ray query
in a `1x1x1` compute dispatch. The first dispatch returns
`VK_ERROR_DEVICE_LOST` on an RTX 3050 Laptop GPU with NVIDIA 610.74.

Slang 2026.13 emits an 8-byte integer load followed by
`OpConvertUToAccelerationStructureKHR`. The host writes the address returned
by `vkGetAccelerationStructureDeviceAddressKHR` and uses the shader's 8-byte
array stride when calculating the heap handle.

The SPIR-V passes `spirv-val --target-env vulkan1.4`. Core and synchronization
validation report no errors while recording the complete workload without
queue submission.

## Environment

| Component | Version |
|---|---|
| GPU | NVIDIA GeForce RTX 3050 Laptop GPU |
| Driver | NVIDIA 610.74 |
| OS | Windows x64 |
| Vulkan device API | 1.4.341 |
| Vulkan SDK | 1.4.350 |
| Slang | 2026.13 |

SPIR-V SHA-256:

```text
93EBAE20651A003F9B41A72D7CF30E1E26067086A996DA56A861CECC9CE5ADFC
```

## Reproduction

```powershell
cmake -S . -B build
cmake --build build --config Release
spirv-val --target-env vulkan1.4 .\ray_query_heap.spv
.\build\Release\vulkan_descriptor_heap_ray_query_repro.exe --validate-only
.\build\Release\vulkan_descriptor_heap_ray_query_repro.exe
```

The last command may reset or hang the GPU and desktop.

Expected: the fence wait returns `VK_SUCCESS`, and the shader reports the
expected triangle hit.

Observed from the single reproducing run:

```text
GPU: NVIDIA GeForce RTX 3050 Laptop GPU, API 1.4.341
Driver: NVIDIA (610.74)
AS heap entry=raw VkDeviceAddress, shaderStride=0x8
sceneHandle=12096, sceneOffset=0x17a00, TLAS=0x8b70000
Dispatching descriptor-heap AS ray query...
REPRODUCED: VK_ERROR_DEVICE_LOST
Device fault: 1 address info(s), description=''
  fault[0] type=VK_DEVICE_FAULT_ADDRESS_TYPE_INSTRUCTION_POINTER_UNKNOWN_EXT (4), reported=0xd00bf8410, precision=0x10
```

The process returned exit code `2`. The fault address is included only as
driver diagnostic output; the application does not identify it as a resource
address. The workload was not repeated.

## Request

Please investigate the device loss during the first ray-query dispatch using
an acceleration-structure device address loaded through `ResourceHeapEXT`.
