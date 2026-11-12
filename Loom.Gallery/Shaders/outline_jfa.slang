// Jump-flood step (Planning.md §7.5). Ping-pong pass: each pixel looks at the eight neighbours (plus itself)
// a `step` distance away, reads the seed coordinate each stored, and keeps whichever is nearest to this
// pixel. Run with the step halving each pass (…4 → 2 → 1) so a seed reaches every pixel within its reach in
// log₂ passes. After the bounce, every pixel holds its nearest seed's coordinate; the resolve turns that into
// a distance. The graph drives the ping-pong by swapping the (read, write) handles in a normal loop — only
// two physical buffers exist (Planning.md §8). step + the target dimensions arrive via the fragment push.

#include "loom_platform.module.slang"

Sampler2D seed; // previous flood (or the seed pass for the first step)

struct PushConstants
{
	float4 p; // x = step (pixels), y = width, z = height
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
	output.position = LOOM_FULLSCREEN_POS(uv);
	return output;
}

[shader("fragment")]
float4 fragmentMain(VsOut input) : SV_Target
{
	float step = pc.p.x;
	float2 dims = float2(pc.p.y, pc.p.z);
	float2 here = input.position.xy;

	float2 best = float2(30000.0, 30000.0);
	float bestDist = 1e20;

	[unroll]
	for (int dy = -1; dy <= 1; dy++)
	{
		[unroll]
		for (int dx = -1; dx <= 1; dx++)
		{
			float2 sampleUv = input.uv + float2(dx, dy) * step / dims;
			float2 candidate = seed.Sample(sampleUv).xy; // a stored seed coordinate (or the sentinel)
			float2 delta = candidate - here;
			float dist = dot(delta, delta);
			if (dist < bestDist)
			{
				bestDist = dist;
				best = candidate;
			}
		}
	}

	return float4(best, 0.0, 0.0);
}
