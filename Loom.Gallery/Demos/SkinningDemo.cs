using System.Numerics;

using CommunityToolkit.Mvvm.ComponentModel;

using Loom.Assets;
using Loom.Core.Animation;
using Loom.Core.Geometry;
using Loom.Core.Graph;
using Loom.Core.Hosting;
using Loom.Core.Materials;

using SUIsei;
using SUIsei.Controls;

namespace Loom.Gallery.Demos;

/// <summary>
/// Skeletal animation via GPU compute pre-skin. Loads a committed rigged glTF (a 2-bone
/// bar with a "bend" clip), then each frame: the CPU <see cref="AnimationSampler"/> samples the clip at
/// <c>ctx.Time</c> into a bone palette, a compute pass skins the bind-pose vertices once into a transient buffer
/// (<c>ctx.DispatchSkin</c>), and the scene pass draws that skinned output with the ordinary forward <c>lit</c>
/// technique (<c>ctx.DrawSkinned</c>) — the payoff of the compute-pre-skin decision: skin once, draw with
/// unchanged techniques. The <c>.ReadWrites</c> → <c>.ReadsVertex</c> edges let the engine derive the
/// compute→vertex barrier, exactly as for GPU particles.
/// </summary>
internal sealed partial class SkinningDemo : ObservableObject, IDemo
{
	public string Name => "Skinning";

	private Mesh _mesh;
	private int _vertexCount;
	private Skeleton _skeleton = null!;
	private AnimationClip _clip = null!;
	private Matrix4x4[] _palette = [];
	private Matrix4x4[] _jointWorld = [];
	private IEngine _engine = null!;
	private ConstantsHandle _viewproj = null!;

	[ObservableProperty]
	private bool _showSkeleton = true; // skeleton overlay (bones + joint axes), on by default for the demo

	public void DefineTechniques(IEngine engine)
	{
		_engine = engine;
		engine.DefineTechnique("skin", TechniqueDescriptor.Compute("skin", hasFragmentPush: true));
		engine.DefineTechnique("lit", new TechniqueDescriptor("lit", HasVertexInput: true));
		engine.DefineTechnique("blit", new TechniqueDescriptor("blit"));
	}

	// The skeleton as world-space line segments — each bone (parent→joint) plus a small axis gizmo per
	// joint. The launcher projects these to screen and draws them as the debug overlay.
	public IReadOnlyList<(Vector3 A, Vector3 B)> DebugWorldLines(float time)
	{
		if (!ShowSkeleton || _skeleton.JointCount == 0)
			return [];
		if (_jointWorld.Length != _skeleton.JointCount)
			_jointWorld = new Matrix4x4[_skeleton.JointCount];
		var t = _clip.Duration > 0f ? time % _clip.Duration : 0f;
		AnimationSampler.EvaluateJointWorld(_skeleton, _clip, t, _jointWorld);

		var lines = new List<(Vector3, Vector3)>();
		const float axis = 0.18f;
		for (var j = 0; j < _skeleton.JointCount; j++)
		{
			var w = _jointWorld[j];
			var o = w.Translation;
			var parent = _skeleton.ParentIndices[j];
			if (parent >= 0)
				lines.Add((_jointWorld[parent].Translation, o)); // bone
			lines.Add((o, o + new Vector3(w.M11, w.M12, w.M13) * axis)); // local X
			lines.Add((o, o + new Vector3(w.M21, w.M22, w.M23) * axis)); // local Y
			lines.Add((o, o + new Vector3(w.M31, w.M32, w.M33) * axis)); // local Z
		}
		return lines;
	}

	private UIElement? _debugPanel;

	public UIElement? DebugPanel => _debugPanel ??= BuildDebugPanel();

	private UIElement BuildDebugPanel()
	{
		var skeleton = new CheckBox { Content = "Show skeleton", IsChecked = ShowSkeleton };
		skeleton.Checked += (_, _) => ShowSkeleton = true;
		skeleton.Unchecked += (_, _) => ShowSkeleton = false;
		var panel = new StackPanel();
		panel.Children.Add(skeleton);
		return panel;
	}

	// Load the committed rigged bar and mint a skinned mesh + capture its skeleton/clip for per-frame sampling. The
	// GLB bytes come through the scene resolver (a file read on desktop, an HTTP fetch in the browser).
	public async Task PreloadAsync(DemoScene scene)
	{
		var bytes = await scene.Asset("rigged_bar.glb");
		var model = GltfLoader.LoadGlb(bytes);
		if (model.Skeleton is null || model.Clips.Count == 0)
			throw new InvalidOperationException("The rigged bar asset is not rigged (no skeleton/clips) — the skinning demo needs an animated glTF.");
		var prim = model.Primitives[0];
		_mesh = await _engine.CreateSkinnedMeshAsync(prim.SkinnedVertices!, prim.Indices);
		_vertexCount = prim.SkinnedVertices!.Length;
		_skeleton = model.Skeleton;
		_clip = model.Clips[0];
		_palette = new Matrix4x4[_skeleton.JointCount];
	}

	public RenderGraph Build(DemoScene scene)
	{
		return RenderGraph.Define(g =>
		{
			var screen = g.Backbuffer;
			var lit = g.Screen.Color("lit", Format.Rgba16F);
			var depth = g.Screen.Depth("depth", Format.Depth32F);
			var skinned = g.Buffer("skinned", _vertexCount, VertexLayout.MeshStandard); // the pre-skin compute's output
			_viewproj = g.Constants<Matrix4x4>("skinning.viewproj");

			// Pre-skin the bar once: sample the clip at this frame's time (looping) into the palette, then dispatch.
			g.Compute("skin")
				.ReadWrites(skinned)
				.Dispatch(ctx =>
				{
					var t = _clip.Duration > 0f ? (float)(ctx.Time % _clip.Duration) : 0f;
					AnimationSampler.Evaluate(_skeleton, _clip, t, _palette);
					ctx.DispatchSkin("skin", _mesh, _palette, skinned, _vertexCount);
				});

			// Draw the skinned output (forward lit) + the ground plane; the .ReadsVertex edge derives the barrier.
			g.Pass("scene")
				.Reads(_viewproj)
				.Writes(lit)
				.WritesDepth(depth)
				.ReadsVertex(skinned)
				.Draw(ctx =>
				{
					DemoDraw.Skinned(ctx, _mesh, skinned, "lit", Matrix4x4.Identity);
					DemoDraw.Mesh(ctx, scene.Plane, "lit", Matrix4x4.Identity);
				});

			g.Pass("blit").Samples(lit).Writes(screen).Draw(ctx => ctx.FullScreen("blit", lit));
			DemoGraph.Hud(g, screen, scene);
			g.Present(screen);
		});
	}

	// Publish this frame's view-projection for the scene pass to read.
	public void Update(Concepts.Camera camera) => _engine.Write(_viewproj, camera.ViewProjection);
}
