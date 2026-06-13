using System.Numerics;

using Loom.Core;
using Loom.Vulkan;
using Silk.NET.Input;
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
			Title = "Loom - M5: deferred + transparency + bloom + pixelation",
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
			["blit"] = new(ShaderPath(shaderDir, "blit.spv"), "vertexMain", "fragmentMain", SampleCount: 1),
			// Geometry: reads a Loom.Core.Vertex buffer (position/normal/uv).
			["mesh"] = new(ShaderPath(shaderDir, "mesh.spv"), "vertexMain", "fragmentMain", HasVertexInput: true),
			// G-buffer geometry: writes albedo + encoded normal (MRT) + depth.
			["gbuffer"] = new(ShaderPath(shaderDir, "gbuffer.spv"), "vertexMain", "fragmentMain", HasVertexInput: true),
			// Deferred lighting: samples albedo (0) + normal (1), writes the lit result.
			["lighting"] = new(ShaderPath(shaderDir, "lighting.spv"), "vertexMain", "fragmentMain", SampleCount: 2),
			// Transparent forward: alpha-blends a tint onto the lit target, depth-tested read-only.
			["transparent"] = new(ShaderPath(shaderDir, "transparent.spv"), "vertexMain", "fragmentMain", HasVertexInput: true, Blend: BlendMode.AlphaOver),
			// Bloom sub-graph (§7.1). All sample with a linear filter (smooth glow); the upsample additively
			// accumulates. "bloom.down" and "bloom.up" share one shader — only their blend/edge intent differs.
			["bloom.prefilter"] = new(ShaderPath(shaderDir, "bloom_prefilter.spv"), "vertexMain", "fragmentMain", SampleCount: 1, LinearFilter: true),
			["bloom.down"] = new(ShaderPath(shaderDir, "bloom_sample.spv"), "vertexMain", "fragmentMain", SampleCount: 1, LinearFilter: true),
			["bloom.up"] = new(ShaderPath(shaderDir, "bloom_sample.spv"), "vertexMain", "fragmentMain", SampleCount: 1, Blend: BlendMode.Additive, LinearFilter: true),
			["bloom.composite"] = new(ShaderPath(shaderDir, "bloom_composite.spv"), "vertexMain", "fragmentMain", SampleCount: 2, LinearFilter: true),
			// JFA hover-outline (§7.4/§7.5). "id" writes the cube's object id; seed/jfa/resolve grow a glowing
			// border from the hovered silhouette. seed + jfa take a fragment push (hovered id, step + dims).
			["id"] = new(ShaderPath(shaderDir, "id.spv"), "vertexMain", "fragmentMain", HasVertexInput: true),
			["outline.seed"] = new(ShaderPath(shaderDir, "outline_seed.spv"), "vertexMain", "fragmentMain", SampleCount: 1, HasFragmentPush: true),
			["outline.jfa"] = new(ShaderPath(shaderDir, "outline_jfa.spv"), "vertexMain", "fragmentMain", SampleCount: 1, HasFragmentPush: true),
			["outline.resolve"] = new(ShaderPath(shaderDir, "outline_resolve.spv"), "vertexMain", "fragmentMain", SampleCount: 2),
		};

		var (vertices, indices) = BuildCube();
		Mesh cube = default; // assigned after the executor exists; the scene pass reads it at draw time

		// The reference frame (Planning.md §6). The whole 3D world — geometry, deferred lighting, transparency,
		// bloom — renders into a small WORLD domain (320×180); the terminal "upscale" pass nearest-samples it
		// to the native backbuffer. That nearest upscale IS the §9 pixelation: it is not a post filter, it is
		// the consequence of rendering everything at low resolution and blowing each world texel up to a hard
		// pixel block, so the pixel grid stays consistent through lighting and bloom. The engine derives pass
		// order, the COLOR_ATTACHMENT↔SHADER_READ flips, depth states, load/store, transient aliasing, present.
		var graph = RenderGraph.Define(g =>
		{
			var screen = g.Backbuffer;
			var world = g.Domain("world", 320, 180);
			var albedo = world.Color("albedo", Format.Rgba8Srgb);
			var normal = world.Color("normal", Format.Rgba16F);
			var depth = world.Depth("depth", Format.Depth32F);
			var lit = world.Color("lit", Format.Rgba16F);

			g.Pass("gbuffer")
				.Writes(albedo, normal)
				.WritesDepth(depth)
				.Draw(ctx => ctx.Draw(cube, "gbuffer"));

			g.Pass("lighting")
				.Samples(albedo, normal)
				.Writes(lit)
				.Draw(ctx => ctx.FullScreen("lighting", albedo, normal));

			// Transparent forward pass: re-draws the cube as glass, accumulating a tint onto the lit target
			// (.Blends → LoadOp.Load + alpha blend) while testing against the opaque depth read-only
			// (.ReadsDepth → DEPTH_READ_ONLY, depth writes off). The engine derives both from these edges.
			g.Pass("transparent")
				.Blends(lit)
				.ReadsDepth(depth)
				.Draw(ctx => ctx.Draw(cube, "transparent"));

			// Hover outline (§7.4 picking + §7.5 JFA). "id" writes the cube's object id; "outline.seed" marks
			// the hovered object's pixels; a jump-flood ping-pong (step 4→2→1) propagates the nearest seed
			// coordinate; "outline.resolve" turns nearest-seed distance into an emissive ring composited over
			// lit. Two physical buffers serve the whole flood — the (read,write) handle swap is the §8 ping-pong,
			// and the graph derives the strict serialization + a barrier between each level from the edges.
			var ids = world.Color("object_id", Format.R16F);
			var seedA = world.Color("outline_a", Format.Rg16F);
			var seedB = world.Color("outline_b", Format.Rg16F);
			var outlined = world.Color("lit_outlined", Format.Rgba16F);

			g.Pass("id")
				.Writes(ids)
				.Draw(ctx => ctx.Draw(cube, "id"));

			g.Pass("outline.seed")
				.Samples(ids)
				.Writes(seedA)
				.Draw(ctx => ctx.FullScreen("outline.seed", [(float)ctx.HoverId], ids));

			var (read, write) = (seedA, seedB);
			const int outlineReach = 4; // a 2-3px ring only needs distance out to its own width: steps 4→2→1
			for (var step = outlineReach; step >= 1; step >>= 1)
			{
				var src = read; // capture per-iteration so the closure follows this step's handle (not the loop var)
				float jump = step;
				g.Pass($"outline.jfa{step}")
					.Samples(src)
					.Writes(write)
					.Draw(ctx => ctx.FullScreen("outline.jfa", [jump, world.Width, world.Height], src));
				(read, write) = (write, read); // swap; the graph follows the handles
			}

			var flood = read; // holds the final flood after the swaps
			g.Pass("outline.resolve")
				.Samples(flood, lit)
				.Writes(outlined)
				.Draw(ctx => ctx.FullScreen("outline.resolve", flood, lit));

			// Bloom the OUTLINED image, still in the world domain so the glow stays chunky and pixel-consistent
			// (§7.1/§9). Blooming after the outline is what makes the hover border glow (§7.5). The sub-graph
			// derives its mip-chain serialization, barriers, and (M5.5b) aliasing of dead memory.
			var glow = g.Bloom(outlined, threshold: 1.0f, mips: 5);

			// Cross domains: nearest-neighbour upscale from the low-res world to the native screen (§6/§9).
			g.Pass("upscale")
				.Samples(glow)
				.Writes(screen)
				.Draw(ctx => ctx.FullScreen("blit", glow));

			g.Present(screen);
		});

		using var executor = new GraphExecutor(context, graph, techniques, window.FramebufferSize);
		cube = executor.CreateMesh(vertices, indices); // upload geometry before the first frame

		Console.WriteLine($"Loom M5 - graph-driven 3D render on {context.DeviceName}. Hover the cube for a glowing outline; close the window to exit.");

		// Mouse, for the hover test that drives the outline (the cube is object id 1).
		using var input = window.CreateInput();
		var mouse = input.Mice.Count > 0 ? input.Mice[0] : null;

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

			// Hover: in headless capture force the cube hovered (no cursor) so the outline shows; otherwise
			// test the cursor against the cube's projected screen bounds. Id 1 = the cube, 0 = nothing.
			var viewProjection = view * projection;
			var size = new Vector2(window.FramebufferSize.X, window.FramebufferSize.Y);
			executor.HoverId = capturePath is not null
				? 1u
				: mouse is not null && CursorOverCube(mouse.Position, viewProjection, size) ? 1u : 0u;

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

	// Is the cursor over the cube? Project its eight corners with the same view-projection the GPU uses and
	// test the cursor against their 2D screen bounding box — a cheap stand-in for true id-buffer readback
	// (§7.4), which is the M6-faithful way to know what's hovered. The transpose matches the shader's
	// column-vector mul() against the row-major System.Numerics matrix the renderer uploads.
	private static bool CursorOverCube(Vector2 cursor, Matrix4x4 viewProjection, Vector2 screen)
	{
		var clipFromWorld = Matrix4x4.Transpose(viewProjection);
		var min = new Vector2(float.MaxValue);
		var max = new Vector2(float.MinValue);
		for (var i = 0; i < 8; i++)
		{
			var corner = new Vector3((i & 1) == 0 ? -0.5f : 0.5f, (i & 2) == 0 ? -0.5f : 0.5f, (i & 4) == 0 ? -0.5f : 0.5f);
			var clip = Vector4.Transform(new Vector4(corner, 1f), clipFromWorld);
			if (clip.W <= 0f)
				return false; // a corner behind the camera — skip the test this frame
			var ndc = new Vector2(clip.X, clip.Y) / clip.W;
			var px = new Vector2((ndc.X * 0.5f + 0.5f) * screen.X, (ndc.Y * 0.5f + 0.5f) * screen.Y);
			min = Vector2.Min(min, px);
			max = Vector2.Max(max, px);
		}
		return cursor.X >= min.X && cursor.X <= max.X && cursor.Y >= min.Y && cursor.Y <= max.Y;
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
