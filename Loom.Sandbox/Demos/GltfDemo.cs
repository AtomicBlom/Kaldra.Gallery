using Loom.Assets;
using Loom.Core.Geometry;
using Loom.Core.Graph;
using Loom.Core.Hosting;
using Loom.Core.Materials;

namespace Loom.Sandbox.Demos;

/// <summary>
/// glTF model loading (PlanningPhase2.md M11). Loads a committed glTF cube via <see cref="GltfLoader"/> —
/// proving the neutral asset path end to end: SharpGLTF parses the file, the loader flattens it to Loom
/// <see cref="Vertex"/> arrays (with tangents) + decoded <see cref="TextureData"/>, and the host turns those
/// into engine meshes/textures/materials. The loaded primitives are then drawn through the same normal-mapped
/// deferred path as <see cref="NormalMappingDemo"/>, sitting on the shared ground plane. (Skinning/animation
/// and PBR are later milestones; M11 loads static geometry + base-colour/normal textures.)
/// </summary>
internal sealed class GltfDemo : IDemo
{
	public string Name => "glTF model";

	private readonly List<(Mesh Mesh, Material Material)> _primitives = [];

	public void DefineTechniques(IEngine engine)
	{
		engine.DefineTechnique("depthonly", new TechniqueDescriptor("depthonly", HasVertexInput: true));
		engine.DefineTechnique("gbuffer.nm", new TechniqueDescriptor("gbuffer_normalmap", HasVertexInput: true, LinearFilter: true, Address: SamplerAddress.Repeat));
		engine.DefineTechnique("light_shadow", new TechniqueDescriptor("light_shadow", HasFragmentPush: true));

		// Load the committed glTF once and mint engine resources from it (geometry + decoded textures).
		var path = Path.Combine(AppContext.BaseDirectory, "assets", "cube.gltf");
		var model = GltfLoader.Load(path);
		_primitives.Clear();
		foreach (var prim in model.Primitives)
		{
			var mesh = engine.CreateMesh(prim.Vertices, prim.Indices);
			if (prim.Material?.BaseColor is not { } baseColor || prim.Material?.Normal is not { } normalMap)
				throw new InvalidOperationException($"glTF primitive at '{path}' lacks a base-colour or normal texture (the M11 demo draws with gbuffer.nm, which needs both).");
			var material = engine.CreateMaterial(engine.CreateTexture(baseColor), engine.CreateTexture(normalMap));
			_primitives.Add((mesh, material));
		}
	}

	public RenderGraph Build(DemoScene scene) => RenderGraph.Define(g =>
	{
		var screen = g.Backbuffer;
		var albedo = g.Screen.Color("albedo", Format.Rgba8Srgb);
		var normal = g.Screen.Color("normal", Format.Rgba16F);
		var position = g.Screen.Color("position", Format.Rgba16F);
		var depth = g.Screen.Depth("depth", Format.Depth32F);

		var sun = g.Domain("sun_shadow", 1024, 1024);
		var shadowMap = sun.Depth("sun_depth", Format.Depth32F);

		g.Pass("shadow")
			.WritesDepth(shadowMap)
			.Draw(ctx =>
			{
				foreach (var (mesh, _) in _primitives)
					ctx.Draw(mesh, "depthonly", ctx.SunView);
				ctx.Draw(scene.Plane, "depthonly", ctx.SunView);
			});

		g.Pass("gbuffer")
			.Writes(albedo, normal, position)
			.WritesDepth(depth)
			.Draw(ctx =>
			{
				foreach (var (mesh, material) in _primitives)
					ctx.Draw(mesh, material, "gbuffer.nm");
				ctx.Draw(scene.Plane, scene.NormalMapped, "gbuffer.nm"); // the ground, brick-textured
			});

		g.Pass("lighting")
			.Samples(albedo, normal, position, shadowMap)
			.Writes(screen)
			.Draw(ctx => ctx.FullScreen("light_shadow", DemoGraph.Mat(ctx.SunView.ViewProjection), albedo, normal, position, shadowMap));

		DemoGraph.Hud(g, screen);
		g.Present(screen);
	});
}
