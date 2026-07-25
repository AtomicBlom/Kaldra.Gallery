using Loom.Gallery.Demos;

namespace Loom.Tests;

/// <summary>
/// The canonical set of demos with committed golden baselines under <c>Goldens/</c> — one source of truth consumed
/// by both <see cref="GalleryGoldenTests"/> (diff against the baselines) and <see cref="GallerySoftwareAdapterTests"/>
/// (the WARP colorcount smoke), so the "tracked demo" list lives in exactly one place. Keep in sync with the
/// committed <c>Goldens/</c> PNGs. (<see cref="GalleryParityTests"/> deliberately covers a wider set — it needs no
/// baseline, only both backends present — so it keeps its own list.)
/// </summary>
internal static class TrackedDemos
{
	// Baseline filename (without .png) → the demo that produces it.
	public static readonly (string Name, Func<IDemo> Factory)[] All =
	[
		("FullReference", () => new FullReferenceDemo()),
		("Bloom", () => new BloomDemo()),
		("Particles", () => new ParticlesDemo()),
		("PixelArt", () => new PixelArtDemo()),
		("StylizedReference", () => new StylizedDemo()),
	];
}
