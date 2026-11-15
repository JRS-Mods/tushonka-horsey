import unreal, os
out = r'd:\Tushonka - Horse\work\exported'
m = unreal.load_asset('/Game/HorseStarterKit/Horse_Meshes/Horse_W_HairPlanes')
opts = unreal.GLTFExportOptions()
for k, v in [('export_vertex_skin_weights', True), ('export_unlit_materials', False), ('bake_material_inputs', unreal.GLTFMaterialBakeMode.DISABLED),
             ('export_level_sequences', False), ('export_animation_sequences', False), ('default_level_of_detail', 0), ('export_preview_mesh', False)]:
    try: opts.set_editor_property(k, v)
    except Exception as e: print('GLTF opt', k, e)
msgs = unreal.GLTFExporter.export_to_gltf(m, os.path.join(out, 'Horse_W_HairPlanes.glb'), opts, set())
print('GLTF_DONE', msgs)
