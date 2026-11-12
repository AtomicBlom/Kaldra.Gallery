using System.Numerics;
using System.Runtime.InteropServices;

using Loom.Core.Graph;
using Loom.Core.Hosting;
using Loom.Core.Materials;
using SUIsei;
using SUIsei.Composition;
using SUIsei.Drawing;
using SUIsei.Media;

using LoomDrawCommand = Loom.Core.Hosting.DrawCommand;
using LoomOverlayVertex = Loom.Core.Geometry.OverlayVertex;

namespace Loom.Gallery.Demos;

/// <summary>
/// SUIsei's vector prim on its own: a stroke in each of WinUI's joins with each kind of cap, a dashed curve, a concave
/// fill and a curved one, the same shapes rotated, a fan of hairlines, and a translucent self-crossing stroke drawn as
/// one group. SUIsei's <see cref="Tessellator"/> turns each path into triangles whose corners carry signed distances to
/// the edges they antialias, a <see cref="DrawListBuilder"/> collects them, and the demo's own pass draws the list
/// through the <c>ui</c> technique — no elements and no compositor, so nothing stands between the frame and the prim.
/// Every edge should ramp across one pixel whatever its angle, and every backend should draw the same pixels.
/// <para>
/// The list is laid out on a fixed 1280×720 canvas stretched over the target, which is 1:1 at the gallery's capture
/// size. It is built and uploaded once and submitted under a stable stream, so a frame re-draws it without staging.
/// </para>
/// </summary>
internal sealed class VectorPrimsDemo : IDemo
{
	/// <summary>The canvas the list is laid out on, in pixels.</summary>
	public static readonly Vector2 Canvas = new(1280f, 720f);

	/// <summary>The opaque linear colour the frame is cleared to before the list is drawn over it.</summary>
	public static readonly Vector4 Background = new(0.02f, 0.025f, 0.04f, 1f);


	private IEngine _engine = null!;
	private IEngine? _uploadedFor;
	private LoomOverlayVertex[] _vertices = [];
	private ushort[] _indices = [];
	private LoomDrawCommand[] _commands = [];
	private TextureRef[] _textures = [];
	private DrawListIdentity _identity;

	public string Name => "Vector Prims";

	// Draws through the ui technique, which the host's UIPainter defines.
	public void DefineTechniques(IEngine engine) => _engine = engine;

	public RenderGraph Build(DemoScene scene)
	{
		if (!ReferenceEquals(_uploadedFor, _engine))
			Upload(BuildList());

		return RenderGraph.Define(g =>
		{
			g.BackbufferClear = Background;
			var screen = g.Backbuffer;
			g.Pass("vector").Writes(screen).Draw(this, static (ctx, demo) => demo.Record(ctx));
			DemoGraph.Hud(g, screen, scene);
			g.Present(screen);
		});
	}

	/// <summary>
	/// The demo's draw list, laid out on <see cref="Canvas"/>. Built fresh by each call, from the public tessellator and
	/// builder alone, so a test can draw the same list another way and compare.
	/// </summary>
	public static DrawListData BuildList()
	{
		var tessellator = new Tessellator();
		var builder = new DrawListBuilder();
		var clip = new Rect(0f, 0f, Canvas.X, Canvas.Y);
		builder.Begin(Canvas);

		var amber = PremultipliedHdrColor.FromStraight(0.95f, 0.55f, 0.12f, 1f);
		var sky = PremultipliedHdrColor.FromStraight(0.2f, 0.6f, 1f, 1f);
		var leaf = PremultipliedHdrColor.FromStraight(0.4f, 0.9f, 0.35f, 1f);
		var rose = PremultipliedHdrColor.FromStraight(0.9f, 0.3f, 0.55f, 1f);
		var chalk = PremultipliedHdrColor.FromStraight(0.9f, 0.9f, 0.9f, 1f);

		// A zig-zag in each join, each with its own pair of caps: every join and cap WinUI has, at an acute angle.
		Stroke(ZigZag(new Vector2(90f, 70f)), Style(18f, CompositionStrokeLineJoin.Miter, CompositionStrokeCap.Flat, CompositionStrokeCap.Square), amber, Matrix3x2.Identity);
		Stroke(ZigZag(new Vector2(480f, 70f)), Style(18f, CompositionStrokeLineJoin.Bevel, CompositionStrokeCap.Round, CompositionStrokeCap.Triangle), sky, Matrix3x2.Identity);
		Stroke(ZigZag(new Vector2(870f, 70f)), Style(18f, CompositionStrokeLineJoin.Round, CompositionStrokeCap.Square, CompositionStrokeCap.Round), leaf, Matrix3x2.Identity);

		// A concave fill, a curved one, and a dashed curve with round dash caps.
		Fill(Star(new Vector2(190f, 350f)), rose, Matrix3x2.Identity);
		Fill(Circle(new Vector2(470f, 350f), 85f), sky, Matrix3x2.Identity);
		var curve = new CompositionPath();
		curve.MoveTo(new Vector2(640f, 430f));
		curve.CubicTo(new Vector2(720f, 230f), new Vector2(860f, 470f), new Vector2(960f, 270f));
		var dashed = Style(12f, CompositionStrokeLineJoin.Round, CompositionStrokeCap.Round, CompositionStrokeCap.Round);
		dashed.DashCap = CompositionStrokeCap.Round;
		dashed.SetDashArray([2f, 1.5f]);
		Stroke(curve, dashed, amber, Matrix3x2.Identity);

		// A self-crossing stroke at half opacity, drawn as one group so its crossing blends once.
		var loop = new CompositionPath();
		loop.MoveTo(new Vector2(1050f, 280f));
		loop.LineTo(new Vector2(1210f, 430f));
		loop.LineTo(new Vector2(1210f, 280f));
		loop.LineTo(new Vector2(1050f, 430f));
		var loopTessellated = new TessellatedPath();
		tessellator.Stroke(loop, Style(20f, CompositionStrokeLineJoin.Round, CompositionStrokeCap.Round, CompositionStrokeCap.Round), TessellationScale.Identity, loopTessellated);
		builder.AddVector(loopTessellated, PathTrim.None, Matrix3x2.Identity, leaf, clip, groupOpacity: 0.5f);

		// The same kinds of shape rotated: no edge left on an axis.
		var starCentre = new Vector2(190f, 600f);
		Fill(Star(starCentre), leaf, Matrix3x2.CreateRotation(MathF.PI / 6f, starCentre));
		Stroke(ZigZag(new Vector2(330f, 500f)), Style(14f, CompositionStrokeLineJoin.Miter, CompositionStrokeCap.Triangle, CompositionStrokeCap.Flat), rose,
			Matrix3x2.CreateRotation(-MathF.PI / 9f, new Vector2(490f, 560f)));
		var square = new CompositionPath();
		square.MoveTo(new Vector2(1060f, 540f));
		square.LineTo(new Vector2(1180f, 540f));
		square.LineTo(new Vector2(1180f, 660f));
		square.LineTo(new Vector2(1060f, 660f));
		square.Close();
		Stroke(square, Style(10f, CompositionStrokeLineJoin.Miter, CompositionStrokeCap.Flat, CompositionStrokeCap.Flat), amber, Matrix3x2.CreateRotation(MathF.PI / 4f, new Vector2(1120f, 600f)));

		// Hairlines one pixel thick, every 15° from horizontal to vertical.
		for (var i = 0; i <= 6; i++)
		{
			var angle = i * MathF.PI / 12f;
			var line = new CompositionPath();
			line.MoveTo(new Vector2(720f, 690f));
			line.LineTo(new Vector2(720f + 220f * MathF.Cos(angle), 690f - 220f * MathF.Sin(angle)));
			Stroke(line, Style(1f, CompositionStrokeLineJoin.Miter, CompositionStrokeCap.Flat, CompositionStrokeCap.Flat), chalk, Matrix3x2.Identity);
		}

		return builder.End();

		void Stroke(CompositionPath path, CompositionStrokeStyle style, PremultipliedHdrColor colour, Matrix3x2 transform)
		{
			var tessellated = new TessellatedPath();
			tessellator.Stroke(path, style, TessellationScale.For(transform), tessellated);
			builder.AddVector(tessellated, transform, colour, clip);
		}

		void Fill(CompositionPath path, PremultipliedHdrColor colour, Matrix3x2 transform)
		{
			var tessellated = new TessellatedPath();
			tessellator.Fill(path, TessellationScale.For(transform), tessellated);
			builder.AddVector(tessellated, transform, colour, clip);
		}
	}

	private static CompositionStrokeStyle Style(float thickness, CompositionStrokeLineJoin join, CompositionStrokeCap start, CompositionStrokeCap end) =>
		new() { Thickness = thickness, LineJoin = join, StartCap = start, EndCap = end };

	// Four legs of 80×120 px, starting at the bottom left of `origin`'s 320×120 box.
	private static CompositionPath ZigZag(Vector2 origin)
	{
		var path = new CompositionPath();
		path.MoveTo(origin + new Vector2(0f, 120f));
		for (var i = 1; i <= 4; i++)
			path.LineTo(origin + new Vector2(80f * i, i % 2 == 1 ? 0f : 120f));
		return path;
	}

	// A five-pointed star, outer radius 95 px and inner 40: a simple contour with five reflex corners.
	private static CompositionPath Star(Vector2 centre)
	{
		var path = new CompositionPath();
		for (var i = 0; i < 10; i++)
		{
			var angle = -MathF.PI / 2f + i * MathF.PI / 5f;
			var radius = i % 2 == 0 ? 95f : 40f;
			var point = centre + radius * new Vector2(MathF.Cos(angle), MathF.Sin(angle));
			if (i == 0)
				path.MoveTo(point);
			else
				path.LineTo(point);
		}
		path.Close();
		return path;
	}

	private static CompositionPath Circle(Vector2 centre, float radius)
	{
		var path = new CompositionPath();
		var radii = new Vector2(radius);
		path.MoveTo(centre + new Vector2(radius, 0f));
		path.ArcTo(radii, 0f, false, CompositionSweepDirection.Clockwise, centre - new Vector2(radius, 0f));
		path.ArcTo(radii, 0f, false, CompositionSweepDirection.Clockwise, centre + new Vector2(radius, 0f));
		path.Close();
		return path;
	}

	// Copy the list into the engine's shapes once, upload its slot table, and mint the stream it is drawn under. The
	// SUIsei and Loom vertex and command structs are byte-identical, so the copies are reinterpretations.
	private void Upload(DrawListData list)
	{
		_vertices = MemoryMarshal.Cast<SUIsei.Drawing.OverlayVertex, LoomOverlayVertex>(list.Vertices.Span).ToArray();
		_indices = list.Indices.ToArray();
		_commands = MemoryMarshal.Cast<SUIsei.Drawing.DrawCommand, LoomDrawCommand>(list.Commands.Span).ToArray();
		var slots = list.TextureSlots;
		_textures = new TextureRef[slots.Count];
		for (var i = 0; i < slots.Count; i++)
		{
			var source = slots[i];
			_textures[i] = _engine.CreateTexture(TextureData.FromRgba8(source.Width, source.Height, source.Pixels.Span, TextureFormat.Rgba8Unorm, generateMips: false));
		}
		_identity = new DrawListIdentity(_engine.AllocateDrawListStream(), list.Revision);
		_uploadedFor = _engine;
	}

	private void Record(IRenderContext ctx)
	{
		// Canvas pixels → clip, then the paper-white lift on an HDR target, exactly as UIPainter pushes them.
		Span<float> push =
		[
			2f / Canvas.X, 2f / Canvas.Y, -1f, -1f,
			ctx.HdrOutput ? _engine.HdrPaperWhiteNits / 80f : 1f, BitConverter.UInt32BitsToSingle(ctx.HdrOutput ? 1u : 0u),
		];
		ctx.DrawList("ui", _identity, _textures, _vertices, _indices, _commands, push);
	}
}
