using System.Numerics;

using Loom.Core.Graph;
using Loom.Core.Hosting;
using Loom.Gallery.UI;
using SUIsei;

namespace Loom.Gallery.Demos;

/// <summary>
/// SUIsei's vector UI through XAML, drawn by the engine: a <c>VectorUIView</c> page of WinUI shapes (<c>Rectangle</c>,
/// <c>Ellipse</c>, <c>Line</c>, <c>Polyline</c>, <c>Polygon</c>, and <c>Path</c> from path markup), every stroke join
/// and cap, dash patterns, linear gradients on fills and strokes, a translucent stroke that crosses itself,
/// <c>UIElement.Clip</c>, and an <c>ImageBrush</c> on rounded borders. SUIsei extensions sit beside them:
/// <c>suisei:StrokeTrim</c> drawing a trail on in a loop, <c>suisei:StrokeBrushMapping.Mode="AlongLength"</c> running its
/// gradient along the trail rather than across the canvas, and <c>suisei:TextEffects</c> giving text a gradient outline,
/// a soft drop shadow, a hard shadow, and an outline over a shadow. Lap times beside them are set in tabular figures
/// (<c>Typography.NumeralAlignment="Tabular"</c>), over a line of the default font's symbols past ASCII (<c>°</c>,
/// <c>·</c>, <c>→</c>).
/// <para>
/// The page is the demo's <see cref="OverlayContent"/>, so the shared shell shows it and the gallery's capture renders
/// it; the graph only clears the frame and draws the shell. Everything on the page reaches the GPU as SUIsei's draw
/// list through the <c>ui</c> technique: shapes as tessellated vector triangles, the translucent stroke opaque inside a
/// group layer that carries its alpha, the image through the vector prim, since a rounded rect's own prim has no room
/// for a texture, and each text effect as glyph quads drawn from the atlas's true-distance channel, under the glyphs in
/// the same command.
/// </para>
/// <para>
/// The page's font is SUIsei's default, Cascadia Code, whose digits all advance alike, so its tabular lap times look as
/// proportional ones would; a font with proportional digits is what shows the setting change anything.
/// </para>
/// </summary>
internal sealed class VectorUIDemo : IDemo
{
	/// <summary>The opaque linear colour behind the page.</summary>
	public static readonly Vector4 Background = new(0.002f, 0.0025f, 0.004f, 1f);

	private VectorUIView? _view;
	private bool _drawingOn;

	public string Name => "Vector UI";

	/// <summary>The page, built on first use.</summary>
	public UIElement OverlayContent => _view ??= new VectorUIView();

	// Draws through the ui technique, which the host's UIPainter defines.
	public void DefineTechniques(IEngine engine) { }

	public RenderGraph Build(DemoScene scene) => RenderGraph.Define(g =>
	{
		g.BackbufferClear = Background;
		var screen = g.Backbuffer;
		// The hud pass blends onto what is already there, so a pass that writes the frame comes first: writing it
		// clears it to Background, and there is nothing else to draw.
		g.Pass("background").Writes(screen).Draw(static _ => { });
		DemoGraph.Hud(g, screen, scene);
		g.Present(screen);
	});

	// The page is in the host's tree by the first frame, so its draw-on storyboard can begin on the host's clock.
	public void PreFrame(double deltaSeconds, float time, Concepts.Camera camera, Vector2 framebufferSize)
	{
		if (_drawingOn || _view is null)
			return;
		_view.StartDrawOn();
		_drawingOn = true;
	}
}
