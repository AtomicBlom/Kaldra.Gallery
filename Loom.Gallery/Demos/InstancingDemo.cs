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
/// <para>
/// Uploaded once (<c>uploadOnce</c>), the same field goes to the GPU a single time as an <see cref="InstanceBuffer"/>,
/// row by row, and each frame draws ranges of it (<see cref="DrawCall.Static(InstanceBuffer, int, int)"/>): the runs of
/// rows whose bounds meet the frustum, nothing uploaded. It draws the same picture, which the tests hold it to. With
/// <c>rangeSize</c> each run is drawn as draws of at most that many instances, so most start part way into the buffer,
/// which is where the backends differ (an offset vertex-buffer view, or a bind offset).
/// </para>
/// </summary>
internal sealed class InstancingDemo(bool uploadOnce = false, int rangeSize = 0) : IDemo
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

	// Uploaded once: the field in rows, each row's box, and the runs of rows to draw this frame
	private InstanceBuffer? _stored;
	private Bounds[] _rows = [];
	private readonly List<(int First, int Count)> _runs = [];

	public string Name => uploadOnce ? "Instancing, uploaded once" : "Instancing + Cull";

	/// <summary>How many of the last frame's draws started part way into the uploaded buffer (a first instance past 0).</summary>
	public int OffsetRanges { get; private set; }

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
		var transforms = new Matrix4x4[GridSize * GridSize];
		_rows = new Bounds[GridSize];
		for (var ix = 0; ix < GridSize; ix++)
		{
			Vector3 lo = new(float.MaxValue), hi = new(float.MinValue);
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
				transforms[ix * GridSize + iz] = transform;
				var reach = new Vector3(CubeScale);   // a spun cube stays inside its scale either way, and then some
				(lo, hi) = (Vector3.Min(lo, new Vector3(x, y, z) - reach), Vector3.Max(hi, new Vector3(x, y, z) + reach));
			}
			_rows[ix] = new Bounds(lo, hi);
		}
		_total = _group.Count;
		_visible = new Matrix4x4[_total];
		_drawn = _total;
		if (uploadOnce)
		{
			if (_stored is { } old)
				_engine.ReleaseInstances(old);
			_stored = _engine.CreateInstances<Matrix4x4>(transforms, VertexLayout.Transform);
			_runs.Clear();
			_runs.Add((0, _total));
		}

		return RenderGraph.Define(g =>
		{
			var screen = g.Backbuffer;
			var depth = g.Screen.Depth("depth", Format.Depth32F);
			_viewproj = g.Constants<Matrix4x4>("instanced.viewproj");

			g.Pass("instances")
				.Reads(_viewproj)
				.Writes(screen)
				.WritesDepth(depth)
				// The app hands the engine the culled draw list — the visible transforms compacted in Update(), or the
				// runs of visible rows of the buffer uploaded once.
				.Draw(ctx =>
				{
					if (_stored is { } stored)
					{
						foreach (var (first, count) in _runs)
							ctx.Draw(new DrawCall { Mesh = scene.Cube, Technique = "instanced", Instances = DrawCall.Static(stored, first, count) });
					}
					else
					{
						ctx.Draw(new DrawCall { Mesh = scene.Cube, Technique = "instanced", Instances = DrawCall.From(_visible.AsSpan(0, _drawn)) });
					}
				});

			DemoGraph.Hud(g, screen, scene);
			g.Present(screen);
		});
	}

	public void Update(Concepts.Camera camera)
	{
		_engine.Write(_viewproj, camera.ViewProjection);
		var frustum = camera.ExtractFrustum();
		if (!uploadOnce)
		{
			_drawn = _group.Cull(frustum, _visible);
			return;
		}
		// Neighbouring visible rows join into one run, so one draw covers them
		_runs.Clear();
		_drawn = 0;
		for (var row = 0; row < _rows.Length; row++)
		{
			if (!frustum.Intersects(_rows[row]))
				continue;
			if (_runs.Count > 0 && _runs[^1].First + _runs[^1].Count == row * GridSize)
				_runs[^1] = (_runs[^1].First, _runs[^1].Count + GridSize);
			else
				_runs.Add((row * GridSize, GridSize));
			_drawn += GridSize;
		}

		// Split into draws of at most rangeSize instances, each but a run's first starting part way into the buffer
		if (rangeSize > 0)
		{
			for (var i = _runs.Count - 1; i >= 0; i--)
			{
				var (first, count) = _runs[i];
				if (count <= rangeSize)
					continue;
				_runs.RemoveAt(i);
				for (var start = first; start < first + count; start += rangeSize)
					_runs.Insert(i + (start - first) / rangeSize, (start, Math.Min(rangeSize, first + count - start)));
			}
		}
		OffsetRanges = 0;
		foreach (var (first, _) in _runs)
		{
			if (first > 0)
				OffsetRanges++;
		}
	}

	public string? HudStatus => uploadOnce
		? $"instances {_drawn}/{_total} drawn in {_runs.Count} ranges of one buffer, uploaded once"
		: $"instances {_drawn}/{_total} drawn ({_total - _drawn} culled)";
}
