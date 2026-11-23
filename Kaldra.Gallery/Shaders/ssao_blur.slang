// SSAO blur (Planning.md §7.6). The ssao pass trades banding for per-pixel noise (a fixed kernel rotated by a
// per-pixel hash), so its raw output is grainy. This is the "usual next edge": a small box blur that averages
// the occlusion term over a neighbourhood, dissolving the noise into clean contact shadows. Adding it is just
// another edge — gbuffer → ssao → ssao.blur → (view|lighting) — and the engine re-derives ordering and barriers.
//
// One full-screen pass, a symmetric box (no separation): the kernel is tiny and the term is single-channel, so a
// box is cheaper than ping-ponging a separable Gaussian and reads the same here. The texel size comes from the
// fullscreen-triangle's uv gradient (ddx(uv).x == 1/width, ddy(uv).y == 1/height), so it tracks the sampled
// target's resolution whether ao is at screen res (SSAO demo) or 320×180 (full frame) — and, unlike
// Sampler2D.GetDimensions (which Slang lowers to a 3-argument call dxc rejects on the DXIL target), it compiles
// identically for SPIR-V and DXIL, keeping the box blur byte-for-byte the same on both backends.

#include "kaldra_platform.module.slang"

Sampler2D aoTex;

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
	output.position = KALDRA_FULLSCREEN_POS(uv);
	return output;
}

[shader("fragment")]
float fragmentMain(VsOut input) : SV_Target
{
	static const int kRadius = 2; // a 5x5 box — wide enough to dissolve the per-pixel rotation noise, still one pass

	float2 texel = float2(ddx(input.uv).x, ddy(input.uv).y); // one texel, from the fullscreen uv gradient

	float sum = 0.0;
	for (int y = -kRadius; y <= kRadius; y++)
		for (int x = -kRadius; x <= kRadius; x++)
			sum += aoTex.Sample(input.uv + float2(x, y) * texel).r;

	float taps = float((2 * kRadius + 1) * (2 * kRadius + 1));
	return sum / taps;
}
