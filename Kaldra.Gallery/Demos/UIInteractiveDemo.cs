using System.Numerics;

using BinaryVibrance.Kaldra.Core.Graph;
using BinaryVibrance.Kaldra.Core.Hosting;
using BinaryVibrance.Kaldra.Core.Materials;
#if KALDRA_WEB
using Kaldra.Web.UI;
#else
using Kaldra.Gallery.UI;
#endif
using BinaryVibrance.Vysara;

namespace Kaldra.Gallery.Demos;

/// <summary>
/// The interactive, data-bound UI proof. The render graph is the same simple lit scene as
/// <see cref="CubeLightingDemo"/>; what's new is the demo's own UI — a <c>UIInteractiveView</c> (a XAML page with a
/// <c>Button</c> and an <c>{x:Bind}</c> label) that the host centres in the shared demo shell and drives with the
/// shell's <c>UIHost</c> + pointer input, so clicking the button updates the bound text. The graph itself just declares
/// the scene pass and the shared <see cref="DemoGraph.Hud"/>, which draws the shell and so the page.
/// </summary>
internal sealed class UIInteractiveDemo : IDemo
{
	private IEngine _engine = null!;
	private ConstantsHandle _viewproj = null!;
	private UIInteractiveView? _view;

	public string Name => "UI Interactive";

	/// <summary>The page: a button and an <c>{x:Bind}</c> label, built on first use.</summary>
	public UIElement OverlayContent => _view ??= new UIInteractiveView();

	public void DefineTechniques(IEngine engine)
	{
		_engine = engine;
		engine.DefineTechnique("lit", new TechniqueDescriptor("lit", HasVertexInput: true));
	}

	public RenderGraph Build(DemoScene scene) => RenderGraph.Define(g =>
	{
		var screen = g.Backbuffer;
		var depth = g.Screen.Depth("depth", Format.Depth32F);
		_viewproj = g.Constants<Matrix4x4>("uiinteractive.viewproj");

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

	// A capture clicks Increment once, so the captured frame shows the bound label counting it.
	public Vector2? CaptureClick(Concepts.Camera camera, Vector2 framebufferSize)
	{
		if (_view is null)
			return null;
		var button = _view.IncrementButton;
		var centre = button.TransformToVisual(null).TransformPoint(new Point(button.LayoutRect.Width / 2f, button.LayoutRect.Height / 2f));
		return new Vector2(centre.X, centre.Y);
	}
}
