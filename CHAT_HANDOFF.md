# Chat handoff: Vulkan descriptor-heap ray query investigation

Generated on 2026-07-10 for continuing this investigation in a new GitHub
Copilot conversation on another computer.

## How to resume

1. Clone or update the repository and check out `main`:

   ```powershell
   git clone https://github.com/qian-o/VulkanRayQueryTriangle.git
   cd VulkanRayQueryTriangle
   git pull origin main
   ```

2. Ask the new conversation to read this file, `README.md`, and
   `ISSUE_DRAFT.md` before taking action.
3. A suitable first message is:

   > Continue the Vulkan descriptor-heap ray-query investigation from
   > `CHAT_HANDOFF.md`. Do not run a GPU-submitting workload. First verify the
   > repository state, then help me submit or refine the Slang issue.

## Objective

Determine whether Slang 2026.13 generates a valid Vulkan SPIR-V access for
`DescriptorHandle<RaytracingAccelerationStructure>` used with
`VK_EXT_descriptor_heap` and inline ray query. Only investigate an NVIDIA
driver defect after the compiler lowering and host usage have both been shown
to conform to the Vulkan and SPIR-V contracts.

The project must remain a small, headless C++20 reproduction.

## Current conclusion

The current evidence is not sufficient to report an NVIDIA driver bug.

The host now writes a standard
`VK_DESCRIPTOR_TYPE_ACCELERATION_STRUCTURE_KHR` descriptor using
`vkWriteResourceDescriptorsEXT`. Slang 2026.13 nevertheless retrieves the
entry from `ResourceHeapEXT` as a fixed-stride 64-bit integer and converts it
with `OpConvertUToAccelerationStructureKHR`.

Vulkan distinguishes two relevant cases:

- A raw 64-bit acceleration-structure address can be stored as ordinary data
  and consumed by `OpConvertUToAccelerationStructureKHR`. The Khronos
  `VK_EXT_mutable_descriptor_type` proposal explicitly says no descriptor is
  required for that path.
- An acceleration-structure descriptor produced by
  `vkWriteResourceDescriptorsEXT` is an opaque descriptor. The Vulkan Heap
  Resource Type Correspondence maps
  `VK_DESCRIPTOR_TYPE_ACCELERATION_STRUCTURE_KHR` to
  `OpTypeAccelerationStructureKHR`.

No published exception has been found that allows the opaque descriptor bytes
from the second case to be loaded as the raw address from the first case.
Therefore the next step is a Slang issue asking whether the lowering is valid
and which host-side contract `DescriptorHandle` expects.

## Repository state and important files

- Branch: `main`
- Remote: `https://github.com/qian-o/VulkanRayQueryTriangle.git`
- `main.cpp`: Vulkan initialization, AS construction, standard descriptor
  writer, descriptor heap binding, dispatch recording, and fault diagnostics.
- `ray_query_heap.slang`: complete minimal shader source.
- `ray_query_heap.spv` and `ray_query_heap.spvasm`: checked-in Slang 2026.13
  artifacts.
- `README.md`: user-facing investigation, validation steps, and report order.
- `ISSUE_DRAFT.md`: Slang-first issue text. Its reproduction commit is filled
  with the immutable commit containing the standard descriptor API path.

The host-side descriptor path now does the following:

1. Queries the AS descriptor size with
   `vkGetPhysicalDeviceDescriptorSizeEXT`.
2. Requires the current device result to be 8 bytes because the Slang module
   has a fixed 8-byte heap stride.
3. Places the descriptor after `minResourceHeapReservedRange`, aligned to at
   least `bufferDescriptorAlignment` and 8 bytes.
4. Calls `vkWriteResourceDescriptorsEXT` with
   `VK_DESCRIPTOR_TYPE_ACCELERATION_STRUCTURE_KHR` and the address returned by
   `vkGetAccelerationStructureDeviceAddressKHR`.
5. Treats the resulting bytes as opaque. Any printed payload is diagnostic
   only; equality with the TLAS address is not a Vulkan guarantee.
6. Encodes the Slang descriptor handle as `descriptorOffset / 8`, matching the
   generated module's fixed stride.

## Generated SPIR-V

The relevant Slang 2026.13 lowering is:

```text
OpDecorate %_runtimearr_ulong ArrayStride 8
OpDecorate %slang_resourceHeap BuiltIn ResourceHeapEXT
%38 = OpUntypedAccessChainKHR ... %_runtimearr_ulong ...
%40 = OpLoad %ulong %38
%42 = OpConvertUToAccelerationStructureKHR %41 %40
```

Artifact SHA-256 values:

```text
ray_query_heap.spv:     93EBAE20651A003F9B41A72D7CF30E1E26067086A996DA56A861CECC9CE5ADFC
ray_query_heap.spvasm:  20650B871BA8F8E1942C755CA52BEAA0157599770561764A3FF2EC9F9B2271AA
```

`spirv-val --target-env vulkan1.4` accepts the module. This validates the
standalone module structure, but it cannot determine whether bytes supplied by
the Vulkan application at runtime are ordinary address data or an opaque
descriptor of a different resource type.

## Specification evidence

Primary references:

- Vulkan Descriptor Heap Interface:
  <https://docs.vulkan.org/spec/latest/chapters/interfaces.html#interfaces-resources-descriptorheap>
- Vulkan Heap Resource Type Correspondence:
  <https://docs.vulkan.org/spec/latest/chapters/interfaces.html#interfaces-resources-heap-type-correspondence>
- Runtime SPIR-V VUID for an AS-typed heap load:
  <https://docs.vulkan.org/spec/latest/appendices/spirvenv.html#VUID-RuntimeSpirv-Result-11350>
- `SPV_EXT_descriptor_heap`:
  <https://github.khronos.org/SPIRV-Registry/extensions/EXT/SPV_EXT_descriptor_heap.html>
- Ordinary raw-address alternative:
  <https://github.com/KhronosGroup/Vulkan-Docs/blob/main/proposals/VK_EXT_mutable_descriptor_type.adoc>

Important points already checked:

- `SPV_EXT_descriptor_heap` describes descriptors as opaque objects with
  descriptor types including `OpTypeAccelerationStructureKHR`.
- The Vulkan correspondence table maps the Vulkan KHR AS descriptor to
  `OpTypeAccelerationStructureKHR`, not an integer.
- `OpConstantSizeOfEXT(OpTypeAccelerationStructureKHR)` and
  `ArrayStrideIdEXT` provide a way for a typed heap array to use the
  implementation-defined descriptor size and alignment.
- `VUID-RuntimeSpirv-Result-11350` describes an `OpLoad` whose result type is
  `OpTypeAccelerationStructureKHR` when its pointer is derived from
  `ResourceHeapEXT`.
- Khronos source searches found no special rule joining `ResourceHeapEXT`, an
  integer load of an API-generated AS descriptor, and
  `OpConvertUToAccelerationStructureKHR`.

## Tested environment

```text
OS:          Windows 11
GPU:         NVIDIA GeForce RTX 3050 Laptop GPU
Driver:      NVIDIA 610.74 (Windows 32.0.16.1074)
Vulkan SDK:  1.4.350
Device API:  1.4.341
Slang:       2026.13
SPIRV-Tools: v2026.2 v2026.2.rc2-1-g2ec8457a
```

The NVIDIA implementation reports an 8-byte acceleration-structure descriptor.
Its observed bit pattern matched the supplied TLAS address, but descriptor
payloads are opaque and this observation is not portable evidence.

## Validation already completed

- Clean Release CMake build succeeded.
- The documented Slang 2026.13 command reproduced the checked-in SPIR-V
  byte-for-byte.
- Both checked-in artifact hashes were rechecked.
- `spirv-val --target-env vulkan1.4 ray_query_heap.spv` passed.
- The standard descriptor-writer revision passed `--validate-only` with core
  and synchronization validation enabled, no validation messages, and
  `queue submissions=0`.
- `--validate-only` records AS builds, heap binding, dispatch, and barriers,
  but deliberately does not submit the command buffer.
- The shader embedded in `ISSUE_DRAFT.md` matches `ray_query_heap.slang`
  exactly.
- `git diff --check` and editor diagnostics passed.

## Critical execution warning

Do not automatically run the executable without `--validate-only`.

The current standard `vkWriteResourceDescriptorsEXT` revision has not executed
an actual GPU dispatch. An older raw-address host path produced
`VK_ERROR_DEVICE_LOST` and a device-fault instruction pointer on this NVIDIA
system. That older result does not establish a failure of the current standard
descriptor-writer path and must not be presented as driver evidence.

Safe commands are:

```powershell
cmake -S . -B build
cmake --build build --config Release
spirv-val --target-env vulkan1.4 .\ray_query_heap.spv
.\build\Release\vulkan_descriptor_heap_ray_query_repro.exe --validate-only
```

Do not run this command unless the user explicitly authorizes a GPU-submitting
test after the shader contract has been resolved:

```powershell
.\build\Release\vulkan_descriptor_heap_ray_query_repro.exe
```

## Reporting order

1. Submit `ISSUE_DRAFT.md` to
   <https://github.com/shader-slang/slang/issues>.
2. Ask Slang to clarify whether `DescriptorHandle` expects an API-generated
   opaque descriptor or ordinary raw address data.
3. If Slang confirms that the integer-load lowering is intended to be valid
   Vulkan, cross-file the answer and reproduction with both:
   - <https://github.com/KhronosGroup/SPIRV-Headers/issues>
   - <https://github.com/KhronosGroup/Vulkan-Docs/issues>
4. Use the NVIDIA bug portal only if the lowering is confirmed valid and the
   same committed standard-API revision then reproduces an actual submitted
   workload failure:
   <https://developer.nvidia.com/nvidia_bug/add>

Do not submit the current evidence directly to NVIDIA.

## Related Slang history

- <https://github.com/shader-slang/slang/issues/10671>
- <https://github.com/shader-slang/slang/pull/11209>
- <https://github.com/shader-slang/slang/issues/11231>
- <https://github.com/shader-slang/slang/pull/11494>

These establish that Slang's current AS conversion and 8-byte stride are
intentional implementation choices, but they do not answer the Vulkan heap
resource type correspondence or opaque descriptor question.

## Next concrete action

Review `ISSUE_DRAFT.md` once more after pulling the latest `main`, then submit
it to the Slang issue tracker. No additional GPU dispatch is required to ask
the compiler/specification question.