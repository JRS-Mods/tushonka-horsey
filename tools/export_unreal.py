import unreal, os, json
# Exports the horse (body with hair planes, saddle, reins, head gear), its animations and its textures from the
# HorseStarterKit to FBX/TGA for tools/convert_blender.py. Run through UnrealEditor-Cmd -run=pythonscript against
# work/HorseExport (a throwaway copy of the kit's horse folders: nothing here is saved back).
out = os.environ.get('HORSE_EXPORT') or os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), 'work', 'exported')
os.makedirs(out, exist_ok=True)
base = '/Game/HorseStarterKit/'

MESHES = ['Horse_W_HairPlanes', 'Saddle', 'ReinsStraps', 'HeadGear']
CLIPS = [
    'Idle_Horse_4', 'Idle_Horse_4_Alt',
    'Walk_Horse_4', 'Walk_Horse_4_Fast', 'Walk_Horse_4_Left', 'Walk_Horse_4_Right', 'Walk_Horse_4_Left_Hard', 'Walk_Horse_4_Right_Hard',
    'Walk_Backwards_Horse_4', 'Walk_Backwards_Horse_4_fast',
    'Trot_Horse_4', 'Trot_Horse_4_Fast', 'Trot_Horse_4_Left', 'Trot_Horse_4_Right', 'Trot_Horse_4_Left_HardLeft', 'Trot_Horse_4_Right_HardRight',
    'Run_Horse_4', 'Run_Horse_4_Fast', 'Run_Horse_4_Left', 'Run_Horse_4_Right', 'Run_Horse_4_Left_Hard', 'Run_Horse_4_Right_Hard',
    'Jump_Horse_4', 'Jump_Horse_4_Start', 'Jump_Horse_4_Loop', 'Jump_Horse_4_End',
    'Mount_Left_Horse', 'Mount_Right_Horse', 'Dismount_Left_Horse_4',
]
TEXTURES = ['Horse_Color_Dark_Brown', 'Horse_Color_Light_Brown', 'Horse_Color_WhiteSpot', 'Horse_Color_Brown_3',
            'Black_Hair_Color', 'Horse_Hair_Color', 'Horse_Hair_Wite', 'eyeBallColor', 'saddle_Color', 'Reins_Color',
            'Horse_L2_Normal']

report = {'exports': [], 'cloth': [], 'movement': {}}

# The mane and tail are cloth-simulated sections, which the FBX exporter leaves out. Unbound from their cloth they're
# ordinary skinned sections again (weighted to the neck and tail bones), so they export and follow the animation.
sub = unreal.get_editor_subsystem(unreal.SkeletalMeshEditorSubsystem)
hair = unreal.load_asset(base + 'Horse_Meshes/Horse_W_HairPlanes')
for lod in range(sub.get_lod_count(hair)):
    for s in range(sub.get_num_sections(hair, lod)):
        report['cloth'].append({'lod': lod, 'section': s, 'cloth': bool(hair.is_section_using_cloth(s, lod) if hasattr(hair, 'is_section_using_cloth') else None)})
unbound = 0
for c in list(hair.get_editor_property('mesh_clothing_assets')):
    for attempt in ('unbind_from_skeletal_mesh',):
        fn = getattr(c, attempt, None)
        if fn:
            try: fn(hair); unbound += 1
            except Exception as e: report['cloth'].append({'unbind_error': str(e)})
if unbound == 0:
    hair.set_editor_property('mesh_clothing_assets', [])
    try: hair.post_edit_change()
    except Exception: pass
report['cloth'].append({'unbound_by_method': unbound, 'clothing_left': len(hair.get_editor_property('mesh_clothing_assets')),
                        'verts_lod0': sub.get_num_verts(hair, 0)})

# Every clip that came with the kit: the horse's (Animation/) and the rider's for the UE4 mannequin
# (Animation/UE4_Rider_Animations/, played in the kit on a rider mesh placed exactly like the horse's, so the two line up).
reg = unreal.AssetRegistryHelpers.get_asset_registry()
def clips_in(folder):
    found = []
    for d in reg.get_assets_by_path(base + folder, recursive=False):
        if str(d.asset_class_path.asset_name) != 'AnimSequence': continue
        a = unreal.load_asset(str(d.package_name))
        if a and a.get_editor_property('skeleton'): found.append(str(d.asset_name))
    return sorted(found)
CLIPS = clips_in('Animation')
RIDER_CLIPS = clips_in('Animation/UE4_Rider_Animations')
report['clip_lists'] = {'horse': CLIPS, 'rider': RIDER_CLIPS}

items = [('Horse_Meshes/' + m, m + '.fbx') for m in MESHES]
items.append(('ThirdPersonBP/Character/Mesh/SK_Mannequin', 'SK_Mannequin.fbx'))
items += [('Animation/' + c, 'anim/' + c + '.fbx') for c in CLIPS]
items += [('Animation/UE4_Rider_Animations/' + c, 'rider/' + c + '.fbx') for c in RIDER_CLIPS]
os.makedirs(os.path.join(out, 'rider'), exist_ok=True)
items += [('Texture/' + t, 'tex/' + t + '.tga') for t in TEXTURES]
os.makedirs(os.path.join(out, 'anim'), exist_ok=True)
os.makedirs(os.path.join(out, 'tex'), exist_ok=True)

for path, filename in items:
    asset = unreal.load_asset(base + path)
    if not asset: raise RuntimeError('Cannot load ' + base + path)
    task = unreal.AssetExportTask()
    task.object = asset
    task.filename = os.path.join(out, filename)
    task.automated = True
    task.prompt = False
    task.replace_identical = True
    if filename.endswith('.fbx'):
        options = unreal.FbxExportOption()
        options.ascii = False
        options.level_of_detail = False
        options.collision = False
        task.options = options
    ok = unreal.Exporter.run_asset_export_task(task)
    report['exports'].append({'asset': path, 'output': filename, 'success': ok, 'errors': list(task.errors)})
    if not ok: raise RuntimeError('Export failed: ' + path)

# The kit's own movement settings for the rideable horse.
try:
    gen = unreal.load_object(None, base + 'Blueprint/Parent_Class/BP_Rideable_Horse_Parent.BP_Rideable_Horse_Parent_C')
    cdo = unreal.get_default_object(gen)
    mc = cdo.get_editor_property('character_movement')
    for p in ['max_walk_speed', 'max_acceleration', 'braking_deceleration_walking', 'jump_z_velocity', 'gravity_scale', 'air_control']:
        try: report['movement'][p] = mc.get_editor_property(p)
        except Exception as e: report['movement'][p] = 'err ' + str(e)
    report['movement']['rotation_rate_yaw'] = mc.get_editor_property('rotation_rate').yaw
except Exception as e:
    report['movement']['error'] = str(e)

with open(os.path.join(out, 'export-report.json'), 'w') as f: json.dump(report, f, indent=2)
print('HORSE_EXPORT_OK', len(report['exports']), json.dumps(report['cloth']))
