using Loom.Core.Hosting;
using Loom.Gallery.Demos;

namespace Loom.Gallery;

/// <summary>
/// The demo roster shared by every host: the ordered set of <see cref="IDemo"/>s the desktop sandbox and the
/// in-process tests both draw from. Keeping the roster here (not in a host) is what lets the sandbox exe and the test
/// project run the exact same gallery, so a demo can never render in one and not the other. A UI demo's own page comes
/// with the demo (<see cref="IDemo.OverlayContent"/>), so every host shows the same tree.
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
			new VectorPrimsDemo(),
			new VectorUIDemo(),
			new UILiveTextureDemo(),
			new ShadowsDemo(),
			new SsaoDemo(),
			new TonemappingDemo(),
			new BloomDemo(),
			new ParticlesDemo(),
			new StorageScatterDemo(),
			new MsaaDemo(),
			new HighlightingDemo(),
			new InstancingDemo(),
			new InstancingDemo(uploadOnce: true),
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
}
