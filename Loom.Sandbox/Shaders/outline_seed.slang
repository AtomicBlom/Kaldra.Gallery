// JFA seed pass (Planning.md §7.5). The first step of the jump-flood outline: every pixel belonging to the
// HOVERED object stores its own pixel coordinates; everything else stores a far-away sentinel. The flood
// passes then propagate the nearest seed coordinate outward, and the resolve turns nearest-seed distance
// into a ring. The hovered id rides an app-writable parameter buffer the host fills from the picking readback
// — id 0 means nothing is hovered, so no pixel seeds and the outline vanishes.

#include "loom_platform.module.slang"

LOOM_BINDING(0) Sampler2D ids; // the object_id buffer from the picking pass

struct OutlineParams
{
	float4 p; // x = hovered object id (0 = none)
};

// The hovered id rides a parameter buffer the seed pass .Reads — bound after the sampled input (Vulkan binding
// 1 = SampleCount; the b1 root CBV on DirectX12), exactly as the reflected uniform seam places it.
LOOM_BINDING(1) ConstantBuffer<OutlineParams> outline : register(b1);

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
	static const float sentinel = 30000.0; // "no seed" — distance to it is effectively infinite

	float hoveredId = outline.p.x;
	float id = ids.Sample(input.uv).r;

	// This pixel is a seed iff it belongs to the hovered object. Store its own pixel coordinates.
	bool isSeed = hoveredId >= 0.5 && abs(id - hoveredId) < 0.5;
	float2 coord = isSeed ? input.position.xy : float2(sentinel, sentinel);
	return float4(coord, 0.0, 0.0);
}
