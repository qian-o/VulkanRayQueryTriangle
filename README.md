# NVIDIA `VK_EXT_descriptor_heap` ray-query device-loss repro

Minimal Vulkan 1.4 repro using a ray-query shader compiled by Slang 2026.13.
On NVIDIA 610.74, submitting the shader causes `VK_ERROR_DEVICE_LOST`.

## Shader

Slang 2026.13 lowers the acceleration-structure `DescriptorHandle` to an
8-byte resource-heap load followed by a conversion:

```text
OpDecorate %_runtimearr_ulong ArrayStride 8
%pointer = OpUntypedAccessChainKHR ... %slang_resourceHeap %handle
%address = OpLoad %ulong %pointer
%scene = OpConvertUToAccelerationStructureKHR %asType %address
```

The host therefore writes the TLAS device address directly into the resource
heap and passes its 8-byte-stride index through the `DescriptorHandle`.

## Requirements

- CMake 3.24 or newer
- Vulkan SDK 1.4 headers, loader, and SPIR-V Tools
- Slang 2026.13 on `PATH`
- A Vulkan 1.4 device supporting `VK_EXT_descriptor_heap`, acceleration
   structures, ray queries, buffer device addresses, and untyped pointers

## Build and run

```powershell
cmake -S . -B build
cmake --build build --config Release
.\build\Release\vulkan_descriptor_heap_ray_query_repro.exe
```

CMake compiles `ray_query.slang`, validates the generated module for Vulkan
1.4, writes its disassembly under `build\shader`, and copies the binary beside
the executable. The command above uses the default Visual Studio
multi-configuration generator. With a single-configuration generator such as
Ninja, the executable is instead under `build`.

The run submits GPU work and may reset an affected device. On the known failing
configuration it prints:

```text
GPU: NVIDIA GeForce RTX 4070 Ti SUPER, API 1.4.341
Driver: NVIDIA (610.74)
AS source=raw TLAS address + Slang OpConvertUToAccelerationStructureKHR, shaderStride=0x8
Dispatching Slang descriptor-heap AS ray query...
REPRODUCED: VK_ERROR_DEVICE_LOST
```

A conforming implementation must not lose the device. A successful run should
report the expected triangle hit and exit with code 0; the affected driver
exits with code 2 after device loss.

Known failing configuration:

```text
GPU:                  NVIDIA GeForce RTX 4070 Ti SUPER
Vendor / device:      0x10de / 0x2705
Driver:               NVIDIA 610.74
Device API:           Vulkan 1.4.341
Slang:                2026.13
VK_EXT_descriptor_heap revision: 1
```