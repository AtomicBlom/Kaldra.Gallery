using System.Numerics;

using Loom.Assets;
using Loom.Core.Geometry;
using Loom.Core.Graph;
using Loom.Core.Hosting;
using Loom.Core.Materials;

namespace Loom.Gallery.Demos;

/// <summary>
/// The draw-throughput workload: thousands of <em>discrete</em> <c>ctx.Draw</c> calls in one pass, each with its own
/// model matrix and its own material. Every other dense demo here is the opposite shape — one instanced draw placing
/// thousands of objects — which measures the GPU and says nothing about what a draw call costs the CPU. This one is
/// deliberately the un-batchable case, so the per-draw path is the whole cost: a pipeline lookup, a uniform-block
/// upload, a descriptor allocation, and a geometry bind, once per object per frame.
/// <para>
/// Two techniques alternate in blocks rather than one-by-one, because that is how an engine-fed draw list actually
/// arrives — sorted by state, so consecutive draws mostly share a pipeline. That ordering is what makes redundant
/// per-draw pipeline re-binds worth eliding, and an interleaved list would hide the win rather than measure it.
/// Materials cycle within each block so consecutive draws differ in their descriptor table but not their pipeline.
/// <c>material.pbr</c> carries a base-colour sampler and <c>material.unlit</c> carries none, so both the
/// descriptor-table path and the uniforms-only path are on the workload.
/// </para>
/// <para>
/// Everything the pass closure touches is built once in <see cref="Build"/> and only read per frame — the objects
/// array, its matrices, and its material handles — so a measured frame allocates nothing and the number reflects the
/// engine rather than this file.
/// </para>
/// </summary>
internal sealed class ScaleDemo : IDemo
{
	/// <summary>
	/// How many discrete draws the demo issues when nobody says otherwise. The interactive gallery and the headless
	/// scale test both take this unless constructed with an explicit count.
	/// </summary>
	public const int DefaultDrawCount = 200;

	/// <summary>How many distinct materials the draws cycle through, per technique.</summary>
	private const int MaterialsPerTechnique = 8;

	private const float Spacing = 0.55f;
	private const float CubeScale = 0.3f;

	private readonly int _drawCount;

	private IEngine _engine = null!;
	private Concepts.Camera _cam = null!;
	private ConstantsHandle _viewproj = null!;
	private Mesh _cube;
	private Placement[] _objects = [];
	private float _orbitRadius = 12f;

	public ScaleDemo() : this(DefaultDrawCount) { }

	public ScaleDemo(int drawCount) =>
		_drawCount = drawCount > 0
			? drawCount
			: throw new ArgumentOutOfRangeException(nameof(drawCount), drawCount, "The scale workload needs at least one draw.");

	/// <summary>One placed object: where it goes, what shades it, and which pipeline that needs.</summary>
	private readonly record struct Placement(Matrix4x4 Model, Material Material, string Technique);

	public string Name => "Draw Scale";

	public void DefineTechniques(IEngine engine)
	{
		_engine = engine;
		_cam = new Concepts.Camera(engine);
		engine.DefineTechnique("material.pbr", new TechniqueDescriptor("material_pbr", HasVertexInput: true));
		engine.DefineTechnique("material.unlit", new TechniqueDescriptor("material_unlit", HasVertexInput: true));
	}

	public RenderGraph Build(DemoScene scene)
	{
		_cube = scene.Cube;
		_objects = BuildField();

		return RenderGraph.Define(g =>
		{
			var screen = g.Backbuffer;
			var depth = g.Screen.Depth("depth", Format.Depth32F);
			_viewproj = g.Constants<Matrix4x4>("scale.viewproj");

			g.Pass("scale")
				.Reads(_viewproj)
				.Writes(screen)
				.WritesDepth(depth)
				.Draw(ctx =>
				{
					// Indexed rather than foreach-over-a-property: the array is the demo's own stable state, read live
					// at execute time, and walking it by index keeps the closure free of any per-frame temporary.
					var objects = _objects;
					for (var i = 0; i < objects.Length; i++)
						DemoDraw.Mesh(ctx, _cube, objects[i].Technique, objects[i].Model, 0, objects[i].Material);
				});

			DemoGraph.Hud(g, screen, scene);
			g.Present(screen);
		});
	}

	// A cube lattice sized to hold the requested draw count, centred on the origin. Sorted by technique: the first
	// half of the field is pbr, the second unlit, so the pass issues two runs of same-pipeline draws.
	private Placement[] BuildField()
	{
		var uploader = new MaterialUploader(_engine);
		var pbr = new Material[MaterialsPerTechnique];
		var unlit = new Material[MaterialsPerTechnique];
		for (var i = 0; i < MaterialsPerTechnique; i++)
		{
			var hue = i / (float)MaterialsPerTechnique;
			var colour = Hue(hue);
			pbr[i] = uploader.Upload("material.pbr", new ModelMaterial
			{
				BaseColorFactor = new Vector4(colour, 1f),
				MetallicFactor = i % 2 == 0 ? 0f : 1f,
				RoughnessFactor = 0.15f + (0.75f * hue),
			});
			unlit[i] = _engine.CreateMaterial("material.unlit", new Vector4(colour * 0.8f, 1f));
		}

		var side = (int)Math.Ceiling(Math.Cbrt(_drawCount));
		var half = (side - 1) * Spacing * 0.5f;
		_orbitRadius = MathF.Max(8f, side * Spacing * 2.2f);

		var objects = new Placement[_drawCount];
		for (var i = 0; i < _drawCount; i++)
		{
			var x = (i % side * Spacing) - half;
			var y = (i / side % side * Spacing) - half;
			var z = (i / (side * side) * Spacing) - half;

			// Per-object spin, so the model matrix is genuinely distinct per draw rather than a translation the
			// hardware could have shared — the per-draw uniform block has to carry real data.
			var angle = (i % 37) * 0.17f;
			var model = Matrix4x4.CreateScale(CubeScale)
				* Matrix4x4.CreateRotationY(angle)
				* Matrix4x4.CreateRotationX(angle * 0.5f)
				* Matrix4x4.CreateTranslation(x, y, z);

			var firstHalf = i < _drawCount / 2;
			objects[i] = firstHalf
				? new Placement(model, pbr[i % MaterialsPerTechnique], "material.pbr")
				: new Placement(model, unlit[i % MaterialsPerTechnique], "material.unlit");
		}
		return objects;
	}

	private static Vector3 Hue(float t)
	{
		var a = t * MathF.Tau;
		return new Vector3(
			0.5f + (0.45f * MathF.Sin(a)),
			0.5f + (0.45f * MathF.Sin(a + 2.094f)),
			0.5f + (0.45f * MathF.Sin(a + 4.189f)));
	}

	// Pulled back far enough that the whole lattice is on screen at whatever count the demo was built with, so the
	// workload is visibly the thing being measured rather than a wall of near-plane cubes.
	public Concepts.Camera? GetCamera(float aspect, float time)
	{
		var angle = time * 0.25f;
		var eye = new Vector3(MathF.Sin(angle) * _orbitRadius, _orbitRadius * 0.35f, MathF.Cos(angle) * _orbitRadius);
		return _cam.Perspective(0.8f, aspect, 0.1f, 500f).LookAt(eye, Vector3.Zero);
	}

	public void Update(Concepts.Camera camera) => _engine.Write(_viewproj, camera.ViewProjection);

	public string? HudStatus => $"{_drawCount} discrete draws";
}
