using System;
using EFT;
using EFT.Communications;
using UnityEngine;
using UnityEngine.AI;

namespace TushonkaHorse
{
    /// <summary>
    /// The horse in a raid (one per raid, on the GameWorld). It stands near you when the raid starts, comes when called
    /// (Shift+H) and can be ridden (H, within a few metres). This part is its life in the raid: spawning, your keys,
    /// and what it does on its own. How it moves is in Horse.Motion, what it plays in Horse.Animation.
    /// </summary>
    internal sealed partial class Horse : MonoBehaviour
    {
        private GameWorld world;
        private GameObject body;
        private HorseModel model;
        private Transform spineA, spineB;
        private HorseDebug debug;
        private bool failed;
        private float nextTry, deadline;
        private float nextNet;
        private bool netSpawned;
        private int mask;

        private enum Mode { Idle, Coming }
        private Mode mode;
        private readonly NavMeshPath path = new NavMeshPath();
        private readonly Vector3[] corners = new Vector3[64];
        private int cornerCount, corner;
        private float nextPath, lastProgress, lastForward;
        private Vector3 progressPoint;

        private float Scale => Plugin.Scale.Value;
        internal float Yaw => yaw;
        internal float Speed => speed;
        /// <summary>Where the reins are gathered (every clip moves this, as if a rider held them).</summary>
        internal Vector3? ReinHold => reinHold != null ? reinHold.position : (Vector3?)null;
        private Transform reinHold;

        /// <summary>A point in the horse model's own space (where the rider animation is) in the world.</summary>
        internal Vector3 ToWorld(Vector3 local) => model.Root.TransformPoint(local);

        /// <summary>The middle of the saddle's seat, following the horse's back as it moves.</summary>
        internal Vector3 Seat => (spineA.position + spineB.position) * .5f + body.transform.up * (.13f * Scale);

        private void Awake()
        {
            world = GetComponent<GameWorld>();
            nextTry = Time.time + 1;
            deadline = Time.time + 120;
            // What your character walks into (walls, terrain, doors), never people, bodies, hitboxes or loot.
            mask = EFTHardSettings.Instance.MOVEMENT_MASK;
            if (mask == 0) mask = LayerMask.GetMask("LowPolyCollider", "Terrain");
            mask |= LayerMask.GetMask("DoorLowPolyCollider");
            mask &= ~LayerMask.GetMask("Player", "Deadbody", "HitCollider", "Loot", "Triggers", "PlayerCollisionTest", "Water");
            Plugin.Log.LogInfo("Horse collision layers: " + LayerList(mask));
        }

        private void Update()
        {
            if (failed) return;
            try { Tick(Time.deltaTime); }
            catch (Exception e)
            {
                failed = true;
                Plugin.Log.LogError("Horse stopped safely: " + e);
                Cleanup();
            }
        }

        private void Tick(float dt)
        {
            if (dt <= 0) return;
            Player player = world.MainPlayer;
            if (player == null) return;
            bool alive = player.HealthController != null && player.HealthController.IsAlive;
            bool ridden = Riding.Horse == this;
            if (!alive && ridden) { Riding.Release(); ridden = false; }
            if (alive) Keys(player);
            if (body == null)
            {
                if (alive && Plugin.SpawnAtRaidStart.Value && Time.time >= nextTry) AutoSpawn(player);
                Network(false, player);
                return;
            }
            // Keys may have got you on or off.
            ridden = Riding.Horse == this;
            if (ridden) Ride(dt);
            else Roam(player, dt);
            Animate(dt);
            GroundAirClips(dt);
            body.transform.SetPositionAndRotation(pos, Quaternion.Euler(pitch, yaw, 0));
            if (ridden) Riding.Tick(dt, model);
            Network(true, player);
            if (Plugin.ShowDebug.Value != (debug != null)) ToggleDebug();
        }

        private void Network(bool spawned, Player player)
        {
            bool online = HorseNet.Online;
            if (!online || (Time.time < nextNet && spawned == netSpawned)) return;
            nextNet = Time.time + (spawned ? .05f : 1f);
            netSpawned = spawned;
            var state = new HorseState
            {
                Spawned = spawned,
                Position = spawned ? pos : Vector3.zero,
                Yaw = yaw,
                Pitch = pitch,
                Scale = Scale,
                Coat = (byte)Plugin.HorseCoat.Value,
                Clip = spawned ? model.Current : null,
                Layer = spawned ? model.Layer : null,
                LayerWeight = spawned ? model.LayerWeight : 0,
                Progress = spawned ? model.Progress : 0,
                Playback = spawned ? model.Playback : 0,
                Looping = spawned && model.Looping,
                RootY = spawned ? model.Root.localPosition.y : 0
            };
            HorseNet.Send(state, player, !spawned);
        }

        // ---- Controls -----------------------------------------------------------------------------------------------

        private void Keys(Player player)
        {
            if (Cursor.lockState != CursorLockMode.Locked || player.IsInventoryOpened) return;
            // BepInEx shortcuts only fire with exactly their own modifiers held, so H and Shift+H don't overlap.
            if (Plugin.CallKey.Value.IsDown()) { Call(player); return; }
            if (!Plugin.MountKey.Value.IsDown()) return;
            if (Riding.Horse == this) { Riding.Dismount(); return; }
            TryMount(player);
        }

        internal bool TryMount(Player player)
        {
            if (Riding.Active) return false;
            if (body == null) { Say($"{Plugin.HorseName.Value} isn't here. {Plugin.CallKey.Value} to call."); return false; }
            float d = Flat(player.Position - pos);
            if (d > Plugin.MountRange.Value * Scale + 1.2f) { Say($"{Plugin.HorseName.Value} is {d:0} m away. {Plugin.CallKey.Value} to call."); return false; }
            if (airborne) return false;
            if (player.IsInPronePose) { Say("Stand up to mount."); return false; }
            if (!player.MovementContext.IsGrounded) return false;
            mode = Mode.Idle; speed = 0; yawRate = 0;
            // Climbing on from whichever side you're standing.
            Vector3 local = Quaternion.Inverse(Quaternion.Euler(0, yaw, 0)) * (player.Position - pos);
            Riding.Mount(player, this, local.x <= 0);
            return true;
        }

        private void Call(Player player)
        {
            if (Riding.Horse == this) return;
            if (body == null)
            {
                Say(TrySpawn(player, 6f) ? Plugin.HorseName.Value + " is here." : "No room for " + Plugin.HorseName.Value + " here.");
                return;
            }
            float d = Flat(player.Position - pos);
            // Far off: it turns up nearer, out of the way behind you, and comes from there.
            if (d > Plugin.CallTeleportDistance.Value && AppearNear(player, 14f)) d = Flat(player.Position - pos);
            mode = Mode.Coming; nextPath = 0; progressPoint = pos; lastProgress = Time.time;
            Say($"{Plugin.HorseName.Value} is coming ({d:0} m).");
        }

        private static void Say(string text)
        {
            try { NotificationManager.DisplayMessageNotification(text); } catch { Plugin.Log.LogInfo(text); }
        }

        private static Vector3 Forward(Player player)
        {
            Vector3 f = player.Transform.forward; f.y = 0;
            return f.sqrMagnitude > .001f ? f.normalized : Vector3.forward;
        }

        private static float Flat(Vector3 v) => Mathf.Sqrt(v.x * v.x + v.z * v.z);
        private static float Bearing(Vector3 from, Vector3 to) => Mathf.Atan2(to.x - from.x, to.z - from.z) * Mathf.Rad2Deg;

        private static string LayerList(int m)
        {
            var names = new System.Collections.Generic.List<string>();
            for (int i = 0; i < 32; i++) if ((m & (1 << i)) != 0) names.Add(LayerMask.LayerToName(i));
            return string.Join(", ", names);
        }

        // ---- Spawning -----------------------------------------------------------------------------------------------

        private void AutoSpawn(Player player)
        {
            if (Time.time > deadline)
            {
                Plugin.Log.LogWarning($"No room for the horse near you; {Plugin.CallKey.Value} tries again.");
                nextTry = float.MaxValue;
                return;
            }
            nextTry = Time.time + 1;
            TrySpawn(player, 3.5f);
        }

        private bool TrySpawn(Player player, float distance)
        {
            // Beside you (right, then left), else behind or in front: never on top of you.
            Vector3 at = player.Position, f = Forward(player), side = Quaternion.Euler(0, 90, 0) * f;
            if (!FindPoint(at + side * distance, at, out Vector3 point)
                && !FindPoint(at - side * distance, at, out point)
                && !FindPoint(at - f * (distance + 1), at, out point)
                && !FindPoint(at + f * (distance + 1), at, out point)) return false;
            if (Flat(point - at) < 1.8f) return false;
            body = new GameObject("Tushonka Horse");
            body.transform.SetParent(world.transform, true);
            try
            {
                model = new HorseModel(body.transform, Scale, Plugin.HorseCoat.Value);
                model.Require(IdleClip, IdleAltClip, BackClip, JumpClip, FallClip, LandClip, "Mount_Left_Horse", "Mount_Right_Horse", "Dismount_Left_Horse_4");
                foreach (GaitClips g in Gaits) model.Require(g.Clip, g.Left, g.Right);
                spineA = model.Bone("Spine_3"); spineB = model.Bone("Spine_4"); reinHold = model.Bone("Reins_Hold");
                if (spineA == null || spineB == null) throw new InvalidOperationException("The horse has no spine bones for the saddle.");
            }
            catch { Destroy(body); body = null; model = null; throw; }
            // Facing the way you are, so it's side-on and ready to get on.
            Place(point, point + f);
            Plugin.Log.LogInfo($"Horse spawned at {pos} ({HorseAssets.ShaderReport.Trim()}).");
            return true;
        }

        private void Place(Vector3 point, Vector3 lookAt)
        {
            pos = point;
            Vector3 look = lookAt - point;
            if (look.x * look.x + look.z * look.z > .01f) yaw = Bearing(point, lookAt);
            speed = 0; yawRate = 0; vy = 0; pitch = 0; airborne = false; airTime = 0; cornerCount = 0; settled = false;
            jumping = fell = landing = false; airClips = 0;
            if (body != null) body.transform.SetPositionAndRotation(pos, Quaternion.Euler(0, yaw, 0));
        }

        // It turns up near you (behind, by 'back' metres, if there's room there).
        private bool AppearNear(Player player, float back)
        {
            Vector3 at = player.Position;
            if (!FindPoint(at - Forward(player) * back, at, out Vector3 near) && !FindPoint(at, at, out near)) return false;
            Place(near, at);
            return true;
        }

        // Open, level ground near 'around' that the horse's body fits on, in sight of 'from'.
        private bool FindPoint(Vector3 around, Vector3 from, out Vector3 point)
        {
            // 'around' itself first, then a ring round it.
            for (int i = 0; i < 9; i++)
            {
                float angle = i * 45 * Mathf.Deg2Rad;
                Vector3 p = i == 0 ? around : around + new Vector3(Mathf.Sin(angle), 0, Mathf.Cos(angle)) * 2.5f;
                if (NavMesh.SamplePosition(p, out NavMeshHit nav, 3f, NavMesh.AllAreas)) p = nav.position;
                if (!Physics.Raycast(p + Vector3.up * 2f, Vector3.down, out RaycastHit hit, 5f, mask, QueryTriggerInteraction.Ignore) || hit.normal.y < .8f) continue;
                p = hit.point;
                if (Mathf.Abs(p.y - from.y) > 4f) continue;
                // Side-on to 'from' if it fits that way, else end-on.
                float facing = Flat(p - from) > .1f ? Bearing(from, p) + 90 : 0;
                if (!Fits(p, facing, 0) && !Fits(p, facing + 90, 0)) continue;
                if (Physics.Linecast(from + Vector3.up * 1.4f, p + Vector3.up * 1.4f, mask, QueryTriggerInteraction.Ignore)) continue;
                point = p;
                return true;
            }
            point = Vector3.zero;
            return false;
        }

        // ---- Ridden -------------------------------------------------------------------------------------------------

        private void Ride(float dt)
        {
            Player rider = Riding.Rider;
            if (rider == null) return;
            Vector2 keys = Riding.Steering;
            bool forward = keys.y > .1f, seated = Riding.Seated;
            // A sprint left switched on lapses once you've stood for a moment (as on foot, where stopping ends it),
            // but holding sprint and then setting off still gallops.
            if (forward) lastForward = Time.time;
            else if (Riding.Sprint && Mathf.Abs(speed) < .5f && Time.time - Mathf.Max(lastForward, Riding.SprintSince) > 2f) Riding.Sprint = false;
            float target = 0, steer = 0;
            if (seated)
            {
                if (forward)
                {
                    bool walk = rider.MovementContext.CharacterMovementSpeed < .55f;
                    target = Riding.Sprint ? Plugin.GallopSpeed.Value : walk ? Plugin.WalkSpeed.Value : Plugin.TrotSpeed.Value;
                }
                else if (keys.y < -.1f) target = -Plugin.BackSpeed.Value;
                steer = keys.x;
                if (Plugin.SteerWithCamera.Value && forward && Mathf.Abs(keys.x) < .1f)
                    steer = Mathf.Clamp(Mathf.DeltaAngle(yaw, rider.MovementContext.Yaw) / 35f, -1, 1);
            }
            Drive(target, steer, dt);
            if (Riding.JumpQueued) { Riding.JumpQueued = false; if (seated) TryJump(); }
        }

        /// <summary>Somewhere to get off: beside the horse on whichever side has room, else behind or in front.</summary>
        internal Vector3 DismountSpot()
        {
            Vector3 f = Quaternion.Euler(0, yaw, 0) * Vector3.forward, r = Quaternion.Euler(0, yaw + 90, 0) * Vector3.forward;
            if (StandSpot(pos - r * (1.15f * Scale), out Vector3 g) || StandSpot(pos + r * (1.15f * Scale), out g)
                || StandSpot(pos - f * (1.9f * Scale), out g) || StandSpot(pos + f * (2.1f * Scale), out g)
                || StandSpot(pos + (f * .5f - r * 1.6f) * Scale, out g) || StandSpot(pos + (f * .5f + r * 1.6f) * Scale, out g)) return g;
            return pos + Vector3.up * .02f;
        }

        /// <summary>Ground near 'near' with room to stand on it, reachable from the saddle.</summary>
        internal bool StandSpot(Vector3 near, out Vector3 ground)
        {
            ground = default;
            if (!Ground(new Vector3(near.x, pos.y, near.z), 1.6f, 2.5f, out RaycastHit hit) || hit.normal.y < .6f) return false;
            Vector3 g = hit.point;
            if (Physics.CheckCapsule(g + Vector3.up * .45f, g + Vector3.up * 1.55f, .3f, mask, QueryTriggerInteraction.Ignore)) return false;
            if (Physics.Linecast(Seat + Vector3.up * .5f, g + Vector3.up * 1.2f, mask, QueryTriggerInteraction.Ignore)) return false;
            ground = g + Vector3.up * .02f;
            return true;
        }

        // ---- On its own ---------------------------------------------------------------------------------------------

        private void Roam(Player player, float dt)
        {
            Vector3 target = player.Position;
            float d = Flat(target - pos);
            if (mode == Mode.Coming && d < 3.2f * Scale) { mode = Mode.Idle; cornerCount = 0; }
            if (mode == Mode.Coming) { Come(player, target, d, dt); return; }
            Drive(0, 0, dt);
            // Idle: it keeps an eye on you when you're close.
            if (d < 10) model.LookAt(Mathf.DeltaAngle(yaw, Bearing(pos, target)));
        }

        private void Come(Player player, Vector3 target, float d, float dt)
        {
            if (Time.time >= nextPath) FindPath(target);
            while (corner < cornerCount && Flat(corners[corner] - pos) < 1.6f * Scale) corner++;
            Vector3 aim = corner < cornerCount ? corners[corner] : target;
            float off = Mathf.DeltaAngle(yaw, Bearing(pos, aim));
            float want = d > 25 ? Plugin.GallopSpeed.Value : d > 9 ? Plugin.TrotSpeed.Value : Plugin.WalkSpeed.Value;
            // Slowly round sharp corners and for the last few steps.
            if (Mathf.Abs(off) > 55 || d < 6) want = Mathf.Min(want, Plugin.WalkSpeed.Value);
            Drive(want, Mathf.Clamp(off / 30f, -1, 1), dt);
            // Not getting anywhere for a while: it turns up near you instead.
            if (Flat(pos - progressPoint) > 2) { progressPoint = pos; lastProgress = Time.time; }
            else if (Time.time - lastProgress > 6)
            {
                AppearNear(player, 6f);
                progressPoint = pos; lastProgress = Time.time;
            }
        }

        private void FindPath(Vector3 target)
        {
            nextPath = Time.time + 1f;
            cornerCount = 0; corner = 0;
            if (NavMesh.SamplePosition(pos, out NavMeshHit a, 2f, NavMesh.AllAreas)
                && NavMesh.SamplePosition(target, out NavMeshHit b, 3f, NavMesh.AllAreas)
                && NavMesh.CalculatePath(a.position, b.position, NavMesh.AllAreas, path)
                && path.status != NavMeshPathStatus.PathInvalid)
            {
                int n = path.GetCornersNonAlloc(corners);
                if (n > 1) { cornerCount = n; corner = 1; }
            }
        }

        // ---- Debug and cleanup --------------------------------------------------------------------------------------

        private void ToggleDebug()
        {
            if (debug != null) { Destroy(debug); debug = null; }
            else { debug = gameObject.AddComponent<HorseDebug>(); debug.Horse = this; }
        }

        internal string DebugLine()
            => body == null ? "Horse: not spawned"
             : $"Horse {gait} {speed:0.0} m/s yaw {yaw:0} turn {yawRate:0} pitch {pitch:0} air {airborne} mode {mode} clip {model.Current} ridden {Riding.Active} sprint {Riding.Sprint} keys {Riding.Steering}";

        private void Cleanup()
        {
            if (netSpawned) HorseNet.Gone(world != null ? world.MainPlayer : null);
            netSpawned = false;
            if (Riding.Horse == this) Riding.Release();
            if (debug != null) Destroy(debug);
            if (body != null) Destroy(body);
            model = null; body = null; debug = null;
        }

        private void OnDestroy() => Cleanup();
    }

    /// <summary>The debug line on screen (5. Debug > Show debug line). Only exists while that's on.</summary>
    internal sealed class HorseDebug : MonoBehaviour
    {
        internal Horse Horse;
        private void Awake() { useGUILayout = false; }
        private void OnGUI()
        {
            if (Horse != null && Event.current.type == EventType.Repaint) GUI.Label(new Rect(20, 200, 1200, 22), Horse.DebugLine());
        }
    }
}
