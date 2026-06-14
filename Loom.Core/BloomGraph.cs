namespace Loom.Core;

/// <summary>
/// The bloom sub-graph (Planning.md §7.1) as an <em>extension method</em> on <see cref="GraphBuilder"/> —
/// the canonical example of a reusable graph fragment composing into a parent graph. Nothing about it is
/// privileged: it just adds ordinary passes and returns the output handle. The structure is bright-pass →
/// N downsamples → N−1 additive upsamples → composite; the engine derives the strict serialization (each
/// upsample reads the mip the previous one accumulated into), the per-mip barriers, and — because the mip
/// chain's lifetime doesn't overlap the consumed G-buffer's — the aliasing of the freed G-buffer memory.
/// <para>
/// The <c>.Writes</c> vs <c>.Blends</c> distinction is load-bearing here: the upsample passes <c>.Blends</c>
/// (read-modify-write, <c>LoadOp.Load</c> + additive) the larger mip, so the graph preserves the
/// downsample's contents and accumulates onto them rather than clearing. A graph that only knew <c>.Writes</c>
/// would lose the accumulation or miss the dependency (§7.1).
/// </para>
/// </summary>
public static class BloomGraph
{
	/// <summary>
	/// Add the bloom passes for <paramref name="source"/> (an HDR colour target) and return the composited
	/// "lit + glow" output. Runs entirely in the source's domain so the glow stays pixel-consistent (§9).
	/// </summary>
	/// <param name="g">The graph being authored.</param>
	/// <param name="source">The HDR image to bloom (e.g. the lit result).</param>
	/// <param name="threshold">Brightness above which a texel glows. Delivered to the prefilter shader through
	/// the fragment push channel (M7-A3).</param>
	/// <param name="mips">Pyramid depth. Near a ceiling at low resolution, not a quality knob (§9): ~5 is the
	/// most a 480×270 world supports before mips become noise.</param>
	/// <param name="intensity">How strongly the glow composites back. Delivered to the composite shader through
	/// the fragment push channel (M7-A3).</param>
	public static ColorHandle Bloom(this GraphBuilder g, ColorHandle source, float threshold = 1.0f, int mips = 5, float intensity = 0.7f)
	{
		ArgumentNullException.ThrowIfNull(g);
		ArgumentNullException.ThrowIfNull(source);
		if (mips < 2)
			throw new ArgumentOutOfRangeException(nameof(mips), "Bloom needs at least 2 mips (one downsample to upsample from).");

		var domain = source.Domain;
		var chain = new ColorHandle[mips];
		for (var i = 0; i < mips; i++)
			chain[i] = domain.Color($"bloom_mip{i}", Format.Rgba16F, scale: 1f / (1 << (i + 1)));

		// Bright-pass + first halving into mip0. The linear sampler box-averages the 2×-larger source.
		g.Pass("bloom.prefilter")
			.Samples(source)
			.Writes(chain[0])
			.Draw(ctx => ctx.FullScreen("bloom.prefilter", [threshold], source));

		// Downsample: each mip is the half-size, box-filtered version of the one above.
		for (var i = 1; i < mips; i++)
		{
			var read = chain[i - 1];
			var write = chain[i];
			g.Pass($"bloom.down{i}")
				.Samples(read)
				.Writes(write)
				.Draw(ctx => ctx.FullScreen("bloom.down", read));
		}

		// Upsample from the smallest mip back up, additively accumulating into the larger mip the downsample
		// already wrote. The RAW edge on each .Blends target forces strict mipN → … → mip0 ordering.
		for (var i = mips - 2; i >= 0; i--)
		{
			var smaller = chain[i + 1]; // already accumulated by the previous (smaller) upsample
			var larger = chain[i];
			g.Pass($"bloom.up{i}")
				.Samples(smaller)
				.Blends(larger)
				.Draw(ctx => ctx.FullScreen("bloom.up", smaller));
		}

		// Composite the fully-accumulated mip0 back over the source into a fresh output (a plain .Writes, so
		// the bloom mips are dead afterwards and their memory can alias away).
		var output = domain.Color("lit_bloomed", Format.Rgba16F);
		var mip0 = chain[0];
		g.Pass("bloom.composite")
			.Samples(source, mip0)
			.Writes(output)
			.Draw(ctx => ctx.FullScreen("bloom.composite", [intensity], source, mip0));

		return output;
	}
}
