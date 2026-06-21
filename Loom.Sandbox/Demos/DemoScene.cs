using Loom.Core.Geometry;
using Loom.Core.Materials;
using Loom.UI.SUIsei;
using SUIsei;

namespace Loom.Sandbox.Demos;

/// <summary>The shared, app-owned geometry every demo draws — uploaded once by the host and handed to each demo.
/// <paramref name="Quad"/> is a unit XY quad for 2D sprite/tile batching (M14); <paramref name="NormalMapped"/> is
/// a brick material (albedo + normal map) the M11 demos draw with; <paramref name="UI"/> renders the overlay
/// draw list through the public API.</summary>
internal readonly record struct DemoScene(Mesh Cube, Mesh Plane, Mesh Quad, Material NormalMapped, UIPainter UI);
