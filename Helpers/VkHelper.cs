using Silk.NET.Vulkan;

namespace VulkanRayQueryTriangle.Helpers;

using Buffer = Silk.NET.Vulkan.Buffer;

/// <summary>
/// A GPU buffer plus its backing memory and device address. Device address is always
/// available because every allocation is made with <see cref="MemoryAllocateFlags.DeviceAddressBit"/>.
/// </summary>
internal sealed unsafe class VkBuffer : IDisposable
{
    private readonly VulkanContext context;
    private nint mapped;

    public Buffer Buffer;
    public DeviceMemory Memory;
    public ulong DeviceAddress;
    public ulong Size;

    public VkBuffer(VulkanContext context, ulong size, BufferUsageFlags usage, bool hostVisible)
    {
        this.context = context;
        Size = size;

        // BufferDeviceAddress is required by both the descriptor heap and the
        // acceleration-structure inputs, so it is always part of the usage flags.
        usage |= BufferUsageFlags.ShaderDeviceAddressBit;

        BufferCreateInfo createInfo = new()
        {
            SType = StructureType.BufferCreateInfo,
            Size = size,
            Usage = usage,
            SharingMode = SharingMode.Exclusive
        };

        context.Vk.CreateBuffer(context.Device, &createInfo, null, out Buffer).ThrowOnError();

        MemoryRequirements requirements;
        context.Vk.GetBufferMemoryRequirements(context.Device, Buffer, &requirements);

        MemoryAllocateFlagsInfo flagsInfo = new()
        {
            SType = StructureType.MemoryAllocateFlagsInfo,
            Flags = MemoryAllocateFlags.DeviceAddressBit
        };

        MemoryPropertyFlags properties = hostVisible
            ? MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit
            : MemoryPropertyFlags.DeviceLocalBit;

        MemoryAllocateInfo allocateInfo = new()
        {
            SType = StructureType.MemoryAllocateInfo,
            PNext = &flagsInfo,
            AllocationSize = requirements.Size,
            MemoryTypeIndex = context.FindMemoryTypeIndex(requirements.MemoryTypeBits, properties)
        };

        context.Vk.AllocateMemory(context.Device, &allocateInfo, null, out Memory).ThrowOnError();
        context.Vk.BindBufferMemory(context.Device, Buffer, Memory, 0).ThrowOnError();

        BufferDeviceAddressInfo addressInfo = new()
        {
            SType = StructureType.BufferDeviceAddressInfo,
            Buffer = Buffer
        };

        DeviceAddress = context.Vk.GetBufferDeviceAddress(context.Device, &addressInfo);
    }

    public nint Map()
    {
        if (mapped is 0)
        {
            void* pointer = null;
            context.Vk.MapMemory(context.Device, Memory, 0, Size, 0, &pointer).ThrowOnError();
            mapped = (nint)pointer;
        }

        return mapped;
    }

    public void Unmap()
    {
        if (mapped is not 0)
        {
            context.Vk.UnmapMemory(context.Device, Memory);
            mapped = 0;
        }
    }

    public void Upload<T>(ReadOnlySpan<T> data) where T : unmanaged
    {
        nint pointer = Map();
        Span<T> destination = new((void*)pointer, data.Length);
        data.CopyTo(destination);
    }

    public void Dispose()
    {
        Unmap();
        context.Vk.DestroyBuffer(context.Device, Buffer, null);
        context.Vk.FreeMemory(context.Device, Memory, null);
    }
}

/// <summary>
/// A GPU image plus its view, created as a general-purpose storage image target.
/// </summary>
internal sealed unsafe class VkImageResource : IDisposable
{
    private readonly VulkanContext context;

    public Image Image;
    public DeviceMemory Memory;
    public ImageView View;
    public Format Format;
    public uint Width;
    public uint Height;

    public VkImageResource(VulkanContext context, uint width, uint height, Format format, ImageUsageFlags usage)
    {
        this.context = context;
        Width = width;
        Height = height;
        Format = format;

        ImageCreateInfo createInfo = new()
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Format = format,
            Extent = new Extent3D(width, height, 1),
            MipLevels = 1,
            ArrayLayers = 1,
            Samples = SampleCountFlags.Count1Bit,
            Tiling = ImageTiling.Optimal,
            Usage = usage,
            SharingMode = SharingMode.Exclusive,
            InitialLayout = ImageLayout.Undefined
        };

        context.Vk.CreateImage(context.Device, &createInfo, null, out Image).ThrowOnError();

        MemoryRequirements requirements;
        context.Vk.GetImageMemoryRequirements(context.Device, Image, &requirements);

        MemoryAllocateInfo allocateInfo = new()
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = requirements.Size,
            MemoryTypeIndex = context.FindMemoryTypeIndex(requirements.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit)
        };

        context.Vk.AllocateMemory(context.Device, &allocateInfo, null, out Memory).ThrowOnError();
        context.Vk.BindImageMemory(context.Device, Image, Memory, 0).ThrowOnError();

        ImageViewCreateInfo viewInfo = new()
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = Image,
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

        context.Vk.CreateImageView(context.Device, &viewInfo, null, out View).ThrowOnError();
    }

    public void Dispose()
    {
        context.Vk.DestroyImageView(context.Device, View, null);
        context.Vk.DestroyImage(context.Device, Image, null);
        context.Vk.FreeMemory(context.Device, Memory, null);
    }
}

internal static class VkHelper
{
    public static void ThrowOnError(this Result result)
    {
        if (result is not Result.Success)
        {
            throw new InvalidOperationException($"Vulkan call failed with {result}.");
        }
    }

    /// <summary>Rounds <paramref name="value"/> up to the next multiple of <paramref name="alignment"/>.</summary>
    public static ulong Align(ulong value, ulong alignment)
    {
        if (alignment is 0)
        {
            return value;
        }

        return (value + alignment - 1) / alignment * alignment;
    }
}
