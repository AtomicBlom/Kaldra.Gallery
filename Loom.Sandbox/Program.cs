using System.Numerics;
using System.Runtime.InteropServices;

using ImGuiNET;
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
			Title = "Loom - M6: deferred reference frame + Dear ImGui inspector",
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
			// SSAO: samples world position (0) + normal (1); the camera view-projection rides the push.
			["ssao"] = new(ShaderPath(shaderDir, "ssao.spv"), "vertexMain", "fragmentMain", SampleCount: 2, HasFragmentPush: true),
			// Deferred lighting: samples albedo (0) + normal (1) + position (2) + shadow map (3) + ao (4); the
			// sun's view-projection rides the fragment push for the shadow test. Writes the lit result.
			["lighting"] = new(ShaderPath(shaderDir, "lighting.spv"), "vertexMain", "fragmentMain", SampleCount: 5, HasFragmentPush: true),
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
			// Runtime buffer inspector + terminal upscale: samples every inspectable buffer (10 combined image
			// samplers) and presents the one the HUD's View menu selects via a fragment push (mode 0 = final).
			["debugview"] = new(ShaderPath(shaderDir, "debugview.spv"), "vertexMain", "fragmentMain", SampleCount: 10, HasFragmentPush: true),
			// GPU particles (§7.6): a compute writes the particle storage buffer (binding 0); the render reads
			// it as point vertices and additively blends emissive sparks (lone compute entry → "main").
			["particle.update"] = new(ShaderPath(shaderDir, "particle_update.spv"), "main", "main", IsCompute: true, StorageBufferCount: 1, HasFragmentPush: true),
			["particle.render"] = new(ShaderPath(shaderDir, "particle_render.spv"), "vertexMain", "fragmentMain", Blend: BlendMode.Additive, ParticleInput: true),
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
		// Debug viz (§7.6), now HUD-driven at RUNTIME: the terminal pass samples every inspectable buffer and a
		// fragment push selects which to present. `inspectView` is the selected index; the HUD's View menu
		// mutates it, and the terminal pass closure reads it each frame. The graph stays immutable — only the
		// uniform varies, the read edges are fixed (§8). `viewNames` is the shared index→name list (menu + the
		// terminal pass's .Samples order must agree). LOOM_DEBUG still works as the initial selection.
		string[] viewNames = ["Final", "Albedo", "Normal", "Position", "Depth", "AO", "Lit", "Outlined", "Glow", "Shadow"];
		var inspectView = 0;
		var initialView = Environment.GetEnvironmentVariable("LOOM_DEBUG");
		if (initialView is not null)
		{
			var idx = Array.FindIndex(viewNames, n => string.Equals(n, initialView, StringComparison.OrdinalIgnoreCase));
			if (idx >= 0)
				inspectView = idx;
			else
				Console.WriteLine($"[loom] LOOM_DEBUG='{initialView}' is not a known view ({string.Join(", ", viewNames)}); starting on Final.");
		}

		var graph = RenderGraph.Define(g =>
		{
			var screen = g.Backbuffer;
			var world = g.Domain("world", 320, 180);
			var albedo = world.Color("albedo", Format.Rgba8Srgb);
			var normal = world.Color("normal", Format.Rgba16F);
			var position = world.Color("position", Format.Rgba16F); // world position, for the shadow test + SSAO
			var depth = world.Depth("depth", Format.Depth32F);
			var ao = world.Color("ao", Format.R16F); // screen-space ambient occlusion
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

			// SSAO (§7.6): samples the g-buffer's position + normal, writes a contact-occlusion term. Just
			// another edge — gbuffer → ssao → lighting; the engine reorders/barriers it in automatically.
			g.Pass("ssao")
				.Samples(position, normal)
				.Writes(ao)
				.Draw(ctx => ctx.FullScreen("ssao", Mat(ctx.Camera.ViewProjection), position, normal));

			g.Pass("lighting")
				.Samples(albedo, normal, position, shadowMap, ao)
				.Writes(lit)
				.Draw(ctx => ctx.FullScreen("lighting", Mat(ctx.SunView.ViewProjection), albedo, normal, position, shadowMap, ao));

			// Transparent forward pass: re-draws the cube as glass, accumulating a tint onto the lit target
			// (.Blends → LoadOp.Load + alpha blend) while testing against the opaque depth read-only
			// (.ReadsDepth → DEPTH_READ_ONLY, depth writes off). The engine derives both from these edges.
			g.Pass("transparent")
				.Blends(lit)
				.ReadsDepth(depth)
				.Draw(ctx => ctx.Draw(cube, "transparent"));

			// GPU particles (§7.6): a compute pass writes positions into a storage BUFFER (a deterministic
			// looping fountain from the cube base); the draw reads that same buffer as point vertices and
			// additively blends glowing sparks onto lit (so they bloom). Two edges — .ReadWrites then
			// .ReadsVertex — and the engine derives the compute→vertex-input barrier (the one new sync here).
			const int particleCount = 4096;
			var particles = g.Buffer("particles", particleCount * 16); // float4 per particle

			g.Compute("particle.update")
				.ReadWrites(particles)
				.Dispatch(ctx => ctx.Dispatch("particle.update", ((uint)(particleCount / 64), 1, 1), [ctx.Time], [], [particles]));

			g.Pass("particle.draw")
				.Blends(lit)
				.ReadsVertex(particles)
				.Draw(ctx => ctx.DrawParticles(particles, particleCount, "particle.render"));

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

			// Terminal pass: nearest-neighbour upscale from the low-res world to the native screen (§6/§9) AND
			// the runtime buffer inspector (§7.6). It samples EVERY inspectable buffer in viewNames order; the
			// debugview shader presents the one `inspectView` selects (0 = the final graded image, the plain §9
			// pixelation upscale). The closure reads `inspectView` each frame, so the HUD's View menu switches the
			// display live without rebuilding the graph — only the push uniform varies, the read edges are fixed
			// (§8). The cost: every sampled buffer stays alive to the end, so less of the transient aliasing folds
			// than a single-input blit would — the honest price of a runtime inspector.
			g.Pass("upscale")
				.Samples(graded, albedo, normal, position, depth, ao, lit, outlined, glow, shadowMap)
				.Writes(screen)
				.Draw(ctx => ctx.FullScreen("debugview", [(float)inspectView], graded, albedo, normal, position, depth, ao, lit, outlined, glow, shadowMap));

			// Dear ImGui HUD (§6/§9: a native-resolution overlay on top of the pixelated world). It .Blends the
			// backbuffer — LoadOp.Load + alpha, so the engine orders it strictly after the upscale (RAW on screen)
			// and derives the barrier — and the ImGui renderer draws into this pass's scope. The host owns the
			// ImGui context and sets the per-frame draw data; ctx.DrawImGui() is the UI-agnostic hand-off (§8).
			g.Pass("hud")
				.Blends(screen)
				.Draw(ctx => ctx.DrawImGui());

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

		// Dear ImGui (§6/§9 HUD). The host owns the context + input; the backend's ImGuiRenderer (the
		// imgui_impl_vulkan equivalent, quarantined in Loom.Vulkan) does the GPU work. Create the context and
		// build the font atlas BEFORE EnableImGui (which uploads it). Per frame we feed input + display size,
		// build the panel, ImGui.Render(), and hand the executor the native draw-data pointer for the hud pass.
		ImGui.CreateContext();
		var io = ImGui.GetIO();
		unsafe { io.NativePtr->IniFilename = null; } // don't persist a layout .ini next to the exe
		io.DisplaySize = new Vector2(window.FramebufferSize.X, window.FramebufferSize.Y);
		io.DisplayFramebufferScale = Vector2.One;
		io.Fonts.AddFontDefault();
		ImGui.StyleColorsDark();
		executor.EnableImGui(ShaderPath(shaderDir, "imgui.spv"));
		var pauseCamera = false;
		var showDemo = false;

		Console.WriteLine($"Loom M6 - graph-driven 3D render on {context.DeviceName}. Hover the cube for a glowing outline; use the View menu to inspect any buffer/pass. Close the window to exit.");

		// Mouse, for the hover test that drives the outline (the cube is object id 1) and ImGui input.
		using var input = window.CreateInput();
		var mouse = input.Mice.Count > 0 ? input.Mice[0] : null;

		// Headless smoke mode: LOOM_SMOKE_FRAMES=N renders N frames then closes (for automated checks).
		// Capture mode: LOOM_CAPTURE=path grabs a warmed-up frame to a PNG, then exits.
		var smokeFrames = int.TryParse(Environment.GetEnvironmentVariable("LOOM_SMOKE_FRAMES"), out var n) ? n : 0;
		var capturePath = Environment.GetEnvironmentVariable("LOOM_CAPTURE");
		var frame = 0;
		var elapsed = 0f;
		// Smoothed FPS for the HUD: at 1000+ fps a per-frame number is an unreadable blur, so average over a
		// fixed window and only refresh the displayed value a few times a second.
		var fpsTimer = 0f;
		var fpsFrames = 0;
		var displayFps = 0.0;
		var displayMs = 0.0;

		window.FramebufferResize += size => executor.OnResize(size);
		window.Render += delta =>
		{
			frame++;
			if (!pauseCamera)
				elapsed += (float)delta; // a paused orbit freezes the camera (and time-driven particles) — a real HUD control

			fpsTimer += (float)delta;
			fpsFrames++;
			if (fpsTimer >= 0.25f)
			{
				displayFps = fpsFrames / fpsTimer;
				displayMs = fpsTimer / fpsFrames * 1000.0;
				fpsTimer = 0f;
				fpsFrames = 0;
			}

			// Orbit the camera around the cube. Host-side per-frame data, handed to the graph via ctx.Camera.
			var eye = new Vector3(MathF.Sin(elapsed) * 2.5f, 1.6f, MathF.Cos(elapsed) * 2.5f);
			var view = Matrix4x4.CreateLookAt(eye, Vector3.Zero, Vector3.UnitY);
			var aspect = window.FramebufferSize.Y == 0 ? 1f : (float)window.FramebufferSize.X / window.FramebufferSize.Y;
			var projection = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 3f, aspect, 0.1f, 100f);
			projection.M22 *= -1f; // Vulkan clip space has +Y down — flip it
			executor.Camera = new Camera(view, projection);
			executor.Time = elapsed; // drives the GPU particle fountain

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

			// Drive ImGui: feed it this frame's display size + input, build the control panel, and hand the
			// executor the resulting draw data for the hud pass to render.
			io.DisplaySize = new Vector2(window.FramebufferSize.X, window.FramebufferSize.Y);
			io.DisplayFramebufferScale = Vector2.One;
			io.DeltaTime = delta > 0 ? (float)delta : 1f / 60f; // ImGui asserts a positive dt
			if (mouse is not null)
			{
				io.AddMousePosEvent(mouse.Position.X, mouse.Position.Y);
				io.AddMouseButtonEvent(0, mouse.IsButtonPressed(MouseButton.Left));
				io.AddMouseButtonEvent(1, mouse.IsButtonPressed(MouseButton.Right));
				io.AddMouseWheelEvent(0f, mouse.ScrollWheels.Count > 0 ? mouse.ScrollWheels[0].Y : 0f);
			}

			ImGui.NewFrame();

			// Title/menu bar across the top. The View menu turns on a visualization of any buffer/pass by setting
			// inspectView (which the terminal pass reads); the Window menu holds the host-side toggles. Both
			// BeginMainMenuBar and BeginViewportSideBar position themselves natively, so they sidestep the
			// SetNextWindow* marshaling quirk in this binding.
			if (ImGui.BeginMainMenuBar())
			{
				if (ImGui.BeginMenu("View"))
				{
					for (var i = 0; i < viewNames.Length; i++)
						if (ImGui.MenuItem(viewNames[i], "", inspectView == i))
							inspectView = i;
					ImGui.EndMenu();
				}
				if (ImGui.BeginMenu("Window"))
				{
					ImGui.MenuItem("Pause camera", "", ref pauseCamera);
					ImGui.MenuItem("ImGui demo window", "", ref showDemo);
					ImGui.EndMenu();
				}

				// Live status on the menu bar. A bottom footer would need SetNextWindowPos (an ImVec2 by value,
				// HFA-broken on win-arm64 in this binding) or igBeginViewportSideBar (not exposed by ImGui.NET
				// 1.91.6.1), so the status rides the bar. It's LEFT-anchored right after the menus (the menu labels
				// are fixed-width, so the start never moves) rather than right-aligned — right-alignment recomputes
				// the X from the text width every frame, which makes the whole block jitter as the numbers change.
				// The FPS is the smoothed value (refreshed 4x/sec), readable even at 1000+ fps.
				ImGui.Separator();
				ImGui.Text($"{displayFps:F0} fps ({displayMs:F2} ms)   |   view: {viewNames[inspectView]}   |   hovered {executor.HoverId}   |   {context.DeviceName}");

				ImGui.EndMainMenuBar();
			}

			if (showDemo)
				ImGui.ShowDemoWindow(ref showDemo);

			ImGui.Render();
			unsafe { executor.ImGuiDrawData = (nint)ImGui.GetDrawData().NativePtr; }

			executor.DrawFrame(delta);

			if (capturePath is not null && frame >= 12)
				window.Close();
			else if (smokeFrames > 0 && frame >= smokeFrames)
				window.Close();
		};

		window.Run();
		executor.WaitIdle(); // Drain the GPU before the using-disposals tear down Vulkan objects.
		ImGui.DestroyContext();
	}

	// View a matrix as its 16 contiguous floats (M11..M44 — System.Numerics' field order, exactly what the
	// shader's float4x4 and the MVP push expect) with no per-frame array allocation. The span aliases the
	// caller's matrix temporary, which lives to the end of the enclosing push call where it is read
	// synchronously, so it never escapes. Used to hand a view-projection to a shader's scalar push channel.
	private static ReadOnlySpan<float> Mat(in Matrix4x4 m) => MemoryMarshal.CreateReadOnlySpan(in m.M11, 16);

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
