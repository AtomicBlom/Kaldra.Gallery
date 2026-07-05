// Bloom prefilter / bright-pass (Planning.md §7.1). The first step of the bloom sub-graph: sample the HDR
// lit image (binding 0) and keep only the part above a brightness threshold, written into mip0 (half-res).
// Sampling a 2x-larger source with a linear sampler already averages a 2x2 box, so this is the bright-pass
// AND the first halving in one pass. The full §7.1 recipe Karis-averages a 13-tap downsample to stop the
// bloom flickering as the camera moves; the single-tap form here is correctness-first (the glow reads
// correctly) — the wider tap is a shader-only quality refinement. The brightness threshold is supplied by
// BloomGraph through the fragment push channel (M7-A3) — no longer a hardcoded constant.

#include "loom_platform.module.slang"

LOOM_BINDING(0) Sampler2D source;

struct PushConstants
{
	float threshold; // brightness above which a texel glows; wired from BloomGraph.Bloom(threshold:) (§7.1)
};
LOOM_PUSH_CONSTANT PushConstants pc;

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
float4 fragmentMain(VsOut input) : SV_Target
{
	float threshold = pc.threshold; // only genuinely HDR (>threshold) texels glow — a hard knee reads as intentional

	float3 c = source.Sample(input.uv).rgb;
	float brightness = max(c.r, max(c.g, c.b));
	// Keep the over-threshold fraction of the colour; below threshold contributes nothing.
	float contribution = max(brightness - threshold, 0.0) / max(brightness, 1e-4);
	return float4(c * contribution, 1.0);
}
