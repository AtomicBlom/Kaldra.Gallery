using System.Numerics;

using Loom.Gallery.Demos;
using Loom.Web.UI;

using SUIsei;
using SUIsei.Controls;

namespace Loom.Web.Demos;

/// <summary>
/// The browser demo registry: maps a <c>?demo=</c> / <c>?capture=</c> name to a shared <see cref="IDemo"/> — the
/// same demo classes the desktop sandbox runs, linked into this assembly, so the two hosts can never drift. For a UI
/// demo it also supplies the host-owned overlay page that demo's graph composites through <c>scene.UI</c>: the demo
/// graph is shared, but the page content (a browser XAML view or a code-built panel) is host-specific.
/// </summary>
internal static class WebGallery
{
	private const string Default = "cube";

	private static readonly Dictionary<string, Func<IDemo>> Factories = new(StringComparer.OrdinalIgnoreCase)
	{
		["cube"] = () => new CubeLightingDemo(),
		["particles"] = () => new ParticlesDemo(),
		["highlighting"] = () => new HighlightingDemo(),
		["bloom"] = () => new BloomDemo(),
		["tonemapping"] = () => new TonemappingDemo(),
		["ssao"] = () => new SsaoDemo(),
		["cascadedshadows"] = () => new CascadedShadowsDemo(),
		["skinning"] = () => new SkinningDemo(),
		["gltf"] = () => new GltfDemo(),
		["uiinteractive"] = () => new UIInteractiveDemo(),
		["uigroupopacity"] = () => new UIGroupOpacityDemo(),
		["fullreference"] = () => new FullReferenceDemo(),
		["inworldui"] = () => new InWorldUIDemo(),
		["debug2d"] = () => new Debug2DDemo(),
		["foliage"] = () => new FoliageDemo(),
		["instancing"] = () => new InstancingDemo(),
		["lighting2d"] = () => new Lighting2DDemo(),
		["nightglade"] = () => new NightGladeDemo(),
		["normalmapping"] = () => new NormalMappingDemo(),
		["parallax"] = () => new ParallaxDemo(),
		["pixelartcastle"] = () => new PixelArtCastleDemo(),
		["pixelart"] = () => new PixelArtDemo(),
		["ruins"] = () => new RuinsDemo(),
		["shadows"] = () => new ShadowsDemo(),
		["skydaynight"] = () => new SkyDayNightDemo(),
		["spike"] = () => new SpikeDemo(),
		["spriteanimation"] = () => new SpriteAnimationDemo(),
		["sprites"] = () => new SpritesDemo(),
		["stylized"] = () => new StylizedDemo(),
		["tilemap"] = () => new TilemapDemo(),
		["toon"] = () => new ToonDemo(),
	};

	/// <summary>The names the host exposes for selection (also the ?demo= keys).</summary>
	public static IEnumerable<string> Names => Factories.Keys;

	/// <summary>Normalize a requested name to a known lower-case key, falling back to the default demo.</summary>
	public static string Resolve(string? name) =>
		name is { } n && Factories.ContainsKey(n) ? n.ToLowerInvariant() : Default;

	/// <summary>Construct the demo for a resolved key.</summary>
	public static IDemo Create(string key) => Factories[key]();

	/// <summary>The host-owned overlay page for a UI demo (its graph composites this through <c>scene.UI</c>), or null.</summary>
	public static UIElement? OverlayContent(string key) => key switch
	{
		"uiinteractive" => new UIInteractiveView(),
		"uigroupopacity" => BuildGroupOpacityPanel(),
		_ => null,
	};

	/// <summary>The deterministic capture click's screen position for this demo, or null if it doesn't want one.
	/// Both are the target page's Increment button — <c>inworldui</c>'s was found empirically (the billboard
	/// panel's screen footprint isn't known statically) by iterating against <c>HudStatus</c>'s reported ray-cast
	/// hit until it landed inside the button's panel-local bounds (~x:[32,192] y:[72,116] of the 512×256 texture).</summary>
	public static Vector2? CaptureClickPosition(string key) => key switch
	{
		"uiinteractive" => new Vector2(144f, 68f),
		"inworldui" => new Vector2(505f, 166f),
		_ => null,
	};

	// A 50%-opacity container over a Canvas of two overlapping opaque fills — the same tree the sandbox host builds,
	// so the group-opacity offscreen-layer composite is pixel-comparable across backends.
	private static UIElement BuildGroupOpacityPanel()
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
