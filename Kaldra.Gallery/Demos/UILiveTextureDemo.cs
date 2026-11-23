using System.Numerics;

using BinaryVibrance.Kaldra.Concepts;
using BinaryVibrance.Kaldra.Core.Graph;
using BinaryVibrance.Kaldra.Core.Hosting;
using BinaryVibrance.Kaldra.Core.Materials;
using BinaryVibrance.Vysara;
using BinaryVibrance.Vysara.Controls;
using BinaryVibrance.Vysara.Extensions;
using BinaryVibrance.Vysara.Media;
using BinaryVibrance.Vysara.Media.Imaging;
using TextureAlphaMode = BinaryVibrance.Vysara.Drawing.TextureAlphaMode;
using TextureColorSpace = BinaryVibrance.Vysara.Drawing.TextureColorSpace;

namespace Kaldra.Gallery.Demos;

/// <summary>
/// Live images in a Vysara page, of the two kinds an app supplies. A minimap: an orthographic pass renders the scene from
/// straight above into a small target every frame, and the page shows it through a <see cref="HostImageSource"/> that
/// <see cref="BinaryVibrance.Kaldra.UI.Vysara.UIPainter.SetExternalTexture"/> binds to that target — a texture the GPU drew this frame,
/// never copied to the CPU. And a trace the app draws on the CPU into a <see cref="WriteableBitmap"/> every frame, which
/// the painter writes into the bitmap's one texture in place (<see cref="IEngine.UpdateTexture"/>), allocating nothing.
/// <para>
/// The UI pass declares the minimap with <c>.Samples</c>, so the engine orders it after the map pass and moves the image
/// into a sampleable layout itself; nothing in the demo names a barrier. The scene is the gallery's floor with a cube
/// spinning at its centre and three still ones around it, and the map marks where the viewer stands, so the map, the
/// marker and the trace all move.
/// </para>
/// </summary>
internal sealed class UILiveTextureDemo : IDemo
{
	/// <summary>The minimap target's size in texels, and the size the page shows it at.</summary>
	public const int MapPixels = 256;

	/// <summary>The tag the page's host image and the painter binding share.</summary>
	public const string MapTag = "uilivetexture.map";

	private const int TraceWidth = 256, TraceHeight = 56;

	// The still cubes, as (position on the floor, uniform scale).
	private static readonly (Vector3 Position, float Scale)[] Props =
	[
		(new Vector3(-2.6f, 0f, -1.4f), 0.9f),
		(new Vector3(2.3f, 0.25f, -2.4f), 1.5f),
		(new Vector3(1.6f, -0.15f, 2.5f), 0.7f),
	];

	private IEngine _engine = null!;
	private DemoScene _scene;
	private ConstantsHandle _viewproj = null!;
	private ConstantsHandle _mapViewproj = null!;
	private Camera? _mapCamera;
	private Vector3 _viewer;
	private UIElement? _page;

	// The map is sRGB-encoded colour, opaque, which is what the host image declares; the trace is straight sRGB, as an
	// image file is, and is premultiplied as it is sampled.
	private readonly HostImageSource _map = new(MapTag, MapPixels, MapPixels, TextureAlphaMode.Premultiplied, TextureColorSpace.Srgb);
	private readonly WriteableBitmap _trace = new(TraceWidth, TraceHeight);

	public string Name => "UI Live Texture";

	/// <summary>The minimap and the trace in one panel, which the shell centres over the scene; built on first use.</summary>
	public UIElement OverlayContent => _page ??= BuildPage();

	public void DefineTechniques(IEngine engine)
	{
		_engine = engine;
		engine.DefineTechnique("lit", new TechniqueDescriptor("lit", HasVertexInput: true));
		// Straight down at the floor, with -Z at the top of the map; wide enough for the whole 8×8 floor.
		_mapCamera = new Camera(engine).Orthographic(9f, 9f, 1f, 40f).LookAt(new Vector3(0f, 20f, 0f), Vector3.Zero, -Vector3.UnitZ);
	}

	public RenderGraph Build(DemoScene scene)
	{
		_scene = scene;
		return RenderGraph.Define(g =>
		{
			var screen = g.Backbuffer;
			var depth = g.Screen.Depth("depth", Format.Depth32F);
			_viewproj = g.Constants<Matrix4x4>("uilivetexture.viewproj");
			_mapViewproj = g.Constants<Matrix4x4>("uilivetexture.map.viewproj");

			var map = g.Domain("uilivetexture.map", MapPixels, MapPixels);
			var mapColor = map.Color("uilivetexture.map.color", Format.Rgba8Srgb, clearValue: new Vector4(0.05f, 0.07f, 0.11f, 1f));
			var mapDepth = map.Depth("uilivetexture.map.depth", Format.Depth32F);
			// Each graph has its own handle, so a rebuilt graph rebinds the tag.
			scene.UI.SetExternalTexture(MapTag, mapColor);

			g.Pass("map")
				.Reads(_mapViewproj)
				.Writes(mapColor)
				.WritesDepth(mapDepth)
				.Draw(this, static (ctx, demo) => demo.DrawScene(ctx, marker: true));

			g.Pass("scene")
				.Reads(_viewproj)
				.Writes(screen)
				.WritesDepth(depth)
				.Draw(this, static (ctx, demo) => demo.DrawScene(ctx, marker: false));

			DemoGraph.Hud(g, screen, scene, mapColor);
			g.Present(screen);
		});
	}

	public void Update(Camera camera)
	{
		_engine.Write(_viewproj, camera.ViewProjection);
		_engine.Write(_mapViewproj, _mapCamera!.ViewProjection);
		Matrix4x4.Invert(camera.View, out var world);
		_viewer = world.Translation;
	}

	// Redraw the trace for this frame's time and show it: the painter writes the new texels into the bitmap's texture
	// before the frame.
	public void PreFrame(double deltaSeconds, float time, Camera camera, Vector2 framebufferSize)
	{
		DrawTrace(_trace.PixelBuffer.Span, time);
		_trace.Invalidate();
	}

	// The floor, the spinning cube, the still ones — and, on the map only, a marker on the floor below the viewer.
	private void DrawScene(IRenderContext ctx, bool marker)
	{
		DemoDraw.Mesh(ctx, _scene.Plane, "lit", Matrix4x4.Identity, objectId: 1);
		DemoDraw.Mesh(ctx, _scene.Cube, "lit", Matrix4x4.CreateRotationY((float)ctx.Time * 0.8f));
		foreach (var (position, scale) in Props)
			DemoDraw.Mesh(ctx, _scene.Cube, "lit", Matrix4x4.CreateScale(scale) * Matrix4x4.CreateTranslation(position));
		if (marker)
		{
			var onFloor = Vector3.Clamp(_viewer with { Y = 0f }, new Vector3(-3.8f), new Vector3(3.8f));
			DemoDraw.Mesh(ctx, _scene.Cube, "lit", Matrix4x4.CreateScale(0.45f, 0.1f, 0.45f) * Matrix4x4.CreateTranslation(onFloor with { Y = -0.4f }));
		}
	}

	// A translucent panel: a caption, the minimap at 1:1, and the trace under it.
	private UIElement BuildPage()
	{
		var column = new StackPanel();
		column.Children.Add(new TextBlock { Text = "Minimap", FontSize = 14f, Margin = new Thickness(0f, 0f, 0f, 8f), Foreground = new SolidColorBrush(HdrColor.FromRgb(220, 226, 236)) });
		column.Children.Add(new Image { Source = _map, Stretch = Stretch.Fill, Width = MapPixels, Height = MapPixels, Margin = new Thickness(0f, 0f, 0f, 8f) });
		column.Children.Add(new Image { Source = _trace, Stretch = Stretch.Fill, Width = TraceWidth, Height = TraceHeight });
		return new Border
		{
			Padding = new Thickness(10f),
			Background = new SolidColorBrush(HdrColor.FromRgb(14, 18, 26)) { Opacity = 0.85f },
			CornerRadius = new CornerRadius(12f),
			Child = column,
		};
	}

	// A scrolling trace of two waves over a translucent backdrop with a faint grid, written as straight sRGB texels: the
	// backdrop is only partly opaque, so the page behind it shows through once the shader premultiplies it.
	private static void DrawTrace(Span<byte> rgba, float time)
	{
		for (var y = 0; y < TraceHeight; y++)
		{
			for (var x = 0; x < TraceWidth; x++)
			{
				var o = (y * TraceWidth + x) * 4;
				var grid = x % 32 == 0 || y == TraceHeight / 2;
				(rgba[o], rgba[o + 1], rgba[o + 2], rgba[o + 3]) = grid ? ((byte)60, (byte)72, (byte)90, (byte)200) : ((byte)8, (byte)10, (byte)16, (byte)120);
			}
		}

		for (var x = 0; x < TraceWidth; x++)
		{
			var phase = x * 0.06f - time * 2f;
			Plot(rgba, x, MathF.Sin(phase) * 0.7f, (255, 176, 48));
			Plot(rgba, x, MathF.Sin(phase * 0.5f + 1f) * 0.35f, (80, 200, 255));
		}
	}

	// A two-texel-tall opaque dot at column x, height v in -1..1.
	private static void Plot(Span<byte> rgba, int x, float v, (byte R, byte G, byte B) colour)
	{
		var y = (int)MathF.Round((1f - (v + 1f) * 0.5f) * (TraceHeight - 3)) + 1;
		for (var dy = 0; dy < 2; dy++)
		{
			var o = ((y + dy) * TraceWidth + x) * 4;
			(rgba[o], rgba[o + 1], rgba[o + 2], rgba[o + 3]) = (colour.R, colour.G, colour.B, 255);
		}
	}
}
