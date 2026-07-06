// Two uniform buffers on one technique (multi-UBO): an app-written Constants buffer the pass .Reads (slot 0, b1)
// and an in-closure push the FullScreen call supplies (slot 1, b2). The fragment multiplies them, so the output
// colour depends on BOTH buffers — a flat-black or single-tint frame would mean one of the two never reached the
// shader. Exercises the same push+Constants coexistence the deferred CSM-splits move needs.

#include "loom_platform.module.slang"

struct Tint
{
	float4 color;
};

LOOM_BINDING(0) ConstantBuffer<Tint> base : register(b1); // the pass's .Reads Constants buffer (host-written)
LOOM_BINDING(1) ConstantBuffer<Tint> mul : register(b2);  // the in-closure FullScreen push

[shader("vertex")]
float4 vertexMain(uint vertexId : SV_VertexID) : SV_Position
{
	float2 uv = float2((vertexId << 1) & 2, vertexId & 2);
	return LOOM_FULLSCREEN_POS(uv);
}

[shader("fragment")]
float4 fragmentMain() : SV_Target
{
	return base.color * mul.color;
}
