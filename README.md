# Vulkan 1.4 descriptor-heap ray-query repro

This repository is a headless C++20 reproduction for an NVIDIA failure when a
ray query loads an opaque `OpTypeAccelerationStructureKHR` descriptor from
`VK_EXT_descriptor_heap`.

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
single `1x1x1` dispatch can black out the display and return
`VK_ERROR_DEVICE_LOST`. Some runs may hang the GPU and desktop hard enough to
require a system restart. Save other work before running. Building and
validating the SPIR-V are safe and do not submit the reproducing workload.

## Requirements

- A C++20 compiler
- CMake 3.24 or newer
- A Vulkan 1.4 SDK with `VK_EXT_descriptor_heap` revision 1 headers and loader
- An NVIDIA device exposing the extensions and features checked by `main.cpp`

CMake locates Vulkan through its standard `FindVulkan` module. Configure the
Vulkan SDK for your platform so `find_package(Vulkan 1.4)` can locate its
headers and loader. This repository is validated with Vulkan SDK 1.4.350.

## Build

From this directory:

```sh
cmake -S . -B build
cmake --build build --config Release
```

The executable and copied shader are written under the selected generator's
build output directory. For a multi-configuration generator such as Visual
Studio, the Release executable is typically
`build/Release/vulkan_descriptor_heap_ray_query_repro.exe`. The adjacent
`ray_query_heap.spv` is shader data loaded by the executable; it is not a
program and should not be run directly.

For a single-configuration generator, configure with
`-DCMAKE_BUILD_TYPE=Release`; the files are typically placed directly under
`build/`.

## Run

With the Visual Studio generator on Windows, after reading the warning above:

```powershell
.\build\Release\vulkan_descriptor_heap_ray_query_repro.exe
```

With a single-configuration generator, the executable is typically run as:

```sh
./build/vulkan_descriptor_heap_ray_query_repro
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

Observed on the affected system: the display blacks out during the dispatch,
then the process returns `VK_ERROR_DEVICE_LOST`. `VK_EXT_device_fault` reports
one address entry of type
`VK_DEVICE_FAULT_ADDRESS_TYPE_INSTRUCTION_POINTER_UNKNOWN_EXT`. A previous run
also caused a hard GPU/desktop hang that required a system restart.

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

```sh
spirv-val --target-env vulkan1.4 ray_query_heap.spv
```
