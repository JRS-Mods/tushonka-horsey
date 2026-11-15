# Tushonka Horse

Client-side BepInEx plugin for SPT, with Fika and headless-host support. Call your horse in a
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
