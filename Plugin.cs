using System;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using EFT;
using HarmonyLib;
using UnityEngine;

namespace TushonkaHorse
{
    [BepInPlugin(Id, "JRS-TushonkaHorse", Version)]
    [BepInDependency("com.fika.core", BepInDependency.DependencyFlags.SoftDependency)]
    // Loaded after Fika.Headless when it's there, so it has already said this is a headless host when Awake asks.
    [BepInDependency(HorseNet.HeadlessId, BepInDependency.DependencyFlags.SoftDependency)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Id = "jrs.tushonkahorse";
        public const string Version = "1.0.0";
        internal static ManualLogSource Log;
        internal static string AssetFolder;

        internal static ConfigEntry<bool> Enabled, SpawnAtRaidStart, AnimatedMount, StowWeapon, DrawInSaddle, ReinHands, OneHanded, GuideView, HeadRoom, SteerWithCamera, NormalMaps, ShowDebug, SelfTest;
        internal static ConfigEntry<string> HorseName;
        internal static ConfigEntry<Coat> HorseCoat;
        internal static ConfigEntry<LegStyle> RiderLegs;
        internal static ConfigEntry<KeyboardShortcut> MountKey, CallKey;
        internal static ConfigEntry<float> Scale, WalkSpeed, TrotSpeed, GallopSpeed, BackSpeed, TurnRate, Acceleration, JumpHeight,
            SeatOffset, MountAnimSpeed, MountRange, CallTeleportDistance, ManeCutoff;

        private static readonly string[] Files =
        {
            "horse.bin", "coat_darkbrown.jpg", "coat_lightbrown.jpg", "coat_spotted.jpg", "coat_brown.jpg", "coat_n.png",
            "mane_black.png", "mane_brown.png", "mane_white.png", "eye.jpg", "saddle.jpg", "reins.jpg", "rider.bin"
        };

        // Fine-tuning, kept out of sight in F12 until "Advanced settings" is ticked.
        private static readonly ConfigurationManagerAttributes Advanced = new ConfigurationManagerAttributes { IsAdvanced = true };

        private void Awake()
        {
            Log = Logger;
            AssetFolder = Path.GetDirectoryName(Info.Location);

            Enabled = Config.Bind("1. General", "Enabled", true, "Have a horse in raids.");
            SpawnAtRaidStart = Config.Bind("1. General", "Spawn at raid start", false, "Off: there's no horse until you call it (the call key), and then it turns up beside you. On: it's waiting beside you when the raid starts.");
            HorseName = Config.Bind("1. General", "Horse name", "Tushonka", "What the horse is called in messages.");
            HorseCoat = Config.Bind("1. General", "Coat", Coat.DarkBrown, "Coat colour (with its mane). Takes effect next raid.");
            Scale = Config.Bind("1. General", "Size", 1f, new ConfigDescription("Size of the horse. Takes effect next raid.", new AcceptableValueRange<float>(.8f, 1.25f), Advanced));

            MountKey = Config.Bind("2. Controls", "Mount / dismount", new KeyboardShortcut(KeyCode.H), "Get on (within a few metres of the horse) or off.");
            CallKey = Config.Bind("2. Controls", "Call horse", new KeyboardShortcut(KeyCode.H, KeyCode.LeftShift), "Whistle: the horse comes to you (and turns up nearer if it's far off).");
            SteerWithCamera = Config.Bind("2. Controls", "Steer with camera", false, new ConfigDescription("Riding forward with no A/D held, the horse turns toward where you look. Off: A/D steer and you look around freely.", null, Advanced));

            WalkSpeed = Config.Bind("3. Riding", "Walk speed", 1.5f, new ConfigDescription("m/s. Riding with the game's walk speed turned down (mouse wheel or walk toggle).", new AcceptableValueRange<float>(.8f, 2.5f)));
            TrotSpeed = Config.Bind("3. Riding", "Trot speed", 3.2f, new ConfigDescription("m/s. W on its own.", new AcceptableValueRange<float>(2f, 5f)));
            GallopSpeed = Config.Bind("3. Riding", "Gallop speed", 8.5f, new ConfigDescription("m/s. W with sprint.", new AcceptableValueRange<float>(5f, 13f)));
            BackSpeed = Config.Bind("3. Riding", "Back-up speed", 1f, new ConfigDescription("m/s. S.", new AcceptableValueRange<float>(.4f, 2f), Advanced));
            TurnRate = Config.Bind("3. Riding", "Turn rate", 95f, new ConfigDescription("Degrees per second at a walk (less at a gallop).", new AcceptableValueRange<float>(40f, 180f), Advanced));
            Acceleration = Config.Bind("3. Riding", "Acceleration", 2.6f, new ConfigDescription("m/s². Stopping is about twice as quick.", new AcceptableValueRange<float>(1f, 8f), Advanced));
            JumpHeight = Config.Bind("3. Riding", "Jump height", 1.1f, new ConfigDescription("Metres.", new AcceptableValueRange<float>(.4f, 1.8f)));
            AnimatedMount = Config.Bind("3. Riding", "Animated mount and dismount", true, "Climb on and off with the kit's rider animation on your own body: your arms and legs do the climbing and your view follows. Off: a quick hop.");
            StowWeapon = Config.Bind("3. Riding", "Put weapon away to climb", true, new ConfigDescription("Whatever's in your hands goes away before you get on or off (the climb waits for it), and comes back out after.", null, Advanced));
            DrawInSaddle = Config.Bind("3. Riding", "Draw weapon in the saddle", true, new ConfigDescription("Once you're on, the weapon you put away comes back out. Off: it stays away until you get off (draw it yourself any time).", null, Advanced));
            ReinHands = Config.Bind("3. Riding", "Hold the reins when unarmed", true, new ConfigDescription("Riding with nothing in your hands, they hold the reins.", null, Advanced));
            OneHanded = Config.Bind("3. Riding", "Left hand on the reins", false, new ConfigDescription("Riding with a gun out, your left hand holds the reins and you shoot one-handed from the hip. Both hands go on the gun to aim down the sights and to reload. Off: both hands on the gun all the time.", null, Advanced));
            GuideView = Config.Bind("3. Riding", "Climb turns your view", true, new ConfigDescription("Getting on and off, your view turns the way the animated rider faces (you can still look about).", null, Advanced));
            MountAnimSpeed = Config.Bind("3. Riding", "Mount animation speed", 1.25f, new ConfigDescription("1 = as animated (3.3 s).", new AcceptableValueRange<float>(.5f, 3f), Advanced));
            RiderLegs = Config.Bind("3. Riding", "Rider legs", LegStyle.Animated, "Animated: astride the horse, feet in the stirrups, moving with each gait (the kit's rider animations). Hidden: not drawn.");
            SeatOffset = Config.Bind("3. Riding", "Seat offset", 0f, new ConfigDescription("Raise (+) or lower (-) yourself in the saddle (m), if your view sits too high or low.", new AcceptableValueRange<float>(-.5f, .5f)));
            HeadRoom = Config.Bind("3. Riding", "Rider needs head room", true, new ConfigDescription("Ridden, the horse won't go where your head wouldn't fit (doorways, low roofs).", null, Advanced));
            MountRange = Config.Bind("3. Riding", "Mount range", 3.2f, new ConfigDescription("How close (m) to the horse's middle you need to be to get on.", new AcceptableValueRange<float>(1.5f, 6f), Advanced));
            CallTeleportDistance = Config.Bind("3. Riding", "Call: appear nearer beyond", 80f, new ConfigDescription("Called from further away than this (m), the horse turns up behind you instead of crossing the map.", new AcceptableValueRange<float>(20f, 500f), Advanced));

            NormalMaps = Config.Bind("5. Graphics", "Coat detail map", true, new ConfigDescription("The coat's normal map (muscle and hair detail). Takes effect next raid.", null, Advanced));
            ManeCutoff = Config.Bind("5. Graphics", "Mane cutoff", .35f, new ConfigDescription("How see-through a hair card's pixel can be and still show. Takes effect next raid.", new AcceptableValueRange<float>(.05f, .9f), Advanced));
            ShowDebug = Config.Bind("6. Debug", "Show debug line", false, new ConfigDescription("Speed, gait and input on screen.", null, Advanced));
            SelfTest = Config.Bind("6. Debug", "Self test", false, new ConfigDescription("A few seconds into a raid (and whenever a file called test.go appears in the mod's folder) the mod gets on, rides and gets off by itself, saving pictures and a log to its 'test' folder. For checking the mod; leave off to play.", null, Advanced));

            HorseNet.Load();
            if (HorseNet.Headless)
            {
                Log.LogInfo($"Tushonka Horse {Version} ready (Fika headless relay).");
                return;
            }

            foreach (string file in Files)
                if (!File.Exists(Path.Combine(AssetFolder, file)))
                {
                    Log.LogError($"Missing {file} in {AssetFolder}. Install the complete package.");
                    return;
                }
            HorseAssets.WarmUp(AssetFolder);
            try { Riding.Animation = RiderAnimation.Load(Path.Combine(AssetFolder, "rider.bin")); }
            catch (Exception e) { Log.LogError("Rider animations couldn't be read (riding still works, without them): " + e.Message); }
            var harmony = new Harmony(Id);
            harmony.Patch(AccessTools.Method(typeof(GameWorld), nameof(GameWorld.OnGameStarted)), postfix: new HarmonyMethod(typeof(Plugin), nameof(RaidStarted)));
            try { Riding.Patch(harmony); }
            catch (Exception e) { Log.LogError("Riding patches failed; the horse can't be ridden: " + e); }
            Log.LogInfo($"Tushonka Horse {Version} ready.");
        }

        private static void RaidStarted(GameWorld __instance)
        {
            try
            {
                if (!Enabled.Value || __instance == null || __instance is HideoutGameWorld || __instance.MainPlayer is HideoutPlayer
                    || HorseNet.Headless || string.Equals(__instance.LocationId, "hideout", StringComparison.OrdinalIgnoreCase)) return;
                if (__instance.GetComponent<Horse>() == null) __instance.gameObject.AddComponent<Horse>();
            }
            catch (Exception e) { Log.LogError("Horse raid setup failed: " + e); }
        }
    }
}
