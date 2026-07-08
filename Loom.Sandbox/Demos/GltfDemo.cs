using System.Numerics;

using Loom.Assets;
using Loom.Core.Geometry;
using Loom.Core.Graph;
using Loom.Core.Hosting;
using Loom.Core.Materials;

namespace Loom.Sandbox.Demos;

/// <summary>
/// glTF model loading. Loads a committed glTF cube via <see cref="GltfLoader"/> — proving the neutral asset
/// path end to end: SharpGLTF parses the file, the loader flattens it to Loom <see cref="Vertex"/> arrays
/// (with tangents) + decoded <see cref="TextureData"/>, and the host turns those into engine meshes/textures/
/// materials. The loaded primitives are drawn through the same normal-mapped deferred path as
/// <see cref="NormalMappingDemo"/>, sitting on the shared ground plane (static geometry + base-colour/normal
/// textures).
/// </summary>
internal sealed class GltfDemo : IDemo
{
	public string Name => "glTF model";

	private readonly List<(Mesh Mesh, Material Material)> _primitives = [];
	private IEngine _engine = null!;
	private Concepts.Camera _sunCam = null!;
	private ConstantsHandle _viewproj = null!;
	private ConstantsHandle _sunViewproj = null!;

	public void DefineTechniques(IEngine engine)
	{
		_engine = engine;
		_sunCam = DemoSun.View(engine);
		engine.DefineTechnique("depthonly", new TechniqueDescriptor("depthonly", HasVertexInput: true));
		engine.DefineTechnique("gbuffer.nm", new TechniqueDescriptor("gbuffer_normalmap", HasVertexInput: true, LinearFilter: true, Address: SamplerAddress.Repeat));
		engine.DefineTechnique("light_shadow", new TechniqueDescriptor("light_shadow", HasFragmentPush: true));
	}

	public RenderGraph Build(DemoScene scene)
	{
		// Load the committed glTF once and mint engine resources from it (geometry + decoded textures). The bytes come
		// through the scene resolver — a file on desktop, an embedded resource in the browser — so the demo loads the
		// model the same way on both hosts. The cube references its brick textures by these sibling names.
		var files = new Dictionary<string, byte[]>
		{
			["cube.gltf"] = scene.Asset("cube.gltf"),
			["brick_albedo.png"] = scene.Asset("brick_albedo.png"),
			["brick_normal.png"] = scene.Asset("brick_normal.png"),
		};
		var model = GltfLoader.LoadGltf(files, "cube.gltf");
		_primitives.Clear();
		foreach (var prim in model.Primitives)
		{
			var mesh = _engine.CreateMesh(prim.Vertices, prim.Indices);
			if (prim.Material?.BaseColor is not { } baseColor || prim.Material?.Normal is not { } normalMap)
				throw new InvalidOperationException("The glTF primitive lacks a base-colour or normal texture (this demo draws with gbuffer.nm, which needs both).");
			var material = _engine.CreateMaterial(_engine.CreateTexture(baseColor), _engine.CreateTexture(normalMap));
			_primitives.Add((mesh, material));
		}

		return RenderGraph.Define(g =>
		{
			var screen = g.Backbuffer;
			var albedo = g.Screen.Color("albedo", Format.Rgba8Srgb);
			var normal = g.Screen.Color("normal", Format.Rgba16F);
			var position = g.Screen.Color("position", Format.Rgba16F);
			var depth = g.Screen.Depth("depth", Format.Depth32F);
			_viewproj = g.Constants<Matrix4x4>("gltf.viewproj");
			_sunViewproj = g.Constants<Matrix4x4>("gltf.sun.viewproj");

			var sun = g.Domain("sun_shadow", 1024, 1024);
			var shadowMap = sun.Depth("sun_depth", Format.Depth32F);

			g.Pass("shadow")
				.Reads(_sunViewproj)
				.WritesDepth(shadowMap)
				.Draw(ctx =>
				{
					foreach (var (mesh, _) in _primitives)
						DemoDraw.Mesh(ctx, mesh, "depthonly", Matrix4x4.Identity);
					DemoDraw.Mesh(ctx, scene.Plane, "depthonly", Matrix4x4.Identity);
				});

			g.Pass("gbuffer")
				.Reads(_viewproj)
				.Writes(albedo, normal, position)
				.WritesDepth(depth)
				.Draw(ctx =>
				{
					foreach (var (mesh, material) in _primitives)
						DemoDraw.Mesh(ctx, mesh, "gbuffer.nm", Matrix4x4.Identity, 0, material);
					DemoDraw.Mesh(ctx, scene.Plane, "gbuffer.nm", Matrix4x4.Identity, 0, scene.NormalMapped); // the ground, brick-textured
				});

			g.Pass("lighting")
				.Samples(albedo, normal, position, shadowMap)
				.Writes(screen)
				.Draw(ctx => ctx.FullScreen("light_shadow", DemoGraph.Mat(_sunCam.ViewProjection), albedo, normal, position, shadowMap));

			DemoGraph.Hud(g, screen, scene);
			g.Present(screen);
		});
	}

	// Publish the camera's view-projection (g-buffer pass) and the sun's (shadow depth pass + the lighting shadow test).
	public void Update(Concepts.Camera camera)
	{
		_engine.Write(_viewproj, camera.ViewProjection);
		_engine.Write(_sunViewproj, _sunCam.ViewProjection);
	}
}
