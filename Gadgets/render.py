import bpy, sys, math, os, glob
import numpy as np
from mathutils import Vector

KINDS = {
    'earbuds': dict(keep='buds', rest=0, tilt=8, exposure=0.0, fill=0.96),
    'headphones': dict(keep='all', silver=True, rest=-25, tilt=8, exposure=0.0, fill=0.94),
    'speaker': dict(keep='all', rest=-20, tilt=12, exposure=-0.6, fill=0.92),
}
FIRST_TURN, TURN_STEP, FRAME_COUNT, COLUMNS = -142, 2, 81, 9
FRAME_WIDTH, FRAME_HEIGHT = 144, 112
LENS, SENSOR = 50, 36

model_path, kind, sheet_path = sys.argv[sys.argv.index('--') + 1:][:3]
look = KINDS[kind]

bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.gltf(filepath=model_path)
scene = bpy.context.scene
bpy.context.view_layer.update()


def corners(o):
    return [o.matrix_world @ Vector(c) for c in o.bound_box]


meshes = [o for o in scene.objects if o.type == 'MESH']
if look['keep'] == 'buds':
    for o in meshes:
        if abs(sum(p.x for p in corners(o)) / 8) < 0.005:
            o.hide_render = True
    meshes = [o for o in meshes if not o.hide_render]

if look.get('silver'):
    for material in bpy.data.materials:
        if not material.use_nodes:
            continue
        nodes = material.node_tree
        surface = next((n for n in nodes.nodes if n.type == 'BSDF_PRINCIPLED'), None)
        if surface is None:
            continue
        base = surface.inputs['Base Color']
        fade = nodes.nodes.new('ShaderNodeHueSaturation')
        fade.inputs['Saturation'].default_value = 0.08
        fade.inputs['Value'].default_value = 1.25
        if base.is_linked:
            nodes.links.new(base.links[0].from_socket, fade.inputs['Color'])
        else:
            fade.inputs['Color'].default_value = base.default_value
        nodes.links.new(fade.outputs['Color'], base)

points = [p for o in meshes for p in corners(o)]
low = Vector((min(p.x for p in points), min(p.y for p in points), min(p.z for p in points)))
high = Vector((max(p.x for p in points), max(p.y for p in points), max(p.z for p in points)))
center, size = (low + high) / 2, (high - low).length

pivot = bpy.data.objects.new('Pivot', None)
scene.collection.objects.link(pivot)
for o in list(scene.objects):
    if o.parent is None and o is not pivot:
        o.location = o.location - center
        o.parent = pivot
bpy.context.view_layer.update()

cloud = []
for o in meshes:
    co = np.empty(len(o.data.vertices) * 3)
    o.data.vertices.foreach_get('co', co)
    co = co.reshape(-1, 3)[::7]
    world = np.array(o.matrix_world)
    cloud.append(co @ world[:3, :3].T + world[:3, 3])
cloud = np.concatenate(cloud)

tilt = math.radians(look['tilt'])
forward = np.array([0, math.cos(tilt), -math.sin(tilt)])
up = np.array([0, math.sin(tilt), math.cos(tilt)])
half_x = SENSOR / 2 / LENS
half_y = half_x * FRAME_HEIGHT / FRAME_WIDTH


def reach(distance):
    eye = -forward * distance
    widest = 0
    for step in range(FRAME_COUNT):
        turn = math.radians(look['rest'] + FIRST_TURN + step * TURN_STEP)
        c, s = math.cos(turn), math.sin(turn)
        turned = cloud @ np.array([[c, -s, 0], [s, c, 0], [0, 0, 1]]).T
        offset = turned - eye
        depth = offset @ forward
        widest = max(widest, np.max(np.abs(offset[:, 0] / depth / half_x)) / look['fill'],
                     np.max(np.abs(offset @ up / depth / half_y)) / look['fill'])
    return widest


distance = size * 4
for _ in range(5):
    distance *= reach(distance)

camera = bpy.data.objects.new('Camera', bpy.data.cameras.new('Camera'))
camera.data.lens, camera.data.sensor_width, camera.data.sensor_fit = LENS, SENSOR, 'HORIZONTAL'
camera.data.clip_start, camera.data.clip_end = distance / 100, distance * 10
camera.location = Vector(-forward * distance)
camera.rotation_euler = (math.radians(90) - tilt, 0, 0)
scene.collection.objects.link(camera)
scene.camera = camera

world = bpy.data.worlds.new('Studio')
scene.world = world
world.use_nodes = True
studio = world.node_tree.nodes.new('ShaderNodeTexEnvironment')
studio.image = bpy.data.images.load(next(p for p in glob.glob(os.path.join(
    os.path.dirname(bpy.app.binary_path), '*', 'datafiles', 'studiolights', 'world', '*.exr')) if 'studio' in os.path.basename(p)))
world.node_tree.links.new(studio.outputs['Color'], world.node_tree.nodes['Background'].inputs['Color'])
world.node_tree.nodes['Background'].inputs['Strength'].default_value = 0.9


def lamp(name, place, power, spread):
    light = bpy.data.objects.new(name, bpy.data.lights.new(name, 'AREA'))
    light.data.energy, light.data.size = power * size * size * 40, spread * size
    light.location = Vector(place) * size
    light.rotation_euler = (-light.location).to_track_quat('-Z', 'Y').to_euler()
    scene.collection.objects.link(light)


lamp('Key', (-1.2, -1.6, 1.8), 60, 1.5)
lamp('Rim', (1.4, 1.6, 1.2), 80, 1.0)

scene.render.engine = 'BLENDER_EEVEE'
scene.eevee.taa_render_samples = 64
scene.render.film_transparent = True
scene.render.resolution_x, scene.render.resolution_y = FRAME_WIDTH, FRAME_HEIGHT
scene.render.image_settings.file_format = 'PNG'
scene.render.image_settings.color_mode = 'RGBA'
scene.render.image_settings.compression = 100
scene.view_settings.view_transform = 'AgX'
scene.view_settings.exposure = look['exposure']

rows = math.ceil(FRAME_COUNT / COLUMNS)
sheet = np.zeros((rows * FRAME_HEIGHT, COLUMNS * FRAME_WIDTH, 4), dtype=np.float32)
frame_path = os.path.join(os.path.dirname(os.path.abspath(sheet_path)), f'.{kind}-frame.png')
for step in range(FRAME_COUNT):
    pivot.rotation_euler = (0, 0, math.radians(look['rest'] + FIRST_TURN + step * TURN_STEP))
    scene.render.filepath = frame_path
    bpy.ops.render.render(write_still=True)
    frame = bpy.data.images.load(frame_path)
    pixels = np.array(frame.pixels[:], dtype=np.float32).reshape(FRAME_HEIGHT, FRAME_WIDTH, 4)[::-1]
    bpy.data.images.remove(frame)
    top, left = step // COLUMNS * FRAME_HEIGHT, step % COLUMNS * FRAME_WIDTH
    sheet[top:top + FRAME_HEIGHT, left:left + FRAME_WIDTH] = pixels
os.remove(frame_path)

out = bpy.data.images.new(kind, COLUMNS * FRAME_WIDTH, rows * FRAME_HEIGHT, alpha=True)
out.pixels.foreach_set(sheet[::-1].ravel())
out.filepath_raw, out.file_format = sheet_path, 'PNG'
out.save()
print('SHEET', sheet_path, f'distance={distance:.4f}')
