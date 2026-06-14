using System.Numerics;
using System.Runtime.InteropServices;

#if LOOM_IMGUI
using ImGuiNET;
#endif
using Loom.Core;
using Loom.Vulkan; // for VulkanEngineSettings only — the one sanctioned backend type (Planning.md §5)
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.Windowing;

namespace Loom.Sandbox;

internal static class Program
{
	private static void Main()
	{
		// Frame-pacing knobs. V-Sync is ON by default — consistent, no tearing, no wasted GPU on
		// every driver. LOOM_VSYNC=0/false/off/no flips to an uncapped, low-latency present mode
		// (Immediate where available, e.g. NVIDIA; else Mailbox, e.g. Adreno). LOOM_MAXFPS bounds the
		// uncapped loop via Silk's own limiter so "off" need not spin at 1000 fps (0 = unlimited).
		var vsyncEnv = Environment.GetEnvironmentVariable("LOOM_VSYNC");
		var vsync = vsyncEnv is null || vsyncEnv.ToLowerInvariant() is not ("0" or "false" or "off" or "no");
		var maxFps = double.TryParse(Environment.GetEnvironmentVariable("LOOM_MAXFPS"), out var f) && f > 0 ? f : 0.0;

		var options = WindowOptions.DefaultVulkan with
		{
			Size = new Vector2D<int>(1280, 720),
			Title = "Loom - M7: neutral engine facade + Dear ImGui inspector",
			FramesPerSecond = maxFps,
		};

		using var window = Window.Create(options);
		window.Initialize();

		if (window.VkSurface is null)
			throw new InvalidOperationException("The windowing platform did not expose a Vulkan surface.");

		// Engine configuration is a settings object now, not scattered env vars / constructor args (Planning.md
		// §3). VulkanEngineSettings is the one backend type the app names; constructing it also loads Loom.Vulkan,
		// which self-registers the Vulkan backend into Engine.AvailableEngines. LOOM_BACKEND optionally selects a
		// backend by name (default: the first available — Vulkan today).
		// LOOM_HDR=1/true/on/yes requests true HDR display output (Planning.md §3): the engine negotiates an
		// scRGB/HDR10 swapchain when the display supports it and the tone-map maps to display nits, falling back
		// to SDR otherwise. Off by default.
		var hdrEnv = Environment.GetEnvironmentVariable("LOOM_HDR");
		var hdr = hdrEnv is not null && hdrEnv.ToLowerInvariant() is "1" or "true" or "on" or "yes";

		var settings = new VulkanEngineSettings
		{
			EnableValidation = true,
			VSync = vsync,
			MaxFramesPerSecond = maxFps,
			HdrOutput = hdr,
			PreferredBackend = Environment.GetEnvironmentVariable("LOOM_BACKEND"),
		};

		// Pick a backend at runtime by name, with a sensible fallback — the app names no graphics-API type here.
		var backend = Engine.AvailableEngines.FirstOrDefault(e => e.Name == settings.PreferredBackend)
			?? Engine.AvailableEngines.First();
		using var engine = backend.Create(window.VkSurface, window.FramebufferSize.X, window.FramebufferSize.Y, settings);

		// Register the techniques the graph draws with, by logical name. The shader is named logically
		// ("fullscreen" → the backend resolves <shaders>/fullscreen.spv); no backend artifact path appears here.
		// The descriptor-binding counts (samplers / storage images / buffers) are NOT stated here — the backend
		// reads them from each shader's Slang reflection (M7-B4). The app states only the pipeline intent: the
		// logical shader/entry names, whether it reads geometry, its blend, sampler filter, and compute-ness.
		engine.DefineTechnique("fullscreen", new TechniqueDescriptor("fullscreen")); // gradient from SV_VertexID, no input
		engine.DefineTechnique("blit", new TechniqueDescriptor("blit")); // fullscreen sample of one input
		engine.DefineTechnique("mesh", new TechniqueDescriptor("mesh", HasVertexInput: true)); // reads a Vertex buffer
		engine.DefineTechnique("gbuffer", new TechniqueDescriptor("gbuffer", HasVertexInput: true)); // MRT: albedo+normal+position+depth
		engine.DefineTechnique("depthonly", new TechniqueDescriptor("depthonly", HasVertexInput: true)); // shadow caster, no colour
		engine.DefineTechnique("ssao", new TechniqueDescriptor("ssao", HasFragmentPush: true)); // samples position+normal
		// Deferred lighting: samples albedo+normal+position+shadow+ao; the sun's VP rides the fragment push.
		engine.DefineTechnique("lighting", new TechniqueDescriptor("lighting", HasFragmentPush: true));
		engine.DefineTechnique("transparent", new TechniqueDescriptor("transparent", HasVertexInput: true, Blend: BlendMode.AlphaOver));
		// Bloom sub-graph (§7.1). All sample with a linear filter; the upsample additively accumulates. "bloom.down"
		// and "bloom.up" share one shader (bloom_sample) — only their blend/edge intent differs.
		engine.DefineTechnique("bloom.prefilter", new TechniqueDescriptor("bloom_prefilter", LinearFilter: true, HasFragmentPush: true));
		engine.DefineTechnique("bloom.down", new TechniqueDescriptor("bloom_sample", LinearFilter: true));
		engine.DefineTechnique("bloom.up", new TechniqueDescriptor("bloom_sample", Blend: BlendMode.Additive, LinearFilter: true));
		engine.DefineTechnique("bloom.composite", new TechniqueDescriptor("bloom_composite", LinearFilter: true, HasFragmentPush: true));
		// JFA hover-outline (§7.4/§7.5). "id" writes the cube's object id; seed/jfa/resolve grow a glowing border.
		engine.DefineTechnique("id", new TechniqueDescriptor("id", HasVertexInput: true));
		engine.DefineTechnique("outline.seed", new TechniqueDescriptor("outline_seed", HasFragmentPush: true));
		engine.DefineTechnique("outline.jfa", new TechniqueDescriptor("outline_jfa", HasFragmentPush: true));
		engine.DefineTechnique("outline.resolve", new TechniqueDescriptor("outline_resolve"));
		// Auto-exposure (§12 M5): a compute reduction samples lit + read-writes the 1×1 persistent exposure
		// storage image; the tonemap samples glow + exposure. slangc names a lone compute entry "main".
		engine.DefineTechnique("exposure", new TechniqueDescriptor("exposure", VertexEntry: "main", FragmentEntry: "main", IsCompute: true, HasFragmentPush: true));
		engine.DefineTechnique("tonemap", new TechniqueDescriptor("tonemap", HasFragmentPush: true));
		// Runtime buffer inspector + terminal upscale: samples every inspectable buffer and presents the one
		// the HUD's View menu selects via a fragment push (mode 0 = final).
		engine.DefineTechnique("debugview", new TechniqueDescriptor("debugview", HasFragmentPush: true));
		// GPU particles (§7.6): a compute writes the particle storage buffer; the render reads it as point vertices
		// and additively blends emissive sparks (lone compute entry → "main").
		engine.DefineTechnique("particle.update", new TechniqueDescriptor("particle_update", VertexEntry: "main", FragmentEntry: "main", IsCompute: true, HasFragmentPush: true));
		engine.DefineTechnique("particle.render", new TechniqueDescriptor("particle_render", Blend: BlendMode.Additive, ParticleInput: true));

		// Upload geometry once; the scene passes draw these stable handles (pulled, not captured — §8).
		var (cubeVerts, cubeIndices) = BuildCube();
		var (planeVerts, planeIndices) = BuildPlane();
		var cube = engine.CreateMesh(cubeVerts, cubeIndices);
		var plane = engine.CreateMesh(planeVerts, planeIndices); // a ground plane to catch the cube's shadow

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

		// Each demo is a self-contained graph the engine swaps at runtime via SetGraph — the concrete payoff of
		// the device/graph split (Planning.md §8: "structural changes rebuild the baked graph — cheap and
		// cached"). The full reference frame is one option among several minimal, single-purpose pipelines; the
		// keyboard (number keys / Tab) and the Demo menu switch between them live, re-deriving the schedule and
		// transients for the chosen graph while the device, pipeline cache, and meshes are reused.
		RenderGraph FullDemo() => RenderGraph.Define(g =>
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
			// R16F (not R32Uint) is deliberate: the only id here is the cube (1), exact in F16, and a float id
			// composes directly with the float-based JFA seed/sample below. Apps needing large/precise picking
			// ids should use Format.R32Uint (integer-exact) and read it back as uint — see the Format docs.
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
				// The HDR flag (ctx.HdrOutput) rides the push: SDR compresses to the LDR backbuffer; HDR keeps
				// the extended range for the negotiated scRGB swapchain (M7-B6).
				.Draw(ctx => ctx.FullScreen("tonemap", [ctx.HdrOutput ? 1f : 0f], glow, exposure));

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

		// A from-scratch fullscreen gradient (no inputs) straight to the backbuffer — the smallest possible
		// pipeline, the clearest demonstration of a swap to a trivial graph.
		RenderGraph TriangleDemo() => RenderGraph.Define(g =>
		{
			var screen = g.Backbuffer;
			g.Pass("gradient").Writes(screen).Draw(ctx => ctx.FullScreen("fullscreen"));
			HudOverlay(g, screen); // the ImGui menu bar rides on top of every demo, not just the full frame
			g.Present(screen);
		});

		// A single forward-shaded mesh: draw the cube (coloured by normal) + ground plane into a native-res
		// colour+depth pair, then blit to the screen. Just geometry → depth-test → present, no deferred stages.
		RenderGraph CubeDemo() => RenderGraph.Define(g =>
		{
			var screen = g.Backbuffer;
			var scene = g.Screen.Color("scene", Format.Rgba8Srgb);
			var depth = g.Screen.Depth("scene_depth", Format.Depth32F);
			g.Pass("scene")
				.Writes(scene)
				.WritesDepth(depth)
				.Draw(ctx =>
				{
					ctx.Draw(cube, "mesh");
					ctx.Draw(plane, "mesh");
				});
			g.Pass("blit").Samples(scene).Writes(screen).Draw(ctx => ctx.FullScreen("blit", scene));
			HudOverlay(g, screen);
			g.Present(screen);
		});

		// The bloom sub-graph (§7.1) in isolation: render the cube into an HDR target, run the threshold/mip
		// pyramid, blit the glow. The same g.Bloom helper the full frame uses, but nothing else around it.
		RenderGraph BloomDemo() => RenderGraph.Define(g =>
		{
			var screen = g.Backbuffer;
			var scene = g.Screen.Color("scene", Format.Rgba16F);
			var depth = g.Screen.Depth("scene_depth", Format.Depth32F);
			g.Pass("scene")
				.Writes(scene)
				.WritesDepth(depth)
				.Draw(ctx =>
				{
					ctx.Draw(cube, "mesh");
					ctx.Draw(plane, "mesh");
				});
			var glow = g.Bloom(scene, threshold: 0.4f, mips: 5); // low threshold so the normal-coloured cube blooms
			g.Pass("blit").Samples(glow).Writes(screen).Draw(ctx => ctx.FullScreen("blit", glow));
			HudOverlay(g, screen);
			g.Present(screen);
		});

		// Build every demo's graph up front (immutable descriptions are cheap); the engine swaps the active one.
		(string Name, RenderGraph Graph)[] demos =
		[
			("Triangle", TriangleDemo()),
			("Cube", CubeDemo()),
			("Bloom", BloomDemo()),
			("Full reference frame", FullDemo()),
		];

		// Start on the full reference frame (so LOOM_CAPTURE still grabs it); LOOM_DEMO=<name|index> overrides.
		var currentDemo = demos.Length - 1;
		var demoEnv = Environment.GetEnvironmentVariable("LOOM_DEMO");
		if (demoEnv is not null)
		{
			var byName = Array.FindIndex(demos, d => string.Equals(d.Name, demoEnv, StringComparison.OrdinalIgnoreCase));
			if (byName >= 0)
				currentDemo = byName;
			else if (int.TryParse(demoEnv, out var byIndex) && byIndex >= 0 && byIndex < demos.Length)
				currentDemo = byIndex;
			else
				Console.WriteLine($"[loom] LOOM_DEMO='{demoEnv}' is not a known demo ({string.Join(", ", demos.Select(d => d.Name))}); starting on {demos[currentDemo].Name}.");
		}

		// Make the chosen graph active — the engine derives its schedule/barriers/aliasing + transients, reusing
		// the device and pipeline cache. SelectDemo (below) calls SetGraph again to swap at runtime.
		engine.SetGraph(demos[currentDemo].Graph);

		void SelectDemo(int index)
		{
			if (index < 0 || index >= demos.Length || index == currentDemo)
				return;
			currentDemo = index;
			engine.SetGraph(demos[index].Graph); // drains the GPU, releases the old transients, binds the new graph
			Console.WriteLine($"[loom] demo → {demos[index].Name}");
		}

		// The sun: a directional light. Its view-projection is constant (the light doesn't move), so build it
		// once and hand the same value in every frame's inputs. Orthographic — directional light. No Y-flip here:
		// the projection is neutral (+Y up); the backend applies its clip-space convention on ingest (M7-B5).
		var lightDir = Vector3.Normalize(new Vector3(0.6f, 0.55f, 0.45f)); // matches lighting.slang's lightDir
		var sunView = Matrix4x4.CreateLookAt(lightDir * 7f, Vector3.Zero, Vector3.UnitY);
		var sunProj = Matrix4x4.CreateOrthographic(9f, 9f, 0.1f, 14f);
		var sunCamera = new Camera(sunView, sunProj);

		// A real HUD control (freezes the orbit); also togglable via the ImGui Window menu when built with ImGui.
		// Declared unconditionally because the camera loop reads it whether or not ImGui is compiled in.
		var pauseCamera = false;
#if LOOM_IMGUI
		// Dear ImGui (§6/§9 HUD). The host owns the context + input; the engine's ImGui renderer (the
		// imgui_impl_vulkan equivalent, quarantined in the backend) does the GPU work. Create the context and
		// build the font atlas BEFORE EnableImGui (which uploads it). Per frame we feed input + display size,
		// build the panel, ImGui.Render(), and hand the engine the native draw-data pointer via FrameInputs.
		// Compiled out entirely when the engine is built without ImGui (UseImGui=false).
		ImGui.CreateContext();
		var io = ImGui.GetIO();
		unsafe { io.NativePtr->IniFilename = null; } // don't persist a layout .ini next to the exe
		io.DisplaySize = new Vector2(window.FramebufferSize.X, window.FramebufferSize.Y);
		io.DisplayFramebufferScale = Vector2.One;
		io.Fonts.AddFontDefault();
		ImGui.StyleColorsDark();
		engine.EnableImGui();
		var showDemo = false;
#endif

		Console.WriteLine($"Loom M7 - graph-driven 3D render on {engine.DeviceName} ({engine.BackendName}). Keys 1-{demos.Length} / Tab switch demos (the engine swaps the active graph live). On the full frame, hover the cube for a glowing outline and use the View menu to inspect any buffer/pass. Close the window to exit.");

		// Mouse, for the hover test that drives the outline (the cube is object id 1) and ImGui input.
		using var input = window.CreateInput();
		var mouse = input.Mice.Count > 0 ? input.Mice[0] : null;

		// Keyboard switches demos at runtime (the user-facing payoff of the swappable graph): number keys 1..N
		// select a demo directly, Tab cycles. Each press calls SelectDemo → engine.SetGraph, re-deriving the
		// chosen graph's schedule + transients while the device and pipeline cache are reused.
		var keyboard = input.Keyboards.Count > 0 ? input.Keyboards[0] : null;
		if (keyboard is not null)
			keyboard.KeyDown += (_, key, _) =>
			{
				if (key == Key.Tab)
					SelectDemo((currentDemo + 1) % demos.Length);
				else if (key >= Key.Number1 && key < Key.Number1 + demos.Length)
					SelectDemo(key - Key.Number1);
			};

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

		window.FramebufferResize += size => engine.Resize(size.X, size.Y);
		window.Render += delta =>
		{
			frame++;

			// Headless smoke mode also validates the runtime graph-swap: walk through every demo (~2 frames
			// each) so a validation-clean smoke run exercises SetGraph for each pipeline. Skipped under capture
			// (which wants a steady frame of the selected demo).
			if (smokeFrames > 0 && capturePath is null)
				SelectDemo(Math.Min((frame - 1) / 2, demos.Length - 1));

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

			// Orbit the camera around the cube. Host-side per-frame data, handed to the graph via FrameInputs.
			// The projection is neutral (+Y up) — the backend applies the Vulkan clip-space Y-flip on ingest.
			var eye = new Vector3(MathF.Sin(elapsed) * 2.5f, 1.6f, MathF.Cos(elapsed) * 2.5f);
			var view = Matrix4x4.CreateLookAt(eye, Vector3.Zero, Vector3.UnitY);
			var aspect = window.FramebufferSize.Y == 0 ? 1f : (float)window.FramebufferSize.X / window.FramebufferSize.Y;
			var projection = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 3f, aspect, 0.1f, 100f);
			var camera = new Camera(view, projection);

			// Hover: in headless capture force the cube hovered (no cursor) so the outline shows; otherwise
			// read the cursor's object id from the picking readback (§7.4) — pixel-accurate to the silhouette,
			// a frame or two stale (imperceptible for hover). Map the cursor from screen to the world domain.
			uint hoverId;
			if (capturePath is not null)
			{
				hoverId = 1u;
			}
			else if (mouse is not null)
			{
				var px = (int)(mouse.Position.X / Math.Max(window.FramebufferSize.X, 1) * 320);
				var py = (int)(mouse.Position.Y / Math.Max(window.FramebufferSize.Y, 1) * 180);
				hoverId = (uint)MathF.Round(engine.SamplePick(px, py));
			}
			else
			{
				hoverId = 0u;
			}

			if (capturePath is not null && frame == 10)
				engine.RequestCapture(capturePath);

			// The native ImDrawData* for this frame; stays null (no UI) when built without ImGui, so the hud
			// pass's ctx.DrawImGui() is a no-op and every demo still renders, just without the overlay.
			nint imguiDrawData = 0;
#if LOOM_IMGUI
			// Drive ImGui: feed it this frame's display size + input, build the control panel, and produce the
			// draw data this frame's FrameInputs forwards to the hud pass.
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
				// Demo menu: pick a pipeline; the engine swaps the active graph at runtime (same as keys 1..N / Tab).
				if (ImGui.BeginMenu("Demo"))
				{
					for (var i = 0; i < demos.Length; i++)
						if (ImGui.MenuItem($"{i + 1}. {demos[i].Name}", "", currentDemo == i))
							SelectDemo(i);
					ImGui.EndMenu();
				}
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
				ImGui.Text($"{displayFps:F0} fps ({displayMs:F2} ms)   |   demo: {demos[currentDemo].Name}   |   view: {viewNames[inspectView]}   |   hovered {hoverId}   |   {engine.DeviceName}");

				ImGui.EndMainMenuBar();
			}

			if (showDemo)
				ImGui.ShowDemoWindow(ref showDemo);

			ImGui.Render();
			unsafe { imguiDrawData = (nint)ImGui.GetDrawData().NativePtr; }
#endif

			engine.Render(new FrameInputs
			{
				DeltaSeconds = delta,
				Time = elapsed, // drives the GPU particle fountain
				Camera = camera,
				SunView = sunCamera,
				HoverId = hoverId,
				ImGuiDrawData = imguiDrawData,
			});

			if (capturePath is not null && frame >= 12)
				window.Close();
			else if (smokeFrames > 0 && frame >= smokeFrames)
				window.Close();
		};

		window.Run();
		engine.WaitIdle(); // Drain the GPU before the using-disposals tear down the engine.
#if LOOM_IMGUI
		ImGui.DestroyContext();
#endif
	}

	// View a matrix as its 16 contiguous floats (M11..M44 — System.Numerics' field order, exactly what the
	// shader's float4x4 and the MVP push expect) with no per-frame array allocation. The span aliases the
	// caller's matrix temporary, which lives to the end of the enclosing push call where it is read
	// synchronously, so it never escapes. Used to hand a view-projection to a shader's scalar push channel.
	private static ReadOnlySpan<float> Mat(in Matrix4x4 m) => MemoryMarshal.CreateReadOnlySpan(in m.M11, 16);

	// The native-resolution Dear ImGui overlay (§6/§9): a pass that .Blends the backbuffer (LoadOp.Load + alpha,
	// so the engine orders it strictly after the present write and derives the barrier) and hands this frame's
	// draw data to the backend ImGui renderer. Every demo ends with this so the menu bar / Demo switcher / status
	// render on all of them, not just the full reference frame. A no-op if the host hasn't enabled ImGui (§8).
	private static void HudOverlay(GraphBuilder g, ColorHandle screen) =>
		g.Pass("hud").Blends(screen).Draw(ctx => ctx.DrawImGui());

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
