#include <vulkan/vulkan.h>

#include <algorithm>
#include <array>
#include <cstdint>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <limits>
#include <stdexcept>
#include <string>
#include <vector>

static_assert(VK_HEADER_VERSION_COMPLETE >= VK_API_VERSION_1_4,
              "Vulkan 1.4 or newer headers are required");
static_assert(VK_EXT_DESCRIPTOR_HEAP_SPEC_VERSION >= 1,
              "VK_EXT_descriptor_heap revision 1 or newer headers are required");

namespace
{

[[noreturn]] void fail(const std::string& message)
{
    throw std::runtime_error(message);
}

void check(VkResult result, const char* call)
{
    if (result != VK_SUCCESS)
    {
        fail(std::string(call) + " failed with VkResult " + std::to_string(result));
    }
}

VkDeviceSize alignUp(VkDeviceSize value, VkDeviceSize alignment)
{
    if (alignment == 0)
    {
        return value;
    }
    return (value + alignment - 1) / alignment * alignment;
}

std::vector<uint32_t> readSpirv(const std::filesystem::path& path)
{
    std::ifstream file(path, std::ios::binary | std::ios::ate);
    if (!file)
    {
        fail("Cannot open SPIR-V file: " + path.string());
    }

    const auto byteSize = static_cast<size_t>(file.tellg());
    if (byteSize == 0 || (byteSize % sizeof(uint32_t)) != 0)
    {
        fail("Invalid SPIR-V file size: " + std::to_string(byteSize));
    }

    std::vector<uint32_t> words(byteSize / sizeof(uint32_t));
    file.seekg(0);
    file.read(reinterpret_cast<char*>(words.data()), static_cast<std::streamsize>(byteSize));
    return words;
}

bool hasExtension(VkPhysicalDevice physicalDevice, const char* name)
{
    uint32_t count = 0;
    check(vkEnumerateDeviceExtensionProperties(physicalDevice, nullptr, &count, nullptr),
          "vkEnumerateDeviceExtensionProperties(count)");
    std::vector<VkExtensionProperties> extensions(count);
    check(vkEnumerateDeviceExtensionProperties(physicalDevice, nullptr, &count, extensions.data()),
          "vkEnumerateDeviceExtensionProperties(data)");
    return std::any_of(extensions.begin(), extensions.end(), [name](const auto& extension)
    {
        return std::strcmp(extension.extensionName, name) == 0;
    });
}

uint32_t findMemoryType(VkPhysicalDevice physicalDevice,
                        uint32_t memoryTypeBits,
                        VkMemoryPropertyFlags required)
{
    VkPhysicalDeviceMemoryProperties properties{};
    vkGetPhysicalDeviceMemoryProperties(physicalDevice, &properties);

    for (uint32_t index = 0; index < properties.memoryTypeCount; ++index)
    {
        const bool allowed = (memoryTypeBits & (1u << index)) != 0;
        const bool matches = (properties.memoryTypes[index].propertyFlags & required) == required;
        if (allowed && matches)
        {
            return index;
        }
    }

    fail("No suitable Vulkan memory type found");
}

struct Buffer
{
    VkBuffer buffer = VK_NULL_HANDLE;
    VkDeviceMemory memory = VK_NULL_HANDLE;
    VkDeviceSize size = 0;
    VkDeviceAddress address = 0;
    void* mapped = nullptr;
};

struct AccelerationStructure
{
    VkAccelerationStructureKHR handle = VK_NULL_HANDLE;
    Buffer storage;
    Buffer scratch;
    VkDeviceSize size = 0;
    VkDeviceAddress address = 0;
};

struct Functions
{
    PFN_vkCreateAccelerationStructureKHR createAccelerationStructure = nullptr;
    PFN_vkDestroyAccelerationStructureKHR destroyAccelerationStructure = nullptr;
    PFN_vkGetAccelerationStructureBuildSizesKHR getAccelerationStructureBuildSizes = nullptr;
    PFN_vkCmdBuildAccelerationStructuresKHR cmdBuildAccelerationStructures = nullptr;
    PFN_vkGetAccelerationStructureDeviceAddressKHR getAccelerationStructureDeviceAddress = nullptr;

    PFN_vkCmdBindResourceHeapEXT cmdBindResourceHeap = nullptr;
    PFN_vkCmdPushDataEXT cmdPushData = nullptr;
};

struct App
{
    VkInstance instance = VK_NULL_HANDLE;
    VkPhysicalDevice physicalDevice = VK_NULL_HANDLE;
    VkDevice device = VK_NULL_HANDLE;
    VkQueue queue = VK_NULL_HANDLE;
    uint32_t queueFamily = 0;
    VkCommandPool commandPool = VK_NULL_HANDLE;

    Functions fn;
    VkPhysicalDeviceDescriptorHeapPropertiesEXT heapProperties{
        VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_DESCRIPTOR_HEAP_PROPERTIES_EXT};
    VkPhysicalDeviceAccelerationStructurePropertiesKHR accelerationStructureProperties{
        VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_ACCELERATION_STRUCTURE_PROPERTIES_KHR};
    VkPhysicalDeviceLimits limits{};

    Buffer vertexBuffer;
    Buffer indexBuffer;
    Buffer instanceBuffer;
    Buffer constantsBuffer;
    Buffer resultBuffer;
    Buffer resourceHeap;
    AccelerationStructure blas;
    AccelerationStructure tlas;
    VkPipeline pipeline = VK_NULL_HANDLE;

    bool deviceLost = false;
};

Buffer createBuffer(App& app,
                    VkDeviceSize size,
                    VkBufferUsageFlags usage,
                    VkMemoryPropertyFlags memoryProperties)
{
    Buffer result{};
    result.size = size;

    VkBufferCreateInfo bufferInfo{VK_STRUCTURE_TYPE_BUFFER_CREATE_INFO};
    bufferInfo.size = size;
    bufferInfo.usage = usage | VK_BUFFER_USAGE_SHADER_DEVICE_ADDRESS_BIT;
    bufferInfo.sharingMode = VK_SHARING_MODE_EXCLUSIVE;
    check(vkCreateBuffer(app.device, &bufferInfo, nullptr, &result.buffer), "vkCreateBuffer");

    VkMemoryRequirements requirements{};
    vkGetBufferMemoryRequirements(app.device, result.buffer, &requirements);

    VkMemoryAllocateFlagsInfo flagsInfo{VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_FLAGS_INFO};
    flagsInfo.flags = VK_MEMORY_ALLOCATE_DEVICE_ADDRESS_BIT;

    VkMemoryAllocateInfo allocation{VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO};
    allocation.pNext = &flagsInfo;
    allocation.allocationSize = requirements.size;
    allocation.memoryTypeIndex = findMemoryType(app.physicalDevice,
                                                requirements.memoryTypeBits,
                                                memoryProperties);
    check(vkAllocateMemory(app.device, &allocation, nullptr, &result.memory), "vkAllocateMemory(buffer)");
    check(vkBindBufferMemory(app.device, result.buffer, result.memory, 0), "vkBindBufferMemory");

    VkBufferDeviceAddressInfo addressInfo{VK_STRUCTURE_TYPE_BUFFER_DEVICE_ADDRESS_INFO};
    addressInfo.buffer = result.buffer;
    result.address = vkGetBufferDeviceAddress(app.device, &addressInfo);

    if ((memoryProperties & VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT) != 0)
    {
        check(vkMapMemory(app.device, result.memory, 0, size, 0, &result.mapped), "vkMapMemory");
    }

    return result;
}

void upload(const Buffer& buffer, const void* data, size_t byteSize)
{
    if (!buffer.mapped || byteSize > buffer.size)
    {
        fail("Invalid buffer upload");
    }
    std::memcpy(buffer.mapped, data, byteSize);
}

template<typename T>
T loadDeviceFunction(VkDevice device, const char* name, bool required = true)
{
    auto function = reinterpret_cast<T>(vkGetDeviceProcAddr(device, name));
    if (required && !function)
    {
        fail(std::string("Missing device function: ") + name);
    }
    return function;
}

void initializeVulkan(App& app)
{
    VkApplicationInfo application{VK_STRUCTURE_TYPE_APPLICATION_INFO};
    application.pApplicationName = "VulkanDescriptorHeapRayQueryRepro";
    application.apiVersion = VK_API_VERSION_1_4;

    VkInstanceCreateInfo instanceInfo{VK_STRUCTURE_TYPE_INSTANCE_CREATE_INFO};
    instanceInfo.pApplicationInfo = &application;
    check(vkCreateInstance(&instanceInfo, nullptr, &app.instance), "vkCreateInstance");

    uint32_t deviceCount = 0;
    check(vkEnumeratePhysicalDevices(app.instance, &deviceCount, nullptr),
          "vkEnumeratePhysicalDevices(count)");
    std::vector<VkPhysicalDevice> physicalDevices(deviceCount);
    check(vkEnumeratePhysicalDevices(app.instance, &deviceCount, physicalDevices.data()),
          "vkEnumeratePhysicalDevices(data)");

    const std::array requiredExtensions{
        VK_EXT_DESCRIPTOR_HEAP_EXTENSION_NAME,
        VK_KHR_ACCELERATION_STRUCTURE_EXTENSION_NAME,
        VK_KHR_DEFERRED_HOST_OPERATIONS_EXTENSION_NAME,
        VK_KHR_RAY_QUERY_EXTENSION_NAME,
        VK_KHR_SHADER_UNTYPED_POINTERS_EXTENSION_NAME,
    };

    for (VkPhysicalDevice candidate : physicalDevices)
    {
        VkPhysicalDeviceDriverProperties driverProperties{
            VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_DRIVER_PROPERTIES};
        VkPhysicalDeviceProperties2 properties2{VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_PROPERTIES_2};
        properties2.pNext = &driverProperties;
        vkGetPhysicalDeviceProperties2(candidate, &properties2);
        const VkPhysicalDeviceProperties& properties = properties2.properties;
        const bool allExtensions = std::all_of(requiredExtensions.begin(), requiredExtensions.end(),
                                               [candidate](const char* extension)
                                               {
                                                   return hasExtension(candidate, extension);
                                               });
        if (allExtensions && properties.vendorID == 0x10DE && properties.apiVersion >= VK_API_VERSION_1_4)
        {
            app.physicalDevice = candidate;
            std::cout << "GPU: " << properties.deviceName
                      << ", API " << VK_API_VERSION_MAJOR(properties.apiVersion) << '.'
                      << VK_API_VERSION_MINOR(properties.apiVersion) << '.'
                      << VK_API_VERSION_PATCH(properties.apiVersion) << '\n'
                      << std::hex
                      << "vendorID=0x" << properties.vendorID
                      << ", deviceID=0x" << properties.deviceID
                      << ", driverVersion=0x" << properties.driverVersion
                      << std::dec
                      << ", driverID=" << static_cast<uint32_t>(driverProperties.driverID) << '\n'
                      << "Driver: " << driverProperties.driverName
                      << " (" << driverProperties.driverInfo << ")\n";
            break;
        }
    }

    if (app.physicalDevice == VK_NULL_HANDLE)
    {
        fail("No NVIDIA Vulkan 1.4 device with VK_EXT_descriptor_heap + ray query found");
    }

    uint32_t queueFamilyCount = 0;
    vkGetPhysicalDeviceQueueFamilyProperties(app.physicalDevice, &queueFamilyCount, nullptr);
    std::vector<VkQueueFamilyProperties> queueFamilies(queueFamilyCount);
    vkGetPhysicalDeviceQueueFamilyProperties(app.physicalDevice, &queueFamilyCount, queueFamilies.data());
    bool foundQueue = false;
    for (uint32_t index = 0; index < queueFamilyCount; ++index)
    {
        if ((queueFamilies[index].queueFlags & VK_QUEUE_COMPUTE_BIT) != 0)
        {
            app.queueFamily = index;
            foundQueue = true;
            break;
        }
    }
    if (!foundQueue)
    {
        fail("No compute queue family found");
    }

    VkPhysicalDeviceVulkan12Features supportedVulkan12{
        VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_VULKAN_1_2_FEATURES};
    VkPhysicalDeviceVulkan13Features supportedVulkan13{
        VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_VULKAN_1_3_FEATURES};
    VkPhysicalDeviceVulkan14Features supportedVulkan14{
        VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_VULKAN_1_4_FEATURES};
    VkPhysicalDeviceAccelerationStructureFeaturesKHR supportedAccelerationStructure{
        VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_ACCELERATION_STRUCTURE_FEATURES_KHR};
    VkPhysicalDeviceRayQueryFeaturesKHR supportedRayQuery{
        VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_RAY_QUERY_FEATURES_KHR};
    VkPhysicalDeviceShaderUntypedPointersFeaturesKHR supportedUntypedPointers{
        VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_SHADER_UNTYPED_POINTERS_FEATURES_KHR};
    VkPhysicalDeviceDescriptorHeapFeaturesEXT supportedDescriptorHeap{
        VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_DESCRIPTOR_HEAP_FEATURES_EXT};
    VkPhysicalDeviceFeatures2 supportedFeatures{VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_FEATURES_2};

    supportedFeatures.pNext = &supportedVulkan12;
    supportedVulkan12.pNext = &supportedVulkan13;
    supportedVulkan13.pNext = &supportedVulkan14;
    supportedVulkan14.pNext = &supportedAccelerationStructure;
    supportedAccelerationStructure.pNext = &supportedRayQuery;
    supportedRayQuery.pNext = &supportedUntypedPointers;
    supportedUntypedPointers.pNext = &supportedDescriptorHeap;
    vkGetPhysicalDeviceFeatures2(app.physicalDevice, &supportedFeatures);

    if (!supportedVulkan12.bufferDeviceAddress || !supportedVulkan13.synchronization2 ||
        !supportedAccelerationStructure.accelerationStructure || !supportedRayQuery.rayQuery ||
        !supportedUntypedPointers.shaderUntypedPointers || !supportedDescriptorHeap.descriptorHeap ||
        !supportedVulkan14.maintenance5)
    {
        fail("A required Vulkan feature is unavailable");
    }

    VkPhysicalDeviceVulkan12Features enabledVulkan12{
        VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_VULKAN_1_2_FEATURES};
    enabledVulkan12.bufferDeviceAddress = VK_TRUE;
    VkPhysicalDeviceVulkan13Features enabledVulkan13{
        VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_VULKAN_1_3_FEATURES};
    enabledVulkan13.synchronization2 = VK_TRUE;
    VkPhysicalDeviceVulkan14Features enabledVulkan14{
        VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_VULKAN_1_4_FEATURES};
    enabledVulkan14.maintenance5 = VK_TRUE;
    VkPhysicalDeviceAccelerationStructureFeaturesKHR enabledAccelerationStructure{
        VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_ACCELERATION_STRUCTURE_FEATURES_KHR};
    enabledAccelerationStructure.accelerationStructure = VK_TRUE;
    VkPhysicalDeviceRayQueryFeaturesKHR enabledRayQuery{
        VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_RAY_QUERY_FEATURES_KHR};
    enabledRayQuery.rayQuery = VK_TRUE;
    VkPhysicalDeviceShaderUntypedPointersFeaturesKHR enabledUntypedPointers{
        VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_SHADER_UNTYPED_POINTERS_FEATURES_KHR};
    enabledUntypedPointers.shaderUntypedPointers = VK_TRUE;
    VkPhysicalDeviceDescriptorHeapFeaturesEXT enabledDescriptorHeap{
        VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_DESCRIPTOR_HEAP_FEATURES_EXT};
    enabledDescriptorHeap.descriptorHeap = VK_TRUE;
    VkPhysicalDeviceFeatures2 enabledFeatures{VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_FEATURES_2};

    enabledFeatures.pNext = &enabledVulkan12;
    enabledVulkan12.pNext = &enabledVulkan13;
    enabledVulkan13.pNext = &enabledVulkan14;
    enabledVulkan14.pNext = &enabledAccelerationStructure;
    enabledAccelerationStructure.pNext = &enabledRayQuery;
    enabledRayQuery.pNext = &enabledUntypedPointers;
    enabledUntypedPointers.pNext = &enabledDescriptorHeap;

    float priority = 1.0f;
    VkDeviceQueueCreateInfo queueInfo{VK_STRUCTURE_TYPE_DEVICE_QUEUE_CREATE_INFO};
    queueInfo.queueFamilyIndex = app.queueFamily;
    queueInfo.queueCount = 1;
    queueInfo.pQueuePriorities = &priority;

    VkDeviceCreateInfo deviceInfo{VK_STRUCTURE_TYPE_DEVICE_CREATE_INFO};
    deviceInfo.pNext = &enabledFeatures;
    deviceInfo.queueCreateInfoCount = 1;
    deviceInfo.pQueueCreateInfos = &queueInfo;
    deviceInfo.enabledExtensionCount = static_cast<uint32_t>(requiredExtensions.size());
    deviceInfo.ppEnabledExtensionNames = requiredExtensions.data();
    check(vkCreateDevice(app.physicalDevice, &deviceInfo, nullptr, &app.device), "vkCreateDevice");
    vkGetDeviceQueue(app.device, app.queueFamily, 0, &app.queue);

    app.fn.createAccelerationStructure = loadDeviceFunction<PFN_vkCreateAccelerationStructureKHR>(
        app.device, "vkCreateAccelerationStructureKHR");
    app.fn.destroyAccelerationStructure = loadDeviceFunction<PFN_vkDestroyAccelerationStructureKHR>(
        app.device, "vkDestroyAccelerationStructureKHR");
    app.fn.getAccelerationStructureBuildSizes = loadDeviceFunction<PFN_vkGetAccelerationStructureBuildSizesKHR>(
        app.device, "vkGetAccelerationStructureBuildSizesKHR");
    app.fn.cmdBuildAccelerationStructures = loadDeviceFunction<PFN_vkCmdBuildAccelerationStructuresKHR>(
        app.device, "vkCmdBuildAccelerationStructuresKHR");
    app.fn.getAccelerationStructureDeviceAddress = loadDeviceFunction<PFN_vkGetAccelerationStructureDeviceAddressKHR>(
        app.device, "vkGetAccelerationStructureDeviceAddressKHR");
    app.fn.cmdBindResourceHeap = loadDeviceFunction<PFN_vkCmdBindResourceHeapEXT>(
        app.device, "vkCmdBindResourceHeapEXT");
    app.fn.cmdPushData = loadDeviceFunction<PFN_vkCmdPushDataEXT>(app.device, "vkCmdPushDataEXT");

    app.heapProperties.pNext = &app.accelerationStructureProperties;
    VkPhysicalDeviceProperties2 properties{VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_PROPERTIES_2};
    properties.pNext = &app.heapProperties;
    vkGetPhysicalDeviceProperties2(app.physicalDevice, &properties);
    app.limits = properties.properties.limits;

    VkCommandPoolCreateInfo poolInfo{VK_STRUCTURE_TYPE_COMMAND_POOL_CREATE_INFO};
    poolInfo.queueFamilyIndex = app.queueFamily;
    poolInfo.flags = VK_COMMAND_POOL_CREATE_RESET_COMMAND_BUFFER_BIT;
    check(vkCreateCommandPool(app.device, &poolInfo, nullptr, &app.commandPool), "vkCreateCommandPool");
}

VkCommandBuffer beginCommandBuffer(App& app)
{
    VkCommandBufferAllocateInfo allocation{VK_STRUCTURE_TYPE_COMMAND_BUFFER_ALLOCATE_INFO};
    allocation.commandPool = app.commandPool;
    allocation.level = VK_COMMAND_BUFFER_LEVEL_PRIMARY;
    allocation.commandBufferCount = 1;

    VkCommandBuffer commandBuffer = VK_NULL_HANDLE;
    check(vkAllocateCommandBuffers(app.device, &allocation, &commandBuffer), "vkAllocateCommandBuffers");

    VkCommandBufferBeginInfo begin{VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO};
    begin.flags = VK_COMMAND_BUFFER_USAGE_ONE_TIME_SUBMIT_BIT;
    check(vkBeginCommandBuffer(commandBuffer, &begin), "vkBeginCommandBuffer");
    return commandBuffer;
}

VkResult submitAndWait(App& app, VkCommandBuffer commandBuffer)
{
    check(vkEndCommandBuffer(commandBuffer), "vkEndCommandBuffer");

    VkFenceCreateInfo fenceInfo{VK_STRUCTURE_TYPE_FENCE_CREATE_INFO};
    VkFence fence = VK_NULL_HANDLE;
    check(vkCreateFence(app.device, &fenceInfo, nullptr, &fence), "vkCreateFence");

    VkSubmitInfo submit{VK_STRUCTURE_TYPE_SUBMIT_INFO};
    submit.commandBufferCount = 1;
    submit.pCommandBuffers = &commandBuffer;
    VkResult result = vkQueueSubmit(app.queue, 1, &submit, fence);
    if (result == VK_SUCCESS)
    {
        result = vkWaitForFences(app.device, 1, &fence, VK_TRUE, UINT64_MAX);
    }

    vkDestroyFence(app.device, fence, nullptr);
    vkFreeCommandBuffers(app.device, app.commandPool, 1, &commandBuffer);
    return result;
}

Buffer allocateScratch(App& app, VkDeviceSize requiredSize, VkDeviceAddress& alignedAddress)
{
    const VkDeviceSize alignment = std::max<VkDeviceSize>(
        app.accelerationStructureProperties.minAccelerationStructureScratchOffsetAlignment, 1);
    Buffer scratch = createBuffer(app,
                                  requiredSize + alignment,
                                  VK_BUFFER_USAGE_STORAGE_BUFFER_BIT,
                                  VK_MEMORY_PROPERTY_DEVICE_LOCAL_BIT);
    alignedAddress = alignUp(scratch.address, alignment);
    return scratch;
}

void barrier(VkCommandBuffer commandBuffer,
             VkPipelineStageFlags2 destinationStage,
             VkAccessFlags2 destinationAccess)
{
    VkMemoryBarrier2 memoryBarrier{VK_STRUCTURE_TYPE_MEMORY_BARRIER_2};
    memoryBarrier.srcStageMask = VK_PIPELINE_STAGE_2_ACCELERATION_STRUCTURE_BUILD_BIT_KHR;
    memoryBarrier.srcAccessMask = VK_ACCESS_2_ACCELERATION_STRUCTURE_WRITE_BIT_KHR;
    memoryBarrier.dstStageMask = destinationStage;
    memoryBarrier.dstAccessMask = destinationAccess;

    VkDependencyInfo dependency{VK_STRUCTURE_TYPE_DEPENDENCY_INFO};
    dependency.memoryBarrierCount = 1;
    dependency.pMemoryBarriers = &memoryBarrier;
    vkCmdPipelineBarrier2(commandBuffer, &dependency);
}

void buildAccelerationStructures(App& app)
{
    const std::array<float, 12> vertices{
         0.0f,  0.5f, 0.0f, 0.0f,
        -0.5f, -0.5f, 0.0f, 0.0f,
         0.5f, -0.5f, 0.0f, 0.0f,
    };
    const std::array<uint32_t, 3> indices{0, 1, 2};

    app.vertexBuffer = createBuffer(app,
                                    sizeof(vertices),
                                    VK_BUFFER_USAGE_ACCELERATION_STRUCTURE_BUILD_INPUT_READ_ONLY_BIT_KHR |
                                        VK_BUFFER_USAGE_STORAGE_BUFFER_BIT,
                                    VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT | VK_MEMORY_PROPERTY_HOST_COHERENT_BIT);
    app.indexBuffer = createBuffer(app,
                                   sizeof(indices),
                                   VK_BUFFER_USAGE_ACCELERATION_STRUCTURE_BUILD_INPUT_READ_ONLY_BIT_KHR |
                                       VK_BUFFER_USAGE_STORAGE_BUFFER_BIT,
                                   VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT | VK_MEMORY_PROPERTY_HOST_COHERENT_BIT);
    upload(app.vertexBuffer, vertices.data(), sizeof(vertices));
    upload(app.indexBuffer, indices.data(), sizeof(indices));

    VkAccelerationStructureGeometryKHR blasGeometry{VK_STRUCTURE_TYPE_ACCELERATION_STRUCTURE_GEOMETRY_KHR};
    blasGeometry.geometryType = VK_GEOMETRY_TYPE_TRIANGLES_KHR;
    blasGeometry.flags = VK_GEOMETRY_OPAQUE_BIT_KHR;
    auto& triangles = blasGeometry.geometry.triangles;
    triangles.sType = VK_STRUCTURE_TYPE_ACCELERATION_STRUCTURE_GEOMETRY_TRIANGLES_DATA_KHR;
    triangles.vertexFormat = VK_FORMAT_R32G32B32_SFLOAT;
    triangles.vertexData.deviceAddress = app.vertexBuffer.address;
    triangles.vertexStride = 4 * sizeof(float);
    triangles.maxVertex = 2;
    triangles.indexType = VK_INDEX_TYPE_UINT32;
    triangles.indexData.deviceAddress = app.indexBuffer.address;

    VkAccelerationStructureBuildGeometryInfoKHR blasBuild{
        VK_STRUCTURE_TYPE_ACCELERATION_STRUCTURE_BUILD_GEOMETRY_INFO_KHR};
    blasBuild.type = VK_ACCELERATION_STRUCTURE_TYPE_BOTTOM_LEVEL_KHR;
    blasBuild.flags = VK_BUILD_ACCELERATION_STRUCTURE_PREFER_FAST_TRACE_BIT_KHR;
    blasBuild.mode = VK_BUILD_ACCELERATION_STRUCTURE_MODE_BUILD_KHR;
    blasBuild.geometryCount = 1;
    blasBuild.pGeometries = &blasGeometry;

    uint32_t primitiveCount = 1;
    VkAccelerationStructureBuildSizesInfoKHR blasSizes{
        VK_STRUCTURE_TYPE_ACCELERATION_STRUCTURE_BUILD_SIZES_INFO_KHR};
    app.fn.getAccelerationStructureBuildSizes(app.device,
                                              VK_ACCELERATION_STRUCTURE_BUILD_TYPE_DEVICE_KHR,
                                              &blasBuild,
                                              &primitiveCount,
                                              &blasSizes);

    app.blas.size = blasSizes.accelerationStructureSize;
    app.blas.storage = createBuffer(app,
                                    blasSizes.accelerationStructureSize,
                                    VK_BUFFER_USAGE_ACCELERATION_STRUCTURE_STORAGE_BIT_KHR,
                                    VK_MEMORY_PROPERTY_DEVICE_LOCAL_BIT);
    VkDeviceAddress blasScratchAddress = 0;
    app.blas.scratch = allocateScratch(app,
                                       std::max(blasSizes.buildScratchSize, blasSizes.updateScratchSize),
                                       blasScratchAddress);

    VkAccelerationStructureCreateInfoKHR blasCreate{VK_STRUCTURE_TYPE_ACCELERATION_STRUCTURE_CREATE_INFO_KHR};
    blasCreate.buffer = app.blas.storage.buffer;
    blasCreate.size = blasSizes.accelerationStructureSize;
    blasCreate.type = VK_ACCELERATION_STRUCTURE_TYPE_BOTTOM_LEVEL_KHR;
    check(app.fn.createAccelerationStructure(app.device, &blasCreate, nullptr, &app.blas.handle),
          "vkCreateAccelerationStructureKHR(BLAS)");

    VkAccelerationStructureDeviceAddressInfoKHR blasAddressInfo{
        VK_STRUCTURE_TYPE_ACCELERATION_STRUCTURE_DEVICE_ADDRESS_INFO_KHR};
    blasAddressInfo.accelerationStructure = app.blas.handle;
    app.blas.address = app.fn.getAccelerationStructureDeviceAddress(app.device, &blasAddressInfo);

    VkAccelerationStructureInstanceKHR instance{};
    instance.transform.matrix[0][0] = 1.0f;
    instance.transform.matrix[1][1] = 1.0f;
    instance.transform.matrix[2][2] = 1.0f;
    instance.mask = 0xFF;
    instance.flags = VK_GEOMETRY_INSTANCE_TRIANGLE_FACING_CULL_DISABLE_BIT_KHR;
    instance.accelerationStructureReference = app.blas.address;

    app.instanceBuffer = createBuffer(app,
                                      sizeof(instance),
                                      VK_BUFFER_USAGE_ACCELERATION_STRUCTURE_BUILD_INPUT_READ_ONLY_BIT_KHR,
                                      VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT | VK_MEMORY_PROPERTY_HOST_COHERENT_BIT);
    upload(app.instanceBuffer, &instance, sizeof(instance));

    VkAccelerationStructureGeometryKHR tlasGeometry{VK_STRUCTURE_TYPE_ACCELERATION_STRUCTURE_GEOMETRY_KHR};
    tlasGeometry.geometryType = VK_GEOMETRY_TYPE_INSTANCES_KHR;
    auto& instances = tlasGeometry.geometry.instances;
    instances.sType = VK_STRUCTURE_TYPE_ACCELERATION_STRUCTURE_GEOMETRY_INSTANCES_DATA_KHR;
    instances.arrayOfPointers = VK_FALSE;
    instances.data.deviceAddress = app.instanceBuffer.address;

    VkAccelerationStructureBuildGeometryInfoKHR tlasBuild{
        VK_STRUCTURE_TYPE_ACCELERATION_STRUCTURE_BUILD_GEOMETRY_INFO_KHR};
    tlasBuild.type = VK_ACCELERATION_STRUCTURE_TYPE_TOP_LEVEL_KHR;
    tlasBuild.flags = VK_BUILD_ACCELERATION_STRUCTURE_PREFER_FAST_TRACE_BIT_KHR;
    tlasBuild.mode = VK_BUILD_ACCELERATION_STRUCTURE_MODE_BUILD_KHR;
    tlasBuild.geometryCount = 1;
    tlasBuild.pGeometries = &tlasGeometry;

    VkAccelerationStructureBuildSizesInfoKHR tlasSizes{
        VK_STRUCTURE_TYPE_ACCELERATION_STRUCTURE_BUILD_SIZES_INFO_KHR};
    app.fn.getAccelerationStructureBuildSizes(app.device,
                                              VK_ACCELERATION_STRUCTURE_BUILD_TYPE_DEVICE_KHR,
                                              &tlasBuild,
                                              &primitiveCount,
                                              &tlasSizes);

    app.tlas.size = tlasSizes.accelerationStructureSize;
    app.tlas.storage = createBuffer(app,
                                    tlasSizes.accelerationStructureSize,
                                    VK_BUFFER_USAGE_ACCELERATION_STRUCTURE_STORAGE_BIT_KHR,
                                    VK_MEMORY_PROPERTY_DEVICE_LOCAL_BIT);
    VkDeviceAddress tlasScratchAddress = 0;
    app.tlas.scratch = allocateScratch(app,
                                       std::max(tlasSizes.buildScratchSize, tlasSizes.updateScratchSize),
                                       tlasScratchAddress);

    VkAccelerationStructureCreateInfoKHR tlasCreate{VK_STRUCTURE_TYPE_ACCELERATION_STRUCTURE_CREATE_INFO_KHR};
    tlasCreate.buffer = app.tlas.storage.buffer;
    tlasCreate.size = tlasSizes.accelerationStructureSize;
    tlasCreate.type = VK_ACCELERATION_STRUCTURE_TYPE_TOP_LEVEL_KHR;
    check(app.fn.createAccelerationStructure(app.device, &tlasCreate, nullptr, &app.tlas.handle),
          "vkCreateAccelerationStructureKHR(TLAS)");

    VkAccelerationStructureDeviceAddressInfoKHR tlasAddressInfo{
        VK_STRUCTURE_TYPE_ACCELERATION_STRUCTURE_DEVICE_ADDRESS_INFO_KHR};
    tlasAddressInfo.accelerationStructure = app.tlas.handle;
    app.tlas.address = app.fn.getAccelerationStructureDeviceAddress(app.device, &tlasAddressInfo);

    blasBuild.dstAccelerationStructure = app.blas.handle;
    blasBuild.scratchData.deviceAddress = blasScratchAddress;
    tlasBuild.dstAccelerationStructure = app.tlas.handle;
    tlasBuild.scratchData.deviceAddress = tlasScratchAddress;

    VkAccelerationStructureBuildRangeInfoKHR blasRange{};
    blasRange.primitiveCount = 1;
    const VkAccelerationStructureBuildRangeInfoKHR* blasRanges[] = {&blasRange};
    VkAccelerationStructureBuildRangeInfoKHR tlasRange{};
    tlasRange.primitiveCount = 1;
    const VkAccelerationStructureBuildRangeInfoKHR* tlasRanges[] = {&tlasRange};

    VkCommandBuffer commandBuffer = beginCommandBuffer(app);
    app.fn.cmdBuildAccelerationStructures(commandBuffer, 1, &blasBuild, blasRanges);
    barrier(commandBuffer,
            VK_PIPELINE_STAGE_2_ACCELERATION_STRUCTURE_BUILD_BIT_KHR,
            VK_ACCESS_2_ACCELERATION_STRUCTURE_READ_BIT_KHR);
    app.fn.cmdBuildAccelerationStructures(commandBuffer, 1, &tlasBuild, tlasRanges);
    barrier(commandBuffer,
            VK_PIPELINE_STAGE_2_COMPUTE_SHADER_BIT,
            VK_ACCESS_2_ACCELERATION_STRUCTURE_READ_BIT_KHR);

    const VkResult result = submitAndWait(app, commandBuffer);
    check(result, "AS build submission");
}

void createDescriptorHeapAndBuffers(App& app)
{
    const VkDeviceSize reserved = app.heapProperties.minResourceHeapReservedRange;
    constexpr VkDeviceSize asStride = sizeof(VkDeviceAddress);
    if (app.heapProperties.resourceHeapAlignment == 0)
    {
        fail("Descriptor heap alignment is zero");
    }

    const VkDeviceSize asOffset = alignUp(reserved, asStride);
    const VkDeviceSize usedSize = asOffset + asStride;
    const VkDeviceSize totalSize = alignUp(usedSize, app.heapProperties.resourceHeapAlignment);

    app.resourceHeap = createBuffer(app,
                                    totalSize,
                                    VK_BUFFER_USAGE_DESCRIPTOR_HEAP_BIT_EXT,
                                    VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT | VK_MEMORY_PROPERTY_HOST_COHERENT_BIT);

    if ((asOffset % alignof(VkDeviceAddress)) != 0 ||
        ((app.resourceHeap.address + asOffset) % alignof(VkDeviceAddress)) != 0 ||
        totalSize > app.heapProperties.maxResourceHeapSize ||
        reserved > totalSize ||
        (app.resourceHeap.address % app.heapProperties.resourceHeapAlignment) != 0 ||
        (app.tlas.address % 256) != 0 ||
        app.tlas.size == 0 ||
        (asOffset / asStride) > std::numeric_limits<uint32_t>::max())
    {
        fail("Invalid descriptor-heap size or alignment");
    }

    const uint32_t sceneHandle = static_cast<uint32_t>(asOffset / asStride);
    auto* sceneSlot = static_cast<std::byte*>(app.resourceHeap.mapped) + asOffset;
    std::memcpy(sceneSlot, &app.tlas.address, sizeof(app.tlas.address));

    struct Constants
    {
        uint32_t sceneHandle;
        uint32_t reserved;
    } constants{};
    static_assert(sizeof(Constants) == 8);
    constants.sceneHandle = sceneHandle;

    app.constantsBuffer = createBuffer(app,
                                       sizeof(Constants),
                                       VK_BUFFER_USAGE_UNIFORM_BUFFER_BIT,
                                       VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT | VK_MEMORY_PROPERTY_HOST_COHERENT_BIT);
    upload(app.constantsBuffer, &constants, sizeof(constants));

    const uint32_t pendingResult = std::numeric_limits<uint32_t>::max();
    app.resultBuffer = createBuffer(app,
                                    sizeof(pendingResult),
                                    VK_BUFFER_USAGE_STORAGE_BUFFER_BIT,
                                    VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT | VK_MEMORY_PROPERTY_HOST_COHERENT_BIT);
    upload(app.resultBuffer, &pendingResult, sizeof(pendingResult));

    const VkDeviceSize uniformAlignment = std::max<VkDeviceSize>(
        app.limits.minUniformBufferOffsetAlignment, 1);
    const VkDeviceSize storageAlignment = std::max<VkDeviceSize>(
        app.limits.minStorageBufferOffsetAlignment, 1);
    if (app.constantsBuffer.address == 0 ||
        (app.constantsBuffer.address % uniformAlignment) != 0 ||
        app.resultBuffer.address == 0 ||
        (app.resultBuffer.address % storageAlignment) != 0 ||
        app.limits.maxStorageBufferRange < sizeof(pendingResult) ||
        app.heapProperties.maxPushDataSize < 2 * sizeof(VkDeviceAddress))
    {
        fail("Invalid mapped-buffer alignment, range, or push-data capacity");
    }

    std::cout << std::hex
              << "resourceHeapAddress=0x" << app.resourceHeap.address
              << ", resourceHeapSize=0x" << app.resourceHeap.size << '\n'
              << "reservedRange=[0x0, 0x" << reserved << ")"
              << '\n'
              << "AS source=raw TLAS address + Slang OpConvertUToAccelerationStructureKHR"
              << ", shaderStride=0x" << asStride << '\n'
              << "sceneHandle=" << std::dec << sceneHandle << std::hex
              << ", sceneOffset=0x" << asOffset
              << ", TLAS=0x" << app.tlas.address << std::dec << '\n';
}

void createPipeline(App& app,
                    const std::filesystem::path& spirvPath)
{
    const std::vector<uint32_t> spirv = readSpirv(spirvPath);

    std::array<VkDescriptorSetAndBindingMappingEXT, 2> mappings{};
    mappings[0].sType = VK_STRUCTURE_TYPE_DESCRIPTOR_SET_AND_BINDING_MAPPING_EXT;
    mappings[0].descriptorSet = 0;
    mappings[0].firstBinding = 0;
    mappings[0].bindingCount = 1;
    mappings[0].resourceMask = VK_SPIRV_RESOURCE_TYPE_UNIFORM_BUFFER_BIT_EXT;
    mappings[0].source = VK_DESCRIPTOR_MAPPING_SOURCE_PUSH_ADDRESS_EXT;
    mappings[0].sourceData.pushAddressOffset = 0;

    mappings[1].sType = VK_STRUCTURE_TYPE_DESCRIPTOR_SET_AND_BINDING_MAPPING_EXT;
    mappings[1].descriptorSet = 0;
    mappings[1].firstBinding = 1;
    mappings[1].bindingCount = 1;
    mappings[1].resourceMask = VK_SPIRV_RESOURCE_TYPE_READ_WRITE_STORAGE_BUFFER_BIT_EXT;
    mappings[1].source = VK_DESCRIPTOR_MAPPING_SOURCE_PUSH_ADDRESS_EXT;
    mappings[1].sourceData.pushAddressOffset = sizeof(VkDeviceAddress);

    VkShaderDescriptorSetAndBindingMappingInfoEXT mappingInfo{
        VK_STRUCTURE_TYPE_SHADER_DESCRIPTOR_SET_AND_BINDING_MAPPING_INFO_EXT};
    mappingInfo.mappingCount = static_cast<uint32_t>(mappings.size());
    mappingInfo.pMappings = mappings.data();

    VkShaderModuleCreateInfo moduleInfo{VK_STRUCTURE_TYPE_SHADER_MODULE_CREATE_INFO};
    moduleInfo.codeSize = spirv.size() * sizeof(uint32_t);
    moduleInfo.pCode = spirv.data();

    mappingInfo.pNext = &moduleInfo;

    VkPipelineShaderStageCreateInfo stage{VK_STRUCTURE_TYPE_PIPELINE_SHADER_STAGE_CREATE_INFO};
    stage.pNext = &mappingInfo;
    stage.stage = VK_SHADER_STAGE_COMPUTE_BIT;
    stage.pName = "main";

    VkPipelineCreateFlags2CreateInfo flags{VK_STRUCTURE_TYPE_PIPELINE_CREATE_FLAGS_2_CREATE_INFO};
    flags.flags = VK_PIPELINE_CREATE_2_DESCRIPTOR_HEAP_BIT_EXT;

    VkComputePipelineCreateInfo pipelineInfo{VK_STRUCTURE_TYPE_COMPUTE_PIPELINE_CREATE_INFO};
    pipelineInfo.pNext = &flags;
    pipelineInfo.stage = stage;
    check(vkCreateComputePipelines(app.device,
                                   VK_NULL_HANDLE,
                                   1,
                                   &pipelineInfo,
                                   nullptr,
                                   &app.pipeline),
          "vkCreateComputePipelines");
}

VkResult dispatch(App& app)
{
    VkCommandBuffer commandBuffer = beginCommandBuffer(app);

    VkBindHeapInfoEXT resourceBind{VK_STRUCTURE_TYPE_BIND_HEAP_INFO_EXT};
    resourceBind.heapRange = {app.resourceHeap.address, app.resourceHeap.size};
    resourceBind.reservedRangeSize = app.heapProperties.minResourceHeapReservedRange;
    app.fn.cmdBindResourceHeap(commandBuffer, &resourceBind);

    vkCmdBindPipeline(commandBuffer, VK_PIPELINE_BIND_POINT_COMPUTE, app.pipeline);

    const std::array<VkDeviceAddress, 2> pushAddresses{
        app.constantsBuffer.address,
        app.resultBuffer.address,
    };
    VkPushDataInfoEXT pushData{VK_STRUCTURE_TYPE_PUSH_DATA_INFO_EXT};
    pushData.data = {pushAddresses.data(), sizeof(pushAddresses)};
    app.fn.cmdPushData(commandBuffer, &pushData);
    vkCmdDispatch(commandBuffer, 1, 1, 1);

    VkMemoryBarrier2 resultBarrier{VK_STRUCTURE_TYPE_MEMORY_BARRIER_2};
    resultBarrier.srcStageMask = VK_PIPELINE_STAGE_2_COMPUTE_SHADER_BIT;
    resultBarrier.srcAccessMask = VK_ACCESS_2_SHADER_STORAGE_WRITE_BIT;
    resultBarrier.dstStageMask = VK_PIPELINE_STAGE_2_HOST_BIT;
    resultBarrier.dstAccessMask = VK_ACCESS_2_HOST_READ_BIT;
    VkDependencyInfo resultDependency{VK_STRUCTURE_TYPE_DEPENDENCY_INFO};
    resultDependency.memoryBarrierCount = 1;
    resultDependency.pMemoryBarriers = &resultBarrier;
    vkCmdPipelineBarrier2(commandBuffer, &resultDependency);

    return submitAndWait(app, commandBuffer);
}

void destroyBuffer(App& app, Buffer& buffer)
{
    if (buffer.mapped)
    {
        vkUnmapMemory(app.device, buffer.memory);
    }
    if (buffer.buffer)
    {
        vkDestroyBuffer(app.device, buffer.buffer, nullptr);
    }
    if (buffer.memory)
    {
        vkFreeMemory(app.device, buffer.memory, nullptr);
    }
    buffer = {};
}

void cleanup(App& app)
{
    if (app.device)
    {
        if (!app.deviceLost)
        {
            vkDeviceWaitIdle(app.device);
        }
        if (app.pipeline)
        {
            vkDestroyPipeline(app.device, app.pipeline, nullptr);
        }
        if (app.tlas.handle)
        {
            app.fn.destroyAccelerationStructure(app.device, app.tlas.handle, nullptr);
        }
        if (app.blas.handle)
        {
            app.fn.destroyAccelerationStructure(app.device, app.blas.handle, nullptr);
        }
        destroyBuffer(app, app.constantsBuffer);
        destroyBuffer(app, app.resultBuffer);
        destroyBuffer(app, app.resourceHeap);
        destroyBuffer(app, app.tlas.scratch);
        destroyBuffer(app, app.tlas.storage);
        destroyBuffer(app, app.blas.scratch);
        destroyBuffer(app, app.blas.storage);
        destroyBuffer(app, app.instanceBuffer);
        destroyBuffer(app, app.indexBuffer);
        destroyBuffer(app, app.vertexBuffer);

        if (app.commandPool)
        {
            vkDestroyCommandPool(app.device, app.commandPool, nullptr);
        }
        vkDestroyDevice(app.device, nullptr);
        app.device = VK_NULL_HANDLE;
    }

    if (app.instance)
    {
        vkDestroyInstance(app.instance, nullptr);
        app.instance = VK_NULL_HANDLE;
    }
}

} // namespace

int main(int, char** argv)
{
    App app{};
    try
    {
        const std::filesystem::path executableDirectory =
            std::filesystem::path(argv[0]).parent_path();
        const std::filesystem::path spirvPath = executableDirectory / "ray_query.spv";

        initializeVulkan(app);
        buildAccelerationStructures(app);
        createDescriptorHeapAndBuffers(app);
        createPipeline(app, spirvPath);

        std::cout << "Dispatching Slang descriptor-heap AS ray query...\n";
        const VkResult result = dispatch(app);
        if (result == VK_ERROR_DEVICE_LOST)
        {
            app.deviceLost = true;
            std::cout << "REPRODUCED: VK_ERROR_DEVICE_LOST\n";
            cleanup(app);
            return 2;
        }

        check(result, "dispatch submission");
        uint32_t rayQueryResult = 0;
        std::memcpy(&rayQueryResult, app.resultBuffer.mapped, sizeof(rayQueryResult));
        if (rayQueryResult != 1)
        {
            fail("Ray query did not report the expected triangle hit; result=" +
                 std::to_string(rayQueryResult));
        }
        std::cout << "No device loss; the ray query reported the expected triangle hit.\n";
        cleanup(app);
        return 0;
    }
    catch (const std::exception& error)
    {
        std::cerr << "ERROR: " << error.what() << '\n';
        cleanup(app);
        return 1;
    }
}
