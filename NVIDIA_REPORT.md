# NVIDIA Vulkan report: `VK_EXT_descriptor_heap` ray query causes `VK_ERROR_DEVICE_LOST`

## Summary

A minimal native Vulkan 1.4 compute application consistently returns
`VK_ERROR_DEVICE_LOST` when a ray query uses an acceleration structure obtained
from `ResourceHeapEXT` data.

The shader loads one raw 64-bit TLAS device address from the resource heap with
`OpUntypedAccessChainKHR`, converts it with
`OpConvertUToAccelerationStructureKHR`, and passes the result to
`OpRayQueryInitializeKHR`. The same dispatch is expected to report a triangle
hit, but it loses the device on an NVIDIA GeForce RTX 4070 Ti SUPER with driver
610.74.

This is a native Vulkan C++ repro. It does not use Direct3D, DXVK,
VKD3D-Proton, or a game engine.

Repository:
https://github.com/qian-o/VulkanRayQueryTriangle

Exact corrected source revision:
https://github.com/qian-o/VulkanRayQueryTriangle/commit/86ff48c50ac6848ec80261744ff6aeb2dbe55d11

## Important corrected baseline

During the final audit, an application-side omission was found: the generated
SPIR-V declares `OpCapability Int64`, but an earlier revision did not enable the
core Vulkan `shaderInt64` feature. That omission has been corrected. The
application now both checks support for and enables `shaderInt64` before device
creation.

All results in this report were collected from commit
`86ff48c50ac6848ec80261744ff6aeb2dbe55d11`, after that correction. Results from
older commits should not be used for driver attribution.

The corrected application still reproduces `VK_ERROR_DEVICE_LOST` with the
Khronos Validation Layer and synchronization validation enabled, with all
third-party implicit layers disabled, and without any Vulkan validation error
or VUID being reported.

## Environment

| Item | Value |
| --- | --- |
| OS | Microsoft Windows 11 Pro 64-bit, version 10.0.26200, build 26200 |
| CPU | Intel Core i7-14700KF |
| GPU | NVIDIA GeForce RTX 4070 Ti SUPER |
| Vendor / device ID | `0x10de` / `0x2705` |
| NVIDIA driver | 610.74 |
| Vulkan driver ID | `VK_DRIVER_ID_NVIDIA_PROPRIETARY` (`4`) |
| Raw driver version | `0x98928000` |
| Device Vulkan API | 1.4.341 |
| Vulkan SDK | 1.4.350.0 |
| `VK_EXT_descriptor_heap` revision | 1 |
| Slang | 2026.13 |
| SPIR-V Tools | v2026.2 (`v2026.2.rc2-1-g2ec8457a`) |
| CMake | 4.4.0 |
| Generator | Visual Studio 18 2026 |
| MSVC | 19.51.36248.0 |
| MSBuild | 18.7.8.30822 |
| Windows SDK used by the build | 10.0.26100.0 |

The logical device enables the following relevant extensions:

- `VK_EXT_descriptor_heap`
- `VK_KHR_acceleration_structure`
- `VK_KHR_deferred_host_operations`
- `VK_KHR_ray_query`
- `VK_KHR_shader_untyped_pointers`

It checks and enables the following relevant features:

- `shaderInt64`
- `bufferDeviceAddress`
- `synchronization2`
- `maintenance5`
- `accelerationStructure`
- `rayQuery`
- `shaderUntypedPointers`
- `descriptorHeap`

## Expected behavior

The compute dispatch should complete successfully. The ray starts in front of
one triangle and points through it, so the shader should write `1` to the result
buffer. The application should print:

```text
No device loss; the ray query reported the expected triangle hit.
```

## Actual behavior

Submitting the dispatch and waiting for its completion returns
`VK_ERROR_DEVICE_LOST`. The repro currently reports the combined submission and
wait result rather than attributing the error to one of those two calls. The
final isolated run produced:

```text
GPU: NVIDIA GeForce RTX 4070 Ti SUPER, API 1.4.341
vendorID=0x10de, deviceID=0x2705, driverVersion=0x98928000, driverID=4
Driver: NVIDIA (610.74)
resourceHeapAddress=0x7f90000, resourceHeapSize=0x17a20
reservedRange=[0x0, 0x17a00)
AS source=raw TLAS address + Slang OpConvertUToAccelerationStructureKHR, shaderStride=0x8
sceneHandle=12096, sceneOffset=0x17a00, TLAS=0x7f50000
Dispatching Slang descriptor-heap AS ray query...
REPRODUCED: VK_ERROR_DEVICE_LOST
Process exit code: 0
```

The executable deliberately returns process code 0 for every outcome. The
authoritative result is the console line beginning with `REPRODUCED`,
`No device loss`, or `ERROR`.

## Reproduction steps

The workload can reset an affected GPU, so please close GPU-sensitive work
before running it.

```powershell
git clone https://github.com/qian-o/VulkanRayQueryTriangle.git
cd VulkanRayQueryTriangle
git checkout 86ff48c50ac6848ec80261744ff6aeb2dbe55d11

cmake -S . -B build
cmake --build build --config Release

$env:VK_INSTANCE_LAYERS = "VK_LAYER_KHRONOS_validation"
$env:VK_LOADER_LAYERS_DISABLE = "~implicit~"
$env:VK_LAYER_VALIDATE_SYNC = "1"

.\build\Release\vulkan_descriptor_heap_ray_query_repro.exe
```

`VK_LOADER_LAYERS_DISABLE=~implicit~` removes OBS, RenderDoc, overlays, Nsight,
and every other implicit layer from the call chain. In the audited run, the
only layer was `VK_LAYER_KHRONOS_validation`, followed directly by the NVIDIA
ICD.

For a single-configuration CMake generator such as Ninja, run the executable
from `build` instead of `build\Release`.

## Shader build and validation

CMake requires exactly Slang 2026.13 and compiles the shader using:

```text
-target spirv
-profile glsl_460
-entry main
-stage compute
-capability spirv_1_4
-capability spvDescriptorHeapEXT
-capability spvRayQueryKHR
```

The build then executes:

```powershell
spirv-val --target-env vulkan1.4 build\shader\ray_query.spv
spirv-dis build\shader\ray_query.spv -o build\shader\ray_query.spvasm
```

`spirv-val` exits successfully without diagnostics.

SHA-256 of the tested SPIR-V module:

```text
6E4AE43B9756081426AAA378293EF056C2E81CE47A6D8620AE6DEEDEB0A3BB78
```

Its declared capabilities and extensions are:

```spirv
OpCapability RayQueryKHR
OpCapability Int64
OpCapability UntypedPointersKHR
OpCapability DescriptorHeapEXT
OpCapability Shader
OpExtension "SPV_KHR_ray_query"
OpExtension "SPV_KHR_untyped_pointers"
OpExtension "SPV_EXT_descriptor_heap"
OpExtension "SPV_KHR_storage_buffer_storage_class"
```

The relevant resource-heap sequence is:

```spirv
OpDecorate %_runtimearr_ulong ArrayStride 8
OpDecorate %slang_resourceHeap BuiltIn ResourceHeapEXT
%58 = OpUntypedAccessChainKHR %_ptr_UniformConstant %_runtimearr_ulong %slang_resourceHeap %54
%60 = OpLoad %ulong %58
%62 = OpConvertUToAccelerationStructureKHR %61 %60
OpRayQueryInitializeKHR %query %62 %uint_0 %uint_255 %98 %99 %100 %101
```

## Descriptor-heap setup

The compute pipeline is created with
`VK_PIPELINE_CREATE_2_DESCRIPTOR_HEAP_BIT_EXT` and a null pipeline layout, as
required by `VUID-VkComputePipelineCreateInfo-flags-11311`.

The shader has two variables decorated with `DescriptorSet=0` and `Binding`.
Both have mappings in `VkShaderDescriptorSetAndBindingMappingInfoEXT`, satisfying
`VUID-VkComputePipelineCreateInfo-flags-11312`:

| Shader binding | SPIR-V resource | Mapping source |
| --- | --- | --- |
| Set 0, binding 0 | Uniform buffer containing the two-word Slang descriptor handle | `VK_DESCRIPTOR_MAPPING_SOURCE_PUSH_DATA_EXT`, offset 0 |
| Set 0, binding 1 | Read/write storage buffer | `VK_DESCRIPTOR_MAPPING_SOURCE_PUSH_ADDRESS_EXT`, offset 8 |

The pushed data is 16 bytes:

```text
offset 0: uint32 sceneHandle
offset 4: uint32 reserved/padding
offset 8: uint64 resultBufferAddress
```

The implementation-reserved resource-heap range is `[0, 0x17a00)`. The raw
eight-byte TLAS address is written at offset `0x17a00`, immediately after that
reserved range. Its shader index is `0x17a00 / 8 = 12096`. The allocation is
large enough to contain the slot, and the slot and pushed addresses meet their
reported alignment requirements.

## Acceleration structures and synchronization

The repro creates:

- One bottom-level acceleration structure containing one non-indexed triangle.
- One top-level acceleration structure containing one identity-transformed
  instance of that BLAS.
- Build scratch addresses aligned to
  `minAccelerationStructureScratchOffsetAlignment`.
- Geometry and instance addresses with the required Vulkan alignments and
  buffer usage flags.

The command stream includes:

- A BLAS-build to TLAS-build acceleration-structure memory barrier.
- A TLAS-build to compute ray-query acceleration-structure memory barrier.
- A compute shader storage-write to host-read barrier for the result.

The build command buffer finishes on the same queue before the dispatch command
buffer is submitted. Host-visible initialization uses coherent memory and is
completed before queue submission.

## Validation results

The corrected source was checked as follows:

1. CMake configure and Release build completed successfully.
2. `spirv-val --target-env vulkan1.4` completed successfully with no output.
3. The SPIR-V capability list was audited against enabled Vulkan features,
   including `OpCapability Int64` mapped to enabled `shaderInt64`.
4. The application was run with `VK_LAYER_KHRONOS_validation` enabled.
5. Synchronization validation was enabled with `VK_LAYER_VALIDATE_SYNC=1`.
6. All implicit layers were disabled with
   `VK_LOADER_LAYERS_DISABLE=~implicit~`.
7. No validation error, VUID, synchronization hazard, or shader validation
   diagnostic was emitted.
8. The dispatch still returned `VK_ERROR_DEVICE_LOST`.

## Slang status

An earlier investigation involved an unsuitable older acceleration-structure
heap lowering. That is not the shader used here. Slang 2026.13 emits the raw
eight-byte resource-heap load followed by
`OpConvertUToAccelerationStructureKHR`, as shown above. The host-side heap ABI
matches that generated sequence.

This report is therefore not claiming a current Slang compiler defect.

## Assessment

The known application-side `shaderInt64` omission has been fixed, every SPIR-V
capability now has the required device support enabled, the SPIR-V module passes
validation, the Vulkan object setup and synchronization have been audited, and
the Khronos Validation Layer reports no API misuse even with synchronization
validation enabled. The result also persists with all third-party implicit
layers removed.

Given those controls, the remaining evidence points to an NVIDIA Vulkan driver
failure in the interaction between `VK_EXT_descriptor_heap`, a raw
resource-heap acceleration-structure address,
`OpConvertUToAccelerationStructureKHR`, and an inline ray query.

## Request

Please reproduce this on an NVIDIA driver build and investigate why the compute
dispatch loses the device. If additional crash-dump, Nsight, Aftermath, or
driver-internal diagnostic data is needed, please specify the preferred capture
procedure.