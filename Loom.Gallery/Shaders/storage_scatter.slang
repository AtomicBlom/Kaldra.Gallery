// The Storage Scatter demo's expansion: a grid of cells, each with at most one spark, kept where a hash of the cell's
// own coordinates falls under the density the app wrote once into a read-only storage buffer (a g.Storage the compute
// pass .Reads), and written into the spark stream a draw reads as instances. What a game does with grass: a map of the
// world written once, expanded every frame around the eye. The map binds after the stream: binding 1 on Vulkan and
// WebGPU, t0 on DirectX 12 (a read-only buffer is a shader resource, after any sampled textures; there are none).

#include "loom_platform.module.slang"

LOOM_BINDING(0) RWStructuredBuffer<float4> sparks;
LOOM_BINDING(1) StructuredBuffer<float> density LOOM_REGISTER(t0);

struct PushConstants
{
	float4 p; // x = elapsed time (seconds)
};

LOOM_PUSH_CONSTANT PushConstants pc;

static const uint Cells = 96;   // across the grid
static const uint Map = 16;     // across the density map

uint Hash(uint a, uint b)
{
	uint h = a * 0x9E3779B1u ^ (b + 0x7F4A7C15u) * 0x85EBCA77u;
	h ^= h >> 15;
	h *= 0x2C1B3C6Du;
	h ^= h >> 12;
	return h;
}

[shader("compute")]
[numthreads(64, 1, 1)]
void computeMain(uint3 id : SV_DispatchThreadID)
{
	uint i = id.x;
	if (i >= Cells * Cells)
		return;
	uint x = i % Cells, z = i / Cells;
	uint h = Hash(x, z);
	float jx = (h & 0xFFu) / 256.0, jz = ((h >> 8) & 0xFFu) / 256.0, pick = (h >> 16) / 65536.0;
	float d = density[(x * Map / Cells) + (z * Map / Cells) * Map];
	float lift = 0.03 + 0.05 * (0.5 + 0.5 * sin(pc.p.x * 2.0 + pick * 6.2831));
	float3 at = float3((x + jx) / Cells * 4.0 - 2.0, lift, (z + jz) / Cells * 4.0 - 2.0);
	sparks[i] = float4(at, pick < d ? 0.8 : 0.0);   // w is the spark's brightness: none where the map keeps none
}
