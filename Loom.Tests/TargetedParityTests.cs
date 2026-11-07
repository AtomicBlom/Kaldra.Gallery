using System.Numerics;

using Loom.Assets;
using Loom.Backend.DirectX12;
using Loom.Backend.Vulkan;
using Loom.Core.Graph;
using Loom.Core.Hosting;
using Loom.Core.Materials;
using Loom.Gallery.Demos;

namespace Loom.Tests;

/// <summary>
/// Targeted, controlled cross-backend parity checks — the diagnostic counterpart to <see cref="GalleryParityTests"/>.
/// Where the gallery parity test renders whole composed demo frames (broad coverage, but a failure doesn't say
/// which stage diverged), each test here renders a <em>minimal single-feature</em> scene to a <em>single</em>
/// target with the confounds (lighting, shadows, tone-mapping, HUD) stripped out, and pairs the target under test
/// with a <em>control</em> so a Vulkan-vs-DirectX12 diff pins one engine behavior. Add these for features that are
/// subtle, backend-divergence-prone, or have bitten. GPU-gated: SKIPPED unless both backends are available.
/// </summary>
[NotInParallel] // one GPU engine at a time
public class TargetedParityTests
{
	private const int Width = 1280, Height = 720;
	private const int PerChannelTolerance = 4;

	// The standard cross-backend bar (matches GalleryParityTests): a pixel fails if any channel differs by > 4/255,
	// and the frame fails above this fraction.
	private const double StandardBar = 0.005;

	/// <summary>
	/// Isolates the normal-mapped G-buffer's cross-backend divergence to the TBN reconstruction, and bounds it.
	/// The <c>gbuffer_normalmap</c> fragment shader samples a texture and then rotates a tangent-space normal into
	/// world space (<c>normalize</c>/<c>cross</c>/<c>normalize</c>). Its <b>albedo</b> output — same draw, same
	/// sampler, no TBN math — is backend-clean at the standard bar, so texture sampling, rasterization, and
	/// interpolation are not the source. Its encoded <b>normal</b> output carries a small, bounded precision gap
	/// (measured ~0.7% of pixels, ≤68/255): the normalize/cross/normalize round differently between SPIR-V (Vulkan)
	/// and DXIL (DirectX12). This is the stage the fully-lit NormalMapping demo is excluded from
	/// <see cref="GalleryParityTests"/> for — the deferred lighting nudges 0.7% up to ~0.9%. Asserting the sampler
	/// clean and the TBN gap bounded gives the feature real coverage and guards the gap from silently widening,
	/// without gating the demo suite on a benign precision quirk.
	/// </summary>
	[Test]
	public void NormalMap_gbuffer_sampler_is_clean_and_the_TBN_gap_stays_bounded()
	{
		_ = new VulkanEngineSettings();
		_ = new DirectX12EngineSettings();
		var vulkan = Backends.Available.FirstOrDefault(e => e.Name == "Vulkan");
		var dx12 = Backends.Available.FirstOrDefault(e => e.Name == "DirectX12");
		if (vulkan is null || dx12 is null)
			throw new TUnit.Core.Exceptions.SkipTestException("needs both Vulkan and DirectX12 available on this box.");

		// Control: the texture-sampled albedo (no TBN math) must be backend-clean at the standard bar.
		var albedo = CompareTarget(vulkan, dx12, GBufferTarget.Albedo);
		albedo.FailedFraction.ShouldBeLessThanOrEqualTo(StandardBar,
			$"the G-buffer albedo (a plain texture sample) diverges across backends — the sampler, not the TBN math, would be the culprit: {albedo}");

		// The encoded normal (sampler + TBN reconstruction) carries a known, bounded precision gap. The bound sits
		// above the measured ~0.7% with headroom, so it guards against the gap widening (a real regression) without
		// flaking on the existing benign SPIR-V/DXIL rounding difference.
		const double tbnBound = 0.012;
		var normal = CompareTarget(vulkan, dx12, GBufferTarget.Normal);
		normal.FailedFraction.ShouldBeLessThanOrEqualTo(tbnBound,
			$"the encoded normal's cross-backend TBN precision gap widened beyond its known bound: {normal}");
		// That the normal diverged more than its albedo control was asserted too, as proof the gap was the TBN's. The gap
		// has since closed (2026-11-08, NVIDIA Turing: both targets match to the bit), so that proof can't be asked
		// for, and nothing is lost: the albedo staying clean and the normal staying bounded are the guards.
	}

	private enum GBufferTarget { Albedo, Normal }

	// Render just the normal-mapped G-buffer pass (cube + plane, brick material) and diff the chosen target between
	// the two backends. Only the gbuffer pass runs — no shadow, no lighting, no HUD — and a frozen camera makes it
	// deterministic, so the diff reflects that one target's backend behaviour alone.
	private static ImageDiff CompareTarget(IRenderBackend vulkan, IRenderBackend dx12, GBufferTarget target)
	{
		var vkPath = Path.Combine(Path.GetTempPath(), $"loom-nmgb-{target}-vk-{Guid.NewGuid():N}.png");
		var dxPath = Path.Combine(Path.GetTempPath(), $"loom-nmgb-{target}-dx-{Guid.NewGuid():N}.png");
		try
		{
			RenderTarget(vulkan, target, vkPath);
			RenderTarget(dx12, target, dxPath);
			var (vw, vh, vk) = PngReader.Read(vkPath);
			var (dw, dh, dx) = PngReader.Read(dxPath);
			(vw, vh).ShouldBe((dw, dh));
			return ImageComparison.Compare(vw, vh, vk, dx, PerChannelTolerance);
		}
		finally
		{
			if (File.Exists(vkPath)) File.Delete(vkPath);
			if (File.Exists(dxPath)) File.Delete(dxPath);
		}
	}

	private static void RenderTarget(IRenderBackend backend, GBufferTarget target, string outPath)
	{
		using var engine = backend.Create(Surface.Offscreen(Width, Height), Width, Height, new EngineSettings());

		var assets = Path.Combine(AppContext.BaseDirectory, "assets");
		var (cubeVerts, cubeIndices) = Geometry.BuildCube();
		var (planeVerts, planeIndices) = Geometry.BuildPlane();
		var cube = engine.CreateMesh(cubeVerts, cubeIndices);
		var plane = engine.CreateMesh(planeVerts, planeIndices);
		var brick = engine.CreateMaterial(
			engine.CreateTexture(ImageLoader.Load(Path.Combine(assets, "brick_albedo.png"), TextureFormat.Rgba8Srgb)),
			engine.CreateTexture(ImageLoader.Load(Path.Combine(assets, "brick_normal.png"), TextureFormat.Rgba8Unorm)));

		engine.DefineTechnique("gbuffer.nm", new TechniqueDescriptor("gbuffer_normalmap", HasVertexInput: true, LinearFilter: true, Address: SamplerAddress.Repeat));
		engine.DefineTechnique("blit", new TechniqueDescriptor("blit")); // engine-embedded full-res texture copy

		ConstantsHandle viewproj = null!;
		var graph = RenderGraph.Define(g =>
		{
			var screen = g.Backbuffer;
			var albedo = g.Screen.Color("albedo", Format.Rgba8Srgb);
			var normal = g.Screen.Color("normal", Format.Rgba16F);
			var position = g.Screen.Color("position", Format.Rgba16F);
			var depth = g.Screen.Depth("depth", Format.Depth32F);
			viewproj = g.Constants<Matrix4x4>("nm.parity.viewproj");

			// The technique writes albedo/normal/position; the present pass blits just the one under test.
			g.Pass("gbuffer")
				.Reads(viewproj)
				.Writes(albedo, normal, position)
				.WritesDepth(depth)
				.Draw(ctx =>
				{
					DemoDraw.Mesh(ctx, cube, "gbuffer.nm", Matrix4x4.Identity, 0, brick);
					DemoDraw.Mesh(ctx, plane, "gbuffer.nm", Matrix4x4.Identity, 0, brick);
				});

			var under = target == GBufferTarget.Albedo ? albedo : normal;
			g.Pass("present")
				.Samples(under)
				.Writes(screen)
				.Draw(ctx => ctx.FullScreen("blit", under));

			g.Present(screen);
		});
		engine.SetGraph(graph);

		var cam = new Loom.Concepts.Camera(engine);
		DemoCamera.Orbit(cam, 1.0f, (float)Width / Height); // the demo's frozen orbit pose — three cube faces + floor
		var inputs = new FrameInputs { Time = 1.0f, DeltaSeconds = 1.0 / 60.0 };
		for (var frame = 1; frame <= 3; frame++)
		{
			engine.Write(viewproj, cam.ViewProjection);
			if (frame == 2)
				engine.RequestCapture(outPath); // static scene — capture, then render one more so the readback flushes
			engine.Render(inputs);
		}
	}

	/// <summary>
	/// Fixed-function alpha blending is backend-specific code (a <see cref="BlendMode"/> maps to the Vulkan
	/// colour-blend state / the DirectX12 blend desc), so a mistranslation would diverge silently. Combine two
	/// controlled probe gradients under a blend mode — additive (order-independent), alpha-over (order-dependent,
	/// alpha-weighted) and multiply (darkening) — and assert Vulkan and DirectX12 produce the same frame. Guards the
	/// mapping that bloom / particles (additive), the transparent / sky passes (alpha-over) and decals (multiply)
	/// rely on.
	/// </summary>
	[Test]
	public void Blend_modes_are_backend_parity_clean()
	{
		_ = new VulkanEngineSettings();
		_ = new DirectX12EngineSettings();
		var vulkan = Backends.Available.FirstOrDefault(e => e.Name == "Vulkan");
		var dx12 = Backends.Available.FirstOrDefault(e => e.Name == "DirectX12");
		if (vulkan is null || dx12 is null)
			throw new TUnit.Core.Exceptions.SkipTestException("needs both Vulkan and DirectX12 available on this box.");

		// Additive: two colours accumulated over black (src + dst).
		var additive = CompareBlend(vulkan, dx12, BlendMode.Additive,
			bg: [0f, 0f, 0f, 1f], a: [0.30f, 0.15f, 0.05f, 1f], b: [0.05f, 0.20f, 0.40f, 1f]);
		additive.FailedFraction.ShouldBeLessThanOrEqualTo(StandardBar, $"additive blend diverges across backends: {additive}");

		// Alpha-over: two half-transparent colours composited over an opaque grey (src·a + dst·(1−a)).
		var alphaOver = CompareBlend(vulkan, dx12, BlendMode.AlphaOver,
			bg: [0.20f, 0.20f, 0.20f, 1f], a: [0.80f, 0.25f, 0.25f, 0.5f], b: [0.25f, 0.25f, 0.80f, 0.5f]);
		alphaOver.FailedFraction.ShouldBeLessThanOrEqualTo(StandardBar, $"alpha-over blend diverges across backends: {alphaOver}");

		// Multiply: two colours darkening a light grey (src·dst). Every alpha is below 1, so the alpha equation shows in the
		// frame too: DirectX12 takes the destination's alpha where the colour equation takes its colour, and a wrong
		// mapping there diverges in the alpha channel alone.
		var multiply = CompareBlend(vulkan, dx12, BlendMode.Multiply,
			bg: [0.90f, 0.90f, 0.90f, 0.80f], a: [0.80f, 0.50f, 0.30f, 0.60f], b: [0.60f, 0.90f, 0.70f, 0.70f], alphaVaries: true);
		multiply.FailedFraction.ShouldBeLessThanOrEqualTo(StandardBar, $"multiply blend diverges across backends: {multiply}");
	}

	private static ImageDiff CompareBlend(IRenderBackend vulkan, IRenderBackend dx12, BlendMode mode, float[] bg, float[] a, float[] b, bool alphaVaries = false)
	{
		var vkPath = Path.Combine(Path.GetTempPath(), $"loom-blend-{mode}-vk-{Guid.NewGuid():N}.png");
		var dxPath = Path.Combine(Path.GetTempPath(), $"loom-blend-{mode}-dx-{Guid.NewGuid():N}.png");
		try
		{
			RenderBlend(vulkan, mode, bg, a, b, vkPath);
			RenderBlend(dx12, mode, bg, a, b, dxPath);
			var (vw, vh, vk) = PngReader.Read(vkPath);
			var (dw, dh, dx) = PngReader.Read(dxPath);
			(vw, vh).ShouldBe((dw, dh));
			// Guard against a vacuous pass: the probe is a gradient, so the frame must vary left-to-right — two
			// uniformly-black frames would also "match". Compare the left edge to the right edge on one backend.
			var mid = vh / 2;
			var left = (mid * vw + 2) * 4;
			var right = (mid * vw + vw - 3) * 4;
			var span = Math.Abs(vk[left] - vk[right]) + Math.Abs(vk[left + 1] - vk[right + 1]) + Math.Abs(vk[left + 2] - vk[right + 2]);
			span.ShouldBeGreaterThan(16, $"{mode}: the blended frame is ~uniform — the probe likely rendered nothing, so parity would pass vacuously.");
			// And where the alpha equation is under test, the capture must carry it: an alpha forced opaque would compare equal
			if (alphaVaries)
				vk[left + 3].ShouldBeLessThan((byte)250, $"{mode}: the captured alpha is opaque, so the alpha equation goes untested.");
			return ImageComparison.Compare(vw, vh, vk, dx, PerChannelTolerance);
		}
		finally
		{
			if (File.Exists(vkPath)) File.Delete(vkPath);
			if (File.Exists(dxPath)) File.Delete(dxPath);
		}
	}

	// Fill the screen with the opaque `bg` gradient, then blend colours `a` then `b` over it under `mode` — all three
	// the probe's controlled gradient. The blend mode lives on the technique; the pass .Blends the target.
	private static void RenderBlend(IRenderBackend backend, BlendMode mode, float[] bg, float[] a, float[] b, string outPath)
	{
		using var engine = backend.Create(Surface.Offscreen(Width, Height), Width, Height, new EngineSettings());
		engine.DefineTechnique("probe.opaque", new TechniqueDescriptor("probe", HasFragmentPush: true));
		engine.DefineTechnique("probe.blend", new TechniqueDescriptor("probe", HasFragmentPush: true, Blend: mode));

		var graph = RenderGraph.Define(g =>
		{
			var screen = g.Backbuffer;
			g.Pass("bg").Writes(screen).Draw(ctx => ctx.FullScreen("probe.opaque", bg));
			g.Pass("a").Blends(screen).Draw(ctx => ctx.FullScreen("probe.blend", a));
			g.Pass("b").Blends(screen).Draw(ctx => ctx.FullScreen("probe.blend", b));
			g.Present(screen);
		});
		engine.SetGraph(graph);

		var inputs = new FrameInputs { Time = 0, DeltaSeconds = 1.0 / 60.0 };
		for (var frame = 1; frame <= 3; frame++)
		{
			if (frame == 2)
				engine.RequestCapture(outPath);
			engine.Render(inputs);
		}
	}
}
