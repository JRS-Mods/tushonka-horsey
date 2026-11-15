using UnityEngine;

namespace TushonkaHorse
{
    /// <summary>
    /// What the horse plays: its half of getting on and off, the jump, falling and landing, standing (two idles, and
    /// turning on the spot), backing up, and the three gaits, each with its turning clip mixed in by how hard it's
    /// turning and played at the pace the horse is really going.
    /// </summary>
    internal sealed partial class Horse
    {
        private enum Gait { Idle, Walk, Trot, Run, Back }

        private readonly struct GaitClips
        {
            public readonly string Clip, Left, Right;
            // The speed the clip was animated at (m/s, measured from a planted hoof: tools/analyze_blender.py), and the
            // turn rate (deg/s) that's the full turning clip.
            public readonly float Authored, FullTurn;
            public GaitClips(string clip, string left, string right, float authored, float fullTurn)
            { Clip = clip; Left = left; Right = right; Authored = authored; FullTurn = fullTurn; }
        }

        // In the order of the Gait enum from Walk.
        private static readonly GaitClips[] Gaits =
        {
            new GaitClips("Walk_Horse_4", "Walk_Horse_4_Left", "Walk_Horse_4_Right", 1.1f, 70),
            new GaitClips("Trot_Horse_4", "Trot_Horse_4_Left", "Trot_Horse_4_Right", 1.69f, 65),
            new GaitClips("Run_Horse_4", "Run_Horse_4_Left", "Run_Horse_4_Right", 5.9f, 50),
        };
        private const string IdleClip = "Idle_Horse_4", IdleAltClip = "Idle_Horse_4_Alt", BackClip = "Walk_Backwards_Horse_4";
        private const string JumpClip = "Jump_Horse_4", FallClip = "Jump_Horse_4_Loop", LandClip = "Jump_Horse_4_End";
        private const float BackAuthored = 1.05f;

        // How far it's bent into a turn (-1 left .. 1 right). It follows the turning, but takes its time: the neck and
        // body swing over about half a second, not with every touch of the keys. (The kit's "hard" turning clips throw
        // the head most of a metre sideways, which read as the head whipping about; these are its gentle ones.)
        private float lean;
        private const float LeanTime = .45f;

        private Gait gait;
        private bool jumping, fell, landing;
        private string idleClip = IdleClip;
        private float nextIdleSwap;

        // The jump, fall and landing clips lift the body by themselves (up to 0.36 m). In the air the horse's height is
        // its motion's, so the clip's own is taken back out (eased in and out), and what's seen is what collides.
        private float airClips;
        private void GroundAirClips(float dt)
        {
            airClips = Mathf.MoveTowards(airClips, jumping || fell || landing ? 1 : 0, dt / .15f);
            float down = airClips > 0 ? -model.RootLift * Scale * airClips : 0;
            Transform root = model.Root;
            if (root.localPosition.y != down) root.localPosition = new Vector3(0, down, 0);
        }

        private void Animate(float dt)
        {
            // Being got on or off: the horse's half of the kit's mount/dismount, in time with the rider's.
            string climbed = Ridden ? Riding.HorseClip : null;
            if (climbed != null)
            {
                if (model.Current != climbed) { model.NextBlend(.3f); model.Restart(); }
                model.Tick(climbed, model.Length(climbed) / Riding.PhaseLength, dt, false);
                gait = Gait.Idle;
                return;
            }
            if (jumping)
            {
                // One jump clip over the time in the air (takeoff and landing included).
                float air = 2 * Mathf.Sqrt(2 * Plugin.JumpHeight.Value * Scale / Gravity);
                bool done = model.Tick(JumpClip, Mathf.Clamp(model.Length(JumpClip) / (air + .35f), .8f, 2.2f), dt, false);
                if (!airborne && (done || model.Progress > .6f)) { jumping = false; model.NextBlend(.2f); }
                // Still falling after the jump's over (off a ledge): on into the fall.
                else if (done) { jumping = false; fell = true; }
                else return;
            }
            // Off a ledge: the kit's in-the-air loop, then its landing.
            if (airborne && (fell || airTime > .2f))
            {
                if (!fell) { fell = true; model.NextBlend(.2f); }
                model.Tick(FallClip, 1, dt);
                return;
            }
            if (fell && !airborne) { fell = false; landing = true; model.NextBlend(.1f); model.Restart(); }
            if (landing)
            {
                if (!model.Tick(LandClip, 1.2f, dt, false)) return;
                landing = false; model.NextBlend(.2f);
            }

            float v = speed / Scale, turn = yawRate;
            if (Mathf.Abs(v) < .12f)
            {
                gait = Gait.Idle;
                // Turning on the spot: stepping round.
                if (Mathf.Abs(turn) > 10)
                {
                    Lean(turn / Gaits[0].FullTurn, dt);
                    model.TickGait(Gaits[0].Clip, lean > 0 ? Gaits[0].Right : Gaits[0].Left, Mathf.Abs(lean), Mathf.Clamp(Mathf.Abs(turn) / 55f, .5f, 1.3f), dt);
                    return;
                }
                Lean(0, dt);
                if (Time.time > nextIdleSwap)
                {
                    nextIdleSwap = Time.time + Random.Range(14f, 26f);
                    idleClip = Random.value < .35f ? IdleAltClip : IdleClip;
                }
                if (model.Current != idleClip) model.NextBlend(.45f);
                model.Tick(idleClip, 1, dt);
                return;
            }
            if (v < 0)
            {
                Lean(0, dt);
                gait = Gait.Back;
                model.TickGait(BackClip, null, 0, Mathf.Clamp(-v / BackAuthored, .5f, 1.8f), dt);
                return;
            }

            // The gait, with a little give between them so it doesn't flicker at a boundary.
            float walkTop = (Plugin.WalkSpeed.Value + Plugin.TrotSpeed.Value) * .5f, trotTop = (Plugin.TrotSpeed.Value + Plugin.GallopSpeed.Value) * .45f;
            gait = v < walkTop + (gait == Gait.Trot || gait == Gait.Run ? -.2f : .2f) ? Gait.Walk
                 : v < trotTop + (gait == Gait.Run ? -.5f : .5f) ? Gait.Trot : Gait.Run;
            GaitClips g = Gaits[(int)gait - 1];
            Lean(turn / g.FullTurn, dt);
            float bend = Mathf.Abs(lean);
            model.TickGait(g.Clip, bend > .02f ? (lean > 0 ? g.Right : g.Left) : null, bend, Mathf.Clamp(v / g.Authored, .5f, 2.2f), dt);
        }

        private void Lean(float toward, float dt)
        {
            lean += (Mathf.Clamp(toward, -1, 1) - lean) * (1 - Mathf.Exp(-dt / (LeanTime / 3)));
        }
    }
}
