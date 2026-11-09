using System.Numerics;

using Loom.Core.Graph;
using Loom.Core.Hosting;
using Loom.Core.Materials;

namespace Loom.Gallery.Demos;

/// <summary>
/// GPU particles — the compute → vertex-buffer hand-off. A compute pass writes particle
/// positions into a storage <em>buffer</em> (a deterministic looping fountain driven by <c>ctx.Time</c>); the
/// render pass reads that same buffer as per-instance vertex data and additively blends glowing sparks over the
/// lit scene. Two edges — <c>.ReadWrites</c> then <c>.ReadsVertex</c> — and the engine derives the
/// compute→vertex-input barrier between them (the one piece of sync unique to this demo). The fountain's shape is a
/// <c>g.Constants</c> block the compute pass <c>.Reads</c>, as a draw reads one, written once; the time, which changes
/// every frame, rides the push.
/// </summary>
internal sealed class ParticlesDemo : IDemo
{
	private IEngine _engine = null!;
	private ConstantsHandle _viewproj = null!;
	private ConstantsHandle _fountain = null!;
	private bool _fountainWritten;   // once per graph: the block keeps its last write

	// The fountain's shape (Shaders/particle_fountain.slang): x how fast a particle loops, y the swirl, z the spread at
	// its base, w how much it widens as it rises; then the height it starts at and how high it rises
	[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
	private readonly record struct Fountain(Vector4 Shape, Vector4 Rise);

	public string Name => "Particles";

	public void DefineTechniques(IEngine engine)
	{
		_engine = engine;
		engine.DefineTechnique("lit", new TechniqueDescriptor("lit", HasVertexInput: true));
		engine.DefineTechnique("particle.update", TechniqueDescriptor.Compute("particle_fountain", hasFragmentPush: true));
		engine.DefineTechnique("particle.render", new TechniqueDescriptor("particle_render", Blend: BlendMode.Additive, Instance: SandboxVertexLayouts.ParticlePosition));
		engine.DefineTechnique("blit", new TechniqueDescriptor("blit"));
	}

	public RenderGraph Build(DemoScene scene) => RenderGraph.Define(g =>
	{
		var screen = g.Backbuffer;
		var lit = g.Screen.Color("lit", Format.Rgba16F);
		var depth = g.Screen.Depth("depth", Format.Depth32F);

		const int particleCount = 4096;
		var particles = g.Buffer("particles", particleCount, SandboxVertexLayouts.ParticlePosition);
		_viewproj = g.Constants<Matrix4x4>("particles.viewproj");
		_fountain = g.Constants<Fountain>("particles.fountain");
		_fountainWritten = false;

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
			.Reads(_fountain)
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

	// Publish this frame's view-projection for the scene pass and the particle render pass to read, and the fountain's
	// shape once, the first frame of its graph.
	public void Update(Concepts.Camera camera)
	{
		_engine.Write(_viewproj, camera.ViewProjection);
		if (_fountainWritten) return;
		_engine.Write(_fountain, new Fountain(new Vector4(0.22f, 0.6f, 0.15f, 0.9f), new Vector4(-0.45f, 3.0f, 0f, 0f)));
		_fountainWritten = true;
	}
}
