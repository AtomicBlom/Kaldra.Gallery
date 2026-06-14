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
			// G-buffer geometry: writes albedo + encoded normal + world position (MRT) + depth.
			["gbuffer"] = new(ShaderPath(shaderDir, "gbuffer.spv"), "vertexMain", "fragmentMain", HasVertexInput: true),
			// Depth-only shadow caster: drawn from the sun's view into the shadow map (no colour output).
			["depthonly"] = new(ShaderPath(shaderDir, "depthonly.spv"), "vertexMain", "fragmentMain", HasVertexInput: true),
			// Deferred lighting: samples albedo (0) + normal (1) + position (2) + shadow map (3); the sun's
			// view-projection rides the fragment push for the shadow test. Writes the lit result.
			["lighting"] = new(ShaderPath(shaderDir, "lighting.spv"), "vertexMain", "fragmentMain", SampleCount: 4, HasFragmentPush: true),
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
			// Auto-exposure (§12 M5 "Persistent + Compute"): a compute reduction samples lit (0) + read-writes
			// the 1×1 persistent exposure storage image (1); the tonemap samples glow (0) + exposure (1).
			// slangc names a lone compute entry point "main" in the SPIR-V (it keeps explicit names only to
			// disambiguate multiple entries, as the graphics modules have); the compute entry rides FragmentEntry.
			["exposure"] = new(ShaderPath(shaderDir, "exposure.spv"), "main", "main", SampleCount: 1, IsCompute: true, StorageCount: 1, HasFragmentPush: true),
			["tonemap"] = new(ShaderPath(shaderDir, "tonemap.spv"), "vertexMain", "fragmentMain", SampleCount: 2),
		};

		var (cubeVerts, cubeIndices) = BuildCube();
		var (planeVerts, planeIndices) = BuildPlane();
		Mesh cube = default;  // assigned after the executor exists; the scene passes read them at draw time
		Mesh plane = default; // a ground plane to catch the cube's shadow

		// The reference frame (Planning.md §6). The whole 3D world — geometry, deferred lighting, transparency,
		// bloom — renders into a small WORLD domain (320×180); the terminal "upscale" pass nearest-samples it
		// to the native backbuffer. That nearest upscale IS the §9 pixelation: it is not a post filter, it is
		// the consequence of rendering everything at low resolution and blowing each world texel up to a hard
		// pixel block, so the pixel grid stays consistent through lighting and bloom. The engine derives pass
		// order, the COLOR_ATTACHMENT↔SHADER_READ flips, depth states, load/store, transient aliasing, present.
		// Debug viz (§7.6): LOOM_DEBUG=<name> blits a named intermediate to the screen instead of the final
		// image, so any stage of the frame is inspectable. A build-time choice (feature toggles are control
		// flow, §8) — set the env var and re-run. Names: albedo, normal, position, depth, lit, outlined,
		// glow, shadow.
		var debugView = Environment.GetEnvironmentVariable("LOOM_DEBUG");

		var graph = RenderGraph.Define(g =>
		{
			var screen = g.Backbuffer;
			var world = g.Domain("world", 320, 180);
			var albedo = world.Color("albedo", Format.Rgba8Srgb);
			var normal = world.Color("normal", Format.Rgba16F);
			var position = world.Color("position", Format.Rgba16F); // world position, for the shadow test
			var depth = world.Depth("depth", Format.Depth32F);
			var lit = world.Color("lit", Format.Rgba16F);

			// Shadows (§7.2): a depth-only pass renders the casters from the SUN's view (the view: param) into
			// a shadow map; lighting projects each pixel into that light space and compares. Same draw, a
			// different view — the novel primitive. The map lives in its own high-res domain.
			var sun = g.Domain("sun_shadow", 1024, 1024);
			var shadowMap = sun.Depth("sun_depth", Format.Depth32F);

			g.Pass("shadow")
				.WritesDepth(shadowMap)
				.Draw(ctx =>
				{
					ctx.Draw(cube, "depthonly", ctx.SunView);
					ctx.Draw(plane, "depthonly", ctx.SunView);
				});

			g.Pass("gbuffer")
				.Writes(albedo, normal, position)
				.WritesDepth(depth)
				.Draw(ctx =>
				{
					ctx.Draw(cube, "gbuffer");
					ctx.Draw(plane, "gbuffer");
				});

			g.Pass("lighting")
				.Samples(albedo, normal, position, shadowMap)
				.Writes(lit)
				.Draw(ctx => ctx.FullScreen("lighting", Mat(ctx.SunView.ViewProjection), albedo, normal, position, shadowMap));

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

			// Picking (§7.4): read the id buffer back to the CPU. The host samples it at the cursor to decide
			// which object is hovered — real GPU→CPU picking, replacing the projected-bbox approximation.
			g.Readback(ids);

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

			// Auto-exposure (§12 M5 "Persistent + Compute"). A compute pass meters the lit scene's luminance
			// and read-writes a 1×1 PERSISTENT exposure value — persistent, so it carries across frames and the
			// adaptation is temporal. The tonemap then reads it. The engine derives the GENERAL-layout storage
			// transition, the compute→fragment hand-off barrier, and (in the backend) the cross-frame layout.
			var meter = g.Domain("meter", 1, 1);
			var exposure = meter.Persistent("exposure", Format.R16F);
			var graded = world.Color("graded", Format.Rgba16F);

			g.Compute("exposure")
				.Samples(lit)
				.ReadWrites(exposure)
				.Dispatch(ctx => ctx.Dispatch("exposure", (1, 1, 1), [8f * ctx.DeltaTime], [lit], [exposure]));

			g.Pass("tonemap")
				.Samples(glow, exposure)
				.Writes(graded)
				.Draw(ctx => ctx.FullScreen("tonemap", glow, exposure));

			// Cross domains: nearest-neighbour upscale from the low-res world to the native screen (§6/§9).
			// In debug-view mode the terminal pass blits the chosen intermediate instead of the graded image —
			// every stage is a named handle, so inspecting one is just pointing the final blit at it (§7.6).
			var present = graded;
			if (debugView is not null)
			{
				var views = new Dictionary<string, ResourceHandle>(StringComparer.OrdinalIgnoreCase)
				{
					["albedo"] = albedo,
					["normal"] = normal,
					["position"] = position,
					["depth"] = depth,
					["lit"] = lit,
					["outlined"] = outlined,
					["glow"] = glow,
					["shadow"] = shadowMap,
				};
				if (views.TryGetValue(debugView, out var chosen))
					present = chosen;
				else
					Console.WriteLine($"[loom] LOOM_DEBUG='{debugView}' is not a known view ({string.Join(", ", views.Keys)}); showing the final image.");
			}

			g.Pass("upscale")
				.Samples(present)
				.Writes(screen)
				.Draw(ctx => ctx.FullScreen("blit", present));

			g.Present(screen);
		});

		using var executor = new GraphExecutor(context, graph, techniques, window.FramebufferSize);
		cube = executor.CreateMesh(cubeVerts, cubeIndices);    // upload geometry before the first frame
		plane = executor.CreateMesh(planeVerts, planeIndices);

		// The sun: a directional light. Its view-projection is constant (the light doesn't move), so set it
		// once; the shadow pass draws from it and lighting projects into it. Orthographic — directional light.
		var lightDir = Vector3.Normalize(new Vector3(0.6f, 0.55f, 0.45f)); // matches lighting.slang's lightDir
		var sunView = Matrix4x4.CreateLookAt(lightDir * 7f, Vector3.Zero, Vector3.UnitY);
		var sunProj = Matrix4x4.CreateOrthographic(9f, 9f, 0.1f, 14f);
		sunProj.M22 *= -1f; // same Vulkan Y-flip as the camera; render + sample share it, so it's consistent
		executor.SunView = new Camera(sunView, sunProj);

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
			// read the cursor's object id from the picking readback (§7.4) — pixel-accurate to the silhouette,
			// a frame or two stale (imperceptible for hover). Map the cursor from screen to the world domain.
			if (capturePath is not null)
			{
				executor.HoverId = 1u;
			}
			else if (mouse is not null)
			{
				var px = (int)(mouse.Position.X / Math.Max(window.FramebufferSize.X, 1) * 320);
				var py = (int)(mouse.Position.Y / Math.Max(window.FramebufferSize.Y, 1) * 180);
				executor.HoverId = (uint)MathF.Round(executor.SamplePickReadback(px, py));
			}

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

	// Flatten a matrix to 16 floats in the same field order System.Numerics stores them (M11..M44) — the
	// layout the shader's float4x4 expects, matching how the MVP push uploads its matrix bytes. Used to hand
	// the sun's view-projection to the lighting shader through the scalar push channel.
	private static float[] Mat(Matrix4x4 m) =>
	[
		m.M11, m.M12, m.M13, m.M14,
		m.M21, m.M22, m.M23, m.M24,
		m.M31, m.M32, m.M33, m.M34,
		m.M41, m.M42, m.M43, m.M44,
	];

	// A flat ground quad at the cube's base (y = -0.5), facing up — the receiver the cube's shadow falls on.
	// Cull is disabled, so winding doesn't matter; the normal points up for lighting.
	private static (Vertex[] Vertices, uint[] Indices) BuildPlane()
	{
		const float s = 4f;
		const float y = -0.5f;
		var up = new Vector3(0, 1, 0);
		Vertex[] vertices =
		[
			new(new(-s, y, -s), up, new(0, 0)),
			new(new(s, y, -s), up, new(1, 0)),
			new(new(s, y, s), up, new(1, 1)),
			new(new(-s, y, s), up, new(0, 1)),
		];
		return (vertices, [0, 1, 2, 0, 2, 3]);
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
