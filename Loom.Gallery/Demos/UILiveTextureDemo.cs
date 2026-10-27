using System.Numerics;

using Loom.Concepts;
using Loom.Core.Geometry;
using Loom.Core.Graph;
using Loom.Core.Hosting;
using Loom.Core.Materials;
using Loom.Core.Primitives;

namespace Loom.Gallery.Demos;

/// <summary>
/// A minimap: an orthographic pass renders the scene from straight above into a small target every frame, and a
/// panel in the corner of the screen shows that target while it is still warm — a texture the GPU drew this frame,
/// sampled by a draw list through a <see cref="TextureRef"/> rather than uploaded from CPU pixels. The panel's pass
/// declares the target with <c>.Samples</c>, so the engine orders it after the map pass and moves the image into a
/// sampleable layout itself; nothing in the demo names a barrier.
/// <para>
/// The scene is the gallery's floor with a cube spinning at its centre and three still ones around it, and the map
/// marks where the viewer stands, so both the map's content and the marker move as the camera orbits.
/// </para>
/// </summary>
internal sealed class UILiveTextureDemo : IDemo
{
	/// <summary>The minimap target's size in texels, and the size it is shown at in framebuffer pixels.</summary>
	public const int MapPixels = 256;

	private const float Margin = 24f, Inset = 8f, Radius = 12f;
	private const uint KindRoundedRect = 1, KindImage = 4;
	// UIPainter's paper-white lift for an HDR target: UIPaperWhiteNits / 80.
	private const float HdrPaperWhite = 200f / 80f;

	// The still cubes, as (position on the floor, uniform scale).
	private static readonly (Vector3 Position, float Scale)[] Props =
	[
		(new Vector3(-2.6f, 0f, -1.4f), 0.9f),
		(new Vector3(2.3f, 0.25f, -2.4f), 1.5f),
		(new Vector3(1.6f, -0.15f, 2.5f), 0.7f),
	];

	private IEngine _engine = null!;
	private IEngine? _uploadedFor;
	private ConstantsHandle _viewproj = null!;
	private ConstantsHandle _mapViewproj = null!;
	private DemoScene _scene;
	private Camera? _mapCamera;
	private Vector3 _viewer;
	private Vector2 _framebuffer = new(1280f, 720f);

	// The panel's draw list: a rounded backdrop sampling the white texel, and the map over it sampling the target.
	private readonly TextureRef[] _textures = new TextureRef[2];
	private readonly OverlayVertex[] _vertices = new OverlayVertex[8];
	private readonly ushort[] _indices = [0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7];
	private readonly DrawCommand[] _commands = new DrawCommand[2];

	public string Name => "UI Live Texture";

	public void DefineTechniques(IEngine engine)
	{
		_engine = engine;
		engine.DefineTechnique("lit", new TechniqueDescriptor("lit", HasVertexInput: true));
	}

	public RenderGraph Build(DemoScene scene)
	{
		if (!ReferenceEquals(_uploadedFor, _engine))
		{
			_textures[0] = _engine.CreateTexture(TextureData.FromRgba8(1, 1, [255, 255, 255, 255], TextureFormat.Rgba8Unorm, generateMips: false));
			// Straight down at the floor, with -Z at the top of the map; wide enough for the whole 8×8 floor.
			_mapCamera = new Camera(_engine).Orthographic(9f, 9f, 1f, 40f).LookAt(new Vector3(0f, 20f, 0f), Vector3.Zero, -Vector3.UnitZ);
			_uploadedFor = _engine;
		}

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
			_textures[1] = mapColor;

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

			g.Pass("minimap")
				.Samples(mapColor)
				.Blends(screen)
				.Draw(this, static (ctx, demo) => demo.DrawPanel(ctx));

			DemoGraph.Hud(g, screen, scene);
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

	public void PreFrame(double deltaSeconds, float time, Camera camera, Vector2 framebufferSize) => _framebuffer = framebufferSize;

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

	// The panel in the top-right corner: a translucent rounded backdrop and the map inset in it at 1:1.
	private void DrawPanel(IRenderContext ctx)
	{
		var size = _framebuffer;
		var outer = MapPixels + 2f * Inset;
		var origin = new Vector2(size.X - Margin - outer, Margin);
		const float alpha = 0.85f;
		Quad(0, origin, new Vector2(outer), new Vector4(0.02f * alpha, 0.025f * alpha, 0.04f * alpha, alpha), new Vector4(Radius, 0f, outer, outer));
		Quad(1, origin + new Vector2(Inset), new Vector2(MapPixels), Vector4.One, Vector4.Zero);

		var clip = new Rect(0f, 0f, size.X, size.Y);
		_commands[0] = new DrawCommand(clip, TextureIndex: 0, IndexOffset: 0, IndexCount: 6, VertexOffset: 0, Tag: KindRoundedRect);
		_commands[1] = new DrawCommand(clip, TextureIndex: 1, IndexOffset: 6, IndexCount: 6, VertexOffset: 0, Tag: KindImage);

		// Framebuffer pixels → clip, then the paper-white lift on an HDR target, exactly as UIPainter pushes them.
		Span<float> push =
		[
			2f / size.X, 2f / size.Y, -1f, -1f,
			ctx.HdrOutput ? HdrPaperWhite : 1f, BitConverter.UInt32BitsToSingle(ctx.HdrOutput ? 1u : 0u),
		];
		ctx.DrawList("ui", DrawListIdentity.Transient, _textures, _vertices, _indices, _commands, push);
	}

	// Quad q of the panel: four corners at `origin` spanning `size`, uv 0..1 across it, a premultiplied colour, and
	// the prim's parameters.
	private void Quad(int q, Vector2 origin, Vector2 size, Vector4 colour, Vector4 custom)
	{
		var c = new Half4((Half)colour.X, (Half)colour.Y, (Half)colour.Z, (Half)colour.W);
		for (var v = 0; v < 4; v++)
		{
			var uv = new Vector2(v is 1 or 2 ? 1f : 0f, v >= 2 ? 1f : 0f);
			_vertices[q * 4 + v] = new OverlayVertex(origin + uv * size, uv, c, custom);
		}
	}
}
