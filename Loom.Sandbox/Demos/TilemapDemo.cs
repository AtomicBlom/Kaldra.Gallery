using System.Numerics;

using Loom.Core;

namespace Loom.Sandbox.Demos;

/// <summary>
/// Tile-index-texture tilemap (PlanningPhase2.md M14-2, Track A — the resolved tilemap decision). A whole map is
/// drawn as ONE quad: a per-cell tile-id texture (uploaded once) drives <c>tilemap.slang</c>, which maps each
/// cell's id to a sub-rect of the shared <see cref="TileAtlas"/> and samples albedo + tangent normal into the 2D
/// G-buffer — so the same deferred compose that lights sprites (M14-1) lights the map, with the tiles' beveled
/// normal maps giving relief. Scales to large maps (an index texture, not a quad per tile). Uses the existing
/// <c>ctx.Draw(mesh, material, technique)</c> verb — no Phase-0 dependency.
/// <para>
/// The map grid (<see cref="MapCols"/>×<see cref="MapRows"/>) is the contract <c>tilemap.slang</c>'s
/// <c>MapSize</c> constant bakes; the atlas grid is <see cref="TileAtlas.TilesAcross"/>×<see cref="TileAtlas.TilesDown"/>.
/// Camera is orthographic over the [0,WorldWidth]×[0,WorldHeight] world.
/// </para>
/// </summary>
internal sealed class TilemapDemo : IDemo
{
	private const int WorldWidth = 320;
	private const int WorldHeight = 180;
	private const int MapCols = 20; // must match tilemap.slang MapSize.x
	private const int MapRows = 12; // must match tilemap.slang MapSize.y

	// The map, top row first. '.' = empty/sky; letters map to TileAtlas ids below. A little platformer scene:
	// floating platforms over a grass-topped ground with stone clumps.
	private static readonly string[] Map =
	[
		"....................",
		"....................",
		".........ggg........",
		".........ddd........",
		"....................",
		"...ggg..............",
		"...ddd.....ggggg....",
		"...........ddddd....",
		"gggggggggggggggggggg",
		"dddddddddddddddddddd",
		"ddsssddddddddsssdddd",
		"dddddddddddddddddddd",
	];

	private Material _material;
	private Mesh _mapQuad;

	public string Name => "Tilemap";

	public void DefineTechniques(IEngine engine)
	{
		engine.DefineTechnique("tilemap", new TechniqueDescriptor("tilemap", HasVertexInput: true, Layout: VertexLayout.MeshStandard));
		engine.DefineTechnique("compose2d", new TechniqueDescriptor("compose2d"));
		engine.DefineTechnique("blit", new TechniqueDescriptor("blit"));

		// Material slots, in shader-binding order: [0] index texture, [1] atlas albedo, [2] atlas normal.
		TileAtlas.CreateTextures(engine, out var albedo, out var normal);
		var index = engine.CreateTexture(TextureData.FromRgba8(MapCols, MapRows, BuildIndexBytes(), TextureFormat.Rgba8Unorm, generateMips: false));
		_material = engine.CreateMaterial(index, albedo, normal);

		// One quad covering the whole world rect; uv (0,0) at the top-left so map row 0 lands at the top (+Y up).
		var n = new Vector3(0, 0, 1);
		var t = new Vector4(1, 0, 0, 1);
		Vertex[] verts =
		[
			new(new Vector3(0, 0, 0), n, new Vector2(0, 1), t),                       // bottom-left
			new(new Vector3(WorldWidth, 0, 0), n, new Vector2(1, 1), t),              // bottom-right
			new(new Vector3(WorldWidth, WorldHeight, 0), n, new Vector2(1, 0), t),    // top-right
			new(new Vector3(0, WorldHeight, 0), n, new Vector2(0, 0), t),             // top-left
		];
		_mapQuad = engine.CreateMesh(verts, [0, 1, 2, 0, 2, 3]);
	}

	public Camera? GetCamera(float aspect, float time)
	{
		var eye = new Vector3(WorldWidth * 0.5f, WorldHeight * 0.5f, 10f);
		var view = Matrix4x4.CreateLookAt(eye, eye with { Z = 0f }, Vector3.UnitY);
		var projection = Matrix4x4.CreateOrthographic(WorldWidth, WorldHeight, 0.1f, 100f);
		return new Camera(view, projection);
	}

	public RenderGraph Build(DemoScene scene) => RenderGraph.Define(g =>
	{
		var screen = g.Backbuffer;
		var world = g.Domain("tilemap2d", WorldWidth, WorldHeight);
		var albedo = world.Color("tile_albedo", Format.Rgba8Srgb);
		var normal = world.Color("tile_normal", Format.Rgba8Unorm);
		var emissive = world.Color("tile_emissive", Format.Rgba8Srgb);
		var lit = world.Color("tile_lit", Format.Rgba16F);

		// One draw rasterises the whole map into the 2D G-buffer (index texture → atlas → albedo/normal).
		g.Pass("tilemap")
			.Writes(albedo, normal, emissive)
			.Draw(ctx => ctx.Draw(_mapQuad, _material, "tilemap"));

		// Reuse the M14-1 fixed-key-light compose (the focus here is the tilemap; M14-8 composes the real lights).
		g.Pass("compose")
			.Samples(albedo, normal, emissive)
			.Writes(lit)
			.Draw(ctx => ctx.FullScreen("compose2d", albedo, normal, emissive));

		g.Pass("upscale")
			.Samples(lit)
			.Writes(screen)
			.Draw(ctx => ctx.FullScreen("blit", lit));

		DemoGraph.Hud(g, screen);
		g.Present(screen);
	});

	public string HudStatus => $"tilemap {MapCols}x{MapRows} (1 draw, index texture)";

	// Row-major tile ids (row 0 = top), one per cell, expanded to Rgba8 with the id in R (linear, not sRGB).
	private static byte[] BuildIndexBytes()
	{
		var bytes = new byte[MapCols * MapRows * 4];
		for (var row = 0; row < MapRows; row++)
			for (var col = 0; col < MapCols; col++)
			{
				var id = CharToId(Map[row][col]);
				var o = (row * MapCols + col) * 4;
				bytes[o + 0] = id;   // tile id
				bytes[o + 3] = 255;  // opaque (channel unused by the shader)
			}
		return bytes;
	}

	private static byte CharToId(char c) => c switch
	{
		'g' => 1, // grass
		'd' => 2, // dirt
		's' => 3, // stone
		'b' => 4, // brick
		'w' => 7, // wood
		'c' => 6, // crystal
		_ => 0,   // '.' empty/sky
	};
}
