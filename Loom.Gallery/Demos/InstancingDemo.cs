using System.Numerics;
using Loom.Core.Geometry;
using Loom.Core.Graph;
using Loom.Core.Hosting;
using Loom.Core.Materials;
using Loom.Core.Scene;

namespace Loom.Gallery.Demos;

/// <summary>
/// GPU instancing + CPU frustum culling. A dense field of cubes — one mesh, one draw — each placed by a
/// per-instance world matrix carried in the per-instance vertex stream (<see cref="VertexLayout.Transform"/>),
/// not baked into geometry. Each frame the demo extracts the camera's <see cref="Frustum"/>, culls the
/// app-owned <see cref="Scene"/> against it on the CPU, and hands only the surviving transforms to
/// <c>ctx.Draw</c> with a <c>DrawCall.Instances</c> source; the HUD reports drawn vs culled. Culling stays optional and app-owned; the engine
/// just offers the Scene + cull helper.
/// </summary>
internal sealed class InstancingDemo : IDemo
{
	private const int GridSize = 50;        // 50×50 = 2500 cubes
	private const float Spacing = 0.5f;
	private const float CubeScale = 0.16f;

	private InstancedGroup _group = null!;
	private Matrix4x4[] _visible = [];
	private int _drawn;
	private int _total;
	private IEngine _engine = null!;
	private ConstantsHandle _viewproj = null!;

	public string Name => "Instancing + Cull";

	public void DefineTechniques(IEngine engine)
	{
		_engine = engine;
		engine.DefineTechnique("instanced", new TechniqueDescriptor(
			"instanced", HasVertexInput: true, Layout: VertexLayout.MeshStandard, Instance: VertexLayout.Transform));
	}

	public RenderGraph Build(DemoScene scene)
	{
		// Build the field once into an app-owned Scene. Reuses the shared unit cube — its MeshStandard layout is
		// exactly the instanced technique's per-vertex layout, so no new geometry is needed.
		var world = new Scene();
		_group = world.AddInstanced(scene.Cube, "instanced");
		var localBounds = Bounds.FromCenterExtents(Vector3.Zero, new Vector3(0.5f)); // the unit cube spans ±0.5
		var half = (GridSize - 1) * Spacing * 0.5f;
		for (var ix = 0; ix < GridSize; ix++)
			for (var iz = 0; iz < GridSize; iz++)
			{
				var x = ix * Spacing - half;
				var z = iz * Spacing - half;
				var y = MathF.Sin(x * 0.6f) * MathF.Cos(z * 0.6f) * 0.6f; // a gentle undulation, so it reads as 3D
				var angle = (ix * 7 + iz * 13) * 0.15f;                   // per-instance spin exercises the matrix
				var transform = Matrix4x4.CreateScale(CubeScale)
					* Matrix4x4.CreateRotationY(angle)
					* Matrix4x4.CreateTranslation(x, y, z);
				_group.Add(transform, localBounds);
			}
		_total = _group.Count;
		_visible = new Matrix4x4[_total];
		_drawn = _total;

		return RenderGraph.Define(g =>
		{
			var screen = g.Backbuffer;
			var depth = g.Screen.Depth("depth", Format.Depth32F);
			_viewproj = g.Constants<Matrix4x4>("instanced.viewproj");

			g.Pass("instances")
				.Reads(_viewproj)
				.Writes(screen)
				.WritesDepth(depth)
				// The app hands the engine the culled draw list — the visible transforms compacted in Update().
				.Draw(ctx => ctx.Draw(new DrawCall { Mesh = scene.Cube, Technique = "instanced", Instances = DrawCall.From(_visible.AsSpan(0, _drawn)) }));

			DemoGraph.Hud(g, screen, scene);
			g.Present(screen);
		});
	}

	public void Update(Concepts.Camera camera)
	{
		_engine.Write(_viewproj, camera.ViewProjection);
		_drawn = _group.Cull(camera.ExtractFrustum(), _visible);
	}

	public string? HudStatus => $"instances {_drawn}/{_total} drawn ({_total - _drawn} culled)";
}
