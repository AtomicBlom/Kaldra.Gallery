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

		var (vertices, indices) = BuildCube();
		Mesh cube = default; // assigned after the executor exists; the scene pass reads it at draw time

		// M5.1b graph: the "scene" pass draws the cube into an offscreen colour target with depth testing;
		// "present" samples that target (nearest) onto the backbuffer. The engine derives the order, the
		// COLOR_ATTACHMENT->SHADER_READ flip on the offscreen target, the depth attachment, the load/store
		// ops, and the present transition.
		var graph = RenderGraph.Define(g =>
		{
			var screen = g.Backbuffer;
			var offscreen = screen.Domain.Color("offscreen", Format.Rgba16F);
			var depth = screen.Domain.Depth("depth", Format.Depth32F);

			g.Pass("scene")
				.Writes(offscreen)
				.WritesDepth(depth)
				.Draw(ctx => ctx.Draw(cube, "mesh"));

			g.Pass("present")
				.Samples(offscreen)
				.Writes(screen)
				.Draw(ctx => ctx.FullScreen("blit", offscreen));

			g.Present(screen);
		});

		using var executor = new GraphExecutor(context, graph, techniques, window.FramebufferSize);
		cube = executor.CreateMesh(vertices, indices); // upload geometry before the first frame

		Console.WriteLine($"Loom M5 - graph-driven 3D render on {context.DeviceName}. Close the window to exit.");

		// Headless smoke mode: LOOM_SMOKE_FRAMES=N renders N frames then closes (for automated checks).
		// Capture mode: LOOM_CAPTURE=path grabs a warmed-up frame to a PNG, then exits.
		var smokeFrames = int.TryParse(Environment.GetEnvironmentVariable("LOOM_SMOKE_FRAMES"), out var n) ? n : 0;
		var capturePath = Environment.GetEnvironmentVariable("LOOM_CAPTURE");
		var frame = 0;
		var elapsed = 0f;

		window.FramebufferResize += size => executor.OnResize(size);
		window.Render += delta =>
		{
			frame++;
			elapsed += (float)delta;

			// Orbit the camera around the cube. Host-side per-frame data, handed to the graph via ctx.Camera.
			var eye = new Vector3(MathF.Sin(elapsed) * 2.5f, 1.6f, MathF.Cos(elapsed) * 2.5f);
			var view = Matrix4x4.CreateLookAt(eye, Vector3.Zero, Vector3.UnitY);
			var aspect = window.FramebufferSize.Y == 0 ? 1f : (float)window.FramebufferSize.X / window.FramebufferSize.Y;
			var projection = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 3f, aspect, 0.1f, 100f);
			projection.M22 *= -1f; // Vulkan clip space has +Y down — flip it
			executor.Camera = new Camera(view, projection);

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

	// A unit cube centred at the origin, 24 vertices (4 per face) so each face carries its own flat normal,
	// 36 indices (two triangles per face). The mesh technique colours by normal, so each face reads distinctly.
	private static (Vertex[] Vertices, uint[] Indices) BuildCube()
	{
		// (normal, and the four corners of that face in CCW order viewed from outside)
		(Vector3 Normal, Vector3 A, Vector3 B, Vector3 C, Vector3 D)[] faces =
		[
			(new(0, 0, 1), new(-0.5f, -0.5f, 0.5f), new(0.5f, -0.5f, 0.5f), new(0.5f, 0.5f, 0.5f), new(-0.5f, 0.5f, 0.5f)),     // +Z
			(new(0, 0, -1), new(0.5f, -0.5f, -0.5f), new(-0.5f, -0.5f, -0.5f), new(-0.5f, 0.5f, -0.5f), new(0.5f, 0.5f, -0.5f)), // -Z
			(new(1, 0, 0), new(0.5f, -0.5f, 0.5f), new(0.5f, -0.5f, -0.5f), new(0.5f, 0.5f, -0.5f), new(0.5f, 0.5f, 0.5f)),      // +X
			(new(-1, 0, 0), new(-0.5f, -0.5f, -0.5f), new(-0.5f, -0.5f, 0.5f), new(-0.5f, 0.5f, 0.5f), new(-0.5f, 0.5f, -0.5f)), // -X
			(new(0, 1, 0), new(-0.5f, 0.5f, 0.5f), new(0.5f, 0.5f, 0.5f), new(0.5f, 0.5f, -0.5f), new(-0.5f, 0.5f, -0.5f)),      // +Y
			(new(0, -1, 0), new(-0.5f, -0.5f, -0.5f), new(0.5f, -0.5f, -0.5f), new(0.5f, -0.5f, 0.5f), new(-0.5f, -0.5f, 0.5f)), // -Y
		];

		var vertices = new List<Vertex>(24);
		var indices = new List<uint>(36);
		foreach (var (normal, a, b, c, d) in faces)
		{
			var v = (uint)vertices.Count;
			vertices.Add(new Vertex(a, normal, new Vector2(0, 0)));
			vertices.Add(new Vertex(b, normal, new Vector2(1, 0)));
			vertices.Add(new Vertex(c, normal, new Vector2(1, 1)));
			vertices.Add(new Vertex(d, normal, new Vector2(0, 1)));
			indices.AddRange([v, v + 1, v + 2, v, v + 2, v + 3]);
		}

		return ([.. vertices], [.. indices]);
	}
}
