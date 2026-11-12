// Fills the screen with a colour read from an app-writable parameter buffer — the smallest exercise of the
// uniform-resource path: the host writes the colour by handle (IEngine.Write) into a graph Constants buffer, a
// pass .Reads it, and the backend binds it to this technique's reflected uniform slot. No sampled inputs and no
// push, so the buffer lands at uniform slot 0 on Vulkan and the b1 root CBV on DirectX12.

#include "loom_platform.module.slang"

struct TintParams
{
	float4 color; // a single float4 packs identically under std140 and cbuffer rules — no cross-backend layout hazard
};
LOOM_BINDING(0) ConstantBuffer<TintParams> tint LOOM_REGISTER(b1);

[shader("vertex")]
float4 vertexMain(uint vertexId : SV_VertexID) : SV_Position
{
	// The standard fullscreen triangle fabricated from the vertex id — covers the target, no vertex buffer.
	float2 uv = float2((vertexId << 1) & 2, vertexId & 2);
	return LOOM_FULLSCREEN_POS(uv);
}

[shader("fragment")]
float4 fragmentMain() : SV_Target
{
	return tint.color;
}
