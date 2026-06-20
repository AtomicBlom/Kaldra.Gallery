using System.Numerics;

using Loom.Assets;
using Loom.Core;

namespace Loom.Sandbox.Demos;

/// <summary>
/// Skeletal animation via GPU compute pre-skin (PlanningPhase2.md M15). Loads a committed rigged glTF (a 2-bone
/// bar with a "bend" clip), then each frame: the CPU <see cref="AnimationSampler"/> samples the clip at
/// <c>ctx.Time</c> into a bone palette, a compute pass skins the bind-pose vertices once into a transient buffer
/// (<c>ctx.DispatchSkin</c>), and the scene pass draws that skinned output with the ordinary forward <c>lit</c>
/// technique (<c>ctx.DrawSkinned</c>) — the payoff of the compute-pre-skin decision: skin once, draw with
/// unchanged techniques. The <c>.ReadWrites</c> → <c>.ReadsVertex</c> edges let the engine derive the
/// compute→vertex barrier, exactly as for GPU particles.
/// </summary>
internal sealed class SkinningDemo : IDemo
{
	public string Name => "Skinning";

	private Mesh _mesh;
	private int _vertexCount;
	private Skeleton _skeleton = null!;
	private AnimationClip _clip = null!;
	private Matrix4x4[] _palette = [];

	public void DefineTechniques(IEngine engine)
	{
		// The Slang compute entry is `computeMain` (so the build's `-entry computeMain` DXIL emit finds it), but
		// slangc renames a lone compute entry to "main" in the SPIR-V — so the Vulkan entry is "main", exactly as
		// particle_update is declared.
		engine.DefineTechnique("skin", new TechniqueDescriptor("skin", VertexEntry: "main", FragmentEntry: "main", IsCompute: true, HasFragmentPush: true));
		engine.DefineTechnique("lit", new TechniqueDescriptor("lit", HasVertexInput: true));
		engine.DefineTechnique("blit", new TechniqueDescriptor("blit"));

		// Load the committed rigged bar and mint a skinned mesh + capture its skeleton/clip for per-frame sampling.
		var path = Path.Combine(AppContext.BaseDirectory, "assets", "rigged_bar.glb");
		var model = GltfLoader.Load(path);
		if (model.Skeleton is null || model.Clips.Count == 0)
			throw new InvalidOperationException($"'{path}' is not rigged (no skeleton/clips) — the skinning demo needs an animated glTF.");
		var prim = model.Primitives[0];
		_mesh = engine.CreateSkinnedMesh(prim.SkinnedVertices!, prim.Indices);
		_vertexCount = prim.SkinnedVertices!.Length;
		_skeleton = model.Skeleton;
		_clip = model.Clips[0];
		_palette = new Matrix4x4[_skeleton.JointCount];
	}

	public RenderGraph Build(DemoScene scene) => RenderGraph.Define(g =>
	{
		var screen = g.Backbuffer;
		var lit = g.Screen.Color("lit", Format.Rgba16F);
		var depth = g.Screen.Depth("depth", Format.Depth32F);
		var skinned = g.Buffer("skinned", _vertexCount * 48); // MeshStandard-shaped (48 bytes) compute output

		// Pre-skin the bar once: sample the clip at this frame's time (looping) into the palette, then dispatch.
		g.Compute("skin")
			.ReadWrites(skinned)
			.Dispatch(ctx =>
			{
				var t = _clip.Duration > 0f ? ctx.Time % _clip.Duration : 0f;
				AnimationSampler.Evaluate(_skeleton, _clip, t, _palette);
				ctx.DispatchSkin("skin", _mesh, _palette, skinned, _vertexCount);
			});

		// Draw the skinned output (forward lit) + the ground plane; the .ReadsVertex edge derives the barrier.
		g.Pass("scene")
			.Writes(lit)
			.WritesDepth(depth)
			.ReadsVertex(skinned)
			.Draw(ctx =>
			{
				ctx.DrawSkinned(_mesh, skinned, "lit");
				ctx.Draw(scene.Plane, "lit");
			});

		g.Pass("blit").Samples(lit).Writes(screen).Draw(ctx => ctx.FullScreen("blit", lit));
		DemoGraph.Hud(g, screen);
		g.Present(screen);
	});
}
