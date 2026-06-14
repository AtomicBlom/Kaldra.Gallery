using Loom.Core;

namespace Loom.Sandbox.Demos;

/// <summary>
/// GPU particles (Planning.md §7.6) — the compute → vertex-buffer hand-off. A compute pass writes particle
/// positions into a storage <em>buffer</em> (a deterministic looping fountain driven by <c>ctx.Time</c>); the
/// render pass reads that same buffer as per-instance vertex data and additively blends glowing sparks over the
/// lit scene. Two edges — <c>.ReadWrites</c> then <c>.ReadsVertex</c> — and the engine derives the
/// compute→vertex-input barrier between them (the one piece of sync unique to this demo).
/// </summary>
internal sealed class ParticlesDemo : IDemo
{
	public string Name => "Particles";

	public void DefineTechniques(IEngine engine)
	{
		engine.DefineTechnique("lit", new TechniqueDescriptor("lit", HasVertexInput: true));
		engine.DefineTechnique("particle.update", new TechniqueDescriptor("particle_update", VertexEntry: "main", FragmentEntry: "main", IsCompute: true, HasFragmentPush: true));
		engine.DefineTechnique("particle.render", new TechniqueDescriptor("particle_render", Blend: BlendMode.Additive, ParticleInput: true));
		engine.DefineTechnique("blit", new TechniqueDescriptor("blit"));
	}

	public RenderGraph Build(DemoScene scene) => RenderGraph.Define(g =>
	{
		var screen = g.Backbuffer;
		var lit = g.Screen.Color("lit", Format.Rgba16F);
		var depth = g.Screen.Depth("depth", Format.Depth32F);

		const int particleCount = 4096;
		var particles = g.Buffer("particles", particleCount * 16); // float4 per particle

		g.Pass("scene")
			.Writes(lit)
			.WritesDepth(depth)
			.Draw(ctx =>
			{
				ctx.Draw(scene.Cube, "lit");
				ctx.Draw(scene.Plane, "lit");
			});

		g.Compute("particle.update")
			.ReadWrites(particles)
			.Dispatch(ctx => ctx.Dispatch("particle.update", ((uint)(particleCount / 64), 1, 1), [ctx.Time], [], [particles]));

		g.Pass("particles")
			.Blends(lit)
			.ReadsVertex(particles)
			.Draw(ctx => ctx.DrawParticles(particles, particleCount, "particle.render"));

		g.Pass("blit").Samples(lit).Writes(screen).Draw(ctx => ctx.FullScreen("blit", lit));
		DemoGraph.Hud(g, screen);
		g.Present(screen);
	});
}
