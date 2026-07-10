using Silk.NET.Core;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;
using Silk.NET.Vulkan.Extensions.KHR;
using Silk.NET.Windowing;
using VulkanRayQueryTriangle.Helpers;

namespace VulkanRayQueryTriangle;

/// <summary>
/// Owns the Vulkan instance, physical/logical device, queue and the extension function
/// tables required by the demo (descriptor heap, acceleration structure, swapchain).
/// Mirrors the setup performed by Zenith.NET.Vulkan's VKGraphicsContext but stripped to
/// exactly what the RayQuery triangle needs.
/// </summary>
internal sealed unsafe class VulkanContext : IDisposable
{
    private static readonly string[] DeviceExtensions =
    [
        ExtDescriptorHeap.ExtensionName,      // VK_EXT_descriptor_heap (bindless)
        "VK_KHR_acceleration_structure",
        "VK_KHR_deferred_host_operations",    // dependency of acceleration_structure
        "VK_KHR_ray_query",
        "VK_KHR_shader_untyped_pointers",     // push-data device-address dereference
        "VK_KHR_maintenance5",                // inline shader module in pipeline stage (stage.module = null)
        KhrSwapchain.ExtensionName
    ];

    public readonly Vk Vk;
    public Instance Instance;
    public PhysicalDevice PhysicalDevice;
    public Device Device;
    public Queue Queue;
    public uint QueueFamilyIndex;

    public KhrSurface KhrSurface = null!;
    public SurfaceKHR Surface;
    public ExtDescriptorHeap DescriptorHeap = null!;
    public KhrAccelerationStructure AccelerationStructure = null!;
    public KhrSwapchain KhrSwapchain = null!;
    public ExtDebugUtils? DebugUtils;

    public CommandPool CommandPool;

    private DebugUtilsMessengerEXT debugMessenger;
    private readonly bool useValidation;

    public VulkanContext(IWindow window, bool useValidation)
    {
        this.useValidation = useValidation;
        Vk = Vk.GetApi();

        CreateInstance(window);
        CreateSurface(window);
        SelectPhysicalDevice();
        CreateLogicalDevice();
        CreateCommandPool();
    }

    private void CreateInstance(IWindow window)
    {
        if (window.VkSurface is null)
        {
            throw new InvalidOperationException("The window was not created for Vulkan (missing VkSurface).");
        }

        ApplicationInfo applicationInfo = new()
        {
            SType = StructureType.ApplicationInfo,
            PApplicationName = (byte*)SilkMarshal.StringToPtr("VulkanRayQueryTriangle"),
            PEngineName = (byte*)SilkMarshal.StringToPtr("None"),
            ApiVersion = new Version32(1, 4, 0)
        };

        // Surface extensions required by the windowing backend.
        byte** windowExtensions = window.VkSurface.GetRequiredExtensions(out uint windowExtensionCount);
        List<string> instanceExtensions = [.. SilkMarshal.PtrToStringArray((nint)windowExtensions, (int)windowExtensionCount)];

        string[] instanceLayers = [];

        if (useValidation)
        {
            instanceExtensions.Add(ExtDebugUtils.ExtensionName);
            instanceLayers = FilterAvailableLayers(["VK_LAYER_KHRONOS_validation"]);
        }

        InstanceCreateInfo createInfo = new()
        {
            SType = StructureType.InstanceCreateInfo,
            PApplicationInfo = &applicationInfo,
            EnabledExtensionCount = (uint)instanceExtensions.Count,
            PpEnabledExtensionNames = (byte**)SilkMarshal.StringArrayToPtr(instanceExtensions),
            EnabledLayerCount = (uint)instanceLayers.Length,
            PpEnabledLayerNames = (byte**)SilkMarshal.StringArrayToPtr(instanceLayers)
        };

        Vk.CreateInstance(&createInfo, null, out Instance).ThrowOnError();

        SilkMarshal.Free((nint)applicationInfo.PApplicationName);
        SilkMarshal.Free((nint)applicationInfo.PEngineName);
        SilkMarshal.Free((nint)createInfo.PpEnabledExtensionNames);
        SilkMarshal.Free((nint)createInfo.PpEnabledLayerNames);

        if (!Vk.TryGetInstanceExtension(Instance, out KhrSurface))
        {
            throw new InvalidOperationException("VK_KHR_surface is not available.");
        }

        if (useValidation && Vk.TryGetInstanceExtension(Instance, out ExtDebugUtils debugUtils))
        {
            DebugUtils = debugUtils;
            SetupDebugMessenger();
        }
    }

    private string[] FilterAvailableLayers(string[] requested)
    {
        uint count = 0;
        Vk.EnumerateInstanceLayerProperties(&count, null).ThrowOnError();

        LayerProperties[] layers = new LayerProperties[count];
        fixed (LayerProperties* pLayers = layers)
        {
            Vk.EnumerateInstanceLayerProperties(&count, pLayers).ThrowOnError();
        }

        HashSet<string> available = [];
        foreach (LayerProperties layer in layers)
        {
            available.Add(SilkMarshal.PtrToString((nint)layer.LayerName)!);
        }

        return [.. requested.Where(available.Contains)];
    }

    private uint DebugCallback(DebugUtilsMessageSeverityFlagsEXT severity, DebugUtilsMessageTypeFlagsEXT type, DebugUtilsMessengerCallbackDataEXT* callbackData, void* userData)
    {
        string message = SilkMarshal.PtrToString((nint)callbackData->PMessage) ?? string.Empty;
        Console.WriteLine($"[{severity}] {message}");
        Console.Out.Flush();
        return Vk.False;
    }

    private void SetupDebugMessenger()
    {
        DebugUtilsMessengerCreateInfoEXT createInfo = new()
        {
            SType = StructureType.DebugUtilsMessengerCreateInfoExt,
            MessageSeverity = DebugUtilsMessageSeverityFlagsEXT.WarningBitExt | DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt,
            MessageType = DebugUtilsMessageTypeFlagsEXT.GeneralBitExt | DebugUtilsMessageTypeFlagsEXT.ValidationBitExt | DebugUtilsMessageTypeFlagsEXT.PerformanceBitExt,
            PfnUserCallback = (DebugUtilsMessengerCallbackFunctionEXT)DebugCallback
        };

        DebugUtils!.CreateDebugUtilsMessenger(Instance, &createInfo, null, out debugMessenger).ThrowOnError();
    }

    private void CreateSurface(IWindow window)
    {
        Surface = window.VkSurface!.Create<AllocationCallbacks>(Instance.ToHandle(), null).ToSurface();
    }

    private void SelectPhysicalDevice()
    {
        uint count = 0;
        Vk.EnumeratePhysicalDevices(Instance, &count, null).ThrowOnError();

        if (count is 0)
        {
            throw new InvalidOperationException("No Vulkan physical devices found.");
        }

        PhysicalDevice[] devices = new PhysicalDevice[count];
        fixed (PhysicalDevice* pDevices = devices)
        {
            Vk.EnumeratePhysicalDevices(Instance, &count, pDevices).ThrowOnError();
        }

        ulong bestScore = 0;
        foreach (PhysicalDevice device in devices)
        {
            PhysicalDeviceProperties properties;
            Vk.GetPhysicalDeviceProperties(device, &properties);

            if (properties.ApiVersion < new Version32(1, 4, 0))
            {
                continue;
            }

            ulong score = properties.DeviceType switch
            {
                PhysicalDeviceType.DiscreteGpu => 100000,
                PhysicalDeviceType.IntegratedGpu => 10000,
                PhysicalDeviceType.VirtualGpu => 1000,
                _ => 1
            };

            if (score > bestScore)
            {
                bestScore = score;
                PhysicalDevice = device;
            }
        }

        if (PhysicalDevice.Handle is 0)
        {
            throw new NotSupportedException("No device supporting Vulkan 1.4 was found.");
        }
    }

    private void CreateLogicalDevice()
    {
        // Pick a queue family that supports graphics + compute + present. On desktop
        // GPUs the primary graphics family satisfies all three, which keeps submission
        // and presentation on a single queue for this single-threaded demo.
        uint queueFamilyCount = 0;
        Vk.GetPhysicalDeviceQueueFamilyProperties(PhysicalDevice, &queueFamilyCount, null);

        QueueFamilyProperties[] families = new QueueFamilyProperties[queueFamilyCount];
        fixed (QueueFamilyProperties* pFamilies = families)
        {
            Vk.GetPhysicalDeviceQueueFamilyProperties(PhysicalDevice, &queueFamilyCount, pFamilies);
        }

        bool found = false;
        for (uint i = 0; i < queueFamilyCount; i++)
        {
            bool graphicsAndCompute = families[i].QueueFlags.HasFlag(QueueFlags.GraphicsBit | QueueFlags.ComputeBit);

            KhrSurface.GetPhysicalDeviceSurfaceSupport(PhysicalDevice, i, Surface, out Bool32 presentSupport).ThrowOnError();

            if (graphicsAndCompute && presentSupport)
            {
                QueueFamilyIndex = i;
                found = true;
                break;
            }
        }

        if (!found)
        {
            throw new NotSupportedException("No queue family supports graphics, compute and present simultaneously.");
        }

        float priority = 1.0f;
        DeviceQueueCreateInfo queueCreateInfo = new()
        {
            SType = StructureType.DeviceQueueCreateInfo,
            QueueFamilyIndex = QueueFamilyIndex,
            QueueCount = 1,
            PQueuePriorities = &priority
        };

        string[] enabledExtensions = FilterAvailableDeviceExtensions(DeviceExtensions);
        if (!enabledExtensions.Contains(ExtDescriptorHeap.ExtensionName))
        {
            throw new NotSupportedException("VK_EXT_descriptor_heap is not supported by this device.");
        }

        // Feature chain. Query what the device supports, then hand the same chain to
        // CreateDevice so every supported feature we depend on is enabled.
        PhysicalDeviceFeatures2 features2 = new() { SType = StructureType.PhysicalDeviceFeatures2 };
        PhysicalDeviceVulkan12Features vulkan12 = new() { SType = StructureType.PhysicalDeviceVulkan12Features };
        PhysicalDeviceVulkan13Features vulkan13 = new() { SType = StructureType.PhysicalDeviceVulkan13Features };
        PhysicalDeviceAccelerationStructureFeaturesKHR accelerationStructure = new() { SType = StructureType.PhysicalDeviceAccelerationStructureFeaturesKhr };
        PhysicalDeviceRayQueryFeaturesKHR rayQuery = new() { SType = StructureType.PhysicalDeviceRayQueryFeaturesKhr };
        PhysicalDeviceShaderUntypedPointersFeaturesKHR untypedPointers = new() { SType = StructureType.PhysicalDeviceShaderUntypedPointersFeaturesKhr };
        PhysicalDeviceDescriptorHeapFeaturesEXT descriptorHeap = new() { SType = StructureType.PhysicalDeviceDescriptorHeapFeaturesExt() };
        PhysicalDeviceMaintenance5FeaturesKHR maintenance5 = new() { SType = StructureType.PhysicalDeviceMaintenance5FeaturesKhr };

        features2.PNext = &vulkan12;
        vulkan12.PNext = &vulkan13;
        vulkan13.PNext = &accelerationStructure;
        accelerationStructure.PNext = &rayQuery;
        rayQuery.PNext = &untypedPointers;
        untypedPointers.PNext = &descriptorHeap;
        descriptorHeap.PNext = &maintenance5;

        Vk.GetPhysicalDeviceFeatures2(PhysicalDevice, &features2);

        if (!vulkan12.BufferDeviceAddress)
        {
            throw new NotSupportedException("bufferDeviceAddress is required but not supported.");
        }

        if (!untypedPointers.ShaderUntypedPointers)
        {
            throw new NotSupportedException("shaderUntypedPointers is required by VK_EXT_descriptor_heap but not supported.");
        }

        if (!descriptorHeap.DescriptorHeap)
        {
            throw new NotSupportedException("descriptorHeap feature is not supported.");
        }

        if (!maintenance5.Maintenance5)
        {
            throw new NotSupportedException("maintenance5 is required for inline shader modules but not supported.");
        }

        DeviceCreateInfo createInfo = new()
        {
            SType = StructureType.DeviceCreateInfo,
            PNext = &features2,
            QueueCreateInfoCount = 1,
            PQueueCreateInfos = &queueCreateInfo,
            EnabledExtensionCount = (uint)enabledExtensions.Length,
            PpEnabledExtensionNames = (byte**)SilkMarshal.StringArrayToPtr(enabledExtensions)
        };

        Vk.CreateDevice(PhysicalDevice, &createInfo, null, out Device).ThrowOnError();

        SilkMarshal.Free((nint)createInfo.PpEnabledExtensionNames);

        Vk.GetDeviceQueue(Device, QueueFamilyIndex, 0, out Queue);

        if (!Vk.TryGetDeviceExtension(Instance, Device, out DescriptorHeap))
        {
            throw new InvalidOperationException("Failed to load VK_EXT_descriptor_heap function table.");
        }

        if (!Vk.TryGetDeviceExtension(Instance, Device, out AccelerationStructure))
        {
            throw new InvalidOperationException("Failed to load VK_KHR_acceleration_structure function table.");
        }

        if (!Vk.TryGetDeviceExtension(Instance, Device, out KhrSwapchain))
        {
            throw new InvalidOperationException("Failed to load VK_KHR_swapchain function table.");
        }
    }

    private string[] FilterAvailableDeviceExtensions(string[] requested)
    {
        uint count = 0;
        Vk.EnumerateDeviceExtensionProperties(PhysicalDevice, (byte*)null, &count, null).ThrowOnError();

        ExtensionProperties[] extensions = new ExtensionProperties[count];
        fixed (ExtensionProperties* pExtensions = extensions)
        {
            Vk.EnumerateDeviceExtensionProperties(PhysicalDevice, (byte*)null, &count, pExtensions).ThrowOnError();
        }

        HashSet<string> available = [];
        foreach (ExtensionProperties extension in extensions)
        {
            available.Add(SilkMarshal.PtrToString((nint)extension.ExtensionName)!);
        }

        return [.. requested.Where(available.Contains)];
    }

    private void CreateCommandPool()
    {
        CommandPoolCreateInfo createInfo = new()
        {
            SType = StructureType.CommandPoolCreateInfo,
            QueueFamilyIndex = QueueFamilyIndex,
            Flags = CommandPoolCreateFlags.ResetCommandBufferBit
        };

        Vk.CreateCommandPool(Device, &createInfo, null, out CommandPool).ThrowOnError();
    }

    public uint FindMemoryTypeIndex(uint memoryTypeBits, MemoryPropertyFlags properties)
    {
        PhysicalDeviceMemoryProperties memoryProperties;
        Vk.GetPhysicalDeviceMemoryProperties(PhysicalDevice, &memoryProperties);

        for (uint i = 0; i < memoryProperties.MemoryTypeCount; i++)
        {
            bool typeAllowed = (memoryTypeBits & (1u << (int)i)) is not 0;
            bool propertiesMatch = (memoryProperties.MemoryTypes[(int)i].PropertyFlags & properties) == properties;

            if (typeAllowed && propertiesMatch)
            {
                return i;
            }
        }

        throw new InvalidOperationException("No suitable memory type found.");
    }

    /// <summary>Allocates, records, submits and waits for a one-shot command buffer.</summary>
    public void SubmitImmediate(Action<CommandBuffer> record)
    {
        CommandBufferAllocateInfo allocateInfo = new()
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = CommandPool,
            Level = CommandBufferLevel.Primary,
            CommandBufferCount = 1
        };

        Vk.AllocateCommandBuffers(Device, &allocateInfo, out CommandBuffer commandBuffer).ThrowOnError();

        CommandBufferBeginInfo beginInfo = new()
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit
        };

        Vk.BeginCommandBuffer(commandBuffer, &beginInfo).ThrowOnError();
        record(commandBuffer);
        Vk.EndCommandBuffer(commandBuffer).ThrowOnError();

        FenceCreateInfo fenceInfo = new() { SType = StructureType.FenceCreateInfo };
        Vk.CreateFence(Device, &fenceInfo, null, out Fence fence).ThrowOnError();

        SubmitInfo submitInfo = new()
        {
            SType = StructureType.SubmitInfo,
            CommandBufferCount = 1,
            PCommandBuffers = &commandBuffer
        };

        Vk.QueueSubmit(Queue, 1, &submitInfo, fence).ThrowOnError();
        Vk.WaitForFences(Device, 1, &fence, true, ulong.MaxValue).ThrowOnError();

        Vk.DestroyFence(Device, fence, null);
        Vk.FreeCommandBuffers(Device, CommandPool, 1, &commandBuffer);
    }

    public void Dispose()
    {
        Vk.DestroyCommandPool(Device, CommandPool, null);
        Vk.DestroyDevice(Device, null);

        if (DebugUtils is not null && debugMessenger.Handle is not 0)
        {
            DebugUtils.DestroyDebugUtilsMessenger(Instance, debugMessenger, null);
        }

        KhrSurface.DestroySurface(Instance, Surface, null);
        Vk.DestroyInstance(Instance, null);
        Vk.Dispose();
    }
}
