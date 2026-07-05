// Spike for the camera/MVP collapse: the view-projection arrives through a `.Reads` constants buffer (the app
// composed it with IEngine.ClipSpaceCorrection) and the model rides the per-draw Uniforms block — neither uses
// the engine's auto-pushed MVP. Proves the neutral-channel delivery renders at Vulkan<->DX12 parity before the
// auto-push is retired engine-wide. A matrix read from a cbuffer is column-major-packed (the transpose of the
// row-major System.Numerics matrix uploaded), so it is applied column-form: mul(M, v) == v . M_original, the
// row-vector convention the rest of Loom uses.

#include "loom_platform.module.slang"

struct ViewProj { float4x4 viewProj; };
struct PerDraw  { float4x4 model; uint objectId; }; // object id (unused here)

// DX12 reserves b0 for the (still-firing, here-unused) root-constant MVP, so the CBVs start at b1; on Vulkan
// there are no samplers ahead of them, so they own bindings 0,1. UniformBufferCount is reflection-derived.
LOOM_BINDING(0) ConstantBuffer<ViewProj> view : register(b1);
LOOM_BINDING(1) ConstantBuffer<PerDraw>  draw : register(b2);

struct VsIn
{
	[[vk::location(0)]] float3 position : POSITION;
	[[vk::location(1)]] float3 normal   : NORMAL;
	[[vk::location(2)]] float2 uv        : TEXCOORD0;
	[[vk::location(3)]] float4 tangent   : TANGENT;
};

struct VsOut
{
	float4 position : SV_Position;
	float3 normal : NORMAL;
	float2 uv : TEXCOORD0;       // consumed only to satisfy the shared 4-attribute vertex layout
	float4 tangent : TANGENT;    // ditto
};

[shader("vertex")]
VsOut vertexMain(VsIn input)
{
	float4 world = mul(draw.model, float4(input.position, 1.0));
	VsOut output;
	output.position = mul(view.viewProj, world);
	output.normal = input.normal; // passed through; the gate is cross-backend parity of the matrix delivery
	output.uv = input.uv;
	output.tangent = input.tangent;
	return output;
}

[shader("fragment")]
float4 fragmentMain(VsOut input) : SV_Target
{
	float3 n = normalize(input.normal);
	float3 albedo = n * 0.5 + 0.5;
	float3 lightDir = normalize(float3(0.45, 0.55, -0.6));
	float ndotl = max(dot(n, lightDir), 0.0);
	return float4(albedo * (0.15 + ndotl * 1.5), 1.0);
}
