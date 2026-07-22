using System.Numerics;

using Loom.Core.Graph;
using Loom.Core.Hosting;
using Loom.Core.Materials;

namespace Loom.Sandbox.Demos;

/// <summary>
/// GPU particles — the compute → vertex-buffer hand-off. A compute pass writes particle
/// positions into a storage <em>buffer</em> (a deterministic looping fountain driven by <c>ctx.Time</c>); the
/// render pass reads that same buffer as per-instance vertex data and additively blends glowing sparks over the
/// lit scene. Two edges — <c>.ReadWrites</c> then <c>.ReadsVertex</c> — and the engine derives the
/// compute→vertex-input barrier between them (the one piece of sync unique to this demo).
/// </summary>
internal sealed class ParticlesDemo : IDemo
{
	private IEngine _engine = null!;
	private ConstantsHandle _viewproj = null!;

	public string Name => "Particles";

	public void DefineTechniques(IEngine engine)
	{
		_engine = engine;
		engine.DefineTechnique("lit", new TechniqueDescriptor("lit", HasVertexInput: true));
		engine.DefineTechnique("particle.update", new TechniqueDescriptor("particle_update", VertexEntry: "main", FragmentEntry: "main", IsCompute: true, HasFragmentPush: true));
		engine.DefineTechnique("particle.render", new TechniqueDescriptor("particle_render", Blend: BlendMode.Additive, Instance: SandboxVertexLayouts.ParticlePosition));
		engine.DefineTechnique("blit", new TechniqueDescriptor("blit"));
	}

	public RenderGraph Build(DemoScene scene) => RenderGraph.Define(g =>
	{
		var screen = g.Backbuffer;
		var lit = g.Screen.Color("lit", Format.Rgba16F);
		var depth = g.Screen.Depth("depth", Format.Depth32F);

		const int particleCount = 4096;
		var particles = g.Buffer("particles", particleCount * 16); // float4 per particle
		_viewproj = g.Constants<Matrix4x4>("particles.viewproj");

		g.Pass("scene")
			.Reads(_viewproj)
			.Writes(lit)
			.WritesDepth(depth)
			.Draw(ctx =>
			{
				DemoDraw.Mesh(ctx, scene.Cube, "lit", Matrix4x4.Identity);
				DemoDraw.Mesh(ctx, scene.Plane, "lit", Matrix4x4.Identity);
			});

		g.Compute("particle.update")
			.ReadWrites(particles)
			.Dispatch(ctx => ctx.Dispatch("particle.update", ((uint)(particleCount / 64), 1, 1), [(float)ctx.Time], [], [particles]));

		g.Pass("particles")
			.Reads(_viewproj)
			.Blends(lit)
			.ReadsVertex(particles)
			.Draw(ctx => ctx.Draw(new DrawCall { Technique = "particle.render", VertexCount = 6, Instances = DrawCall.Gpu(particles, particleCount) })); // 6 = a billboard quad fabricated per particle

		g.Pass("blit").Samples(lit).Writes(screen).Draw(ctx => ctx.FullScreen("blit", lit));
		DemoGraph.Hud(g, screen, scene);
		g.Present(screen);
	});

	// Publish this frame's view-projection for the scene pass and the particle render pass to read.
	public void Update(Concepts.Camera camera) => _engine.Write(_viewproj, camera.ViewProjection);
}
