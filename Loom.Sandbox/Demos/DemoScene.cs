using Loom.Core;

namespace Loom.Sandbox.Demos;

/// <summary>The shared, app-owned geometry every demo draws — uploaded once by the host and handed to each demo.
/// <paramref name="NormalMapped"/> is a brick material (albedo + normal map) the M11 demos draw with.</summary>
internal readonly record struct DemoScene(Mesh Cube, Mesh Plane, Material NormalMapped);