using System;
using EFT;
using UnityEngine;

namespace TushonkaHorse
{
    public enum LegStyle { Animated, Hidden }

    /// <summary>
    /// You, on the horse. While mounted the horse is what moves: your movement keys, sprint and jump are taken from your
    /// character and given to the horse (Riding.Patches), and each frame your character is put where the rider animation
    /// has it, at the point where the game would have moved it. You count as on the ground the whole time.
    ///
    /// Getting on: you put your weapon away and step to the horse's side, then climb with the rider animation
    /// (its dismount, backwards) on your own body: hips, chest, arms and legs follow it, faded in from
    /// how you stood, and your view goes where the rider's head goes and turns the way the rider faces. In the saddle
    /// your weapon comes back out. Getting off is the same the other way round.
    ///
    /// In the saddle your pelvis is kept on the animated rider's and your legs sit astride with your feet in the
    /// stirrups, moving with each gait, while your upper body is yours: look, aim, shoot, lean, heal as usual. Your
    /// left hand keeps the reins while your right holds your gun (both hands on it to aim and reload); with nothing in
    /// your hands, both hold the reins.
    /// </summary>
    internal static partial class Riding
    {
        internal static Horse Horse { get; private set; }
        internal static Player Rider { get; private set; }
        internal static RiderAnimation Animation;
        internal static bool Sprint;
        internal static bool JumpQueued;
        private static MovementContext context;
        // A plain flag for the patches, which run for every character every frame.
        private static bool active;

        private enum Phase { Approach, Mounting, Riding, Dismounting }
        private static Phase phase;
        private static float phaseStart, phaseLength;
        // The rider clip being climbed through (null: in the saddle, or a quick hop on or off).
        private static string riderClip;
        // The horse's half of the climb, started with yours once you've walked up.
        private static string horseClipNext;
        private static Vector3 from, dismountTo;
        // Where you were put last frame, and what's left of the gap to where a new phase wants you (eased away).
        private static Vector3 last, carry;
        private static float carryStart, placedAt;
        private static bool carryPending;

        // Your weapon: put away by the mod (so it's the mod that gets it back out), and how long that's waited for.
        private static bool stowed, dismountWanted;
        private static float handsBy;

        private static RiderRig rig;
        // The animation's joints this frame: as sampled, eased, and in the world; and a spare for looking ahead.
        private static Vector3[] joints, smooth, world, scratch;
        private static bool haveJoints;
        // How much of the animation is on your body, arms and legs right now.
        private static float bodyWeight, armWeight, reinWeight, legWeight;

        // Your feet below the saddle when there's no animation to go by.
        private const float SeatDrop = .78f;
        private const float QuickTime = .5f, CatchTime = .35f, JointEase = .08f, BlendTime = .3f;
        private const float ReinTime = .22f;
        private const float HandsTimeout = 2.5f, ApproachSpeed = 2.4f, ApproachTimeout = 3.5f;
        private const string StandingClip = "DismountLeft_Rider";

        internal static bool Active => active;
        // For the self test.
        internal static string State => !active ? "on foot" : $"{phase} {PhaseProgress:0.00} clip {riderClip ?? "-"} body {bodyWeight:0.0} arms {armWeight:0.0} rein {reinWeight:0.0} legs {legWeight:0.0}{(stowed ? " stowed" : "")}{(dismountWanted ? " getting off" : "")}";
        internal static string Report => rig?.Report ?? "";
        /// <summary>In the saddle with the reins: the horse goes where you steer it.</summary>
        internal static bool Seated => active && phase == Phase.Riding && !dismountWanted;

        /// <summary>The horse's own clip while you get on or off (null otherwise), and how long that takes.</summary>
        internal static string HorseClip { get; private set; }
        internal static float PhaseLength => Mathf.Max(.1f, phaseLength);

        private static float PhaseProgress => Mathf.Clamp01((Time.time - phaseStart) / PhaseLength);
        private static bool Climbing => riderClip != null && (phase == Phase.Mounting || phase == Phase.Dismounting);
        private static bool LegsWanted => Plugin.RiderLegs.Value == LegStyle.Animated && Animation != null && rig != null && rig.HasLegs;
        private static bool HandsFree => !Plugin.StowWeapon.Value || Rider.HandsIsEmpty || Time.time > handsBy;

        // ---- Getting on and off -----------------------------------------------------------------------------------

        internal static void Mount(Player player, Horse horse, bool leftSide)
        {
            if (active) return;
            player.EnableSprint(false);
            if (player.MovementContext.PoseLevel < .99f) player.MovementContext.SetPoseLevel(1f, true);
            Horse = horse; Rider = player; context = player.MovementContext; active = true;
            input = Vector2.zero; Sprint = false; JumpQueued = false; haveJoints = false;
            stowed = false; dismountWanted = false; bodyWeight = armWeight = reinWeight = legWeight = 0; approach = Vector2.zero;
            from = last = context.TransformPosition; placedAt = Time.time;
            rig = Rig(player);
            string clip = leftSide ? "DismountLeft_Rider" : "Dismount_Right_Rider";
            bool animated = Plugin.AnimatedMount.Value && rig != null && rig.Head != null && Animation.Has(clip);
            horseClipNext = animated ? (leftSide ? "Mount_Left_Horse" : "Mount_Right_Horse") : null;
            Stow();
            Begin(Phase.Approach, animated ? clip : null, null);
            Plugin.Log.LogInfo($"Mounting {Plugin.HorseName.Value} from the {(leftSide ? "left" : "right")}{(animated ? " (animated)" : "")}{(stowed ? ", weapon put away" : "")}.");
        }

        // Your character's limbs, found once and kept while it's the same character.
        private static RiderRig Rig(Player player)
        {
            if (Animation == null) return null;
            if (rig != null && rig.Owner == player && rig.Valid) return rig;
            try { return RiderRig.Bind(player, Animation, StandingClip); }
            catch (Exception e) { Plugin.Log.LogWarning("Rider rig: " + e.Message); return null; }
        }

        private static void Begin(Phase next, string clip, string horseClip)
        {
            phase = next; phaseStart = Time.time; carryPending = next != Phase.Approach;
            riderClip = clip; HorseClip = horseClip;
            phaseLength = clip != null ? Animation.Length(clip) / Mathf.Max(.3f, Plugin.MountAnimSpeed.Value) : QuickTime;
            if (next == Phase.Approach) return;
            RiderView.Legs(Rider, Plugin.RiderLegs.Value == LegStyle.Animated);
            if (next == Phase.Riding && Plugin.DrawInSaddle.Value) Draw(Rider);
        }

        // Puts away whatever's in your hands; the climb waits for it (not for ever).
        private static void Stow()
        {
            handsBy = Time.time + HandsTimeout;
            if (!Plugin.StowWeapon.Value || Rider.HandsIsEmpty) return;
            try
            {
                Rider.TrySaveLastItemInHands();
                Rider.SetEmptyHands(null);
                stowed = true;
            }
            catch (Exception e) { Plugin.Log.LogWarning("Putting your weapon away: " + e.Message); }
        }

        // Gets back out what Stow put away.
        private static void Draw(Player player)
        {
            if (!stowed) return;
            stowed = false;
            try { if (player != null && player.HandsIsEmpty) player.TrySetLastEquippedWeapon(false); }
            catch (Exception e) { Plugin.Log.LogWarning("Getting your weapon back out: " + e.Message); }
        }

        /// <summary>Gets off: the horse pulls up and your weapon goes away first, then you climb down.</summary>
        internal static void Dismount()
        {
            if (!Seated) return;
            Sprint = false;
            dismountWanted = true;
            Stow();
        }

        // Animated on whichever side has room for you to land, else a quick step down.
        private static void StartDismount()
        {
            dismountWanted = false;
            from = context.TransformPosition;
            if (Plugin.AnimatedMount.Value && rig != null && rig.Head != null && haveJoints)
            {
                // The side you're looking toward first (left when looking ahead), then the other.
                bool right = Mathf.DeltaAngle(Horse.Yaw, Rider.MovementContext.Yaw) > 25;
                if (AnimatedDismount(right ? "Dismount_Right_Rider" : "DismountLeft_Rider")) return;
                if (AnimatedDismount(right ? "DismountLeft_Rider" : "Dismount_Right_Rider")) return;
            }
            dismountTo = Horse.DismountSpot();
            Begin(Phase.Dismounting, null, null);
        }

        private static bool AnimatedDismount(string clip)
        {
            if (!Animation.Has(clip)) return false;
            // Where the animation ends: the rider's feet on the ground beside the horse.
            Animation.Sample(clip, 1, null, 0, scratch);
            Vector3 feet = (Horse.ToWorld(scratch[Animation.LeftFoot]) + Horse.ToWorld(scratch[Animation.RightFoot])) * .5f;
            if (!Horse.StandSpot(feet, out dismountTo)) return false;
            Begin(Phase.Dismounting, clip, "Dismount_Left_Horse_4");
            return true;
        }

        /// <summary>Lets go at once (raid over, rider died, something failed): no teleport.</summary>
        internal static void Release()
        {
            RiderView.Restore(Rider);
            rig?.Restore();
            rig?.FollowHands(null);
            active = false;
            Horse = null; Rider = null; context = null;
            Sprint = false; JumpQueued = false; HorseClip = null; horseClipNext = null; riderClip = null; haveJoints = false;
            stowed = false; dismountWanted = false; bodyWeight = armWeight = reinWeight = legWeight = 0;
        }

        private static void Finish()
        {
            Player rider = Rider;
            Vector3 spot = dismountTo;
            bool draw = stowed;
            Release();
            try { rider.Teleport(spot); } catch (Exception e) { Plugin.Log.LogWarning("Dismount teleport: " + e.Message); }
            stowed = draw;
            Draw(rider);
            Plugin.Log.LogInfo("Dismounted.");
        }

        // ---- Each frame ----------------------------------------------------------------------------------------------

        /// <summary>The rider animation's joints for this frame (called by the horse once it has animated and moved).</summary>
        internal static void Tick(float dt, HorseModel model)
        {
            if (!active) return;
            // Off once the horse has pulled up and your hands are empty.
            if (dismountWanted && phase == Phase.Riding && ((HandsFree && Mathf.Abs(Horse.Speed) < 1.5f) || Time.time > handsBy)) StartDismount();
            if (!active || Animation == null) return;
            int n = Animation.JointCount;
            if (joints == null || joints.Length != n) { joints = new Vector3[n]; smooth = new Vector3[n]; world = new Vector3[n]; scratch = new Vector3[n]; }
            float ease = 1;
            if (riderClip != null)
            {
                // Walking up: the first moment of the climb, standing beside the horse.
                float p = phase == Phase.Approach ? 0 : PhaseProgress;
                Animation.Sample(riderClip, phase == Phase.Dismounting ? p : 1 - p, null, 0, joints);
            }
            else
            {
                // In the saddle: the rider clip that goes with whatever the horse is playing, at the same point of it,
                // eased a little as the horse's own pose is while it changes clips.
                string layer = model.Layer != null ? Animation.ForHorse(model.Layer) : null;
                Animation.Sample(Animation.ForHorse(model.Current), model.Progress, layer, model.LayerWeight, joints);
                if (haveJoints) ease = 1 - Mathf.Exp(-dt / JointEase);
            }
            Transform root = model.Root;
            for (int i = 0; i < n; i++)
            {
                smooth[i] = ease >= 1 ? joints[i] : Vector3.LerpUnclamped(smooth[i], joints[i], ease);
                world[i] = root.TransformPoint(smooth[i]);
            }
            haveJoints = true;
            Weights(dt);
            if (riderClip != null && Plugin.GuideView.Value) TurnView(dt);
        }

        // How much of the animation is on you: all of it while climbing (faded in from, or out to, standing on the
        // ground), legs in the saddle, and arms there too while your hands are empty (on the reins).
        private static void Weights(float dt)
        {
            float climb = !Climbing ? 0
                        : phase == Phase.Mounting ? PoseMath.Smooth((Time.time - phaseStart) / BlendTime)
                        : PoseMath.Smooth((phaseStart + phaseLength - Time.time) / BlendTime);
            bool riding = phase == Phase.Riding;
            float step = dt / BlendTime;
            bodyWeight = Mathf.MoveTowards(bodyWeight, Climbing ? climb : 0, step);
            armWeight = Mathf.MoveTowards(armWeight, Climbing ? climb : riding && Plugin.ReinHands.Value && Rider.HandsIsEmpty ? 1 : 0, step);
            reinWeight = Mathf.MoveTowards(reinWeight, riding && ReinHandFree() ? 1 : 0, dt / ReinTime);
            legWeight = Mathf.MoveTowards(legWeight, !LegsWanted ? 0 : Climbing ? climb : riding ? 1 : 0, step);
        }

        // Riding with a gun out, your left hand stays on the reins and the gun is held in your right. It comes back to
        // the gun to aim down the sights and to reload, and whatever else you take out (meds, grenades, a knife) has
        // both hands.
        private static bool ReinHandFree()
        {
            if (!Plugin.OneHanded.Value || Rider.HandsIsEmpty) return false;
            return Rider.HandsController is Player.FirearmController gun && !gun.IsAiming && !gun.IsInReloadOperation();
        }

        // Your view goes the way the animated rider faces, and down to the saddle while your hands are on it, level
        // again by the time you're up (or down). It pulls gently: you can still look about.
        private static void TurnView(float dt)
        {
            int l = Animation.Joint("upperarm_l"), r = Animation.Joint("upperarm_r");
            if (l < 0 || r < 0) return;
            Vector3 facing = Vector3.Cross(world[r] - world[l], Vector3.up);
            if (facing.sqrMagnitude < 1e-6f) return;
            Vector3 saddle = Horse.Seat - world[Animation.Head];
            float down = Mathf.Clamp(Mathf.Atan2(-saddle.y, Mathf.Max(.35f, Mathf.Sqrt(saddle.x * saddle.x + saddle.z * saddle.z))) * Mathf.Rad2Deg, 0, 40);
            float p = PhaseProgress;
            float look = phase == Phase.Approach ? .5f
                       : phase == Phase.Mounting ? 1 - PoseMath.Smooth((p - .55f) / .4f)
                       : PoseMath.Smooth(p / .25f) * (1 - PoseMath.Smooth((p - .7f) / .3f));
            Vector2 view = Rider.Rotation;
            float pull = 1 - Mathf.Exp(-dt * 4f);
            view.x += Mathf.DeltaAngle(view.x, Mathf.Atan2(facing.x, facing.z) * Mathf.Rad2Deg) * pull;
            view.y += Mathf.DeltaAngle(view.y, down * look) * pull;
            Rider.Rotation = view;
        }

        // Where one of your bones is from your feet, as the game has you posed right now (without its step smoothing).
        private static Vector3 Offset(Transform bone)
        {
            Vector3 o = bone.position - context.TransformPosition;
            o.y -= rig.Lift;
            return o;
        }

        // Where your character goes this frame.
        private static Vector3 Placement()
        {
            float dt = Mathf.Clamp(Time.time - placedAt, 0, .1f);
            placedAt = Time.time;
            bool posed = haveJoints && rig != null && rig.Valid;
            if (phase == Phase.Approach)
            {
                // To where the climb starts, at a walk, while your weapon goes away.
                Vector3 start = riderClip != null && posed ? world[Animation.Head] - Offset(rig.Head) : last;
                last = Vector3.MoveTowards(last, start, ApproachSpeed * dt);
                bool there = (last - start).sqrMagnitude < .0025f && (riderClip == null || posed);
                approach = there ? Vector2.zero : Heading(start - last);
                if ((there && HandsFree) || Time.time - phaseStart > ApproachTimeout)
                {
                    approach = Vector2.zero; from = last;
                    Begin(Phase.Mounting, riderClip, horseClipNext);
                }
                else return last;
            }
            if (phase == Phase.Mounting && PhaseProgress >= 1) Begin(Phase.Riding, null, null);
            float t = PhaseProgress;
            Vector3 want;
            if (phase == Phase.Riding)
            {
                // Your pelvis on the animated rider's.
                want = posed ? world[Animation.Pelvis] - Offset(rig.Pelvis) : Horse.Seat + Vector3.down * SeatDrop;
                want.y += Plugin.SeatOffset.Value;
            }
            else if (riderClip != null && posed)
            {
                // Climbing: your head on the animated rider's; stepping off, the last of it settles onto the ground.
                want = world[Animation.Head] - Offset(rig.Head);
                if (phase == Phase.Dismounting) want = Vector3.LerpUnclamped(want, dismountTo, PoseMath.Smooth((t - .8f) / .2f));
            }
            else if (phase == Phase.Mounting) want = Arc(from, Horse.Seat + Vector3.up * (Plugin.SeatOffset.Value - SeatDrop), t, .35f);
            else want = Arc(from, dismountTo, t, .15f);

            // A new phase starts from wherever you were; the difference is eased away.
            if (carryPending) { carry = last - want; carryStart = Time.time; carryPending = false; }
            float left = 1 - PoseMath.Smooth((Time.time - carryStart) / CatchTime);
            if (left > 0) want += carry * left;
            return last = want;
        }

        // A direction in the world as movement keys (x right, y forward) for the way you're facing: your legs walk it.
        private static Vector2 Heading(Vector3 direction)
        {
            direction.y = 0;
            if (direction.sqrMagnitude < 1e-6f) return Vector2.zero;
            Vector3 local = Quaternion.Euler(0, -Rider.MovementContext.Yaw, 0) * direction.normalized;
            return new Vector2(local.x, local.z);
        }

        private static Vector3 Arc(Vector3 a, Vector3 b, float t, float hop)
        {
            Vector3 p = Vector3.LerpUnclamped(a, b, PoseMath.Smooth(t));
            p.y += Mathf.Sin(t * Mathf.PI) * hop;
            return p;
        }

        // Just before a camera draws, the animation goes onto your body (after the game's own animation and IK, so it
        // wins); once that camera has drawn it comes off again, so the game never works from a pose it didn't make.
        // (Cameras can draw inside one another: the pose stays on until the outermost is done.)
        private static int drawing, drawingFrame;

        private static void PreCull(Camera camera)
        {
            if (!active || !haveJoints || rig == null || camera.cameraType != CameraType.Game) return;
            if (drawingFrame != Time.frameCount) { drawingFrame = Time.frameCount; drawing = 0; }
            if (drawing++ > 0) return;
            if (bodyWeight <= 0 && armWeight <= 0 && reinWeight <= 0 && legWeight <= 0) return;
            try
            {
                if (!rig.Valid) { rig = null; return; }
                // The rein hand goes to the reins themselves; with both hands on them, each goes where the animation's is.
                bool reins = reinWeight > armWeight;
                // Holding a gun, the arms you see are the gun's; empty-handed (and climbing), your body's.
                rig.FollowHands(reinWeight > 0 && !Rider.HandsIsEmpty ? Rider.HandsController?.HandsHierarchy : null);
                rig.Pose(world, bodyWeight, Mathf.Max(armWeight, reinWeight), armWeight, RiderView.LegsShown ? legWeight : 0, riderClip != null, reins ? Horse.ReinHold : null);
            }
            catch (Exception e) { Plugin.Log.LogWarning("Rider pose: " + e.Message); rig = null; }
        }

        private static void PostRender(Camera camera)
        {
            if (drawing == 0 || camera.cameraType != CameraType.Game) return;
            if (--drawing > 0) return;
            try { rig?.Restore(); }
            catch (Exception e) { Plugin.Log.LogWarning("Rider pose: " + e.Message); rig = null; }
        }
    }
}
