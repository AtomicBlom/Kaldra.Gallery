using Loom.Core.Hosting;
using Loom.Gallery.Demos;
using Loom.Gallery.UI;

using SUIsei;
using SUIsei.Controls;

namespace Loom.Gallery;

/// <summary>
/// The demo roster shared by every host: the ordered set of <see cref="IDemo"/>s the desktop sandbox and the
/// in-process tests both draw from, plus the host-owned overlay content a UI demo composites through its scene.
/// Keeping the roster here (not in a host) is what lets the sandbox exe and the test project run the exact same
/// gallery, so a demo can never render in one and not the other.
/// </summary>
internal static class DemoGallery
{
	/// <summary>
	/// Register every demo's techniques (idempotent across demos that share one) and return those that came up —
	/// skipping any whose shaders don't cross-compile on this backend/toolchain (e.g. a fragment stage with no
	/// DirectX12 DXIL on some RID): <see cref="IDemo.DefineTechniques"/> throws <see cref="NotSupportedException"/>
	/// for the missing stage, so that demo is dropped from the gallery rather than crashing the whole harness (the
	/// same spirit as the SetGraph-time capability guards). Eager and cheap — no async asset I/O; each demo's
	/// actual preload + graph build happens lazily, the first time it is viewed.
	/// </summary>
	public static IReadOnlyList<IDemo> Build(IEngine engine)
	{
		IDemo[] all =
		[
			new SpikeDemo(),
			new CubeLightingDemo(),
			new UIInteractiveDemo(),
			new UIGroupOpacityDemo(),
			new ShadowsDemo(),
			new SsaoDemo(),
			new TonemappingDemo(),
			new BloomDemo(),
			new ParticlesDemo(),
			new HighlightingDemo(),
			new InstancingDemo(),
			new ScaleDemo(),
			new SpritesDemo(),
			new Lighting2DDemo(),
			new SpriteAnimationDemo(),
			new TilemapDemo(),
			new ParallaxDemo(),
			new Debug2DDemo(),
			new PixelArtDemo(),
			new FullReferenceDemo(),
			new NormalMappingDemo(),
			new GltfDemo(),
			new MaterialParametersDemo(),
			new InWorldUIDemo(),
			new SkinningDemo(),
			new CascadedShadowsDemo(),
			new SkyDayNightDemo(),
			new FoliageDemo(),
			new ToonDemo(),
			new StylizedDemo(),
			new NightGladeDemo(),
			new RuinsDemo(),
			new PixelArtCastleDemo(),
		];

		var live = new List<IDemo>();
		foreach (var demo in all)
		{
			try
			{
				demo.DefineTechniques(engine);
				live.Add(demo);
			}
			catch (ShaderAssetMissingException ex)
			{
				Console.WriteLine($"[loom] skipping demo '{demo.Name}' — {ex.Message}");
			}
		}
		return live;
	}

	/// <summary>
	/// The host-owned overlay content a UI demo composites through <see cref="DemoScene.UI"/>, or null for a demo
	/// that owns no overlay: the demo graph is shared, but the centred page/panel (a XAML view or a code-built
	/// tree) is host-specific and supplied here. Keyed by demo type so the sandbox host and the parity tests build
	/// the identical tree.
	/// </summary>
	public static UIElement? OverlayContent(IDemo demo) => demo switch
	{
		UIInteractiveDemo => new UIInteractiveView(),
		UIGroupOpacityDemo => BuildGroupOpacityPanel(),
		_ => null,
	};

	// A 50%-opacity container over a Canvas of two overlapping opaque fills. True group opacity composites the
	// subtree once and fades the result, so the overlap stays the same colour as the non-overlap; folding the fade
	// per child would darken the overlap. The backend renders this via its offscreen layer composite — the one UI
	// path whose pixels depend on the backend, hence the cross-backend parity gate.
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
