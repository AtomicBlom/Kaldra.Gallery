using System.Numerics;

using Loom.Core.Graph;
using Loom.Core.Hosting;
using Loom.Core.Materials;
using SUIsei;
using SUIsei.Controls;

namespace Loom.Gallery.Demos;

/// <summary>
/// Group-opacity proof. The graph is the same lit scene + overlay <c>ui</c> pass as the other UI demos; what it
/// exercises is the demo's own UI: a faded container holding overlapping translucent children. True group opacity
/// renders the whole subtree once at full strength and fades the result a single time — so the overlap region stays the
/// same colour as the non-overlap. The backend proves this by rendering the faded subtree into a pooled offscreen
/// target and compositing it back at the group alpha (the GPU twin of the software rasterizer's layer stack); a
/// per-child fade would visibly darken the overlap. This is the cross-backend parity gate — the only UI path whose
/// pixels depend on the backend.
/// </summary>
internal sealed class UIGroupOpacityDemo : IDemo
{
	private IEngine _engine = null!;
	private ConstantsHandle _viewproj = null!;
	private UIElement? _panel;

	public string Name => "UI Group Opacity";

	/// <summary>The faded panel over two overlapping fills, built on first use.</summary>
	public UIElement OverlayContent => _panel ??= BuildPanel();

	public void DefineTechniques(IEngine engine)
	{
		_engine = engine;
		engine.DefineTechnique("lit", new TechniqueDescriptor("lit", HasVertexInput: true));
	}

	public RenderGraph Build(DemoScene scene) => RenderGraph.Define(g =>
	{
		var screen = g.Backbuffer;
		var depth = g.Screen.Depth("depth", Format.Depth32F);
		_viewproj = g.Constants<Matrix4x4>("uigroupopacity.viewproj");

		g.Pass("scene")
			.Reads(_viewproj)
			.Writes(screen)
			.WritesDepth(depth)
			.Draw(ctx =>
			{
				DemoDraw.Mesh(ctx, scene.Cube, "lit", Matrix4x4.Identity);
				DemoDraw.Mesh(ctx, scene.Plane, "lit", Matrix4x4.Identity);
			});

		DemoGraph.Hud(g, screen, scene);
		g.Present(screen);
	});

	// Publish this frame's view-projection for the scene pass to read.
	public void Update(Concepts.Camera camera) => _engine.Write(_viewproj, camera.ViewProjection);

	// A 50%-opacity container over a Canvas of two overlapping opaque fills of the same colour. True group opacity
	// composites the subtree once and fades the result, so the overlap stays the same colour as the non-overlap;
	// folding the fade per child would darken the overlap.
	private static UIElement BuildPanel()
	{
		var overlapCanvas = new Canvas { Width = 240f, Height = 180f };
		var fillA = new Border { Width = 150f, Height = 120f, CornerRadius = 8f, Background = HdrColor.FromRgb(80, 170, 255) };
		var fillB = new Border { Width = 150f, Height = 120f, CornerRadius = 8f, Background = HdrColor.FromRgb(80, 170, 255) };
		Canvas.SetLeft(fillA, 0f);
		Canvas.SetTop(fillA, 0f);
		Canvas.SetLeft(fillB, 80f);
		Canvas.SetTop(fillB, 50f);
		overlapCanvas.Children.Add(fillA);
		overlapCanvas.Children.Add(fillB);
		return new Border
		{
			Margin = new Thickness(48f, 48f, 0f, 0f),
			HorizontalAlignment = HorizontalAlignment.Left,
			VerticalAlignment = VerticalAlignment.Top,
			Padding = new Thickness(24f),
			Background = HdrColor.FromRgb(18, 22, 30),
			CornerRadius = 14f,
			Opacity = 0.5f,
			Child = overlapCanvas,
		};
	}
}
