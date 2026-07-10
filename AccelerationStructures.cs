using Silk.NET.Vulkan;
using VulkanRayQueryTriangle.Helpers;

namespace VulkanRayQueryTriangle;

/// <summary>
/// Builds one BLAS (a single triangle) and one TLAS referencing it, then exposes the
/// TLAS device address so it can be written into the descriptor heap.
///
/// The BLAS→TLAS write→read synchronisation follows the Zenith.NET.Vulkan design: a
/// single global <see cref="MemoryBarrier2"/> is emitted at the *start* of the TLAS
/// build (waiting on all prior acceleration-structure builds) rather than after each
/// BLAS, allowing multiple BLAS builds to run in parallel.
/// </summary>
internal sealed unsafe class AccelerationStructures : IDisposable
{
    private readonly VulkanContext context;

    private readonly VkBuffer vertexBuffer;
    private readonly VkBuffer indexBuffer;
    private readonly VkBuffer instanceBuffer;

    private AccelerationStructureKHR blas;
    private VkBuffer blasStorage = null!;
    private VkBuffer blasScratch = null!;

    private AccelerationStructureKHR tlas;
    private VkBuffer tlasStorage = null!;
    private VkBuffer tlasScratch = null!;

    public ulong TlasDeviceAddress { get; private set; }
    public ulong TlasSize { get; private set; }

    // VkPhysicalDeviceAccelerationStructurePropertiesKHR::minAccelerationStructureScratchOffsetAlignment.
    // The scratch device address passed to vkCmdBuildAccelerationStructuresKHR MUST be a multiple of
    // this value (typically 128 on NVIDIA); an unaligned scratch corrupts the built BVH and faults
    // during traversal. VkBuffer binds memory at offset 0 and returns whatever address the driver
    // assigns, which is not guaranteed to satisfy this, so scratch buffers are over-allocated and the
    // build address is rounded up (see AllocateScratch).
    private readonly ulong scratchAlignment;

    public AccelerationStructures(VulkanContext context)
    {
        this.context = context;

        scratchAlignment = QueryScratchAlignment(context);

        // A single triangle on the z = 0 plane, in normalized device-ish coordinates
        // so the orthographic shader camera (see the .slang) sees it centered.
        ReadOnlySpan<float> vertices =
        [
             0.0f,  0.5f, 0.0f, 0.0f,
            -0.5f, -0.5f, 0.0f, 0.0f,
             0.5f, -0.5f, 0.0f, 0.0f
        ];
        ReadOnlySpan<uint> indices = [0, 1, 2];

        vertexBuffer = new VkBuffer(
            context,
            (ulong)(vertices.Length * sizeof(float)),
            BufferUsageFlags.AccelerationStructureBuildInputReadOnlyBitKhr | BufferUsageFlags.StorageBufferBit,
            hostVisible: true);
        vertexBuffer.Upload(vertices);

        indexBuffer = new VkBuffer(
            context,
            (ulong)(indices.Length * sizeof(uint)),
            BufferUsageFlags.AccelerationStructureBuildInputReadOnlyBitKhr | BufferUsageFlags.StorageBufferBit,
            hostVisible: true);
        indexBuffer.Upload(indices);

        instanceBuffer = new VkBuffer(
            context,
            (ulong)sizeof(AccelerationStructureInstanceKHR),
            BufferUsageFlags.AccelerationStructureBuildInputReadOnlyBitKhr,
            hostVisible: true);

        // The whole build (BLAS + barrier + TLAS) is recorded into one command buffer
        // and waited on before rendering begins.
        context.SubmitImmediate(commandBuffer =>
        {
            BuildBlas(commandBuffer);
            BuildTlas(commandBuffer);
        });
    }

    private static ulong QueryScratchAlignment(VulkanContext context)
    {
        PhysicalDeviceAccelerationStructurePropertiesKHR asProperties = new()
        {
            SType = StructureType.PhysicalDeviceAccelerationStructurePropertiesKhr
        };
        PhysicalDeviceProperties2 properties2 = new()
        {
            SType = StructureType.PhysicalDeviceProperties2,
            PNext = &asProperties
        };

        context.Vk.GetPhysicalDeviceProperties2(context.PhysicalDevice, &properties2);

        return Math.Max(asProperties.MinAccelerationStructureScratchOffsetAlignment, 1u);
    }

    /// <summary>
    /// Allocates a scratch buffer whose build device address is aligned to
    /// <see cref="scratchAlignment"/>. The buffer is over-allocated by one alignment unit so the
    /// returned address can be rounded up while still leaving <paramref name="requiredSize"/> usable
    /// bytes past the aligned offset.
    /// </summary>
    private VkBuffer AllocateScratch(ulong requiredSize, out ulong alignedAddress)
    {
        VkBuffer scratch = new(context, requiredSize + scratchAlignment, BufferUsageFlags.StorageBufferBit, hostVisible: false);
        alignedAddress = VkHelper.Align(scratch.DeviceAddress, scratchAlignment);

        return scratch;
    }

    private void BuildBlas(CommandBuffer commandBuffer)
    {
        AccelerationStructureGeometryKHR geometry = new()
        {
            SType = StructureType.AccelerationStructureGeometryKhr,
            GeometryType = GeometryTypeKHR.TrianglesKhr,
            Geometry = new()
            {
                Triangles = new()
                {
                    SType = StructureType.AccelerationStructureGeometryTrianglesDataKhr,
                    VertexFormat = Format.R32G32B32Sfloat,
                    VertexData = new() { DeviceAddress = vertexBuffer.DeviceAddress },
                    VertexStride = 4 * sizeof(float),
                    MaxVertex = 2,
                    IndexType = IndexType.Uint32,
                    IndexData = new() { DeviceAddress = indexBuffer.DeviceAddress }
                }
            },
            Flags = GeometryFlagsKHR.OpaqueBitKhr
        };

        AccelerationStructureBuildGeometryInfoKHR buildInfo = new()
        {
            SType = StructureType.AccelerationStructureBuildGeometryInfoKhr,
            Type = AccelerationStructureTypeKHR.BottomLevelKhr,
            Flags = BuildAccelerationStructureFlagsKHR.PreferFastTraceBitKhr,
            Mode = BuildAccelerationStructureModeKHR.BuildKhr,
            GeometryCount = 1,
            PGeometries = &geometry
        };

        uint primitiveCount = 1;

        AccelerationStructureBuildSizesInfoKHR sizeInfo = new() { SType = StructureType.AccelerationStructureBuildSizesInfoKhr };
        context.AccelerationStructure.GetAccelerationStructureBuildSizes(
            context.Device,
            AccelerationStructureBuildTypeKHR.DeviceKhr,
            &buildInfo,
            &primitiveCount,
            &sizeInfo);

        blasStorage = new VkBuffer(context, sizeInfo.AccelerationStructureSize, BufferUsageFlags.AccelerationStructureStorageBitKhr, hostVisible: false);
        blasScratch = AllocateScratch(Math.Max(sizeInfo.BuildScratchSize, sizeInfo.UpdateScratchSize), out ulong blasScratchAddress);

        AccelerationStructureCreateInfoKHR createInfo = new()
        {
            SType = StructureType.AccelerationStructureCreateInfoKhr,
            Buffer = blasStorage.Buffer,
            Size = sizeInfo.AccelerationStructureSize,
            Type = AccelerationStructureTypeKHR.BottomLevelKhr
        };

        context.AccelerationStructure.CreateAccelerationStructure(context.Device, &createInfo, null, out blas).ThrowOnError();

        buildInfo.DstAccelerationStructure = blas;
        buildInfo.ScratchData = new() { DeviceAddress = blasScratchAddress };

        AccelerationStructureBuildRangeInfoKHR buildRange = new() { PrimitiveCount = primitiveCount };
        AccelerationStructureBuildRangeInfoKHR* pBuildRange = &buildRange;

        // No barrier here on purpose: multiple BLAS builds may run in parallel; the
        // single barrier at the start of the TLAS build synchronises all of them.
        context.AccelerationStructure.CmdBuildAccelerationStructures(commandBuffer, 1, &buildInfo, &pBuildRange);
    }

    private void BuildTlas(CommandBuffer commandBuffer)
    {
        // Identity 3x4 row-major transform.
        TransformMatrixKHR transform = default;
        transform.Matrix[0] = 1.0f;
        transform.Matrix[5] = 1.0f;
        transform.Matrix[10] = 1.0f;

        AccelerationStructureDeviceAddressInfoKHR blasAddressInfo = new()
        {
            SType = StructureType.AccelerationStructureDeviceAddressInfoKhr,
            AccelerationStructure = blas
        };
        ulong blasDeviceAddress = context.AccelerationStructure.GetAccelerationStructureDeviceAddress(context.Device, &blasAddressInfo);

        AccelerationStructureInstanceKHR instance = new()
        {
            Transform = transform,
            InstanceCustomIndex = 0,
            Mask = 0xFF,
            InstanceShaderBindingTableRecordOffset = 0,
            Flags = GeometryInstanceFlagsKHR.TriangleFacingCullDisableBitKhr,
            AccelerationStructureReference = blasDeviceAddress
        };
        instanceBuffer.Upload<AccelerationStructureInstanceKHR>([instance]);

        AccelerationStructureGeometryKHR geometry = new()
        {
            SType = StructureType.AccelerationStructureGeometryKhr,
            GeometryType = GeometryTypeKHR.InstancesKhr,
            Geometry = new()
            {
                Instances = new()
                {
                    SType = StructureType.AccelerationStructureGeometryInstancesDataKhr,
                    ArrayOfPointers = false,
                    Data = new() { DeviceAddress = instanceBuffer.DeviceAddress }
                }
            }
        };

        AccelerationStructureBuildGeometryInfoKHR buildInfo = new()
        {
            SType = StructureType.AccelerationStructureBuildGeometryInfoKhr,
            Type = AccelerationStructureTypeKHR.TopLevelKhr,
            Flags = BuildAccelerationStructureFlagsKHR.PreferFastTraceBitKhr,
            Mode = BuildAccelerationStructureModeKHR.BuildKhr,
            GeometryCount = 1,
            PGeometries = &geometry
        };

        uint primitiveCount = 1;

        AccelerationStructureBuildSizesInfoKHR sizeInfo = new() { SType = StructureType.AccelerationStructureBuildSizesInfoKhr };
        context.AccelerationStructure.GetAccelerationStructureBuildSizes(
            context.Device,
            AccelerationStructureBuildTypeKHR.DeviceKhr,
            &buildInfo,
            &primitiveCount,
            &sizeInfo);

        tlasStorage = new VkBuffer(context, sizeInfo.AccelerationStructureSize, BufferUsageFlags.AccelerationStructureStorageBitKhr, hostVisible: false);
        tlasScratch = AllocateScratch(Math.Max(sizeInfo.BuildScratchSize, sizeInfo.UpdateScratchSize), out ulong tlasScratchAddress);

        AccelerationStructureCreateInfoKHR createInfo = new()
        {
            SType = StructureType.AccelerationStructureCreateInfoKhr,
            Buffer = tlasStorage.Buffer,
            Size = sizeInfo.AccelerationStructureSize,
            Type = AccelerationStructureTypeKHR.TopLevelKhr
        };

        context.AccelerationStructure.CreateAccelerationStructure(context.Device, &createInfo, null, out tlas).ThrowOnError();

        buildInfo.DstAccelerationStructure = tlas;
        buildInfo.ScratchData = new() { DeviceAddress = tlasScratchAddress };

        // Wait for all prior BLAS builds before the TLAS reads them.
        BuildSyncBarrier(commandBuffer, PipelineStageFlags2.AccelerationStructureBuildBitKhr);

        AccelerationStructureBuildRangeInfoKHR buildRange = new() { PrimitiveCount = primitiveCount };
        AccelerationStructureBuildRangeInfoKHR* pBuildRange = &buildRange;
        context.AccelerationStructure.CmdBuildAccelerationStructures(commandBuffer, 1, &buildInfo, &pBuildRange);

        // Ensure the finished TLAS is visible to subsequent shader (ray query) reads.
        BuildSyncBarrier(commandBuffer, PipelineStageFlags2.AllCommandsBit);

        AccelerationStructureDeviceAddressInfoKHR tlasAddressInfo = new()
        {
            SType = StructureType.AccelerationStructureDeviceAddressInfoKhr,
            AccelerationStructure = tlas
        };

        TlasDeviceAddress = context.AccelerationStructure.GetAccelerationStructureDeviceAddress(context.Device, &tlasAddressInfo);
        TlasSize = sizeInfo.AccelerationStructureSize;
    }

    private void BuildSyncBarrier(CommandBuffer commandBuffer, PipelineStageFlags2 dstStage)
    {
        MemoryBarrier2 memoryBarrier = new()
        {
            SType = StructureType.MemoryBarrier2,
            SrcStageMask = PipelineStageFlags2.AccelerationStructureBuildBitKhr,
            SrcAccessMask = AccessFlags2.AccelerationStructureWriteBitKhr,
            DstStageMask = dstStage,
            DstAccessMask = AccessFlags2.AccelerationStructureReadBitKhr
        };

        DependencyInfo dependencyInfo = new()
        {
            SType = StructureType.DependencyInfo,
            MemoryBarrierCount = 1,
            PMemoryBarriers = &memoryBarrier
        };

        context.Vk.CmdPipelineBarrier2(commandBuffer, &dependencyInfo);
    }

    public void Dispose()
    {
        context.AccelerationStructure.DestroyAccelerationStructure(context.Device, tlas, null);
        context.AccelerationStructure.DestroyAccelerationStructure(context.Device, blas, null);

        tlasScratch.Dispose();
        tlasStorage.Dispose();
        blasScratch.Dispose();
        blasStorage.Dispose();

        instanceBuffer.Dispose();
        indexBuffer.Dispose();
        vertexBuffer.Dispose();
    }
}
