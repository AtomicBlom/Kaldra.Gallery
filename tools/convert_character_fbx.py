# Headless Blender script: combine the Kenney "Animated Characters - Survivors" FBX mesh/armature with its
# separate per-clip animation FBXs into a single GLB with named animation clips (idle/run/jump), for the Loom
# glTF loader (SharpGLTF → model.Skeleton + model.Clips).
#
#   blender --background --python tools/convert_character_fbx.py
#
# Each clip ships as its own FBX (mesh-less or duplicate-armature); we import the mesh+armature once, then for each
# clip import it, lift the created Action onto the base armature's NLA as a named track, and discard the imported
# duplicate — so the export carries one armature + one skinned mesh + N named animations.

import bpy
import os
import sys

SRC = r"C:/Dev/Personal/Loom.worktrees/AssetsTemp/kenney_animated-characters-survivors"
MODEL = os.path.join(SRC, "Model", "characterMedium.fbx")
CLIPS = [
	("idle", os.path.join(SRC, "Animations", "idle.fbx")),
	("run", os.path.join(SRC, "Animations", "run.fbx")),
	("jump", os.path.join(SRC, "Animations", "jump.fbx")),
]
OUT = r"C:/Dev/Personal/Loom.worktrees/Review/Loom.Sandbox/assets/kenney/characters/character_animated.glb"


def import_fbx(path):
	before = set(bpy.data.objects)
	bpy.ops.import_scene.fbx(filepath=path, automatic_bone_orientation=True)
	return [o for o in bpy.data.objects if o not in before]


def main():
	bpy.ops.wm.read_factory_settings(use_empty=True)

	base = import_fbx(MODEL)
	base_arm = next((o for o in base if o.type == "ARMATURE"), None)
	if base_arm is None:
		print("CONVERT-FAIL: no armature in", MODEL)
		sys.exit(1)
	print("CONVERT: base armature =", base_arm.name, "bones =", len(base_arm.data.bones))
	if base_arm.animation_data is None:
		base_arm.animation_data_create()

	for name, path in CLIPS:
		if not os.path.exists(path):
			print("CONVERT: missing clip", path)
			continue
		before = set(bpy.data.actions)
		imported = import_fbx(path)
		new_actions = [a for a in bpy.data.actions if a not in before]
		if not new_actions:
			imp_arm = next((o for o in imported if o.type == "ARMATURE"), None)
			if imp_arm and imp_arm.animation_data and imp_arm.animation_data.action:
				new_actions = [imp_arm.animation_data.action]
		if new_actions:
			act = new_actions[0]
			act.name = name
			act.use_fake_user = True
			start = int(act.frame_range[0])
			track = base_arm.animation_data.nla_tracks.new()
			track.name = name
			track.strips.new(name, start, act)
			print("CONVERT: clip", name, "frames", act.frame_range[:])
		else:
			print("CONVERT: no action found in", path)
		for o in imported:
			bpy.data.objects.remove(o, do_unlink=True)

	os.makedirs(os.path.dirname(OUT), exist_ok=True)
	bpy.ops.export_scene.gltf(
		filepath=OUT,
		export_format="GLB",
		export_animations=True,
		export_yup=True,
		export_skins=True,
		use_selection=False,
	)
	print("CONVERT-OK:", OUT)


main()
