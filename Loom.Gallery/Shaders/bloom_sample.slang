// Bloom downsample / upsample tap (Planning.md §7.1). A bare linear-filtered sample of one input, shared by
// both the downsample and upsample passes — they are the SAME fragment work; what differs is graph/pipeline
// state, derived from the authoring intent, not the shader:
//   * downsample ("bloom.down"): .Writes a half-size mip, BlendMode.None  -> reads the larger mip, the
//     linear sampler box-averages it down.
//   * upsample   ("bloom.up"):   .Blends a larger mip, BlendMode.Additive -> reads the smaller, already-
//     accumulated mip; linear magnification spreads it (tent-ish) and the additive blend sums it in.
// The §7.1 recipe uses a 3x3 tent on upsample; the single linear tap here compounds across the pyramid into
// a smooth glow (correctness-first) — the multi-tap tent is a shader-only quality refinement.

#include "loom_platform.module.slang"

LOOM_BINDING(0) Sampler2D source;

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
	output.position = LOOM_FULLSCREEN_POS(uv);
	return output;
}

[shader("fragment")]
float4 fragmentMain(VsOut input) : SV_Target
{
	return float4(source.Sample(input.uv).rgb, 1.0);
}
