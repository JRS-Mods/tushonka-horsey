import bpy, os, json
from mathutils import Vector
# Looks at the exported horse in Blender: its bones, mesh parts and materials, and for each clip whether the root
# travels (root motion) and how fast a planted hoof slides back (the ground speed an in-place clip was made for).
root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
source = os.environ.get('HORSE_EXPORT') or os.path.join(root, 'work', 'exported')
bpy.ops.wm.read_factory_settings(use_empty=True)
result = {'meshes': {}, 'clips': {}}
for name in ['Horse_W_HairPlanes', 'Saddle', 'ReinsStraps', 'HeadGear']:
    before = set(bpy.data.objects)
    bpy.ops.import_scene.fbx(filepath=os.path.join(source, name + '.fbx'))
    added = set(bpy.data.objects) - before
    arm = next(o for o in added if o.type == 'ARMATURE')
    if name == 'Horse_W_HairPlanes':
        result['bones'] = [(b.name, b.parent.name if b.parent else None, [round(v, 3) for v in (arm.matrix_world @ b.head_local)]) for b in arm.data.bones]
        result['armature_matrix'] = [list(r) for r in arm.matrix_world]
    for o in added:
        if o.type == 'MESH':
            result['meshes'][name + '/' + o.name] = {'verts': len(o.data.vertices), 'tris': sum(len(p.vertices) - 2 for p in o.data.polygons),
                                                    'materials': [m.name if m else None for m in o.data.materials],
                                                    'uv_layers': [u.name for u in o.data.uv_layers], 'dims': [round(v, 3) for v in o.dimensions]}
for o in list(bpy.data.objects): bpy.data.objects.remove(o, do_unlink=True)

hooves = None
for clip in sorted(os.listdir(os.path.join(source, 'anim'))):
    name = clip[:-4]
    before = set(bpy.data.objects)
    bpy.ops.import_scene.fbx(filepath=os.path.join(source, 'anim', clip))
    added = set(bpy.data.objects) - before
    arm = next(o for o in added if o.type == 'ARMATURE')
    if hooves is None:
        hooves = [b.name for b in arm.data.bones if not b.children and any(k in b.name.lower() for k in ('hoof', 'toe', 'foot', 'phalanx'))]
        if not hooves:
            # The lowest leaf bones are the hooves.
            leaves = [b for b in arm.data.bones if not b.children]
            leaves.sort(key=lambda b: (arm.matrix_world @ b.head_local).z)
            hooves = [b.name for b in leaves[:4]]
        result['hooves'] = hooves
    act = arm.animation_data.action
    start, end = act.frame_range
    fps = bpy.context.scene.render.fps / bpy.context.scene.render.fps_base
    rootbone = arm.pose.bones[0]
    samples = []
    steps = max(8, int((end - start)))
    for j in range(steps + 1):
        f = start + (end - start) * j / steps
        bpy.context.scene.frame_set(int(f), subframe=f - int(f))
        samples.append((f, (arm.matrix_world @ rootbone.matrix.translation).copy(),
                        [(arm.matrix_world @ arm.pose.bones[h].matrix.translation).copy() for h in hooves]))
    # Ground speed: while a hoof is near its lowest, how fast it moves horizontally (in place: it slides back).
    speeds = []
    for k in range(len(hooves)):
        low = min(s[2][k].z for s in samples)
        for a, b in zip(samples, samples[1:]):
            if a[2][k].z < low + 0.03 and b[2][k].z < low + 0.03:
                dt = (b[0] - a[0]) / fps
                if dt > 0:
                    d = (b[2][k] - a[2][k]); d.z = 0
                    rd = (b[1] - a[1]); rd.z = 0
                    speeds.append(((d - rd).length / dt, d.x / dt, d.y / dt))
    speeds.sort()
    med = speeds[len(speeds) // 2] if speeds else (0, 0, 0)
    travel = samples[-1][1] - samples[0][1]
    result['clips'][name] = {'seconds': round((end - start) / fps, 3), 'frames': end - start, 'fps': fps,
                             'root_travel': [round(v, 3) for v in travel], 'root_bone': rootbone.name,
                             'planted_hoof_speed': round(med[0], 3), 'hoof_vx': round(med[1], 3), 'hoof_vy': round(med[2], 3),
                             'root_z_range': round(max(s[1].z for s in samples) - min(s[1].z for s in samples), 3)}
    for o in added: bpy.data.objects.remove(o, do_unlink=True)

with open(os.path.join(source, 'analysis.json'), 'w') as f: json.dump(result, f, indent=1)
print('HORSE_ANALYSIS_OK')
