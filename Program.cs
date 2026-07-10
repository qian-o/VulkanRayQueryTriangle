using Silk.NET.Maths;
using Silk.NET.Windowing;
using VulkanRayQueryTriangle;

// Entry point: opens a Vulkan window, builds the scene (a single triangle in a TLAS),
// and renders it every frame with an inline ray-query compute shader.

const uint initialWidth = 1280;
const uint initialHeight = 720;

WindowOptions options = WindowOptions.DefaultVulkan with
{
    Title = "Vulkan RayQuery Triangle",
    Size = new Vector2D<int>((int)initialWidth, (int)initialHeight)
};

using IWindow window = Window.Create(options);
window.Initialize();

if (window.VkSurface is null)
{
    throw new InvalidOperationException("Windowing platform does not expose a Vulkan surface.");
}

VulkanContext context = new(window, useValidation: true);

// A generous resource-heap capacity; the demo only uses a couple of descriptors.
DescriptorHeap descriptorHeap = DescriptorHeap.Create(context, bufferCapacity: 1024, imageCapacity: 1024);

AccelerationStructures accelerationStructures = new(context);

uint sceneHandle = descriptorHeap.WriteAccelerationStructure(
    accelerationStructures.TlasDeviceAddress,
    accelerationStructures.TlasSize);

// The window is already initialized, so create the renderer immediately rather than
// waiting on the Load event (which has already fired).
RayQueryRenderer renderer = new(
    context,
    descriptorHeap,
    accelerationStructures,
    sceneHandle,
    (uint)window.FramebufferSize.X,
    (uint)window.FramebufferSize.Y);

window.Render += _ =>
{
    if (window.FramebufferSize.X is 0 || window.FramebufferSize.Y is 0)
    {
        return;
    }

    renderer.DrawFrame();
};

window.Closing += renderer.WaitIdle;

window.Run();

renderer.Dispose();
accelerationStructures.Dispose();
descriptorHeap.Dispose();
context.Dispose();
