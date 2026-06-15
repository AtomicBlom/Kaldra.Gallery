// SSAO blur (Planning.md §7.6). The ssao pass trades banding for per-pixel noise (a fixed kernel rotated by a
// per-pixel hash), so its raw output is grainy. This is the "usual next edge": a small box blur that averages
// the occlusion term over a neighbourhood, dissolving the noise into clean contact shadows. Adding it is just
// another edge — gbuffer → ssao → ssao.blur → (view|lighting) — and the engine re-derives ordering and barriers.
//
// One full-screen pass, a symmetric box (no separation): the kernel is tiny and the term is single-channel, so a
// box is cheaper than ping-ponging a separable Gaussian and reads the same here. Texel size comes from the
// sampled target's own dimensions, so it works whether ao is at screen res (SSAO demo) or 320×180 (full frame).

[[vk::binding(0, 0)]] Sampler2D aoTex;

struct VsOut
{
	float4 position : SV_Position;
	float2 uv : TEXCOORD0;
};

[shader("vertex")]
VsOut vertexMain(uint vertexId : SV_VertexID)
{
	float2 uv = float2((vertexId << 1) & 2, vertexId & 2);

	VsOut output;
	output.uv = uv;
	output.position = float4(uv * 2.0 - 1.0, 0.0, 1.0);
	return output;
}

[shader("fragment")]
float fragmentMain(VsOut input) : SV_Target
{
	static const int kRadius = 2; // a 5x5 box — wide enough to dissolve the per-pixel rotation noise, still one pass

	uint width, height;
	aoTex.GetDimensions(width, height);
	float2 texel = 1.0 / float2(width, height);

	float sum = 0.0;
	for (int y = -kRadius; y <= kRadius; y++)
		for (int x = -kRadius; x <= kRadius; x++)
			sum += aoTex.Sample(input.uv + float2(x, y) * texel).r;

	float taps = float((2 * kRadius + 1) * (2 * kRadius + 1));
	return sum / taps;
}
