#:package SharpGLTF.Toolkit@1.0.3

// One-time generator: builds a small deterministic rigged glTF — a 2-bone vertical bar (8 ring vertices ×
// 3 height levels) skinned so the lower ring follows bone0 and the upper rings follow bone1, with a "bend"
// rotation clip on bone1. Committed as Loom.Gallery/Assets/rigged_bar.glb for the skinning demo + loader test.

using System.Numerics;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;
using SharpGLTF.Scenes;

using VERTEX = SharpGLTF.Geometry.VertexBuilder<
	SharpGLTF.Geometry.VertexTypes.VertexPositionNormal,
	SharpGLTF.Geometry.VertexTypes.VertexTexture1,
	SharpGLTF.Geometry.VertexTypes.VertexJoints4>;

const int sides = 8;
const float radius = 0.25f;
float[] levels = [0f, 0.5f, 1.0f, 1.5f, 2.0f]; // y heights; weight blends from bone0 (bottom) to bone1 (top)

var material = new MaterialBuilder("bar").WithMetallicRoughnessShader()
	.WithBaseColor(new Vector4(0.8f, 0.6f, 0.4f, 1f));

var mesh = new MeshBuilder<VertexPositionNormal, VertexTexture1, VertexJoints4>("bar");
var prim = mesh.UsePrimitive(material);

// Ring vertices at each level. Weight: fully bone0 at y=0, fully bone1 at y=2, linear between.
VERTEX Vert(int side, int level)
{
	var angle = side / (float)sides * MathF.Tau;
	var y = levels[level];
	var pos = new Vector3(MathF.Cos(angle) * radius, y, MathF.Sin(angle) * radius);
	var normal = Vector3.Normalize(new Vector3(pos.X, 0, pos.Z));
	var t = Math.Clamp(y / 2.0f, 0f, 1f);
	var bindings = new VertexJoints4((0, 1f - t), (1, t));
	return new VERTEX(
		new VertexPositionNormal(pos, normal),
		new VertexTexture1(new Vector2(side / (float)sides, t)),
		bindings);
}

for (var level = 0; level < levels.Length - 1; level++)
	for (var side = 0; side < sides; side++)
	{
		var next = (side + 1) % sides;
		var a = Vert(side, level);
		var b = Vert(next, level);
		var c = Vert(next, level + 1);
		var d = Vert(side, level + 1);
		prim.AddTriangle(a, b, c);
		prim.AddTriangle(a, c, d);
	}

// Two bones: bone0 at the origin, bone1 its child at y=1 (the bar's midpoint). A bend clip rotates bone1.
var bone0 = new NodeBuilder("bone0");
var bone1 = bone0.CreateNode("bone1").WithLocalTranslation(new Vector3(0, 1f, 0));

// "bend": bone1 sways about Z, returning to rest — a clear, looping deformation for the parity capture.
bone1.UseRotation("bend")
	.WithPoint(0f, Quaternion.Identity)
	.WithPoint(1f, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.7f))
	.WithPoint(2f, Quaternion.Identity);

var scene = new SceneBuilder();
scene.AddSkinnedMesh(mesh, Matrix4x4.Identity, bone0, bone1);

var model = scene.ToGltf2();
var outPath = args.Length > 0 ? args[0] : "rigged_bar.glb";
model.SaveGLB(outPath);
Console.WriteLine($"wrote {outPath}: {model.LogicalMeshes.Count} mesh, {model.LogicalSkins.Count} skin, {model.LogicalAnimations.Count} anim, {model.LogicalSkins[0].JointsCount} joints");
