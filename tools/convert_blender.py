import bpy, os, struct, json, math
from mathutils import Matrix, Vector
# Turns the exported horse (tools/export_unreal.py) into what the mod loads: assets/horse.bin (skeleton, the skinned
# body + saddle + reins + head gear, every clip baked at 30 fps) and its textures, scaled down for the game.
#
# horse.bin, little endian:
#   int 'HRS1', int boneCount; per bone: string name, int parent, pose (p.xyz, q.xyzw, s.xyz)
#   int meshCount; per mesh: string name, int vertexCount, per vertex (p3 n3 uv2 int4 bone w4) = 64 bytes,
#       int submeshCount; per submesh: string material, int indexCount, int[indexCount]
#   int clipCount; per clip: string name, int frames, float fps, frames * boneCount poses
# Strings are int length + UTF-8. Positions in metres, Unity axes (x right, y up, z forward: the horse faces +z).
root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
source = os.environ.get('HORSE_EXPORT') or os.path.join(root, 'work', 'exported')
dest = os.path.join(root, 'assets')
os.makedirs(dest, exist_ok=True)

MESHES = ['Horse_W_HairPlanes', 'Saddle', 'ReinsStraps', 'HeadGear']
# Every clip the kit came with (tools/export_unreal.py lists them).
with open(os.path.join(source, 'export-report.json')) as fh: _lists = json.load(fh)['clip_lists']
CLIPS = _lists['horse']
RIDER_CLIPS = _lists['rider']
# The rider's joints kept for retargeting onto the game's character (positions, horse space).
JOINTS = ['pelvis', 'spine_01', 'spine_03', 'neck_01', 'head',
          'clavicle_l', 'upperarm_l', 'lowerarm_l', 'hand_l', 'middle_01_l', 'index_01_l', 'pinky_01_l',
          'clavicle_r', 'upperarm_r', 'lowerarm_r', 'hand_r', 'middle_01_r', 'index_01_r', 'pinky_01_r',
          'thigh_l', 'calf_l', 'foot_l', 'ball_l', 'thigh_r', 'calf_r', 'foot_r', 'ball_r']
# Each Unreal material slot becomes one of these (the mod knows how to draw each).
def material_kind(mesh_name, slot):
    s = (slot or '').lower()
    if 'eye' in s: return 'eye'
    if 'mane' in s or 'hair' in s or 'tail' in s: return 'mane'
    if 'body' in s: return 'coat'
    if 'saddle_metal' in s: return 'saddle_metal'
    if 'saddle_wood' in s: return 'wood'
    if 'saddle' in s: return 'saddle'
    if 'reins_metal' in s: return 'metal'
    if 'reins' in s or 'leather' in s: return 'leather'
    if mesh_name == 'Saddle': return 'saddle'
    return 'leather'

bpy.ops.wm.read_factory_settings(use_empty=True)
C = Matrix(((-1, 0, 0, 0), (0, 0, 1, 0), (0, -1, 0, 0), (0, 0, 0, 1)))
Ci = C.inverted()
stats = {'meshes': [], 'clips': []}

def load(path):
    before = set(bpy.data.objects)
    bpy.ops.import_scene.fbx(filepath=path)
    return set(bpy.data.objects) - before

def pose(mat):
    p, q, s = mat.decompose()
    values = (*p, q.x, q.y, q.z, q.w, *s)
    assert all(math.isfinite(v) for v in values)
    return values

# The body's skeleton is the one everything is drawn with.
main_objects = load(os.path.join(source, MESHES[0] + '.fbx'))
arm = next(o for o in main_objects if o.type == 'ARMATURE')
bones = list(arm.data.bones)
names = [b.name for b in bones]
parents = [names.index(b.parent.name) if b.parent else -1 for b in bones]
assert all(p < i for i, p in enumerate(parents))
rest_blender = {b.name: arm.matrix_world @ b.matrix_local for b in bones}
rest = [C @ rest_blender[n] @ Ci for n in names]

def glb_alignment(own_arm):
    # The glTF import's space onto the FBX one: one affine map fitted to every shared bone's head (bones themselves are
    # oriented differently by the two importers, so per-bone matrices can't be compared).
    import numpy as np
    shared = [b.name for b in own_arm.data.bones if b.name in rest_blender]
    src = np.array([list(own_arm.matrix_world @ own_arm.data.bones[n].head_local) + [1] for n in shared])
    dst = np.array([list(rest_blender[n].translation) for n in shared])
    sol, *_ = np.linalg.lstsq(src, dst, rcond=None)
    m = Matrix(((*sol[:, 0],), (*sol[:, 1],), (*sol[:, 2],), (0, 0, 0, 1)))
    residual = float(np.abs(src @ sol - dst).max())
    return m, residual, len(shared)

def write_mesh(f, ints, floats, text, ob, mesh_name, own_arm, only_kind=None, align=None):
    mesh = ob.data
    mesh.calc_loop_triangles()
    # A part made on its own copy of the skeleton (the saddle is older): moved from its bind pose into the body's.
    fix = {}
    worst = 0
    if align is not None:
        fix = {b.name: align for b in own_arm.data.bones}
    else:
        for b in own_arm.data.bones:
            if b.name in rest_blender:
                own = own_arm.matrix_world @ b.matrix_local
                fix[b.name] = rest_blender[b.name] @ own.inverted()
                worst = max(worst, max(abs(a - c) for ra, rc in zip(own, rest_blender[b.name]) for a, c in zip(ra, rc)))
    keep = None
    if only_kind is not None:
        keep = set()
        for t in mesh.loop_triangles:
            slot = mesh.materials[t.material_index].name if mesh.materials[t.material_index] else None
            if material_kind(mesh_name, slot) == only_kind: keep.update(t.loops)
    transform = C @ ob.matrix_world
    groups = {g.index: g.name for g in ob.vertex_groups}
    uv = mesh.uv_layers.active.data
    vertex_cache = {}
    verts = []
    key_of_loop = {}
    for loop in mesh.loops:
        if keep is not None and loop.index not in keep: continue
        v = mesh.vertices[loop.vertex_index]
        n = mesh.corner_normals[loop.index].vector
        u = uv[loop.index].uv
        key = (loop.vertex_index, round(u.x, 5), round(u.y, 5), round(n.x, 3), round(n.y, 3), round(n.z, 3))
        idx = vertex_cache.get(key)
        if idx is None:
            influences = sorted([(g.weight, groups[g.group]) for g in v.groups if groups.get(g.group) in names and g.weight > 0], reverse=True)[:4]
            assert influences, 'Unweighted vertex in ' + mesh_name
            total = sum(w for w, _ in influences)
            weights = [w / total for w, _ in influences]
            bnames = [b for _, b in influences]
            # Rebind: blend of each bone's own-to-body bind change.
            m = Matrix.Diagonal((0, 0, 0, 0))
            for w, b in zip(weights, bnames):
                m = m + (fix.get(b, Matrix.Identity(4)) * w)
            world = ob.matrix_world @ v.co
            p = C @ (m @ world)
            nm = (C.to_3x3() @ (m.to_3x3().inverted().transposed() @ (ob.matrix_world.to_3x3().inverted().transposed() @ n))).normalized()
            indices = [names.index(b) for b in bnames]
            while len(weights) < 4: weights.append(0); indices.append(0)
            idx = len(verts)
            vertex_cache[key] = idx
            verts.append((p, nm, (u.x, u.y), indices, weights))
        key_of_loop[loop.index] = idx
    subs = {}
    for t in mesh.loop_triangles:
        slot = mesh.materials[t.material_index].name if t.material_index < len(mesh.materials) and mesh.materials[t.material_index] else None
        kind = material_kind(mesh_name, slot)
        if only_kind is not None and kind != only_kind: continue
        a, b, c = (key_of_loop[l] for l in t.loops)
        subs.setdefault(kind, []).extend((a, c, b))
    text(mesh_name)
    ints(len(verts))
    for p, nm, u, ind, w in verts:
        floats(*p, *nm, *u); ints(*ind); floats(*w)
    ints(len(subs))
    for kind, tris in subs.items():
        text(kind); ints(len(tris)); ints(*tris)
    stats['meshes'].append({'name': mesh_name, 'vertices': len(verts), 'triangles': sum(len(t) for t in subs.values()) // 3,
                            'submeshes': {k: len(t) // 3 for k, t in subs.items()}, 'rebind_error': worst})

with open(os.path.join(dest, 'horse.bin'), 'wb') as f:
    def ints(*a): f.write(struct.pack('<' + 'i' * len(a), *a))
    def floats(*a): f.write(struct.pack('<' + 'f' * len(a), *a))
    def text(s):
        raw = s.encode('utf8'); ints(len(raw)); f.write(raw)
    ints(0x31535248, len(bones))
    for i, b in enumerate(bones):
        text(b.name); ints(parents[i])
        local = rest[parents[i]].inverted() @ rest[i] if parents[i] >= 0 else rest[i]
        floats(*pose(local))
    ints(len(MESHES) + 1)
    for mesh_name in MESHES:
        objs = main_objects if mesh_name == MESHES[0] else load(os.path.join(source, mesh_name + '.fbx'))
        own_arm = next(o for o in objs if o.type == 'ARMATURE')
        mesh_objs = [o for o in objs if o.type == 'MESH']
        assert len(mesh_objs) == 1, (mesh_name, len(mesh_objs))
        write_mesh(f, ints, floats, text, mesh_objs[0], mesh_name, own_arm)
        if mesh_name != MESHES[0]:
            for o in objs: bpy.data.objects.remove(o, do_unlink=True)
    # The mane and tail: cloth sections in Unreal, which its FBX export leaves out, so they come from the glTF export
    # (tools/export_hair_gltf.py), skinned to the neck and tail bones.
    before = set(bpy.data.objects)
    bpy.ops.import_scene.gltf(filepath=os.path.join(source, 'Horse_W_HairPlanes.glb'))
    objs = set(bpy.data.objects) - before
    own_arm = next(o for o in objs if o.type == 'ARMATURE')
    hair_mesh = next(o for o in objs if o.type == 'MESH' and o.parent == own_arm)
    align, residual, shared = glb_alignment(own_arm)
    assert residual < 0.002, ('glTF skeleton does not line up with the FBX one', residual)
    stats['hair_alignment'] = {'residual_m': residual, 'bones': shared}
    write_mesh(f, ints, floats, text, hair_mesh, 'Mane', own_arm, only_kind='mane', align=align)
    for o in objs: bpy.data.objects.remove(o, do_unlink=True)

    ints(len(CLIPS))
    for name in CLIPS:
        added = load(os.path.join(source, 'anim', name + '.fbx'))
        anim = next(o for o in added if o.type == 'ARMATURE')
        action = anim.animation_data.action
        start, end = action.frame_range
        fps = bpy.context.scene.render.fps / bpy.context.scene.render.fps_base
        duration = (end - start) / fps
        frames = max(2, round(duration * 30) + 1)
        effective_fps = (frames - 1) / duration
        text(name); ints(frames); floats(effective_fps)
        missing = [n for n in names if n not in anim.pose.bones]
        assert not missing, (name, 'missing bones', missing)
        for j in range(frames):
            frame = start + (end - start) * j / (frames - 1)
            bpy.context.scene.frame_set(int(frame), subframe=frame - int(frame))
            matrices = [C @ anim.matrix_world @ anim.pose.bones[n].matrix @ Ci for n in names]
            for i, world in enumerate(matrices):
                local = matrices[parents[i]].inverted() @ world if parents[i] >= 0 else world
                floats(*pose(local))
        stats['clips'].append({'name': name, 'frames': frames, 'duration': round(duration, 3)})
        for ob in added: bpy.data.objects.remove(ob, do_unlink=True)

# The rider: rider.bin, little endian: int 'RDR1', int jointCount, strings, int clipCount; per clip: string name,
# int frames, float fps, frames * joints * (x, y, z). Positions are in the horse model's space (the kit plays the rider
# clips on a mesh placed exactly where the horse's is), in metres, Unity axes.
rider_stats = []
with open(os.path.join(dest, 'rider.bin'), 'wb') as f:
    def ints(*a): f.write(struct.pack('<' + 'i' * len(a), *a))
    def floats(*a): f.write(struct.pack('<' + 'f' * len(a), *a))
    def text(s):
        raw = s.encode('utf8'); ints(len(raw)); f.write(raw)
    ints(0x31524452, len(JOINTS))
    for j in JOINTS: text(j)
    ints(len(RIDER_CLIPS))
    for name in RIDER_CLIPS:
        added = load(os.path.join(source, 'rider', name + '.fbx'))
        anim = next(o for o in added if o.type == 'ARMATURE')
        action = anim.animation_data.action
        start, end = action.frame_range
        fps = bpy.context.scene.render.fps / bpy.context.scene.render.fps_base
        duration = (end - start) / fps
        frames = max(2, round(duration * 30) + 1)
        bpy.context.scene.frame_set(int(start))
        pel = anim.matrix_world @ anim.pose.bones['pelvis'].matrix.translation
        # A few clips came out of Unreal lying on their back (y up): stood up again.
        fix = Matrix.Rotation(math.radians(90), 4, 'X') if pel.z < .6 and abs(pel.y) > 1 else Matrix.Identity(4)
        text(name); ints(frames); floats((frames - 1) / duration)
        for k in range(frames):
            frame = start + (end - start) * k / (frames - 1)
            bpy.context.scene.frame_set(int(frame), subframe=frame - int(frame))
            for j in JOINTS:
                p = C @ fix @ (anim.matrix_world @ anim.pose.bones[j].matrix.translation)
                floats(p.x, p.y, p.z)
        rider_stats.append({'name': name, 'frames': frames, 'stood_up': fix != Matrix.Identity(4)})
        for ob in added: bpy.data.objects.remove(ob, do_unlink=True)
stats['rider'] = rider_stats

# Textures: colour as JPEG (smaller), anything with see-through hair as PNG.
def save(src, out, size, fmt='JPEG', transform=None):
    image = bpy.data.images.load(os.path.join(source, 'tex', src + '.tga'))
    if transform:
        # Changing these reloads the picture, so before scaling it.
        image.alpha_mode = 'STRAIGHT'
        image.colorspace_settings.name = 'Non-Color'
    if max(image.size) > size: image.scale(size, size)
    if transform:
        transform(image)
    image.filepath_raw = os.path.join(dest, out)
    image.file_format = fmt
    if fmt == 'JPEG':
        bpy.context.scene.render.image_settings.quality = 90
    image.save()
    bpy.data.images.remove(image)

def unity_normal(image):
    # Unreal's normal maps are DirectX style (green down); Unity's shader reads x from alpha (DXT5nm): (1, 1-g, 1, r).
    import numpy as np
    px = np.empty(image.size[0] * image.size[1] * 4, dtype=np.float32)
    image.pixels.foreach_get(px)
    px = px.reshape(-1, 4)
    out = np.ones_like(px)
    out[:, 1] = 1 - px[:, 1]
    out[:, 3] = px[:, 0]
    image.pixels.foreach_set(out.ravel())

for src, out in [('Horse_Color_Dark_Brown', 'coat_darkbrown.jpg'), ('Horse_Color_Light_Brown', 'coat_lightbrown.jpg'),
                 ('Horse_Color_WhiteSpot', 'coat_spotted.jpg'), ('Horse_Color_Brown_3', 'coat_brown.jpg'),
                 ('saddle_Color', 'saddle.jpg')]:
    save(src, out, 2048)
save('Reins_Color', 'reins.jpg', 1024)
save('eyeBallColor', 'eye.jpg', 512)
for src, out in [('Black_Hair_Color', 'mane_black.png'), ('Horse_Hair_Color', 'mane_brown.png'), ('Horse_Hair_Wite', 'mane_white.png')]:
    save(src, out, 1024, 'PNG')
save('Horse_L2_Normal', 'coat_n.png', 2048, 'PNG', unity_normal)

with open(os.path.join(source, 'asset-validation.json'), 'w') as f: json.dump(stats, f, indent=2)
print('HORSE_CONVERSION_OK', json.dumps(stats))
