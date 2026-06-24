// Tile-index-texture tilemap (PlanningPhase2.md M14-2, Track A — the resolved tilemap decision). One quad covers
// the whole map's world rect; the fragment turns its uv into a cell, reads that cell's tile id from a per-cell
// index texture (slot 0), maps the id to the tile's sub-rect in a shared atlas, and samples the atlas albedo
// (slot 1) + tangent-space normal (slot 2) into the 2D G-buffer — so the same deferred compose/lights that light
// sprites light the map. Scales to large maps (one draw, an index texture instead of a quad per tile). Tiles are
// flat quads facing +Z with a fixed TBN (T=+X, B=+Y, N=+Z), like sprites, so the atlas normal map gives relief.
// Entry points vertexMain/fragmentMain so the build emits DXIL.
//
// The map cell grid (MapSize) and the atlas tile grid (AtlasTiles) are compile-time constants — the demo↔shader
// contract for this tilemap (the demo builds a MapSize index texture against an AtlasTiles atlas). A fully general
// tilemap would pass them as a per-pass uniform; a focused demo bakes them. The tile *data* is fully dynamic (the
// index texture). The camera's view-projection arrives as a constants buffer the pass .Reads (clip-space-corrected).

[[vk::binding(0, 0)]] Sampler2D indexTex;  // per-cell tile id in .r (Rgba8Unorm, NOT sRGB — exact byte), nearest
[[vk::binding(1, 0)]] Sampler2D albedoTex; // tile atlas albedo (sRGB)
[[vk::binding(2, 0)]] Sampler2D normalTex; // tile atlas tangent-space normal map (linear)

struct ViewProj
{
	float4x4 viewProj; // the app's view-projection, clip-space-corrected
};

// The view-proj UBO follows the three samplers (binding 3); register(b1) — b0 is the unused root-constant slot.
[[vk::binding(3, 0)]] ConstantBuffer<ViewProj> view : register(b1);

struct VsIn
{
	[[vk::location(0)]] float3 position : POSITION; // world-space quad corner (the map rect)
	[[vk::location(1)]] float3 normal : NORMAL;     // unused (fixed TBN)
	[[vk::location(2)]] float2 uv : TEXCOORD0;      // 0..1 across the whole map
	[[vk::location(3)]] float4 tangent : TANGENT;   // unused
};

struct VsOut
{
	float4 position : SV_Position;
	float2 uv : TEXCOORD0;
};

struct GBuffer
{
	float4 albedo : SV_Target0;
	float4 normal : SV_Target1;
	float4 emissive : SV_Target2;
};

[shader("vertex")]
VsOut vertexMain(VsIn input)
{
	VsOut output;
	output.position = mul(view.viewProj, float4(input.position, 1.0));
	output.uv = input.uv;
	return output;
}

[shader("fragment")]
GBuffer fragmentMain(VsOut input) : SV_Target
{
	static const float2 MapSize = float2(20.0, 12.0);   // index-texture cell grid (cols, rows)
	static const float2 AtlasTiles = float2(4.0, 4.0);  // atlas tile grid (cols, rows) — tile 0 is empty/transparent

	// Cell id from the index texture (nearest — picks the cell containing uv). .r is a linear byte → exact id.
	float tileId = round(indexTex.Sample(input.uv).r * 255.0);

	// Atlas sub-uv: the tile's cell in the atlas + the within-cell position. Nearest sampling + integer tile
	// sizes keep the sample inside the tile (no neighbour bleed), so no inset is needed.
	float2 within = frac(input.uv * MapSize);
	float2 atlasCell = float2(fmod(tileId, AtlasTiles.x), floor(tileId / AtlasTiles.x));
	float2 atlasUv = (atlasCell + within) / AtlasTiles;

	float4 texel = albedoTex.Sample(atlasUv);
	if (texel.a < 0.5)
		discard; // empty/transparent tiles (id 0, the sky) — let the background show

	float3 ts = normalTex.Sample(atlasUv).rgb * 2.0 - 1.0;
	float3 worldN = normalize(ts); // fixed sprite-style TBN → the sample IS the world normal

	GBuffer output;
	output.albedo = float4(texel.rgb, 1.0);
	output.normal = float4(worldN * 0.5 + 0.5, 1.0);
	output.emissive = float4(0.0, 0.0, 0.0, 1.0);
	return output;
}
