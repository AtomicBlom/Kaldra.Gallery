// Forward-lit geometry shaded from a per-material parameter block — the technique that exercises Loom's material
// parameter channel end to end. Everything about the surface's *appearance* comes from `loomMaterial`, a constant
// buffer reflection identifies by that reserved name; the engine reads its slot and size from the reflection sidecar
// and stages the app's bytes into the per-frame uniform ring on every draw that uses the material. The app supplies
// whatever struct it likes — the engine names no material model — and this shader's LoomMaterial is that struct's
// shader-side half.
//
// Base colour is the material factor times the sampled albedo, so a material with a white 1x1 texture shades from the
// factor alone (matching glTF's "texture absent => the factor applies unmodified"). Metallic darkens the diffuse
// response and roughness widens the specular lobe — enough of a PBR response to make the factors visibly distinct
// without pulling in IBL. Emissive is added unlit, so it survives a black key light.

#include "loom_platform.module.slang"

#include "transform.module.slang"

struct ViewProj { float4x4 viewProj; };             // the app's view-projection, clip-space-corrected
struct PerDraw  { float4x4 model; uint objectId; }; // per-object model + picking id (id unused here)

// The per-material block. 48 bytes, every member 16-byte aligned, so HLSL cbuffer packing and WGSL uniform layout
// agree without a per-backend branch — a float3 here would pack differently across targets.
struct LoomMaterial
{
	float4 baseColor;         // rgb tint * alpha
	float4 emissive;          // rgb added unlit, a unused
	float4 metallicRoughness; // metallic, roughness, normalScale (unused here), occlusionStrength (unused here)
};

// One sampler precedes the UBOs, so it owns descriptor 0 and the constant buffers start at 1. On DX12 the cbuffers
// are register(b1..) regardless (b0 is the reserved root-constant slot) — the two numbering schemes are independent.
// `loomMaterial` is declared last by convention, but nothing depends on that: the engine binds it at whatever slot
// reflection reports, which is its ordinal among these three cbuffers.
LOOM_BINDING(0) Sampler2D albedoTex;
LOOM_BINDING(1) ConstantBuffer<ViewProj> view : register(b1);
LOOM_BINDING(2) ConstantBuffer<PerDraw> draw : register(b2);
LOOM_BINDING(3) ConstantBuffer<LoomMaterial> loomMaterial : register(b3);

struct VsIn
{
	[[vk::location(0)]] float3 position : POSITION;
	[[vk::location(1)]] float3 normal : NORMAL;
	[[vk::location(2)]] float2 uv : TEXCOORD0;
	[[vk::location(3)]] float4 tangent : TANGENT;
};

struct VsOut
{
	float4 position : SV_Position;
	float4 tangent : TANGENT; // consumed only to satisfy the shared 4-attribute vertex layout
	float3 normal : NORMAL;
	float2 uv : TEXCOORD0;
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
	const float3 keyDirection = normalize(float3(0.4, 0.8, 0.45));
	const float3 viewDirection = float3(0.0, 0.0, 1.0);

	// Hemispheric ambient — brighter sky above, dimmer bounce below — stands in for an environment. A flat constant
	// would do for the dielectrics, whose diffuse term already varies across the surface, but a smooth metal has
	// *nothing* but the environment to reflect: with a constant it renders as one flat disc, which in a demo about
	// material response reads as broken just as surely as the black disc it replaced.
	const float3 skyAmbient = float3(0.34, 0.38, 0.46);
	const float3 groundAmbient = float3(0.10, 0.09, 0.08);

	float3 normal = normalize(input.normal);
	float3 albedo = loomMaterial.baseColor.rgb * albedoTex.Sample(input.uv).rgb;
	float metallic = saturate(loomMaterial.metallicRoughness.x);
	float roughness = clamp(loomMaterial.metallicRoughness.y, 0.05, 1.0);

	// A metal has no diffuse albedo of its own, so metallic fades the *directional* diffuse term out. The ambient
	// term stays outside that factor: it stands in for the environment, and a metal reflects an environment (tinted
	// by its own colour) rather than ignoring it. Folding ambient into the (1 - metallic) factor instead leaves a
	// smooth metal as a black disc with a highlight dot, since without IBL it would have nothing at all to reflect.
	float lambert = max(dot(normal, keyDirection), 0.0);
	float3 diffuse = albedo * lambert * (1.0 - metallic);
	float3 environment = albedo * lerp(groundAmbient, skyAmbient, normal.y * 0.5 + 0.5);

	// Blinn-Phong stands in for a microfacet lobe: roughness maps to a specular exponent, and the highlight takes
	// the material's own colour when metallic and white when dielectric.
	//
	// The (exponent + 8) / 8pi factor is the standard normalisation — it keeps roughly constant energy as the lobe
	// widens. It is deliberately NOT a (1 - roughness) falloff, which reads plausibly but hits exactly zero at
	// roughness 1: paired with metallic 1 (which zeroes the diffuse term) that leaves a fully rough metal with no
	// reflectance at all, rendering bit-exact black. A rough metal is dim and broad, not absent.
	float3 halfway = normalize(keyDirection + viewDirection);
	float exponent = max(2.0 / (roughness * roughness) - 2.0, 1.0);
	float specular = pow(max(dot(normal, halfway), 0.0), exponent) * (exponent + 8.0) / (8.0 * 3.14159265);
	// Reflectance at normal incidence. A dielectric reflects about 4% of the light striking it head-on, so its
	// specular is a faint sheen over the diffuse body colour; a metal reflects nearly all of it, tinted by its own
	// colour, which is what makes a metal read as metal. Scaling both at full strength — the obvious-looking
	// lerp(white, albedo, metallic) — gives plastic a mirror's highlight, and washes out whatever else the surface
	// is doing (it turned a red-emissive material pink).
	float3 f0 = lerp(float3(0.04, 0.04, 0.04), albedo, metallic);

	float3 colour = environment + diffuse + (f0 * specular) + loomMaterial.emissive.rgb;
	return float4(colour, loomMaterial.baseColor.a);
}
