// Forward directional lighting — the simplest "lit geometry" technique, the basis for most of the focused
// sandbox demos. One pass: it reads a Loom.Core.Vertex stream, transforms by the app's view-projection (a
// constants buffer the pass .Reads, clip-space-corrected) and the per-object model matrix (the per-draw Uniforms
// block), and shades each fragment with a single directional key light plus a flat ambient. The albedo is derived
// from the surface normal so each cube face reads as a distinct colour. Output is HDR (the key light pushes lit
// faces above 1.0) so the bloom and tone-map demos have highlights to work with; a demo that just wants the lit
// image blits it to an SDR backbuffer (which clamps).

#include "transform.module.slang"

struct ViewProj { float4x4 viewProj; };           // the app's view-projection, clip-space-corrected
struct PerDraw  { float4x4 model; float4 misc; };  // per-object model; misc.x = object id (unused here)

// No samplers precede them, so the UBOs own Vulkan bindings 0,1; register(b1)/(b2) on DX12 (b0 is the unused
// root-constant slot). A cbuffer-packed matrix is the transpose of the row-major upload, so the model applies
// column-form (mul(M, v) == v . M_original) and the normal by the inverse of that 3x3 (correct under any scale).
[[vk::binding(0, 0)]] ConstantBuffer<ViewProj> view : register(b1);
[[vk::binding(1, 0)]] ConstantBuffer<PerDraw>  draw : register(b2);

struct VsIn
{
	// HLSL semantics alongside the vk::location qualifiers: Slang pins the SPIR-V input locations from the
	// vk::location (Vulkan unchanged) AND emits the semantics for the HLSL/DXIL target (the DX12 backend), so
	// one source is cross-target. Without the semantics, dxc/fxc reject the entry ("semantic must be defined").
	[[vk::location(0)]] float3 position : POSITION;
	[[vk::location(1)]] float3 normal   : NORMAL;
	[[vk::location(2)]] float2 uv        : TEXCOORD0;
	[[vk::location(3)]] float4 tangent   : TANGENT;
};

struct VsOut
{
	float4 position : SV_Position;
	float4 tangent : TANGENT; // consumed only to satisfy the shared 4-attribute vertex layout
	float3 normal : NORMAL;
	float2 uv : TEXCOORD0; // passed through (the engine's vertex layout provides it) though the shading ignores it
};

[shader("vertex")]
VsOut vertexMain(VsIn input)
{
	VsOut output;
	float4 world = mul(draw.model, float4(input.position, 1.0));
	output.position = mul(view.viewProj, world);
	output.tangent = input.tangent;
	output.normal = mul(input.normal, inverse3x3((float3x3)draw.model));
	output.uv = input.uv;
	return output;
}

[shader("fragment")]
float4 fragmentMain(VsOut input) : SV_Target
{
	float3 n = normalize(input.normal);
	float3 albedo = n * 0.5 + 0.5;                          // distinct per-face colour
	float3 lightDir = normalize(float3(0.45, 0.55, -0.6));   // matches the sandbox sun
	float ndotl = max(dot(n, lightDir), 0.0);

	const float ambient = 0.15;
	const float key = 1.5; // > 1 so the directly-lit faces are HDR — bloom/tone-map have something to catch
	return float4(albedo * (ambient + ndotl * key), 1.0);
}
