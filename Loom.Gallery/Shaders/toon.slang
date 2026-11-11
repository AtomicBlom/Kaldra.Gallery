// Forward toon / cel shading for a small hero object (the pixel-art castle boss golem). One directional key,
// quantized into a few flat bands, plus a dark view-grazing fresnel rim that reads as a hand-drawn outline around
// the silhouette — so the chunky low-res render composites into the 2D scene as a cel-shaded sprite. Transforms by
// the .Reads view-projection + the per-draw model; outputs HDR so the lit bands carry through the scene's bloom.

#include "loom_platform.module.slang"

#include "transform.module.slang"

struct ViewProj { float4x4 viewProj; };              // the app's view-projection, clip-space-corrected
struct PerDraw  { float4x4 model; uint objectId; };  // per-object model + picking id (id unused by toon)

// No samplers precede them, so the UBOs own bindings 0,1 (register b1/b2; b0 is the unused root-constant slot).
LOOM_BINDING(0) ConstantBuffer<ViewProj> view LOOM_REGISTER(b1);
LOOM_BINDING(1) ConstantBuffer<PerDraw>  draw LOOM_REGISTER(b2);

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
	float4 tangent : TANGENT; // consumed only to satisfy the shared 4-attribute vertex layout
	float3 normal : NORMAL;
	float3 worldPos : POSITION0;
};

[shader("vertex")]
VsOut vertexMain(VsIn input)
{
	VsOut output;
	float4 world = mul(draw.model, float4(input.position, 1.0));
	output.position = mul(view.viewProj, world);
	output.tangent = input.tangent;
	output.normal = mul(input.normal, inverse3x3((float3x3)draw.model));
	output.worldPos = world.xyz;
	return output;
}

[shader("fragment")]
float4 fragmentMain(VsOut input) : SV_Target
{
	float3 N = normalize(input.normal);
	float3 L = normalize(float3(0.4, 0.7, 0.5));
	float3 V = normalize(float3(0.0, 0.95, 3.4) - input.worldPos); // the fixed boss-camera eye
	float ndotl = max(dot(N, L), 0.0);

	// Quantize the key into three flat cel bands (shadowed / mid / lit), with wide contrast so the bands stay
	// distinct after the backbuffer's sRGB encode (which lifts the darks).
	float band = ndotl > 0.62 ? 1.0 : ndotl > 0.28 ? 0.52 : 0.22;

	// Darker bronze-stone so the cel bands read with contrast (LDR: the boss composites straight to the backbuffer
	// with no tonemap, so an HDR key would clip the whole figure to flat pale).
	float3 albedo = float3(0.50, 0.37, 0.25); // weathered bronze-stone golem
	float3 colour = albedo * band * 1.25;

	// Dark view-grazing fresnel rim → a near-black cel outline hugging the silhouette.
	float fres = pow(1.0 - max(dot(N, V), 0.0), 2.5);
	colour *= 1.0 - smoothstep(0.35, 0.85, fres);

	// A cool top rim highlight so the lit edge reads as a raised, stylised form.
	float topRim = pow(saturate(dot(N, float3(0.0, 1.0, 0.2))), 6.0);
	colour += float3(0.45, 0.6, 0.8) * topRim * 0.35;
	return float4(colour, 1.0);
}
