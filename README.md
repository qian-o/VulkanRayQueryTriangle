# Vulkan descriptor-heap ray-query repro

Minimal headless C++20 reproduction for a device loss when an inline ray query
loads its acceleration structure through `VK_EXT_descriptor_heap`.

The Slang 2026.13 shader uses:

```hlsl
DescriptorHandle<RaytracingAccelerationStructure> Scene;
RaytracingAccelerationStructure scene = constants.Scene;
```

Slang lowers this to an 8-byte load from `ResourceHeapEXT`, followed by
`OpConvertUToAccelerationStructureKHR`. The host writes the TLAS
`VkDeviceAddress` returned by `vkGetAccelerationStructureDeviceAddressKHR` to
that heap element and computes the handle using the emitted 8-byte stride.

## Requirements

- CMake 3.24 or newer and a C++20 compiler
- Vulkan SDK 1.4 with `VK_EXT_descriptor_heap` revision 1 headers
- A device supporting the extensions and features checked by `main.cpp`
- `VK_LAYER_KHRONOS_validation` for `--validate-only`

Tested with Vulkan SDK 1.4.350, an RTX 3050 Laptop GPU, and NVIDIA 610.74.

## Build and validate

```powershell
cmake -S . -B build
cmake --build build --config Release
spirv-val --target-env vulkan1.4 .\ray_query_heap.spv
.\build\Release\vulkan_descriptor_heap_ray_query_repro.exe --validate-only
```

`--validate-only` enables core and synchronization validation and records the
AS builds and dispatch without queue submission. It completes with no
validation errors and reports `queue submissions=0` on the tested system.

The checked-in shader is reproducible from `ray_query_heap.slang` with Slang
2026.13. Its SHA-256 is:

```text
93EBAE20651A003F9B41A72D7CF30E1E26067086A996DA56A861CECC9CE5ADFC
```

## Reproduce

Warning: this command may reset or hang the GPU and desktop. Save other work
before running it.

```powershell
.\build\Release\vulkan_descriptor_heap_ray_query_repro.exe
```

Expected: the dispatch completes and the shader reports the triangle hit.

Observed on the tested system:

```text
Dispatching descriptor-heap AS ray query...
REPRODUCED: VK_ERROR_DEVICE_LOST
Device fault: 1 address info(s), description=''
  fault[0] type=VK_DEVICE_FAULT_ADDRESS_TYPE_INSTRUCTION_POINTER_UNKNOWN_EXT (4), reported=0xd00bf8410, precision=0x10
```

The process returned exit code `2`. The workload was run once and was not
repeated.
