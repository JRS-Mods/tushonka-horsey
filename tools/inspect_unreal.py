import unreal, os, json
# Lists what the horse kit is made of (meshes, skeletons, materials and their textures, animations) so the
# export picks the right assets. Run through UnrealEditor-Cmd -run=pythonscript.
out = os.environ.get('HORSE_EXPORT') or os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), 'work', 'exported')
os.makedirs(out, exist_ok=True)
reg = unreal.AssetRegistryHelpers.get_asset_registry()
report = {'meshes': [], 'animations': [], 'materials': [], 'blueprints': []}

def tex_params(mi):
    res = {}
    try:
        for p in unreal.MaterialEditingLibrary.get_texture_parameter_names(mi):
            t = unreal.MaterialEditingLibrary.get_material_instance_texture_parameter_value(mi, p) if isinstance(mi, unreal.MaterialInstance) else None
            res[str(p)] = t.get_path_name() if t else None
    except Exception as e:
        res['error'] = str(e)
    try:
        for p in unreal.MaterialEditingLibrary.get_vector_parameter_names(mi):
            v = unreal.MaterialEditingLibrary.get_material_instance_vector_parameter_value(mi, p) if isinstance(mi, unreal.MaterialInstance) else None
            res['vec:' + str(p)] = [v.r, v.g, v.b, v.a] if v else None
    except Exception:
        pass
    try:
        res['used_textures'] = [t.get_path_name() for t in unreal.MaterialEditingLibrary.get_used_textures(mi)]
    except Exception as e:
        res['used_textures_error'] = str(e)
    try:
        base = mi.get_base_material()
        res['base'] = base.get_path_name() if base else None
        res['blend_mode'] = str(base.get_editor_property('blend_mode')) if base else None
        res['two_sided'] = bool(base.get_editor_property('two_sided')) if base else None
    except Exception as e:
        res['base_error'] = str(e)
    return res

seen_materials = set()
for data in reg.get_assets_by_path('/Game/HorseStarterKit', recursive=True):
    path = str(data.package_name)
    cls = str(data.asset_class_path.asset_name)
    if cls == 'SkeletalMesh':
        m = unreal.load_asset(path)
        skel = m.get_editor_property('skeleton')
        b = m.get_bounds()
        info = {'path': path, 'skeleton': skel.get_path_name() if skel else None,
                'bounds_extent': [b.box_extent.x, b.box_extent.y, b.box_extent.z], 'origin': [b.origin.x, b.origin.y, b.origin.z],
                'materials': [], 'sockets': []}
        try: info['lods'] = m.get_lod_num()
        except Exception: pass
        for s in m.get_editor_property('materials'):
            mi = s.get_editor_property('material_interface')
            info['materials'].append({'slot': str(s.get_editor_property('material_slot_name')), 'material': mi.get_path_name() if mi else None})
            if mi and mi.get_path_name() not in seen_materials:
                seen_materials.add(mi.get_path_name())
                report['materials'].append({'path': mi.get_path_name(), **tex_params(mi)})
        try:
            for i in range(m.num_sockets()):
                so = m.get_socket_by_index(i)
                info['sockets'].append({'name': str(so.get_editor_property('socket_name')), 'bone': str(so.get_editor_property('bone_name')),
                                        'loc': [so.relative_location.x, so.relative_location.y, so.relative_location.z]})
        except Exception as e:
            info['sockets_error'] = str(e)
        try:
            info['bone_count'] = len(skel.get_editor_property('bone_tree')) if skel else None
        except Exception:
            pass
        report['meshes'].append(info)
    elif cls == 'StaticMesh' and 'Horse_Meshes' in path:
        m = unreal.load_asset(path)
        b = m.get_bounds()
        report['meshes'].append({'path': path, 'class': 'StaticMesh', 'bounds_extent': [b.box_extent.x, b.box_extent.y, b.box_extent.z],
                                 'materials': [ (s.get_editor_property('material_interface').get_path_name() if s.get_editor_property('material_interface') else None) for s in m.get_editor_property('static_materials')]})
    elif cls == 'AnimSequence':
        a = unreal.load_asset(path)
        skel = a.get_editor_property('skeleton')
        info = {'path': path, 'skeleton': skel.get_path_name() if skel else None, 'length': a.get_play_length(),
                'root_motion': a.get_editor_property('enable_root_motion')}
        try:
            info['frames'] = unreal.AnimationLibrary.get_num_frames(a)
            info['rate'] = str(unreal.AnimationLibrary.get_frame_rate(a))
        except Exception:
            pass
        report['animations'].append(info)
    elif cls == 'Blueprint' and 'Rideable_Horse' in path:
        bp = unreal.load_asset(path)
        gen = unreal.load_object(None, path + '.' + path.split('/')[-1] + '_C')
        comps = []
        try:
            subs = unreal.get_engine_subsystem(unreal.SubobjectDataSubsystem)
            handles = subs.k2_gather_subobject_data_for_blueprint(bp)
            for h in handles:
                d = unreal.SubobjectDataBlueprintFunctionLibrary.get_data(h)
                o = unreal.SubobjectDataBlueprintFunctionLibrary.get_object(d)
                if o is None: continue
                c = {'name': o.get_name(), 'class': o.get_class().get_name()}
                if isinstance(o, unreal.SkinnedMeshComponent):
                    mesh = o.get_editor_property('skinned_asset') if hasattr(o, 'skinned_asset') else None
                    try: mesh = o.get_skinned_asset()
                    except Exception: pass
                    c['mesh'] = mesh.get_path_name() if mesh else None
                    try: c['overrides'] = [mm.get_path_name() if mm else None for mm in o.get_editor_property('override_materials')]
                    except Exception: pass
                if isinstance(o, unreal.SceneComponent):
                    t = o.get_editor_property('relative_location'); r = o.get_editor_property('relative_rotation')
                    c['loc'] = [t.x, t.y, t.z]; c['rot'] = [r.roll, r.pitch, r.yaw]
                    try:
                        c['attach_socket'] = str(o.get_attach_socket_name())
                        par = o.get_attach_parent()
                        c['parent'] = par.get_name() if par else None
                    except Exception: pass
                if isinstance(o, unreal.CapsuleComponent):
                    c['capsule'] = [o.get_editor_property('capsule_radius'), o.get_editor_property('capsule_half_height')]
                comps.append(c)
        except Exception as e:
            comps.append({'error': str(e)})
        report['blueprints'].append({'path': path, 'components': comps})

with open(os.path.join(out, 'inspect.json'), 'w') as f:
    json.dump(report, f, indent=1)
print('HORSE_INSPECT_OK meshes=%d anims=%d materials=%d' % (len(report['meshes']), len(report['animations']), len(report['materials'])))
