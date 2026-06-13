using Silk.NET.Maths;
using Silk.NET.Windowing;

using Loom.Vulkan;

namespace Loom.Sandbox;

internal static class Program
{
	private static void Main()
	{
		var options = WindowOptions.DefaultVulkan with
		{
			Size = new Vector2D<int>(1280, 720),
			Title = "Loom - M0: clear to colour",
		};

		using var window = Window.Create(options);
		window.Initialize();

		if (window.VkSurface is null)
			throw new InvalidOperationException("The windowing platform did not expose a Vulkan surface.");

		using var context = new VulkanContext(window.VkSurface, enableValidation: true);

		var shaderPath = Path.Combine(AppContext.BaseDirectory, "shaders", "fullscreen.spv");
		if (!File.Exists(shaderPath))
			throw new FileNotFoundException($"Compiled shader not found: {shaderPath}. Did Loom.Shaders build?");

		using var renderer = new Renderer(context, window.FramebufferSize, shaderPath);

		Console.WriteLine($"Loom M1 - fullscreen triangle on {context.DeviceName}. Close the window to exit.");

		// Headless smoke mode: LOOM_SMOKE_FRAMES=N renders N frames then closes (for automated checks).
		var smokeFrames = int.TryParse(Environment.GetEnvironmentVariable("LOOM_SMOKE_FRAMES"), out var n) ? n : 0;
		var frame = 0;

		window.FramebufferResize += size => renderer.OnResize(size);
		window.Render += delta =>
		{
			renderer.DrawFrame(delta);
			if (smokeFrames > 0 && ++frame >= smokeFrames)
				window.Close();
		};

		window.Run();
		renderer.WaitIdle(); // Drain the GPU before the using-disposals tear down Vulkan objects.
	}
}
