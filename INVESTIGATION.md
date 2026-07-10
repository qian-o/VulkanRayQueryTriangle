# Vulkan RayQuery + VK_EXT_descriptor_heap 加速结构堆加载问题调查报告

> 目的：本文件汇总了当前 bug 的**结论、分析经过、问题总结**，以便在公司电脑上继续实验。
> 生成日期：2026-07-10 ・ 平台：Windows / NVIDIA ・ 项目：`VulkanRayQueryTriangle`

---

## 1. 结论（TL;DR）

- **现象**：通过 descriptor heap（`ResourceDescriptorHeap[]`）加载 TLAS 加速结构（AS）时，`rayQuery.Proceed()` 首次遍历即触发 **device lost**（GPU page fault）。
- **根因**：**不是** Slang 编译器旧版本的 bug。使用**最新** `Slangc.NET 2026.13.0` 编译，SPIR-V 输出已完全正确（见 §4）。真正的问题是 **NVIDIA 驱动在 `VK_EXT_descriptor_heap` 路径下，对「堆槽内存放 uint64 设备地址 + 着色器内 `OpConvertUToAccelerationStructureKHR`」这一 AS 加载模式的运行期支持存在缺陷**。
- **当前可用解法（workaround，已在代码中生效）**：绕过 Scene 的 descriptor handle，直接把 TLAS 设备地址通过 push constant（`Params.z/w`）传入着色器，在着色器内 `OpConvertUToAccelerationStructureKHR` 转换。此路径工作正常。
- **仍未解**：让纯 descriptor-heap AS 加载路径在 NVIDIA 上稳定工作（待驱动侧确认 / 上游 issue 跟进）。

---

## 2. 环境与版本审计

| 组件 | 使用版本 | 最新版本 | 状态 |
|------|----------|----------|------|
| Silk.NET.Vulkan（及 EXT/KHR/Windowing/Input） | 2.23.0 | 2.23.0 | ✅ 最新 |
| **Slangc.NET（运行时着色器编译器）** | **2026.13.0** | 2026.13.0 | ✅ 最新（NuGet + GitHub 均确认） |
| Vulkan SDK | 1.4.350.0 | — | ✅ |
| SDK 自带 `slangc.exe` | **2026.8** | — | ⚠️ **过时**，探针一度误用 |
| DXC（`dxcompiler.dll`） | 1.9.0.5347 | stable 1.9.2602.24 / preview 1.10.2605.24 | ℹ️ SDK 附带，仅用于对照探针 |
| .NET | net10.0 | — | ✅ |

> **关键教训**：早期 SPIR-V 探针使用了 Vulkan SDK 里的 `slangc.exe`（**2026.8**），它**早于** Slang PR #11209（该修复在 v2026.11 落地），因此产出的是**旧的、有 bug 的** AS 堆加载代码。项目运行时实际使用的 `Slangc.NET 2026.13.0` 已包含全部相关修复。**已用 2026.13 重新编译探针**，见下。

- Slangc.NET 2026.13.0 native 二进制路径：
  `C:\Users\13247\.nuget\packages\slangc.net\2026.13.0\runtimes\win-x64\native\`

---

## 3. 相关上游修复（Slang changelog）

按时间线，descriptor-heap AS / buffer 相关修复：

| 版本 | Commit / PR | 说明 |
|------|-------------|------|
| v2026.10.2 | `aaa5f89dd` #11037 / #11211 | 修复 `ConstantBuffer<T>.Handle` 与 `spvDescriptorHeapEXT` 同用时崩溃 |
| **v2026.11** | `726e0973b` **#11209** | **Fix descriptor heap acceleration structure loads** ← 与本 bug 直接相关 |
| v2026.11 | `360da3b12` #11431 | Handle descriptor heap byte address buffers |
| v2026.12 | #11494 / issue #11231 | AS 堆加载改为 raw uint64 设备地址 load（ArrayStride 8）+ `OpConvertUToAccelerationStructureKHR` |
| v2026.13 | `012051b20` #11483 / #11647 | descriptor-heap `ConstantBuffer` 以 StorageBuffer 存储类发射 |

**待办（在公司电脑继续）**：核实 #11209 / #11494 是否**在 2026.13 完全修复**、以及 NVIDIA 侧是否有对应 driver issue。检索关键词见 §7。

---

## 4. SPIR-V 分析经过（核心证据）

### 4.1 探针着色器
HLSL 探针：`shaderdebug/src/hlsl_rayquery_heap.hlsl`，镜像了 `Assets/Shaders/RayQueryTriangle.slang`。关键行：

```hlsl
RaytracingAccelerationStructure scene = ResourceDescriptorHeap[constants.Scene];
RayQuery<RAY_FLAG_NONE> query;
query.TraceRayInline(scene, RAY_FLAG_NONE, 0xFF, ray);
```

### 4.2 用 2026.13 编译的 SPIR-V（`shaderdebug/heap_as_2026_13.spvasm`）

AS 加载序列（正确）：

```
%_runtimearr_ulong = OpTypeRuntimeArray %ulong          ; uint64 运行时数组
OpDecorate %_runtimearr_ulong ArrayStride 8             ; stride = 8（字面量，正确）
%137 = OpTypeAccelerationStructureKHR
...
%134 = OpUntypedAccessChainKHR %_ptr_UniformConstant %_runtimearr_ulong %slang_resourceHeap %130
%136 = OpLoad %ulong %134                                ; 从堆槽加载 uint64 设备地址
%138 = OpConvertUToAccelerationStructureKHR %137 %136    ; 地址 -> AS handle
OpRayQueryInitializeKHR %query %138 ...
```

关键点：
- Capability：`RayQueryKHR`、`UntypedPointersKHR`、`DescriptorHeapEXT`。
- `slang_resourceHeap` 声明为 `BuiltIn ResourceHeapEXT` 的 `OpUntypedVariableKHR`。
- AS 槽被当作 **8 字节 stride 的 uint64 设备地址**读取，`ArrayStride 8` 是**字面量**（不再是 `ArrayStrideIdEXT` + `OpConstantSizeOfEXT`）。

### 4.3 旧版 2026.8 输出（`shaderdebug/heap_as.spvasm`）对比

```
%137 = OpTypeAccelerationStructureKHR
%138 = OpConstantSizeOfEXT %uint %137                    ; 用 AS 类型的“描述符大小”做 stride
%_runtimearr_137 = OpTypeRuntimeArray %137               ; 数组元素类型 = AS（不透明描述符）
OpDecorateId %_runtimearr_137 ArrayStrideIdEXT %138      ; stride = sizeof(AS descriptor)
%141 = OpUntypedAccessChainKHR ... %_runtimearr_137 ...
```

**差异（这是 #11209 修复的核心）**：
- **旧（2026.8）**：把堆槽当作**不透明 AS 描述符数组**，stride = `OpConstantSizeOfEXT(AS)`（设备报告的 buffer descriptor size）。
- **新（2026.13）**：把堆槽当作 **uint64 设备地址数组**，stride = 8，加载后 `OpConvertUToAccelerationStructureKHR`。

→ 二者对**堆槽应写入什么内容**、以及**索引 stride**的要求完全不同。这解释了为何早期用旧编译器探针时对不上宿主端布局。

---

## 5. 宿主端（C#）实现要点

### 5.1 `DescriptorHeap.cs` — AS 槽布局
- AS 条目**不**通过 `vkWriteResourceDescriptorsEXT` 写不透明描述符，而是**直接写 8 字节的 TLAS 设备地址**：

```csharp
public uint WriteAccelerationStructure(ulong deviceAddress, ulong size)
{
    uint index = accelerationStructureBaseIndex8 + accelerationStructureHead++;
    *(ulong*)(mapped + (nint)(sizeof(ulong) * (long)index)) = deviceAddress;
    return index;
}
```

- AS 区域独立、以 8 字节 stride 排布，紧跟 image region 之后，使字节偏移 `handleIndex * 8` 与着色器的 stride-8 索引精确匹配。
- 若在此写不透明 AS 描述符 → 着色器加载到垃圾值 → 转成非法 AS handle → 首次 `Proceed()` 遍历 page fault（device lost）。

### 5.2 `RayQueryRenderer.cs` — 当前 workaround
`Constants` 结构注释已记录关键结论：

```
// 即便 Slang >= 2026.12（正确的 uint64 + OpConvertU SPIR-V）且堆槽已确认存放精确 TLAS 地址，
// spvDescriptorHeapEXT 的 AS 堆加载在 NVIDIA 上仍于 rayQuery 遍历时 device-lost。
// 同一地址在着色器内 OpConvertUToAccelerationStructureKHR 转换则工作正常。
```

因此 TLAS 地址通过 `Params.z/w`（push constant）传入，而非 Scene descriptor handle。

Slang 编译参数（`CreateComputePipeline`）：
```
-entry CSMain -matrix-layout-row-major -target spirv
-capability spirv_latest -capability spvDescriptorHeapEXT
-spirv-unified-descriptor-heap-stride
-fvk-use-entrypoint-name
```

---

## 6. 问题总结

1. **编译器版本已确认最新**（Slangc.NET 2026.13.0），SPIR-V 输出正确，**不是编译器 bug**。早期困惑源于误用 SDK 内过时的 2026.8 `slangc.exe`。
2. Slang 对 AS 堆加载的建模在 v2026.11（#11209）/ v2026.12（#11494）后已改为 **uint64 设备地址 + `OpConvertUToAccelerationStructureKHR`**，宿主端 `DescriptorHeap` 也已按此布局（stride 8、写裸地址）对齐。
3. **即使 SPIR-V 与堆内存布局都正确，纯 descriptor-heap AS 加载路径在 NVIDIA 上仍 device-lost**。这指向 **NVIDIA 驱动对 `VK_EXT_descriptor_heap` + AS 组合的运行期支持缺陷**，而非应用或编译器问题。
4. **当前绕过方案**（push TLAS 地址 + 着色器内转换）稳定可用，triangle 可正常渲染。

---

## 7. 待办 / 在公司电脑继续的实验

- [ ] **确认 NVIDIA 驱动侧问题**：
  - 检索 NVIDIA Developer Forums / Vulkan bug tracker，关键词：`VK_EXT_descriptor_heap acceleration structure`、`spvDescriptorHeapEXT ray query device lost`、`OpConvertUToAccelerationStructureKHR heap`。
  - 记录当前驱动版本（`vulkaninfo` / `nvidia-smi`）并试验不同驱动分支（Game Ready / Studio / Vulkan Beta driver）。
- [ ] **上游 issue 跟进**：
  - shader-slang/slang：#11209、#11231、#11494、#11431、#11483/#11647 是否声明「完全修复」；必要时开新 issue 附最小复现 + `heap_as_2026_13.spvasm`。
  - 检查 Khronos `SPV_EXT_descriptor_heap` 规范对 AS 加载语义的表述。
- [ ] **最小复现验证**：
  - 恢复「Scene 走 descriptor handle」路径（去掉 push 地址 workaround），确认在公司电脑 GPU/驱动上是否复现 device lost；启用 Vulkan validation + GPU-AV / Aftermath 抓取 fault 地址。
- [ ] **交叉对照 DXC 路径**：`shaderdebug/dxc_rayquery_heap.spvasm` 与 Slang 输出对比，确认 DXC 是否走相同 AS 堆加载建模。

### 相关文件索引
- 探针 HLSL：`shaderdebug/src/hlsl_rayquery_heap.hlsl`
- 2026.13 SPIR-V（正确）：`shaderdebug/heap_as_2026_13.spvasm` / `.spv`
- 2026.8 SPIR-V（旧、对照）：`shaderdebug/heap_as.spvasm`
- set/binding 对照：`shaderdebug/set_as.spvasm`
- DXC 对照：`shaderdebug/dxc_rayquery_heap.spvasm`、`dxc_rayquery_emulated.spvasm`
- 宿主端堆布局：`DescriptorHeap.cs`（`WriteAccelerationStructure`）
- workaround 与编译参数：`RayQueryRenderer.cs`（`Constants` 结构、`CreateComputePipeline`）
- TLAS 构建：`AccelerationStructures.cs`
- 入口：`Program.cs`

### 复现探针编译命令（PowerShell）
```powershell
$slangc = "C:\Users\13247\.nuget\packages\slangc.net\2026.13.0\runtimes\win-x64\native\slangc.exe"
cd 'c:\Users\13247\Desktop\VulkanRayQueryTriangle\shaderdebug'
& $slangc .\src\hlsl_rayquery_heap.hlsl -entry CSMain -target spirv `
  -capability spirv_latest -capability spvDescriptorHeapEXT `
  -spirv-unified-descriptor-heap-stride -fvk-use-entrypoint-name `
  -o .\heap_as_2026_13.spv
spirv-dis .\heap_as_2026_13.spv -o .\heap_as_2026_13.spvasm
```
