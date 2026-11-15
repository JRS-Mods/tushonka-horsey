# Tushonka Horse

Client-side BepInEx plugin for SPT, with Fika and headless-host support. Call your horse (a saddled one, from the HorseStarterKit Unreal project) in a
raid and it turns up beside you, to ride. It doesn't appear until you call it, unless you set it to wait beside
you at the start of each raid.

## Install

Needs SPT 4.1.x. Unzip the release zip into your SPT folder (it holds `BepInEx\plugins\TushonkaHorse\`). With Fika,
every player and any headless host installs the same version.

## Controls

| Key | Action |
| --- | --- |
| H | Mount / dismount (within about 3 m of the horse) |
| Shift+H | Call the horse: the first time it turns up beside you; after that it comes to you (and turns up nearer if it's far off) |
| W | Trot (walk if the game's walk speed is turned down: mouse wheel or walk toggle) |
| W + Sprint | Gallop |
| S | Back up / brake |
| A / D | Turn |
| Jump | Jump (about 1.1 m). Off a ledge it falls and lands with the kit's fall and landing clips |

Getting on, you put your weapon away and step to the horse's side, then climb with the kit's rider animation on
your own body: your arms grab the saddle, your leg swings over, and your view goes where the rider's head goes,
turning the way the rider faces and down to the saddle while your hands are on it. The animation fades in from how
you stood and out again at the end. Once you're up, your weapon comes back out. Getting off is the same the other
way round (the horse pulls up first). You get on from whichever side you're standing, and off toward the side
you're looking (left when looking ahead), or the other side if there's no room.

In the saddle your legs sit astride with your feet in the stirrups, moving with the horse's walk, trot, gallop,
turns and jumps (the kit's rider animations, put onto your own character). Your upper body is yours: look around,
aim, shoot, lean, heal and loot as usual. Your left hand keeps the reins while your right holds your gun (both
hands go on it to aim down the sights and to reload); with nothing in your hands, both hold the reins. Crouch, prone and vaulting are off in the saddle. You never fall or take
fall damage from being on the horse.

All 56 horse animations and all 55 UE4 rider animations from the kit are included. The `_Fast` and `_Reverse` clips
are frame-for-frame copies the kit plays faster or backwards; the mod does that with playback speed.

## Settings (F12)

The everyday settings are shown; the fine-tuning ones appear when "Advanced settings" is ticked at the top of the
window.

Name, coat (dark brown, light brown, spotted, brown), size, keys, speeds, turn rate, acceleration, jump height,
animated or quick mounting, mount animation speed, whether your weapon is put away to climb and drawn again in the
saddle, left hand on the reins with a gun out, reins in empty hands, whether the climb turns your view, animated or hidden legs, seat offset (if your view
sits too high or low), a debug line, and a self test (the mod gets on, rides and gets off by itself a few seconds
into a raid, saving pictures and a log to its `test` folder).

## Fika

Every player, and the headless host when one is used, must install the same complete version. Each player owns and
controls their own horse. Horse position, orientation, coat, scale and animation are relayed by the host so the other
players see it; the ordinary Fika player replication keeps its rider on it. A headless host runs only the small network
relay and does not create a horse, load its meshes, patch rider controls, or access presentation/UI objects.

Solo SPT still works without Fika installed; the optional `TushonkaHorse.Fika.dll` is loaded only when Fika is present.

## Limits

- The horse can't be hurt, and bots ignore it.
- In the saddle your upper body keeps the game's own pose (so you can still aim and shoot); only your legs (and
  empty hands) follow the rider animations there. The whole body follows them while getting on and off.
- Ridden, it won't go through doorways or under low roofs (your head wouldn't fit).

## Source

| File | What |
| --- | --- |
| `Plugin.cs` | Settings, the raid hook |
| `Horse.cs`, `Horse.Motion.cs`, `Horse.Animation.cs` | The horse in a raid: spawning, keys, coming when called; how it moves and collides; what it plays |
| `HorseAssets.cs`, `HorseModel.cs`, `TextureCache.cs`, `PoseMath.cs` | `horse.bin` read in the background and its meshes, materials and textures kept for the session; one horse's skeleton and animation |
| `Riding.cs`, `Riding.Patches.cs`, `RiderView.cs` | You on the horse: getting on and off, your weapon, where your character is put; the hooks into the game's character; the "hidden legs" setting |
| `HorseNet.cs`, `Fika/FikaLink.cs` | Optional Fika handshake, host/headless relay, and remote horse replicas |
| `RiderAnimation.cs`, `RiderRig.cs` | `rider.bin`; putting it onto your character's hips, chest, arms and legs, blended over the game's pose |
| `HorseTest.cs` | The self test |

## Building

`.\build.ps1` builds against `F:\SPT`, packs `dist\TushonkaHorse-<version>.zip` and installs it.

The assets in `assets\` are made from the Unreal project:

1. `UnrealEditor-Cmd work\HorseExport\HorseExport.uproject -run=pythonscript -script=tools\export_unreal.py`
   (meshes, animations, textures as FBX/TGA) and the same with `tools\export_hair_gltf.py` (the mane and tail are
   cloth sections, which the FBX exporter leaves out). `work\HorseExport` is a throwaway copy of the kit's horse folders.
2. `blender -b --python tools\convert_blender.py` writes `assets\horse.bin` (horse), `assets\rider.bin`
   (rider joint positions, in the horse's space) and the textures.
3. `blender -b --python tools\validate_preview.py` reads `horse.bin` back and renders `work\exported\preview.png`
   (horse with the rider drawn as a stick figure).
