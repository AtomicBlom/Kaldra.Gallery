using Loom.Core.Geometry;
using Loom.Core.Materials;
using Loom.UI.SUIsei;

namespace Loom.Sandbox.Demos;

/// <summary>The shared, app-owned resources every demo draws — uploaded once by the host and handed to each demo.
/// <paramref name="Quad"/> is a unit XY quad for 2D sprite/tile batching; <paramref name="NormalMapped"/> is
/// a brick material (albedo + normal map) the normal-mapped demos draw with; <paramref name="UI"/> renders the overlay
/// draw list through the public API. <paramref name="Asset"/> resolves a demo asset's raw bytes by file name — the
/// host owns where they come from (a file on desktop, an embedded resource in the browser), so a demo loads a model
/// or texture the same way on both.</summary>
internal readonly record struct DemoScene(Mesh Cube, Mesh Plane, Mesh Quad, Material NormalMapped, UIPainter UI, Func<string, byte[]> Asset);
