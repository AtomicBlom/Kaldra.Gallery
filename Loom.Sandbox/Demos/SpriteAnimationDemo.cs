using System.Numerics;

using Loom.Assets;
using Loom.Core.Geometry;
using Loom.Core.Graph;
using Loom.Core.Hosting;
using Loom.Core.Materials;

namespace Loom.Sandbox.Demos;

/// <summary>
/// Sprite-sheet frame animation (PlanningPhase2.md M14-5, Track A — extends M14-1). The same batched-sprite 2D
/// deferred path as <see cref="SpritesDemo"/>, but the per-instance atlas uv-rect is advanced over time: the CPU
/// picks the current frame from <c>ctx.Time</c> (fps × the <see cref="SpinnerAtlas"/> frame grid) and writes that
/// frame's uv-rect into each <see cref="SpriteInstance"/> before the instanced draw. The instances are
/// CPU-uploaded every frame anyway (that is what <c>DrawInstanced</c> does), so animation is free — no shader
/// state, no per-sprite buffers. Each sprite gets a phase offset (frame + i), so the grid shows the spinner at
/// different angles at once — proof the frame is data-driven per instance, not a global uniform.
/// <para>
/// Frame selection is pulled from <c>ctx.Time</c> inside the pass closure (never captured), keeping the graph
/// frame-independent (§8) — the same reason the M14-3 lights orbit from <c>ctx.Time</c>. Camera is orthographic.
/// </para>
/// </summary>
internal sealed class SpriteAnimationDemo : IDemo
{
	private const int WorldWidth = 320;
	private const int WorldHeight = 180;
	private const float Fps = 6f; // chunky pixel-art cadence; at the frozen capture Time=1.0 this lands mid-cycle

	private Material _atlas;
	private SpriteSheet _sheet = new(1, 1);
	private SpriteInstance[] _sprites = [];

	public string Name => "Sprite Animation";

	public void DefineTechniques(IEngine engine)
	{
		engine.DefineTechnique("sprite", new TechniqueDescriptor(
			"gbuffer_sprite", HasVertexInput: true, Layout: VertexLayout.MeshStandard, Instance: SandboxVertexLayouts.Sprite));
		engine.DefineTechnique("compose2d", new TechniqueDescriptor("compose2d"));
		engine.DefineTechnique("blit", new TechniqueDescriptor("blit"));

		_atlas = SpinnerAtlas.CreateMaterial(engine, out _sheet);

		// A 6×3 field of spinners; each animates with a per-sprite phase offset (set per frame in the closure).
		const int cols = 6, rows = 3;
		var sprites = new SpriteInstance[cols * rows];
		for (var r = 0; r < rows; r++)
			for (var c = 0; c < cols; c++)
			{
				var i = r * cols + c;
				var pos = new Vector2((c + 0.5f) * (WorldWidth / (float)cols), (r + 0.5f) * (WorldHeight / (float)rows));
				sprites[i] = SpriteInstance.At(pos, new Vector2(34f, 34f), _sheet.Uv(0));
			}
		_sprites = sprites;
	}

	// The same centred orthographic view as the other 2D demos (the backend handles the Y-flip).
	public Camera? GetCamera(float aspect, float time)
	{
		var eye = new Vector3(WorldWidth * 0.5f, WorldHeight * 0.5f, 10f);
		var view = Matrix4x4.CreateLookAt(eye, eye with { Z = 0f }, Vector3.UnitY);
		var projection = Matrix4x4.CreateOrthographic(WorldWidth, WorldHeight, 0.1f, 100f);
		return new Camera(view, projection);
	}

	public RenderGraph Build(DemoScene scene) => RenderGraph.Define(g =>
	{
		var screen = g.Backbuffer;
		var world = g.Domain("anim2d", WorldWidth, WorldHeight);
		var albedo = world.Color("anim_albedo", Format.Rgba8Srgb);
		var normal = world.Color("anim_normal", Format.Rgba8Unorm);
		var emissive = world.Color("anim_emissive", Format.Rgba8Srgb);
		var lit = world.Color("anim_lit", Format.Rgba16F);

		// One instanced draw — but the uv-rects are re-stamped from ctx.Time each frame, so the sprites animate.
		g.Pass("sprites")
			.Writes(albedo, normal, emissive)
			.Draw(ctx =>
			{
				var frame = (int)(ctx.Time * Fps);
				Span<SpriteInstance> anim = stackalloc SpriteInstance[_sprites.Length];
				for (var i = 0; i < _sprites.Length; i++)
					anim[i] = _sprites[i] with { UvRect = _sheet.Uv(frame + i) }; // +i: per-sprite phase offset
				ctx.Draw(new DrawCall { Mesh = scene.Quad, Material = _atlas, Technique = "sprite", Instances = DrawCall.From(anim) });
			});

		// Reuse the M14-1 fixed-key-light compose (the focus here is the animation, not the lighting).
		g.Pass("compose")
			.Samples(albedo, normal, emissive)
			.Writes(lit)
			.Draw(ctx => ctx.FullScreen("compose2d", albedo, normal, emissive));

		g.Pass("upscale")
			.Samples(lit)
			.Writes(screen)
			.Draw(ctx => ctx.FullScreen("blit", lit));

		DemoGraph.Hud(g, screen);
		g.Present(screen);
	});

	public string HudStatus => $"{_sprites.Length} animated sprites @ {Fps:0}fps ({SpinnerAtlas.Frames} frames)";
}
