using System.Numerics;
using Loom.Concepts;
using Loom.Core.Geometry;
using Loom.Core.Graph;
using Loom.Core.Hosting;
using Loom.Core.Materials;

namespace Loom.Sandbox.Demos;

/// <summary>
/// Foliage instancing + wind + alpha-test. A dense field of grass blades, each an instanced yaw-rotated
/// card swayed by a vertex-shader wind (height-weighted + per-instance phase), alpha-tested to its blade
/// silhouette (a plain <c>discard</c> — no special pipeline state). The blades + ground write the deferred
/// g-buffer and are shaded by a simple directional light. The field is frustum-culled on the CPU each frame
/// and the drawn/total count is reported on the HUD.
/// </summary>
internal sealed class FoliageDemo : IDemo
{
	public string Name => "Foliage + wind";

	// One blade: world position + scale, then yaw + wind phase + bend stiffness (matches SandboxVertexLayouts.Foliage).
	private readonly record struct Blade(Vector4 PosScale, Vector4 YawPhaseStiffness);

	private const int Grid = 48; // 48×48 blades
	private const float FieldHalf = 12f;
	private const float BladeRadius = 1.0f; // cull bound

	private Mesh _ground;
	private Mesh _card;
	private Material _bladeMaterial;
	private Blade[] _field = [];
	private Blade[] _visible = [];
	private int _visibleCount;
	private readonly float[] _wind = new float[8];
	private IEngine _engine = null!;
	private ViewConstants _view = null!;
	private readonly DrawSubmitter _submit = new();

	public void DefineTechniques(IEngine engine)
	{
		_engine = engine;
		engine.DefineTechnique("gbuffer", new TechniqueDescriptor("gbuffer", HasVertexInput: true));
		engine.DefineTechnique("foliage", new TechniqueDescriptor("foliage", HasVertexInput: true, Layout: VertexLayout.MeshStandard, Instance: SandboxVertexLayouts.Foliage));
		engine.DefineTechnique("lighting_basic", new TechniqueDescriptor("lighting_basic"));
		engine.DefineTechnique("blit", new TechniqueDescriptor("blit"));

		// Ground (a flat green-ish plane the gbuffer shades).
		const float s = FieldHalf + 2f, y = 0f;
		var up = Vector3.UnitY;
		var t = new Vector4(1, 0, 0, 1);
		Vertex[] ground =
		[
			new(new Vector3(-s, y, -s), up, new Vector2(0, 0), t),
			new(new Vector3(s, y, -s), up, new Vector2(1, 0), t),
			new(new Vector3(s, y, s), up, new Vector2(1, 1), t),
			new(new Vector3(-s, y, s), up, new Vector2(0, 1), t),
		];
		_ground = engine.CreateMesh(ground, [0, 1, 2, 0, 2, 3]);

		// A blade card: x ∈ [-0.5,0.5], y ∈ [0,1] (base→tip), UV v=1 at base / v=0 at tip.
		Vertex[] card =
		[
			new(new Vector3(-0.5f, 0f, 0f), up, new Vector2(0, 1), t),
			new(new Vector3(0.5f, 0f, 0f), up, new Vector2(1, 1), t),
			new(new Vector3(0.5f, 1f, 0f), up, new Vector2(1, 0), t),
			new(new Vector3(-0.5f, 1f, 0f), up, new Vector2(0, 0), t),
		];
		_card = engine.CreateMesh(card, [0, 1, 2, 0, 2, 3]);
		_bladeMaterial = engine.CreateMaterial(engine.CreateTexture(BuildBladeTexture()));

		// Build the field: a jittered grid of blades, each with a random yaw / wind phase / scale.
		var rng = new Random(1234);
		_field = new Blade[Grid * Grid];
		for (var i = 0; i < _field.Length; i++)
		{
			var gx = i % Grid;
			var gz = i / Grid;
			var x = (gx / (float)(Grid - 1) - 0.5f) * 2f * FieldHalf + ((float)rng.NextDouble() - 0.5f) * 0.6f;
			var z = (gz / (float)(Grid - 1) - 0.5f) * 2f * FieldHalf + ((float)rng.NextDouble() - 0.5f) * 0.6f;
			var scale = 0.8f + (float)rng.NextDouble() * 0.6f;
			var yaw = (float)rng.NextDouble() * MathF.Tau;
			var phase = (float)rng.NextDouble() * MathF.Tau;
			var stiffness = 1.2f + (float)rng.NextDouble() * 1.0f;
			_field[i] = new Blade(new Vector4(x, 0f, z, scale), new Vector4(yaw, phase, stiffness, 0f));
		}
		_visible = new Blade[_field.Length];
	}

	// CPU frustum cull each frame + publish the camera view-projection the g-buffer/foliage passes read.
	public void Update(Camera camera)
	{
		_view.Publish(_engine, camera);
		var frustum = camera.ExtractFrustum();
		_visibleCount = 0;
		foreach (var blade in _field)
		{
			var center = new Vector3(blade.PosScale.X, blade.PosScale.W * 0.5f, blade.PosScale.Z);
			if (frustum.Intersects(center, BladeRadius * blade.PosScale.W))
				_visible[_visibleCount++] = blade;
		}
	}

	public Camera? GetCamera(float aspect, float time)
	{
		var view = Matrix4x4.CreateLookAt(new Vector3(0f, 2.2f, 11f), new Vector3(0f, 0.8f, 0f), Vector3.UnitY);
		var projection = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 3f, aspect, 0.1f, 60f);
		return new Camera(view, projection);
	}

	public string HudStatus => $"foliage: {_visibleCount}/{_field.Length} blades drawn";

	public RenderGraph Build(DemoScene scene) => RenderGraph.Define(g =>
	{
		var screen = g.Backbuffer;
		var albedo = g.Screen.Color("albedo", Format.Rgba8Srgb);
		var normal = g.Screen.Color("normal", Format.Rgba16F);
		var position = g.Screen.Color("position", Format.Rgba16F);
		var depth = g.Screen.Depth("depth", Format.Depth32F);
		var lit = g.Screen.Color("lit", Format.Rgba16F);
		_view = new ViewConstants(g, "foliage.viewproj");

		g.Pass("gbuffer")
			.Reads(_view.Handle)
			.Writes(albedo, normal, position)
			.WritesDepth(depth)
			.Draw(ctx =>
			{
				_submit.Draw(ctx, _ground, "gbuffer", Matrix4x4.Identity);
				// time + wind params reach the vertex stage through the uniform seam.
				_wind[0] = 1f; _wind[1] = 0.35f; _wind[2] = ctx.Time; _wind[3] = 0f; // dir.xy, time
				_wind[4] = 0.18f; _wind[5] = 2.2f; _wind[6] = 0f; _wind[7] = 0f;     // amplitude, frequency
				ctx.Draw(new DrawCall { Mesh = _card, Material = _bladeMaterial, Technique = "foliage", Instances = DrawCall.From<Blade>(_visible.AsSpan(0, _visibleCount)), Uniforms = DrawCall.Bytes(_wind) });
			});

		g.Pass("lighting")
			.Samples(albedo, normal, position)
			.Writes(lit)
			.Draw(ctx => ctx.FullScreen("lighting_basic", albedo, normal, position));

		g.Pass("blit").Samples(lit).Writes(screen).Draw(ctx => ctx.FullScreen("blit", lit));
		DemoGraph.Hud(g, screen);
		g.Present(screen);
	});

	// A tapered green blade with an alpha cutout — wide at the base (v=1), narrowing to the tip (v=0).
	private static TextureData BuildBladeTexture()
	{
		const int w = 24, h = 48;
		var rgba = new byte[w * h * 4];
		for (var py = 0; py < h; py++)
		{
			var v = py / (float)(h - 1);          // 0 top (tip) → 1 bottom (base)
			var halfWidth = float.Lerp(0.06f, 0.42f, v); // narrow tip, wide base
			var shade = float.Lerp(0.65f, 0.30f, v);     // lighter tip, darker base
			for (var px = 0; px < w; px++)
			{
				var u = px / (float)(w - 1);
				var inside = MathF.Abs(u - 0.5f) < halfWidth;
				var o = (py * w + px) * 4;
				rgba[o + 0] = (byte)(0.20f * shade * 255);            // R
				rgba[o + 1] = (byte)(shade * 255);                    // G (green blade)
				rgba[o + 2] = (byte)(0.15f * shade * 255);            // B
				rgba[o + 3] = inside ? (byte)255 : (byte)0;           // alpha cutout
			}
		}
		return TextureData.FromRgba8(w, h, rgba, TextureFormat.Rgba8Srgb, generateMips: false);
	}
}
