using System.Numerics;

using Loom.Core;
using Loom.Vulkan;
using Silk.NET.Maths;
using Silk.NET.Windowing;

namespace Loom.Sandbox;

internal static class Program
{
	private static void Main()
	{
		var options = WindowOptions.DefaultVulkan with
		{
			Size = new Vector2D<int>(1280, 720),
			Title = "Loom - M3: graph-driven (offscreen -> blit)",
		};

		using var window = Window.Create(options);
		window.Initialize();

		if (window.VkSurface is null)
			throw new InvalidOperationException("The windowing platform did not expose a Vulkan surface.");

		using var context = new VulkanContext(window.VkSurface, enableValidation: true);

		var shaderDir = Path.Combine(AppContext.BaseDirectory, "shaders");
		var techniques = new Dictionary<string, TechniqueTemplate>
		{
			// A gradient written from SV_VertexID (no input).
			["fullscreen"] = new(ShaderPath(shaderDir, "fullscreen.spv"), "vertexMain", "fragmentMain"),
			// A fullscreen sample of one input (combined image sampler at set 0/binding 0).
			["blit"] = new(ShaderPath(shaderDir, "blit.spv"), "vertexMain", "fragmentMain", SamplesInput: true),
			// Geometry: reads a Loom.Core.Vertex buffer (position/normal/uv).
			["mesh"] = new(ShaderPath(shaderDir, "mesh.spv"), "vertexMain", "fragmentMain", HasVertexInput: true),
		};

		// A single triangle in clip space (M5.1a — camera arrives in 1b). Constant normal; UVs drive the colour.
		Vertex[] vertices =
		[
			new(new Vector3(-0.6f, -0.6f, 0f), new Vector3(0, 0, 1), new Vector2(0f, 0f)),
			new(new Vector3(0.6f, -0.6f, 0f), new Vector3(0, 0, 1), new Vector2(1f, 0f)),
			new(new Vector3(0f, 0.6f, 0f), new Vector3(0, 0, 1), new Vector2(0.5f, 1f)),
		];
		uint[] indices = [0, 1, 2];
		Mesh triangle = default; // assigned after the executor exists; the scene pass reads it at draw time

		// M3d reference graph: pass 1 renders a gradient into an offscreen Rgba16F target; pass 2 samples that
		// target (nearest) onto the backbuffer. The engine derives the order, the COLOR_ATTACHMENT->SHADER_READ
		// transition on the offscreen target between the passes, the load/store ops, and the present transition.
		var graph = RenderGraph.Define(g =>
		{
			var screen = g.Backbuffer;
			var offscreen = screen.Domain.Color("offscreen", Format.Rgba16F);

			g.Pass("scene")
				.Writes(offscreen)
				.Draw(ctx => ctx.Draw(triangle, "mesh"));

			g.Pass("present")
				.Samples(offscreen)
				.Writes(screen)
				.Draw(ctx => ctx.FullScreen("blit", offscreen));

			g.Present(screen);
		});

		using var executor = new GraphExecutor(context, graph, techniques, window.FramebufferSize);
		triangle = executor.CreateMesh(vertices, indices); // upload geometry before the first frame

		Console.WriteLine($"Loom M3 - graph-driven render on {context.DeviceName}. Close the window to exit.");

		// Headless smoke mode: LOOM_SMOKE_FRAMES=N renders N frames then closes (for automated checks).
		// Capture mode: LOOM_CAPTURE=path grabs a warmed-up frame to a PNG, then exits.
		var smokeFrames = int.TryParse(Environment.GetEnvironmentVariable("LOOM_SMOKE_FRAMES"), out var n) ? n : 0;
		var capturePath = Environment.GetEnvironmentVariable("LOOM_CAPTURE");
		var frame = 0;

		window.FramebufferResize += size => executor.OnResize(size);
		window.Render += delta =>
		{
			frame++;
			if (capturePath is not null && frame == 10)
				executor.RequestCapture(capturePath);

			executor.DrawFrame(delta);

			if (capturePath is not null && frame >= 12)
				window.Close();
			else if (smokeFrames > 0 && frame >= smokeFrames)
				window.Close();
		};

		window.Run();
		executor.WaitIdle(); // Drain the GPU before the using-disposals tear down Vulkan objects.
	}

	private static string ShaderPath(string dir, string file)
	{
		var path = Path.Combine(dir, file);
		if (!File.Exists(path))
			throw new FileNotFoundException($"Compiled shader not found: {path}. Did Loom.Shaders build?");
		return path;
	}
}
