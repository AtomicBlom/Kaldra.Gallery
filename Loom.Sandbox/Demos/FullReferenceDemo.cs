using System.Numerics;

using Loom.Concepts;
using Loom.Core.Geometry;
using Loom.Core.Graph;
using Loom.Core.Hosting;
using Loom.Core.Materials;

namespace Loom.Sandbox.Demos;

/// <summary>
/// The full reference frame — every capability composed into one graph, the proof that the vocabulary all fits
/// together. The whole 3D world renders into a small 320×180 domain (deferred g-buffer → shadows → SSAO →
/// lighting → forward transparency → GPU particles → JFA hover outline → bloom → auto-exposure → tone-map),
/// then a nearest upscale blows it up to the native screen. The focused demos isolate each of these; this one
/// shows them coexisting, with the engine deriving all ordering, the COLOR_ATTACHMENT↔SHADER_READ flips, depth
/// states, load/store, transient aliasing, and present.
/// </summary>
internal sealed class FullReferenceDemo : IDemo
{
	private ColorHandle _ids = null!;
	private ConstantsHandle _hoverParams = null!;
	private IEngine _engine = null!;
	private ViewConstants _view = null!;
	private readonly DrawSubmitter _submit = new();

	public string Name => "Full reference frame";

	public void DefineTechniques(IEngine engine)
	{
		_engine = engine;
		engine.DefineTechnique("depthonly", new TechniqueDescriptor("depthonly", HasVertexInput: true));
		engine.DefineTechnique("gbuffer", new TechniqueDescriptor("gbuffer", HasVertexInput: true));
		engine.DefineTechnique("ssao", new TechniqueDescriptor("ssao", HasFragmentPush: true));
		engine.DefineTechnique("ssao.blur", new TechniqueDescriptor("ssao_blur"));
		engine.DefineTechnique("lighting", new TechniqueDescriptor("lighting", HasFragmentPush: true)); // 5-input deferred lighting + shadow + ao
		engine.DefineTechnique("transparent", new TechniqueDescriptor("transparent", HasVertexInput: true, Blend: BlendMode.AlphaOver));
		engine.DefineTechnique("particle.update", new TechniqueDescriptor("particle_update", VertexEntry: "main", FragmentEntry: "main", IsCompute: true, HasFragmentPush: true));
		engine.DefineTechnique("particle.render", new TechniqueDescriptor("particle_render", Blend: BlendMode.Additive, Instance: SandboxVertexLayouts.ParticlePosition));
		engine.DefineTechnique("id", new TechniqueDescriptor("id", HasVertexInput: true));
		engine.DefineTechnique("outline.seed", new TechniqueDescriptor("outline_seed"));
		engine.DefineTechnique("outline.jfa", new TechniqueDescriptor("outline_jfa", HasFragmentPush: true));
		engine.DefineTechnique("outline.resolve", new TechniqueDescriptor("outline_resolve"));
		engine.DefineTechnique("bloom.prefilter", new TechniqueDescriptor("bloom_prefilter", LinearFilter: true, HasFragmentPush: true));
		engine.DefineTechnique("bloom.down", new TechniqueDescriptor("bloom_sample", LinearFilter: true));
		engine.DefineTechnique("bloom.up", new TechniqueDescriptor("bloom_sample", Blend: BlendMode.Additive, LinearFilter: true));
		engine.DefineTechnique("bloom.composite", new TechniqueDescriptor("bloom_composite", LinearFilter: true, HasFragmentPush: true));
		engine.DefineTechnique("exposure", new TechniqueDescriptor("exposure", VertexEntry: "main", FragmentEntry: "main", IsCompute: true, HasFragmentPush: true));
		engine.DefineTechnique("tonemap", new TechniqueDescriptor("tonemap", HasFragmentPush: true));
		engine.DefineTechnique("blit", new TechniqueDescriptor("blit"));
	}

	public RenderGraph Build(DemoScene scene) => RenderGraph.Define(g =>
	{
		var screen = g.Backbuffer;
		var world = g.Domain("world", 320, 180);
		var albedo = world.Color("albedo", Format.Rgba8Srgb);
		var normal = world.Color("normal", Format.Rgba16F);
		var position = world.Color("position", Format.Rgba16F);
		var depth = world.Depth("depth", Format.Depth32F);
		var ao = world.Color("ao", Format.R16F);
		var aoBlur = world.Color("ao_blur", Format.R16F);
		var lit = world.Color("lit", Format.Rgba16F);

		var sun = g.Domain("sun_shadow", 1024, 1024);
		var shadowMap = sun.Depth("sun_depth", Format.Depth32F);
		_view = new ViewConstants(g, "fullref.viewproj");

		g.Pass("shadow")
			.WritesDepth(shadowMap)
			.Draw(ctx =>
			{
				ctx.Draw(new DrawCall { Mesh = scene.Cube, Technique = "depthonly", View = ctx.Views[ViewKeys.Sun] });
				ctx.Draw(new DrawCall { Mesh = scene.Plane, Technique = "depthonly", View = ctx.Views[ViewKeys.Sun] });
			});

		g.Pass("gbuffer")
			.Reads(_view.Handle)
			.Writes(albedo, normal, position)
			.WritesDepth(depth)
			.Draw(ctx =>
			{
				_submit.Draw(ctx, scene.Cube, "gbuffer", Matrix4x4.Identity);
				_submit.Draw(ctx, scene.Plane, "gbuffer", Matrix4x4.Identity);
			});

		g.Pass("ssao")
			.Samples(position, normal)
			.Writes(ao)
			.Draw(ctx => ctx.FullScreen("ssao", DemoGraph.Mat(ctx.View.ViewProjectionMatrix), position, normal));

		g.Pass("ssao.blur")
			.Samples(ao)
			.Writes(aoBlur)
			.Draw(ctx => ctx.FullScreen("ssao.blur", ao));

		g.Pass("lighting")
			.Samples(albedo, normal, position, shadowMap, aoBlur)
			.Writes(lit)
			.Draw(ctx => ctx.FullScreen("lighting", DemoGraph.Mat(ctx.Views[ViewKeys.Sun].ViewProjectionMatrix), albedo, normal, position, shadowMap, aoBlur));

		g.Pass("transparent")
			.Blends(lit)
			.ReadsDepth(depth)
			.Draw(ctx => ctx.Draw(DrawCall.Create(scene.Cube, "transparent")));

		const int particleCount = 4096;
		var particles = g.Buffer("particles", particleCount * 16);

		g.Compute("particle.update")
			.ReadWrites(particles)
			.Dispatch(ctx => ctx.Dispatch("particle.update", ((uint)(particleCount / 64), 1, 1), [ctx.Time], [], [particles]));

		g.Pass("particle.draw")
			.Blends(lit)
			.ReadsVertex(particles)
			.Draw(ctx => ctx.Draw(new DrawCall { Technique = "particle.render", VertexCount = 6, Instances = DrawCall.Gpu(particles, particleCount) })); // 6 = a billboard quad fabricated per particle

		var ids = world.Color("object_id", Format.R16F);
		var seedA = world.Color("outline_a", Format.Rg16F);
		var seedB = world.Color("outline_b", Format.Rg16F);
		var outlined = world.Color("lit_outlined", Format.Rgba16F);

		_ids = ids;
		_hoverParams = g.Constants<DemoGraph.OutlineParams>("outline.params");
		g.Pass("id").Writes(ids).Draw(ctx => ctx.Draw(DrawCall.Create(scene.Cube, "id")));
		g.Readback(ids);

		g.Pass("outline.seed")
			.Samples(ids)
			.Reads(_hoverParams)
			.Writes(seedA)
			.Draw(ctx => ctx.FullScreen("outline.seed", ids));

		var (read, write) = (seedA, seedB);
		for (var step = 4; step >= 1; step >>= 1)
		{
			var src = read;
			float jump = step;
			g.Pass($"outline.jfa{step}")
				.Samples(src)
				.Writes(write)
				.Draw(ctx => ctx.FullScreen("outline.jfa", [jump, world.Width, world.Height], src));
			(read, write) = (write, read);
		}

		var flood = read;
		g.Pass("outline.resolve")
			.Samples(flood, lit)
			.Writes(outlined)
			.Draw(ctx => ctx.FullScreen("outline.resolve", flood, lit));

		var glow = g.Bloom(outlined, threshold: 1.0f, mips: 5);

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
			.Draw(ctx => ctx.FullScreen("tonemap", [ctx.HdrOutput ? 1f : 0f], glow, exposure));

		// Nearest upscale from the low-res world to the native screen.
		g.Pass("upscale").Samples(graded).Writes(screen).Draw(ctx => ctx.FullScreen("blit", graded));
		DemoGraph.Hud(g, screen);
		g.Present(screen);
	});

	// Publish this frame's camera view-projection for the g-buffer pass (shadow/id/transparent still ride the auto-push).
	public void Update(Camera camera) => _view.Publish(_engine, camera);

	public ResourceHandle? PickTarget => _ids;

	public void WriteHover(IEngine engine, uint hoveredId) =>
		engine.Write(_hoverParams, new DemoGraph.OutlineParams(new Vector4(hoveredId, 0f, 0f, 0f)));
}
