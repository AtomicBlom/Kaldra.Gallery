using Loom.Gallery.Demos;

namespace Loom.Web.Demos;

/// <summary>
/// The browser demo registry: maps a <c>?demo=</c> / <c>?capture=</c> name to a shared <see cref="IDemo"/> — the
/// same demo classes the desktop sandbox runs, linked into this assembly, so the two hosts can never drift. A UI demo
/// brings its own page (<see cref="IDemo.OverlayContent"/>) and its capture click (<see cref="IDemo.CaptureClick"/>),
/// so those cannot drift between the hosts either.
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
		["vectorprims"] = () => new VectorPrimsDemo(),
		["vectorui"] = () => new VectorUIDemo(),
		["uilivetexture"] = () => new UILiveTextureDemo(),
		["debug2d"] = () => new Debug2DDemo(),
		["foliage"] = () => new FoliageDemo(),
		["instancing"] = () => new InstancingDemo(),
		["instancing-once"] = () => new InstancingDemo(uploadOnce: true),
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
}
