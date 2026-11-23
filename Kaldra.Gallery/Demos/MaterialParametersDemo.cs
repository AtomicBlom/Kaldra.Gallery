using System.Numerics;

using BinaryVibrance.Kaldra.Assets;
using BinaryVibrance.Kaldra.Core.Geometry;
using BinaryVibrance.Kaldra.Core.Graph;
using BinaryVibrance.Kaldra.Core.Hosting;
using BinaryVibrance.Kaldra.Core.Materials;

using BinaryVibrance.Vysara;
using BinaryVibrance.Vysara.Controls;

namespace Kaldra.Gallery.Demos;

/// <summary>
/// The per-material parameter channel: one mesh, one technique, eleven materials that look nothing alike. Every
/// sphere here is the same icosphere drawn with the same <c>material.pbr</c> technique — what differs is the
/// parameter block each material carries, which the engine stages into the <c>kaldraMaterial</c> constant buffer the
/// shader declares.
/// <para>
/// The top row is dielectric (metallic 0) and the bottom row metal (metallic 1), each sweeping roughness left to
/// right, which is the classic way to read whether a surface model is actually responding to its inputs. The sphere
/// in front is emissive and its base colour cycles — rewritten every frame with <c>engine.Write(material, …)</c>, so
/// it shows that a material's parameters are live data and not baked into the graph. Nothing rebuilds; the graph is
/// built once like every other demo's.
/// </para>
/// <para>
/// The materials are built through <see cref="MaterialUploader"/> from <see cref="ModelMaterial"/>s assembled in
/// code rather than loaded from a file — the same path <see cref="GltfDemo"/> takes for a real asset, minus the
/// asset. None of them supplies a base-colour texture, so each gets the uploader's neutral 1×1 stand-in and shades
/// from its factors alone, which is glTF's own rule for an absent channel.
/// </para>
/// </summary>
internal sealed class MaterialParametersDemo : IDemo
{
	private const int Columns = 5;
	private const float Spacing = 1.5f;

	// Hand-picked rather than an even sweep, because roughness is not perceptually linear: 0.76 and 1.0 are under a
	// percent apart on screen, while 0.05 and 0.11 differ only in the size of a highlight dot. These five each read
	// as a distinctly different surface.
	private static readonly float[] RoughnessSweep = [0.05f, 0.15f, 0.35f, 0.6f, 1.0f];

	private IEngine _engine = null!;
	private Concepts.Camera _cam = null!;
	private ConstantsHandle _viewproj = null!;
	private Mesh _sphere;
	private float _time;
	private readonly List<(Material Material, Matrix4x4 Model)> _spheres = [];
	private Material _animated;
	private float _emissiveStrength = 1.6f;

	public string Name => "Material Parameters";

	public void DefineTechniques(IEngine engine)
	{
		_engine = engine;
		_cam = new Concepts.Camera(engine);
		engine.DefineTechnique("material.pbr", new TechniqueDescriptor("material_pbr", HasVertexInput: true));

		var (vertices, indices) = Geometry.BuildIcosphere(3);
		_sphere = engine.CreateMesh(vertices, indices);

		// One uploader for the whole demo so the eleven materials share a single 1x1 stand-in rather than uploading
		// their own. How many textures it uploads comes from material.pbr's reflection — one sampler, base colour.
		var materials = new MaterialUploader(engine);

		// Two sweeps: dielectric on top, metal below, roughness increasing left to right.
		foreach (var (metallic, row) in new[] { (0f, 0.9f), (1f, -0.9f) })
		{
			for (var i = 0; i < Columns; i++)
			{
				var roughness = RoughnessSweep[i];
				var material = materials.Upload("material.pbr", new ModelMaterial
				{
					BaseColorFactor = new Vector4(0.85f, 0.55f, 0.25f, 1f),
					MetallicFactor = metallic,
					RoughnessFactor = roughness,
				});
				var x = (i - (Columns - 1) / 2f) * Spacing;
				_spheres.Add((material, Matrix4x4.CreateTranslation(x, row, 0f)));
			}
		}

		// The mutating one, in front of the grid. Its factors are overwritten every frame in Update.
		_animated = materials.Upload("material.pbr", Animated(0f));
	}

	// GetCamera runs once per frame before Update, so it is also where this demo picks up the frame's time for the
	// animated material — Update itself is handed only a camera.
	public Concepts.Camera? GetCamera(float aspect, float time)
	{
		_time = time;
		return _cam.Perspective(0.7f, aspect, 0.1f, 100f).LookAt(new Vector3(0f, -0.35f, 11f), new Vector3(0f, -0.35f, 0f));
	}

	public RenderGraph Build(DemoScene scene) => RenderGraph.Define(g =>
	{
		var screen = g.Backbuffer;
		var depth = g.Screen.Depth("depth", Format.Depth32F);
		_viewproj = g.Constants<Matrix4x4>("materialparams.viewproj");

		g.Pass("scene")
			.Reads(_viewproj)
			.Writes(screen)
			.WritesDepth(depth)
			.Draw(ctx =>
			{
				// The closure reads the live list rather than capturing the models by value — the demo's own
				// stable state, which is what a pass closure is allowed to reach for.
				foreach (var (material, model) in _spheres)
					DemoDraw.Mesh(ctx, _sphere, "material.pbr", model, 0, material);
				// Below the grid rather than in front of it: sitting in the middle it covered the whole top of the
				// centre column, so that sphere's highlight — the thing the roughness sweep exists to show — was
				// invisible. Nearer the camera than the grid, so it still reads as a separate foreground object.
				DemoDraw.Mesh(ctx, _sphere, "material.pbr", Matrix4x4.CreateScale(1.2f) * Matrix4x4.CreateTranslation(0f, -2.1f, 2.5f), 0, _animated);
			});

		DemoGraph.Hud(g, screen, scene);
		g.Present(screen);
	});

	public void Update(Concepts.Camera camera)
	{
		_engine.Write(_viewproj, camera.ViewProjection);

		// Rewriting a material's parameter block takes effect on the next draw. No graph rebuild, no technique
		// re-registration — the block is staged into the per-frame uniform ring as part of recording the draw.
		var factors = GltfMaterialFactors.From(Animated(_time));
		_engine.Write(_animated, in factors);
	}

	// A hue sweep through the base colour plus a matching emissive lift, so the mutation is legible whether or not
	// the key light happens to be facing the sphere.
	private ModelMaterial Animated(float time)
	{
		var hue = time * 0.35f;
		var colour = new Vector3(
			0.5f + (0.5f * MathF.Sin(hue)),
			0.5f + (0.5f * MathF.Sin(hue + 2.094f)),
			0.5f + (0.5f * MathF.Sin(hue + 4.189f)));
		return new ModelMaterial
		{
			BaseColorFactor = new Vector4(colour, 1f),
			MetallicFactor = 0.1f,
			RoughnessFactor = 0.35f,
			EmissiveFactor = colour * _emissiveStrength * 0.25f,
		};
	}

	private UIElement? _debugPanel;

	public UIElement? DebugPanel => _debugPanel ??= BuildDebugPanel();

	private UIElement BuildDebugPanel()
	{
		var panel = new StackPanel();
		panel.Children.Add(DebugSlider.Labeled("Emissive", _emissiveStrength, 0f, 4f, v => _emissiveStrength = v));
		return panel;
	}
}
