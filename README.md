# Tushonka Horse

**This is a joke mod.** You get a horse in Tarkov. You can ride it into a firefight. That's it, and it's funny as
hell. It's not going to get updates, fixes or new features. What's here is what you get.

Client-side BepInEx plugin for SPT, with Fika and headless-host support. Whistle and your horse turns up beside you
in the raid. Get on and ride.

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
| Jump | Jump (about 1.1 m). Off a ledge it falls and lands |

## Settings (F12)

Name, coat (dark brown, light brown, spotted, brown), size, keys, speeds, turn rate, acceleration, jump height,
animated or quick mounting, weapon and reins options, animated or hidden legs, seat offset, a debug line, and a self
test (the mod gets on, rides and gets off by itself a few seconds into a raid, saving pictures and a log to its
`test` folder). The fine-tuning ones appear when "Advanced settings" is ticked.

## Fika

Every player, and the headless host when one is used, must install the same version. Each player has their own
horse, and everyone sees everyone else's horse and rider. A headless host only relays and doesn't load the horse.

## Limits

- The horse can't be hurt, and bots ignore it.
- Ridden, it won't go through doorways or under low roofs (your head wouldn't fit).
- Things are a bit janky in places. It's a joke mod. Enjoy the jank.

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
