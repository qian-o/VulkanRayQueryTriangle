using Silk.NET.Vulkan;
using VulkanRayQueryTriangle.Helpers;

namespace VulkanRayQueryTriangle;

/// <summary>
/// A minimal VK_EXT_descriptor_heap resource heap.
///
/// The heap is a single host-visible <see cref="VkBuffer"/> (usage
/// <c>VK_BUFFER_USAGE_DESCRIPTOR_HEAP_BIT_EXT</c>) that stays mapped. Descriptors are
/// written directly into the mapped memory at <c>stride * index</c> offsets via
/// <c>vkWriteResourceDescriptorsEXT</c>. The shader-visible handle is simply the
/// region-relative index.
///
/// Two regions live back-to-back inside the buffer:
///   - buffer region  (UniformBuffer / StorageBuffer / AccelerationStructure) stride = bufferDescriptorSize
///   - image region    (SampledImage / StorageImage)                          stride = imageDescriptorSize
/// </summary>
internal sealed unsafe class DescriptorHeap : IDisposable
{
    private readonly VulkanContext context;
    private readonly VkBuffer buffer;
    private readonly nint mapped;

    private readonly ulong bufferStride;
    private readonly ulong imageStride;
    private readonly uint bufferBaseIndex;
    private readonly uint imageBaseIndex;

    // Acceleration structures are NOT written as opaque descriptors. Slang's spvDescriptorHeapEXT
    // emits AS heap loads as a raw uint64 device-address load (ArrayStride 8) + OpConvertUTo-
    // AccelerationStructureKHR (PR #11494 / issue #11231). AS entries therefore live in their own
    // 8-byte-strided region so that byte offset "asBaseIndex8 * 8" matches the shader's stride-8
    // indexing exactly, past the reserved range and clear of the buffer/image regions.
    private readonly uint accelerationStructureBaseIndex8;

    private uint bufferHead;
    private uint imageHead;
    private uint accelerationStructureHead;

    // A sampler heap must also be bound whenever a VK_PIPELINE_CREATE_2_DESCRIPTOR_HEAP_BIT_EXT
    // pipeline dispatches or draws, even when no sampler descriptors are used. Binding only the
    // resource heap leaves the sampler heap unbound and results in GPU faults on dispatch.
    private readonly VkBuffer samplerBuffer;
    private readonly ulong samplerReservedBytes;

    public ulong ReservedBytes { get; }

    /// <summary>
    /// The device-reported size of a buffer-region descriptor (UniformBuffer / StorageBuffer /
    /// AccelerationStructure). Used to correct the acceleration-structure heap ArrayStride that
    /// Slang currently hardcodes to 8 in the emitted SPIR-V (see RayQueryRenderer).
    /// </summary>
    public ulong BufferDescriptorSize { get; }

    private DescriptorHeap(VulkanContext context,
                           VkBuffer buffer,
                           ulong reservedBytes,
                           ulong bufferStride,
                           ulong imageStride,
                           uint bufferBaseIndex,
                           uint imageBaseIndex,
                           uint accelerationStructureBaseIndex8,
                           VkBuffer samplerBuffer,
                           ulong samplerReservedBytes)
    {
        this.context = context;
        this.buffer = buffer;
        this.bufferStride = bufferStride;
        this.imageStride = imageStride;
        this.bufferBaseIndex = bufferBaseIndex;
        this.imageBaseIndex = imageBaseIndex;
        this.accelerationStructureBaseIndex8 = accelerationStructureBaseIndex8;
        this.samplerBuffer = samplerBuffer;
        this.samplerReservedBytes = samplerReservedBytes;

        ReservedBytes = reservedBytes;
        BufferDescriptorSize = bufferStride;
        mapped = buffer.Map();
    }

    public DeviceAddressRangeEXT Range => new(buffer.DeviceAddress, buffer.Size);

    public DeviceAddressRangeEXT SamplerRange => new(samplerBuffer.DeviceAddress, samplerBuffer.Size);

    public static DescriptorHeap Create(VulkanContext context, uint bufferCapacity, uint imageCapacity)
    {
        PhysicalDeviceProperties2 properties2 = new() { SType = StructureType.PhysicalDeviceProperties2 };
        PhysicalDeviceDescriptorHeapPropertiesEXT heapProperties = new() { SType = StructureType.PhysicalDeviceDescriptorHeapPropertiesExt() };
        properties2.PNext = &heapProperties;

        context.Vk.GetPhysicalDeviceProperties2(context.PhysicalDevice, &properties2);

        ulong reservedBytes = heapProperties.MinResourceHeapReservedRange;

        ulong bufferStride = heapProperties.BufferDescriptorSize;
        ulong bufferOffset = VkHelper.Align(reservedBytes, bufferStride);

        ulong imageStride = heapProperties.ImageDescriptorSize;
        ulong imageOffset = VkHelper.Align(bufferOffset + (bufferCapacity * bufferStride), imageStride);

        // Acceleration-structure entries are raw uint64 device addresses (stride 8), placed in
        // their own region after the image region. The shader indexes them as heapBase + handle*8.
        const ulong accelerationStructureStride = sizeof(ulong);
        const uint accelerationStructureCapacity = 64;
        ulong accelerationStructureOffset = VkHelper.Align(imageOffset + (imageCapacity * imageStride), accelerationStructureStride);

        ulong totalSize = VkHelper.Align(
            accelerationStructureOffset + (accelerationStructureCapacity * accelerationStructureStride),
            heapProperties.ResourceHeapAlignment);

        VkBuffer buffer = new(context, totalSize, BufferUsageFlags.DescriptorHeapBitExt(), hostVisible: true);

        // A minimal sampler heap: just the reserved range rounded up to the required alignment.
        // No sampler descriptors are written, but the heap must exist and be bound.
        ulong samplerReservedBytes = heapProperties.MinSamplerHeapReservedRange;
        ulong samplerSize = VkHelper.Align(samplerReservedBytes + heapProperties.SamplerDescriptorSize, heapProperties.SamplerHeapAlignment);

        VkBuffer samplerBuffer = new(context, samplerSize, BufferUsageFlags.DescriptorHeapBitExt(), hostVisible: true);

        return new DescriptorHeap(context,
                                  buffer,
                                  reservedBytes,
                                  bufferStride,
                                  imageStride,
                                  (uint)(bufferOffset / bufferStride),
                                  (uint)(imageOffset / imageStride),
                                  (uint)(accelerationStructureOffset / accelerationStructureStride),
                                  samplerBuffer,
                                  samplerReservedBytes);
    }

    /// <summary>
    /// Writes a TLAS descriptor and returns its heap index.
    ///
    /// Slang's <c>spvDescriptorHeapEXT</c> emits acceleration-structure heap loads as a raw
    /// 64-bit device-address load (<c>OpLoad %ulong</c> over a <c>uint64</c> runtime array with
    /// literal <c>ArrayStride 8</c>) followed by <c>OpConvertUToAccelerationStructureKHR</c>
    /// (see shader-slang/slang PR #11494 / issue #11231, in release ≥ v2026.12). The heap slot
    /// must therefore contain the TLAS device address as a plain <c>uint64</c> at byte offset
    /// <c>handleIndex * 8</c> — NOT an opaque descriptor written through
    /// <c>vkWriteResourceDescriptorsEXT</c>. Writing an opaque AS descriptor here makes the
    /// shader load garbage, convert it to an invalid AS handle, and page-fault on the first
    /// <c>rayQuery.Proceed()</c> traversal (device lost).
    /// </summary>
    public uint WriteAccelerationStructure(ulong deviceAddress, ulong size)
    {
        // AS heap entries occupy their own uint64-strided (8-byte) region so the byte offset
        // handleIndex * 8 matches the stride the shader uses to index the resource heap.
        uint index = accelerationStructureBaseIndex8 + accelerationStructureHead++;
        *(ulong*)(mapped + (nint)(sizeof(ulong) * (long)index)) = deviceAddress;

        return index;
    }

    /// <summary>Writes a StorageBuffer descriptor (device address range) and returns its heap index.</summary>
    public uint WriteStorageBuffer(ulong deviceAddress, ulong size)
    {
        DeviceAddressRangeEXT addressRange = new(deviceAddress, size);

        ResourceDescriptorInfoEXT info = new()
        {
            SType = StructureType.ResourceDescriptorInfoExt(),
            Type = DescriptorType.StorageBuffer,
            Data = new() { PAddressRange = &addressRange }
        };

        return WriteBufferRegion(&info);
    }

    /// <summary>Writes a StorageImage descriptor (image view in General layout) and returns its heap index.</summary>
    public uint WriteStorageImage(ImageView view, Image image, Format format)
    {
        ImageViewCreateInfo viewInfo = new()
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = image,
            ViewType = ImageViewType.Type2D,
            Format = format,
            SubresourceRange = new()
            {
                AspectMask = ImageAspectFlags.ColorBit,
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = 1
            }
        };

        ImageDescriptorInfoEXT imageInfo = new()
        {
            SType = StructureType.ImageDescriptorInfoExt(),
            PView = &viewInfo,
            Layout = ImageLayout.General
        };

        ResourceDescriptorInfoEXT info = new()
        {
            SType = StructureType.ResourceDescriptorInfoExt(),
            Type = DescriptorType.StorageImage,
            Data = new() { PImage = &imageInfo }
        };

        uint index = imageBaseIndex + imageHead++;
        HostAddressRangeEXT target = new((void*)(mapped + (nint)(imageStride * index)), (nuint)imageStride);
        context.DescriptorHeap.WriteResourceDescriptors(context.Device, 1, &info, &target).ThrowOnError();

        return index;
    }

    private uint WriteBufferRegion(ResourceDescriptorInfoEXT* info)
    {
        uint index = bufferBaseIndex + bufferHead++;
        HostAddressRangeEXT target = new((void*)(mapped + (nint)(bufferStride * index)), (nuint)bufferStride);
        context.DescriptorHeap.WriteResourceDescriptors(context.Device, 1, info, &target).ThrowOnError();

        return index;
    }

    /// <summary>Binds the resource heap at the start of a command buffer recording.</summary>
    public void Bind(CommandBuffer commandBuffer)
    {
        BindHeapInfoEXT bindInfo = new()
        {
            SType = StructureType.BindHeapInfoExt(),
            HeapRange = Range,
            ReservedRangeSize = ReservedBytes
        };

        context.DescriptorHeap.CmdBindResourceHeap(commandBuffer, &bindInfo);

        // The sampler heap must be bound too, or descriptor-heap pipelines fault on dispatch.
        BindHeapInfoEXT samplerBindInfo = new()
        {
            SType = StructureType.BindHeapInfoExt(),
            HeapRange = SamplerRange,
            ReservedRangeSize = samplerReservedBytes
        };

        context.DescriptorHeap.CmdBindSamplerHeap(commandBuffer, &samplerBindInfo);
    }

    public void Dispose()
    {
        samplerBuffer.Dispose();
        buffer.Dispose();
    }
}
