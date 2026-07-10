# NVIDIA `VK_EXT_descriptor_heap` ray-query device-loss repro

Minimal Vulkan 1.4 repro using a ray-query shader compiled by Slang 2026.13.
On NVIDIA 610.74, submitting the shader causes `VK_ERROR_DEVICE_LOST`.

## Shader files

The project contains exactly three shader files:

- `ray_query.slang`: readable source and the file to edit or debug.
- `ray_query.spv`: exact Slang-generated binary used for the reported result.
- `ray_query.spvasm`: disassembly for auditing the generated instructions.

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
.\build\vulkan_descriptor_heap_ray_query_repro.exe
```

CMake compiles `ray_query.slang`, validates the generated module for Vulkan
1.4, writes its disassembly under `build\shader`, and copies the binary beside
the executable. With a multi-configuration generator, the executable may be
under `build\Release`.

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

## Regenerate checked-in artifacts

The checked-in shader artifacts were generated with Slang 2026.13 and have
SHA-256 `6E4AE43B9756081426AAA378293EF056C2E81CE47A6D8620AE6DEEDEB0A3BB78`.

```powershell
slangc .\ray_query.slang `
   -target spirv `
   -profile glsl_460 `
   -entry main `
   -stage compute `
   -capability spirv_1_4 `
   -capability spvDescriptorHeapEXT `
   -capability spvRayQueryKHR `
   -o .\ray_query.spv

spirv-val --target-env vulkan1.4 .\ray_query.spv
spirv-dis .\ray_query.spv -o .\ray_query.spvasm
```

Known failing configuration:

```text
GPU:                  NVIDIA GeForce RTX 4070 Ti SUPER
Vendor / device:      0x10de / 0x2705
Driver:               NVIDIA 610.74
Device API:           Vulkan 1.4.341
Slang:                2026.13
VK_EXT_descriptor_heap revision: 1
```