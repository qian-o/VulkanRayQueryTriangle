# Vulkan 1.4 descriptor-heap ray-query repro

This repository is a headless C++20 reproduction for an NVIDIA failure when a
ray query loads an opaque `OpTypeAccelerationStructureKHR` descriptor from
`VK_EXT_descriptor_heap`. It contains no .NET project, window system, swapchain,
or runtime shader compiler. The checked-in SPIR-V is loaded directly by Vulkan.

The host targets Vulkan 1.4 and uses the ratified
`VK_EXT_descriptor_heap` revision 1 interface. The shader entry under test is a
Slang descriptor handle, not a pushed acceleration-structure address:

```hlsl
DescriptorHandle<RaytracingAccelerationStructure> Scene;
RaytracingAccelerationStructure scene = constants.Scene;
```

That entry lowers to a direct opaque acceleration-structure load from the
`ResourceHeapEXT` built-in in the checked-in SPIR-V.

## Warning

On the affected RTX 3050 Laptop GPU and NVIDIA 610.74 driver, submitting the
single `1x1x1` dispatch can hang the GPU and desktop hard enough to require a
system restart. Save other work before running. Building and validating the
SPIR-V are safe and do not submit the reproducing workload.

## Requirements

- Windows x64
- Visual Studio 2022 C++ tools
- CMake 3.24 or newer
- Vulkan SDK 1.4 with `VK_EXT_descriptor_heap` revision 1 headers
- An NVIDIA device exposing the extensions and features checked by `main.cpp`

The build script requires `VULKAN_SDK`. It uses `cmake` from `PATH`, or the
CMake bundled with Visual Studio when necessary. This repository is validated
with Vulkan SDK 1.4.350.

## Build

From this directory:

```powershell
.\build.ps1
```

The executable and copied shader are written to:

```text
build\Release\vulkan_descriptor_heap_ray_query_repro.exe
build\Release\ray_query_heap.spv
```

The build script also runs `spirv-val --target-env vulkan1.4` on the exact
checked-in module.

## Run

After reading the warning above:

```powershell
.\build.ps1 -Run
```

The program prints the GPU, driver, descriptor sizes, descriptor offset, and
shader-visible handle before dispatch. It returns:

- `0`: the dispatch completed; the issue did not reproduce
- `1`: setup or a Vulkan call failed before a recoverable device loss
- `2`: `VK_ERROR_DEVICE_LOST` was returned and the issue reproduced

A hard GPU or desktop hang may prevent the process from returning an exit code.
If `VK_EXT_device_fault` is supported and device loss is recoverable, the
program also prints the available fault information.

## Expected and observed behavior

Expected: the one-ray query completes and `vkWaitForFences` returns success.

Observed on the affected system: submission reaches the dispatch, then the GPU
or desktop hangs. Related variants have returned `VK_ERROR_DEVICE_LOST`, but a
hard hang is the observed result for this opaque-descriptor reproduction.

## Descriptor layout

The resource heap contains one acceleration-structure descriptor written with
`vkWriteResourceDescriptorsEXT` and
`VkResourceDescriptorInfoEXT(VK_DESCRIPTOR_TYPE_ACCELERATION_STRUCTURE_KHR)`,
whose data points to a `VkDeviceAddressRangeEXT`.
The descriptor address comes from `vkGetAccelerationStructureDeviceAddressKHR`.
The supplied nonzero range covers the TLAS storage allocation; acceleration-
structure descriptors do not consume the range, but the specification permits
it as host-side validation of the application's address-range assumptions.

The shader indexes a runtime array of `OpTypeAccelerationStructureKHR`. Its
`ArrayStrideIdEXT` is `OpConstantSizeOfEXT` for that type. Per the
`VK_EXT_descriptor_heap` rules, the host mirrors that stride as:

```text
shaderStride = alignUp(bufferDescriptorSize, bufferDescriptorAlignment)
asOffset     = alignUp(minResourceHeapReservedRange, shaderStride)
sceneHandle  = asOffset / shaderStride
```

On the affected system, the exact acceleration-structure descriptor size is 8
bytes while the shader-visible buffer descriptor stride is 16 bytes. The exact
descriptor payload size and the shader array stride are intentionally distinct.

## Shader audit and validation

- `ray_query_heap.slang` is the minimal shader source.
- `ray_query_heap.spv` is the precompiled module loaded by the executable.
- `ray_query_heap.spvasm` is the checked-in disassembly for inspection.

The module uses an opaque acceleration-structure load, not a raw `uint64_t`
load or `OpConvertUToAccelerationStructureKHR`. Its relevant instructions are
`OpTypeAccelerationStructureKHR`, `OpConstantSizeOfEXT`, `OpLoad`, and
`OpRayQueryInitializeKHR`.

Validate it without running the workload:

```powershell
& "$env:VULKAN_SDK\Bin\spirv-val.exe" --target-env vulkan1.4 .\ray_query_heap.spv
```