using System.Runtime.InteropServices;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Slangc.NET;
using VulkanRayQueryTriangle.Helpers;

namespace VulkanRayQueryTriangle;

using Semaphore = Silk.NET.Vulkan.Semaphore;

/// <summary>
/// Owns the compute pipeline, the output storage image, the swapchain and the per-frame
/// present logic. Each frame:
///   1. bind the resource heap
///   2. bind the compute pipeline (created with VK_PIPELINE_CREATE_2_DESCRIPTOR_HEAP_BIT_EXT)
///   3. push the constant buffer's device address (vkCmdPushDataEXT)
///   4. dispatch, then copy the output image into the acquired swapchain image and present.
/// </summary>
internal sealed unsafe class RayQueryRenderer : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Constants
    {
        // Must match RayQueryConstants (std140) in RayQueryTriangle.slang exactly:
        //   offset  0: uint4 Params   (x=Width, y=Height, z=TlasAddrLo, w=TlasAddrHi)
        //   offset 16: uint2 Scene    (unused DescriptorHandle, kept for std140 layout)
        //   offset 24: uint2 Output   (DescriptorHandle: heap index + 0)
        //
        // The TLAS device address is pushed in Params.z/w rather than referenced through the
        // Scene descriptor handle: even with Slang >= 2026.12 (correct uint64+OpConvertU SPIR-V)
        // and the heap slot verified to hold the exact TLAS address, the spvDescriptorHeapEXT
        // acceleration-structure heap load device-losts on NVIDIA at rayQuery traversal. The same
        // address converted in-shader (OpConvertUToAccelerationStructureKHR) works.
        public uint Width;
        public uint Height;
        public uint TlasAddressLo;
        public uint TlasAddressHi;
        private uint sceneX;
        private uint sceneY;
        public uint OutputHandle;
        private uint outputHandleY;
    }

    private readonly VulkanContext context;
    private readonly DescriptorHeap descriptorHeap;
    private readonly AccelerationStructures accelerationStructures;

    private Pipeline pipeline;

    private VkImageResource outputImage = null!;
    private VkBuffer constantBuffer = null!;
    private uint outputHandle;

    // Swapchain state.
    private SwapchainKHR swapchain;
    private Image[] swapchainImages = [];
    private Format swapchainFormat;
    private Extent2D swapchainExtent;

    // Per-frame sync (single frame in flight keeps the demo simple).
    private Semaphore imageAvailableSemaphore;
    private Semaphore[] renderFinishedSemaphores = [];
    private Fence inFlightFence;
    private CommandBuffer commandBuffer;

    private uint width;
    private uint height;

    public RayQueryRenderer(VulkanContext context,
                            DescriptorHeap descriptorHeap,
                            AccelerationStructures accelerationStructures,
                            uint sceneHandle,
                            uint width,
                            uint height)
    {
        this.context = context;
        this.descriptorHeap = descriptorHeap;
        this.accelerationStructures = accelerationStructures;
        this.width = width;
        this.height = height;

        CreateComputePipeline();
        CreateOutputResources(sceneHandle);
        CreateSyncObjects();
        CreateSwapchain();
    }

    private void CreateComputePipeline()
    {
        string shaderPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Shaders", "RayQueryTriangle.slang");

        // Compile Slang -> SPIR-V with the descriptor-heap capability enabled, matching
        // the argument set used by Zenith.NET's ZenithCompiler for its Vulkan target.
        byte[] spirv = SlangCompiler.CompileWithReflection(
        [
            shaderPath,
            "-entry", "CSMain",
            "-matrix-layout-row-major",
            "-target", "spirv",
            "-capability", "spirv_latest",
            "-capability", "spvDescriptorHeapEXT",
            "-spirv-unified-descriptor-heap-stride",
            "-fvk-use-entrypoint-name"
        ], out _);

        byte* entryPoint = (byte*)SilkMarshal.StringToPtr("CSMain");

        // Descriptor-heap shaders receive their constants through vkCmdPushDataEXT rather than
        // a descriptor set. The mapping below tells the driver that the shader's single uniform
        // buffer is sourced from the pushed device address, and it must sit on the pipeline
        // stage's pNext chain (ahead of the inline shader module). The inline module itself
        // requires VK_KHR_maintenance5, which the device enables in VulkanContext.
        DescriptorSetAndBindingMappingEXT mapping = new()
        {
            SType = StructureType.DescriptorSetAndBindingMappingExt(),
            DescriptorSet = 0,
            FirstBinding = 0,
            BindingCount = 1,
            ResourceMask = SpirvResourceTypeFlagsEXT.UniformBufferBitExt,
            Source = DescriptorMappingSourceEXT.PushAddressExt,
            SourceData = new DescriptorMappingSourceDataEXT { PushAddressOffset = 0 }
        };

        ShaderDescriptorSetAndBindingMappingInfoEXT mappingInfo = new()
        {
            SType = StructureType.ShaderDescriptorSetAndBindingMappingInfoExt(),
            MappingCount = 1,
            PMappings = &mapping
        };

        fixed (byte* pCode = spirv)
        {
            // The pNext chain must match the working reference (VKShader.cs) exactly:
            //   stage -> ShaderModuleCreateInfo -> ShaderDescriptorSetAndBindingMappingInfoEXT
            // i.e. the mapping hangs off the inline module, and the module hangs off the stage.
            // Chaining the mapping directly on the stage (ahead of the module) makes the driver
            // never apply the uniform-buffer -> push-address mapping: the shader then reads a
            // garbage heap index for constants.Scene and dereferences an invalid TLAS, which
            // surfaces only as VK_ERROR_DEVICE_LOST on submit.
            ShaderModuleCreateInfo moduleInfo = new()
            {
                SType = StructureType.ShaderModuleCreateInfo,
                PNext = &mappingInfo,
                CodeSize = (nuint)spirv.Length,
                PCode = (uint*)pCode
            };

            PipelineShaderStageCreateInfo stageInfo = new()
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                PNext = &moduleInfo,
                Stage = ShaderStageFlags.ComputeBit,
                PName = entryPoint
            };

            // Descriptor-heap pipelines do not use a VkPipelineLayout with descriptor set
            // layouts; the flag below switches the pipeline into heap-indexed access mode.
            PipelineCreateFlags2CreateInfo flags2 = new()
            {
                SType = StructureType.PipelineCreateFlags2CreateInfo,
                Flags = PipelineCreateFlags2.Vk2DescriptorHeapBitExt()
            };

            ComputePipelineCreateInfo createInfo = new()
            {
                SType = StructureType.ComputePipelineCreateInfo,
                PNext = &flags2,
                Stage = stageInfo
            };

            context.Vk.CreateComputePipelines(context.Device, default, 1, &createInfo, null, out pipeline).ThrowOnError();
        }

        SilkMarshal.Free((nint)entryPoint);
    }

    private void CreateOutputResources(uint sceneHandle)
    {
        // The output image is copied into the swapchain, so it uses the same format.
        outputImage = new VkImageResource(
            context,
            width,
            height,
            Format.B8G8R8A8Unorm,
            ImageUsageFlags.StorageBit | ImageUsageFlags.TransferSrcBit);

        // One-time transition Undefined -> General so the compute shader can write to it.
        context.SubmitImmediate(cmd => TransitionImage(
            cmd,
            outputImage.Image,
            ImageLayout.Undefined,
            ImageLayout.General,
            0,
            AccessFlags2.ShaderWriteBit,
            PipelineStageFlags2.TopOfPipeBit,
            PipelineStageFlags2.ComputeShaderBit));

        outputHandle = descriptorHeap.WriteStorageImage(outputImage.View, outputImage.Image, outputImage.Format);

        constantBuffer = new VkBuffer(context, (ulong)sizeof(Constants), BufferUsageFlags.UniformBufferBit, hostVisible: true);

        Constants constants = new()
        {
            Width = width,
            Height = height,
            // TLAS device address, converted in-shader via OpConvertUToAccelerationStructureKHR.
            // (The descriptor-heap AS path device-losts on NVIDIA; see the Constants comment.)
            TlasAddressLo = (uint)(accelerationStructures.TlasDeviceAddress & 0xFFFFFFFF),
            TlasAddressHi = (uint)(accelerationStructures.TlasDeviceAddress >> 32),
            OutputHandle = outputHandle
        };
        constantBuffer.Upload<Constants>([constants]);
    }

    private void CreateSyncObjects()
    {
        SemaphoreCreateInfo semaphoreInfo = new() { SType = StructureType.SemaphoreCreateInfo };
        context.Vk.CreateSemaphore(context.Device, &semaphoreInfo, null, out imageAvailableSemaphore).ThrowOnError();

        FenceCreateInfo fenceInfo = new()
        {
            SType = StructureType.FenceCreateInfo,
            Flags = FenceCreateFlags.SignaledBit
        };
        context.Vk.CreateFence(context.Device, &fenceInfo, null, out inFlightFence).ThrowOnError();

        CommandBufferAllocateInfo allocateInfo = new()
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = context.CommandPool,
            Level = CommandBufferLevel.Primary,
            CommandBufferCount = 1
        };
        context.Vk.AllocateCommandBuffers(context.Device, &allocateInfo, out commandBuffer).ThrowOnError();
    }

    private void CreateSwapchain()
    {
        context.KhrSurface.GetPhysicalDeviceSurfaceCapabilities(context.PhysicalDevice, context.Surface, out SurfaceCapabilitiesKHR capabilities).ThrowOnError();

        swapchainExtent = capabilities.CurrentExtent.Width is not uint.MaxValue
            ? capabilities.CurrentExtent
            : new Extent2D(
                Math.Clamp(width, capabilities.MinImageExtent.Width, capabilities.MaxImageExtent.Width),
                Math.Clamp(height, capabilities.MinImageExtent.Height, capabilities.MaxImageExtent.Height));

        swapchainFormat = Format.B8G8R8A8Unorm;

        uint imageCount = capabilities.MinImageCount + 1;
        if (capabilities.MaxImageCount is not 0 && imageCount > capabilities.MaxImageCount)
        {
            imageCount = capabilities.MaxImageCount;
        }

        SwapchainCreateInfoKHR createInfo = new()
        {
            SType = StructureType.SwapchainCreateInfoKhr,
            Surface = context.Surface,
            MinImageCount = imageCount,
            ImageFormat = swapchainFormat,
            ImageColorSpace = ColorSpaceKHR.SpaceSrgbNonlinearKhr,
            ImageExtent = swapchainExtent,
            ImageArrayLayers = 1,
            ImageUsage = ImageUsageFlags.TransferDstBit | ImageUsageFlags.ColorAttachmentBit,
            ImageSharingMode = SharingMode.Exclusive,
            PreTransform = capabilities.CurrentTransform,
            CompositeAlpha = CompositeAlphaFlagsKHR.OpaqueBitKhr,
            PresentMode = PresentModeKHR.FifoKhr,
            Clipped = true
        };

        context.KhrSwapchain.CreateSwapchain(context.Device, &createInfo, null, out swapchain).ThrowOnError();

        uint count = 0;
        context.KhrSwapchain.GetSwapchainImages(context.Device, swapchain, &count, null).ThrowOnError();
        swapchainImages = new Image[count];
        fixed (Image* pImages = swapchainImages)
        {
            context.KhrSwapchain.GetSwapchainImages(context.Device, swapchain, &count, pImages).ThrowOnError();
        }

        // One render-finished semaphore per swapchain image. A binary semaphore signalled
        // by submit and waited on by present cannot be safely reused until its image is
        // re-acquired, so indexing by image avoids the reuse hazard.
        SemaphoreCreateInfo semaphoreInfo = new() { SType = StructureType.SemaphoreCreateInfo };
        renderFinishedSemaphores = new Semaphore[count];
        for (uint i = 0; i < count; i++)
        {
            context.Vk.CreateSemaphore(context.Device, &semaphoreInfo, null, out renderFinishedSemaphores[i]).ThrowOnError();
        }
    }

    public void DrawFrame()
    {
        context.Vk.WaitForFences(context.Device, 1, in inFlightFence, true, ulong.MaxValue).ThrowOnError();

        uint imageIndex = 0;
        Result acquireResult = context.KhrSwapchain.AcquireNextImage(
            context.Device,
            swapchain,
            ulong.MaxValue,
            imageAvailableSemaphore,
            default,
            ref imageIndex);

        if (acquireResult is Result.ErrorOutOfDateKhr)
        {
            RecreateSwapchain();
            return;
        }
        // SuboptimalKhr still yields a usable image, so render this frame and recreate
        // after present. Any other non-success code is a real failure (including
        // ErrorDeviceLost) and must surface instead of continuing with a stale index.
        if (acquireResult is not (Result.Success or Result.SuboptimalKhr))
        {
            acquireResult.ThrowOnError();
        }

        context.Vk.ResetFences(context.Device, 1, in inFlightFence).ThrowOnError();
        context.Vk.ResetCommandBuffer(commandBuffer, CommandBufferResetFlags.None).ThrowOnError();

        RecordCommandBuffer(imageIndex);

        Semaphore waitSemaphore = imageAvailableSemaphore;
        Semaphore signalSemaphore = renderFinishedSemaphores[imageIndex];
        // The acquired swapchain image is first touched at the transfer stage (its
        // Undefined -> TransferDst transition and the subsequent copy/blit), not at
        // compute. Waiting only at ComputeShaderBit would let the transfer writes race
        // ahead of image acquisition, which the validation layer flags and can escalate
        // to VK_ERROR_DEVICE_LOST. Wait at the transfer stage instead.
        PipelineStageFlags waitStage = PipelineStageFlags.TransferBit;
        CommandBuffer cmd = commandBuffer;

        SubmitInfo submitInfo = new()
        {
            SType = StructureType.SubmitInfo,
            WaitSemaphoreCount = 1,
            PWaitSemaphores = &waitSemaphore,
            PWaitDstStageMask = &waitStage,
            CommandBufferCount = 1,
            PCommandBuffers = &cmd,
            SignalSemaphoreCount = 1,
            PSignalSemaphores = &signalSemaphore
        };

        context.Vk.QueueSubmit(context.Queue, 1, &submitInfo, inFlightFence).ThrowOnError();

        SwapchainKHR sc = swapchain;
        uint presentIndex = imageIndex;
        PresentInfoKHR presentInfo = new()
        {
            SType = StructureType.PresentInfoKhr,
            WaitSemaphoreCount = 1,
            PWaitSemaphores = &signalSemaphore,
            SwapchainCount = 1,
            PSwapchains = &sc,
            PImageIndices = &presentIndex
        };

        Result presentResult = context.KhrSwapchain.QueuePresent(context.Queue, &presentInfo);
        if (presentResult is Result.ErrorOutOfDateKhr or Result.SuboptimalKhr)
        {
            RecreateSwapchain();
        }
    }

    private void RecordCommandBuffer(uint imageIndex)
    {
        CommandBufferBeginInfo beginInfo = new() { SType = StructureType.CommandBufferBeginInfo };
        context.Vk.BeginCommandBuffer(commandBuffer, &beginInfo).ThrowOnError();

        // The descriptor heap must be bound before the pipeline and before push data.
        descriptorHeap.Bind(commandBuffer);

        context.Vk.CmdBindPipeline(commandBuffer, PipelineBindPoint.Compute, pipeline);

        // Push the constant buffer's device address (8 bytes). The shader dereferences
        // it through spvDescriptorHeapEXT to read Width/Height and the resource handles.
        ulong constantAddress = constantBuffer.DeviceAddress;
        PushDataInfoEXT pushDataInfo = new()
        {
            SType = StructureType.PushDataInfoExt(),
            Data = new HostAddressRangeConstEXT(&constantAddress, sizeof(ulong))
        };
        context.DescriptorHeap.CmdPushData(commandBuffer, &pushDataInfo);

        uint groupsX = (width + 15) / 16;
        uint groupsY = (height + 15) / 16;
        context.Vk.CmdDispatch(commandBuffer, groupsX, groupsY, 1);

        Image swapchainImage = swapchainImages[imageIndex];

        // output: General (compute write) -> TransferSrc
        TransitionImage(commandBuffer, outputImage.Image,
            ImageLayout.General, ImageLayout.TransferSrcOptimal,
            AccessFlags2.ShaderWriteBit, AccessFlags2.TransferReadBit,
            PipelineStageFlags2.ComputeShaderBit, PipelineStageFlags2.TransferBit);

        // swapchain: Undefined -> TransferDst
        TransitionImage(commandBuffer, swapchainImage,
            ImageLayout.Undefined, ImageLayout.TransferDstOptimal,
            0, AccessFlags2.TransferWriteBit,
            PipelineStageFlags2.TopOfPipeBit, PipelineStageFlags2.TransferBit);

        BlitOrCopy(swapchainImage);

        // swapchain: TransferDst -> PresentSrc
        TransitionImage(commandBuffer, swapchainImage,
            ImageLayout.TransferDstOptimal, ImageLayout.PresentSrcKhr,
            AccessFlags2.TransferWriteBit, 0,
            PipelineStageFlags2.TransferBit, PipelineStageFlags2.BottomOfPipeBit);

        // output: TransferSrc -> General (ready for next frame's compute write)
        TransitionImage(commandBuffer, outputImage.Image,
            ImageLayout.TransferSrcOptimal, ImageLayout.General,
            AccessFlags2.TransferReadBit, AccessFlags2.ShaderWriteBit,
            PipelineStageFlags2.TransferBit, PipelineStageFlags2.ComputeShaderBit);

        context.Vk.EndCommandBuffer(commandBuffer).ThrowOnError();
    }

    private void BlitOrCopy(Image swapchainImage)
    {
        ImageSubresourceLayers subresource = new()
        {
            AspectMask = ImageAspectFlags.ColorBit,
            MipLevel = 0,
            BaseArrayLayer = 0,
            LayerCount = 1
        };

        // If the output image matches the swapchain extent, a straight copy is enough;
        // otherwise fall back to a blit that scales to the swapchain size.
        if (outputImage.Width == swapchainExtent.Width && outputImage.Height == swapchainExtent.Height)
        {
            ImageCopy region = new()
            {
                SrcSubresource = subresource,
                DstSubresource = subresource,
                Extent = new Extent3D(swapchainExtent.Width, swapchainExtent.Height, 1)
            };

            context.Vk.CmdCopyImage(
                commandBuffer,
                outputImage.Image, ImageLayout.TransferSrcOptimal,
                swapchainImage, ImageLayout.TransferDstOptimal,
                1, &region);
        }
        else
        {
            ImageBlit region = new()
            {
                SrcSubresource = subresource,
                DstSubresource = subresource
            };
            region.SrcOffsets[0] = new Offset3D(0, 0, 0);
            region.SrcOffsets[1] = new Offset3D((int)outputImage.Width, (int)outputImage.Height, 1);
            region.DstOffsets[0] = new Offset3D(0, 0, 0);
            region.DstOffsets[1] = new Offset3D((int)swapchainExtent.Width, (int)swapchainExtent.Height, 1);

            context.Vk.CmdBlitImage(
                commandBuffer,
                outputImage.Image, ImageLayout.TransferSrcOptimal,
                swapchainImage, ImageLayout.TransferDstOptimal,
                1, &region, Filter.Linear);
        }
    }

    private void TransitionImage(CommandBuffer cmd,
                                 Image image,
                                 ImageLayout oldLayout,
                                 ImageLayout newLayout,
                                 AccessFlags2 srcAccess,
                                 AccessFlags2 dstAccess,
                                 PipelineStageFlags2 srcStage,
                                 PipelineStageFlags2 dstStage)
    {
        ImageMemoryBarrier2 barrier = new()
        {
            SType = StructureType.ImageMemoryBarrier2,
            SrcStageMask = srcStage,
            SrcAccessMask = srcAccess,
            DstStageMask = dstStage,
            DstAccessMask = dstAccess,
            OldLayout = oldLayout,
            NewLayout = newLayout,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = image,
            SubresourceRange = new()
            {
                AspectMask = ImageAspectFlags.ColorBit,
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = 1
            }
        };

        DependencyInfo dependencyInfo = new()
        {
            SType = StructureType.DependencyInfo,
            ImageMemoryBarrierCount = 1,
            PImageMemoryBarriers = &barrier
        };

        context.Vk.CmdPipelineBarrier2(cmd, &dependencyInfo);
    }

    private void RecreateSwapchain()
    {
        context.Vk.DeviceWaitIdle(context.Device).ThrowOnError();
        DestroySwapchain();
        CreateSwapchain();
    }

    private void DestroySwapchain()
    {
        foreach (Semaphore semaphore in renderFinishedSemaphores)
        {
            context.Vk.DestroySemaphore(context.Device, semaphore, null);
        }
        renderFinishedSemaphores = [];

        context.KhrSwapchain.DestroySwapchain(context.Device, swapchain, null);
    }

    public void WaitIdle()
    {
        context.Vk.DeviceWaitIdle(context.Device).ThrowOnError();
    }

    public void Dispose()
    {
        context.Vk.DeviceWaitIdle(context.Device);

        DestroySwapchain();

        context.Vk.DestroyFence(context.Device, inFlightFence, null);
        context.Vk.DestroySemaphore(context.Device, imageAvailableSemaphore, null);

        constantBuffer.Dispose();
        outputImage.Dispose();

        context.Vk.DestroyPipeline(context.Device, pipeline, null);
    }
}
