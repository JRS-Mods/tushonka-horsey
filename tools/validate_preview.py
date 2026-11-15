import bpy, os, struct, math, json
import numpy as np
from mathutils import Matrix, Vector, Quaternion
# Reads assets/horse.bin back exactly as the mod does, skins it on the CPU (the same linear blend the game's skinned
# mesh does) for a frame of several clips, and renders them side by side: work/exported/preview.png.
# HORSE_PREVIEW_CLIPS=a,b,c picks the clips; HORSE_PREVIEW_VIEW=x,y,z,ortho moves the camera.
root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
data = os.path.join(root, 'assets')
out = os.environ.get('HORSE_EXPORT') or os.path.join(root, 'work', 'exported')
bpy.ops.wm.read_factory_settings(use_empty=True)
f = open(os.path.join(data, 'horse.bin'), 'rb')
def ints(n=1): return struct.unpack('<' + 'i' * n, f.read(4 * n))
def floats(n): return struct.unpack('<' + 'f' * n, f.read(4 * n))
def text(): return f.read(ints()[0]).decode('utf8')
def matrix():
    v = floats(10)
    assert all(math.isfinite(x) for x in v)
    q = Quaternion((v[6], v[3], v[4], v[5]))
    assert abs(q.magnitude - 1) < 0.001
    return Matrix.LocRotScale(Vector(v[:3]), q, Vector(v[7:]))
assert ints()[0] == 0x31535248
count = ints()[0]; parents = []; rest = []; bone_names = []
for i in range(count):
    name = text(); bone_names.append(name); p = ints()[0]; parents.append(p); local = matrix()
    rest.append(rest[p] @ local if p >= 0 else local)
meshes = []
for _ in range(ints()[0]):
    name = text(); n = ints()[0]
    raw = np.frombuffer(f.read(64 * n), dtype=np.uint8).reshape(n, 64)
    fl = raw.view(np.float32).reshape(n, 16); it = raw.view(np.int32).reshape(n, 16)
    pos = fl[:, 0:3].astype(np.float64); uv = fl[:, 6:8]; bi = it[:, 8:12]; bw = fl[:, 12:16]
    assert np.all(np.abs(bw.sum(1) - 1) < 1e-4) and bi.min() >= 0 and bi.max() < count
    subs = []
    for _ in range(ints()[0]):
        kind = text(); tri = np.array(ints(ints()[0])).reshape(-1, 3)
        assert tri.max() < n
        subs.append((kind, tri))
    meshes.append((name, pos, uv, bi, bw, subs))
clips = {}
for _ in range(ints()[0]):
    name = text(); frames = ints()[0]; fps = floats(1)[0]; poses = []
    for frame in range(frames):
        world = []
        for i in range(count):
            m = matrix(); p = parents[i]; world.append(world[p] @ m if p >= 0 else m)
        poses.append(world)
    clips[name] = (fps, poses)
assert not f.read(1); f.close()

C = Matrix(((-1, 0, 0, 0), (0, 0, -1, 0), (0, 1, 0, 0), (0, 0, 0, 1)))
def material(name, image=None, color=(.5, .5, .5, 1), alpha=False, metal=0, rough=.6):
    m = bpy.data.materials.new(name); m.use_nodes = True
    bsdf = m.node_tree.nodes.get('Principled BSDF')
    bsdf.inputs['Roughness'].default_value = rough; bsdf.inputs['Metallic'].default_value = metal
    bsdf.inputs['Base Color'].default_value = color
    if image:
        t = m.node_tree.nodes.new('ShaderNodeTexImage'); t.image = bpy.data.images.load(os.path.join(data, image))
        m.node_tree.links.new(t.outputs['Color'], bsdf.inputs['Base Color'])
        if alpha: m.node_tree.links.new(t.outputs['Alpha'], bsdf.inputs['Alpha'])
    return m
coat = os.environ.get('HORSE_PREVIEW_COAT', 'darkbrown')
mane = {'darkbrown': 'black', 'lightbrown': 'brown', 'spotted': 'white', 'brown': 'black'}[coat]
materials = {'coat': material('coat', 'coat_%s.jpg' % coat, rough=.7), 'eye': material('eye', 'eye.jpg', rough=.1),
             'mane': material('mane', 'mane_%s.png' % mane, alpha=True, rough=.8), 'saddle': material('saddle', 'saddle.jpg', rough=.5),
             'saddle_metal': material('saddle_metal', 'saddle.jpg', metal=1, rough=.3), 'wood': material('wood', color=(.25, .14, .07, 1)),
             'leather': material('leather', 'reins.jpg', rough=.55), 'metal': material('metal', color=(.6, .6, .6, 1), metal=1, rough=.3)}

# The rider (rider.bin), drawn as a stick figure on the horse: entries are horseclip:riderclip:progress.
rider = {}
rpath = os.path.join(data, 'rider.bin')
if os.path.exists(rpath):
    f = open(rpath, 'rb')
    assert ints()[0] == 0x31524452
    jn = ints()[0]; joints = [text() for _ in range(jn)]
    for _ in range(ints()[0]):
        name = text(); frames = ints()[0]; fps = floats(1)[0]
        rider[name] = np.array(floats(frames * jn * 3)).reshape(frames, jn, 3)
    assert not f.read(1); f.close()
CHAINS = [['pelvis', 'spine_01', 'spine_03', 'neck_01', 'head'], ['spine_03', 'clavicle_l', 'upperarm_l', 'lowerarm_l', 'hand_l', 'middle_01_l'],
          ['spine_03', 'clavicle_r', 'upperarm_r', 'lowerarm_r', 'hand_r', 'middle_01_r'], ['pelvis', 'thigh_l', 'calf_l', 'foot_l', 'ball_l'],
          ['pelvis', 'thigh_r', 'calf_r', 'foot_r', 'ball_r']]
bounds = {}
preview = os.environ.get('HORSE_PREVIEW_CLIPS', 'Idle_Horse_4:Idle_Rider_4:.3,Walk_Horse_4:Walk_Rider_4:.3,Trot_Horse_4:Trot_Rider_4:.3,'
                         'Run_Horse_4:Run_Rider_4:.3,Mount_Left_Horse:DismountLeft_Rider:.55,Dismount_Left_Horse_4:Dismount_Right_Rider:.5').split(',')
stick = material('rider', color=(.8, .1, .1, 1))
rest_inv = [r.inverted() for r in rest]
for offset, entry in enumerate(preview):
    parts = entry.split(':')
    name = parts[0]; prog = float(parts[2]) if len(parts) > 2 else 1 / 3
    fps, poses = clips[name]
    sample = poses[min(len(poses) - 1, int(prog * (len(poses) - 1)))]
    skins = np.array([np.array(sample[i] @ rest_inv[i]) for i in range(count)])
    place = Vector(((offset % 3 - 1) * 3.0, (offset // 3) * 3.4, 0))
    lows = []
    for mname, pos, uv, bi, bw, subs in meshes:
        hom = np.c_[pos, np.ones(len(pos))]
        posed = np.zeros((len(pos), 3))
        for k in range(4):
            m = skins[bi[:, k]]
            posed += bw[:, k:k + 1] * np.einsum('nij,nj->ni', m, hom)[:, :3]
        assert np.all(np.isfinite(posed))
        verts = [C @ Vector(p) for p in posed]
        lows.append(min(v.z for v in verts))
        if mname == 'Horse_W_HairPlanes':
            bounds[name] = [[min(v[d] for v in verts), max(v[d] for v in verts)] for d in range(3)]
        for kind, tri in subs:
            mesh = bpy.data.meshes.new(mname + kind)
            mesh.from_pydata(verts, [], [(a, c, b) for a, b, c in tri.tolist()]); mesh.update()
            uvl = mesh.uv_layers.new()
            idx = np.empty(len(mesh.loops), dtype=np.int32); mesh.loops.foreach_get('vertex_index', idx)
            uvl.data.foreach_set('uv', uv[idx].ravel())
            for poly in mesh.polygons: poly.use_smooth = True
            ob = bpy.data.objects.new(mname + kind, mesh); bpy.context.collection.objects.link(ob)
            ob.location = place; ob.data.materials.append(materials[kind])
    if len(parts) > 1 and parts[1] in rider:
        r = rider[parts[1]]; fr = r[min(len(r) - 1, int(prog * (len(r) - 1)))]
        for chain in CHAINS:
            cu = bpy.data.curves.new('stick', 'CURVE'); cu.dimensions = '3D'; cu.bevel_depth = .025
            sp = cu.splines.new('POLY'); sp.points.add(len(chain) - 1)
            for k, j in enumerate(chain):
                v = C @ Vector(fr[joints.index(j)]); sp.points[k].co = (*v, 1)
            ob = bpy.data.objects.new('stick', cu); bpy.context.collection.objects.link(ob); ob.location = place
            ob.data.materials.append(stick)
        bpy.ops.mesh.primitive_uv_sphere_add(radius=.11, location=place + C @ Vector(fr[joints.index('head')]))
        bpy.context.object.data.materials.append(stick)
    curve = bpy.data.curves.new(name + ' label', 'FONT'); curve.body = entry; curve.align_x = 'CENTER'; curve.size = 0.2
    label = bpy.data.objects.new(name + ' label', curve); bpy.context.collection.objects.link(label)
    label.location = place + Vector((0, -1.7, 0.01))
bpy.ops.mesh.primitive_plane_add(size=200)
floor = bpy.context.object; floor.data.materials.append(material('floor', color=(.12, .15, .18, 1)))
for location, energy, size in [((3, -6, 8), 5000, 8), ((-8, 2, 5), 3000, 6)]:
    bpy.ops.object.light_add(type='AREA', location=location)
    light = bpy.context.object; light.data.energy = energy; light.data.size = size
    light.rotation_euler = (Vector((0, 1.5, 1)) - light.location).to_track_quat('-Z', 'Y').to_euler()
view = [float(x) for x in os.environ.get('HORSE_PREVIEW_VIEW', '9,-9,7,11').split(',')]
look = Vector((0, 1.7, 0.9))
bpy.ops.object.camera_add(location=tuple(view[:3]))
camera = bpy.context.object; camera.rotation_euler = (look - camera.location).to_track_quat('-Z', 'Y').to_euler()
camera.data.type = 'ORTHO'; camera.data.ortho_scale = view[3]
scene = bpy.context.scene; scene.camera = camera; scene.render.engine = 'CYCLES'; scene.cycles.samples = 24
scene.render.resolution_x = 1600; scene.render.resolution_y = 1100
scene.world = bpy.data.worlds.new('World'); scene.world.use_nodes = True
scene.world.node_tree.nodes['Background'].inputs[0].default_value = (0.35, 0.35, 0.35, 1)
scene.view_settings.view_transform = 'AgX'
scene.render.filepath = os.path.join(out, os.environ.get('HORSE_PREVIEW_NAME', 'preview.png'))
with open(os.path.join(out, 'pose-validation.json'), 'w') as fh: json.dump({'bounds': bounds, 'bones': bone_names}, fh, indent=1)
bpy.ops.render.render(write_still=True)
print('HORSE_BINARY_VALIDATED', json.dumps(bounds))
