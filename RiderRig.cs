using System;
using System.Collections.Generic;
using System.Linq;
using EFT;
using UnityEngine;

namespace TushonkaHorse
{
    /// <summary>
    /// Puts the rider animation onto your own character. The animation is joint positions, so nothing about the two
    /// skeletons has to match: your hips and chest are turned to face the way the animation's do (worked out from its
    /// hip, shoulder and spine joints), then each arm and leg is bent (two-bone IK) so the hand or foot lands where
    /// the animation's does, with the elbow or knee toward where the animation's points.
    ///
    /// Each part (body, arms, legs) has a weight, so the animation fades in over the pose the game gave you and out
    /// again. Your view never moves: the body hangs from your head, wherever the game has it.
    ///
    /// The pose is put on just before a camera draws and taken off again after (Riding.Patches), so the rest of the
    /// game only ever sees its own pose.
    /// </summary>
    internal sealed class RiderRig
    {
        private sealed class Limb
        {
            public Transform Upper, Lower, End, Tip, Index, Pinky;
            // The animation's joints for this limb: where it starts, its elbow/knee, its hand/foot, what that points
            // at, and (hands) the knuckles either side, which give the palm's roll.
            public int Root, Mid, Hand, Point, IndexJoint = -1, PinkyJoint = -1;
            public override string ToString() => $"{Upper?.name} > {Lower?.name} > {End?.name} > {Tip?.name}";
        }

        private readonly Limb[] arms, legs;
        private readonly Transform[] spine;
        private readonly Transform neck;
        private readonly BifacialTransform lifted;
        private readonly int headJoint, pelvisJoint, spineLow, spineHigh, neckJoint, hipL, hipR, shoulderL, shoulderR;
        // How far each skeleton's own "up" line leans when standing upright (so upright means upright on both).
        private readonly float hipTilt, chestTilt, animHipTilt, animChestTilt;

        // What the game had this frame, for the bones the animation moves.
        private readonly Transform[] bodyBones, armBones, legBones;
        private readonly Quaternion[] bodyWas, armsWas, legsWas;
        private Vector3 pelvisWas;
        private bool posed;

        /// <summary>The self test: after each pose, how far your hands and feet ended up from where the animation has them.</summary>
        public static bool Measure;
        public string Report { get; private set; } = "";

        public Player Owner { get; }
        public Transform Head { get; }
        public Transform Pelvis { get; }
        public bool HasArms => arms != null && Head != null;
        public bool HasLegs => legs != null;
        public bool HasBody { get; }
        // Still this character's live skeleton (it's rebuilt if the character or its bones went away).
        public bool Valid => Owner != null && Pelvis != null && Head != null;

        /// <summary>
        /// How far the game has your body shifted up or down from your feet right now to smooth steps (it eases changes
        /// of height). Left out of the seat's measurements, or the smoothing would be cancelled by the seat chasing it.
        /// </summary>
        public float Lift => lifted != null ? lifted.localPosition.y : 0;

        private RiderRig(Player player, RiderAnimation animation, string standingClip)
        {
            Owner = player;
            PlayerBones b = player.PlayerBones;
            Head = b.Head?.Original;
            Pelvis = b.Pelvis?.Original;
            headJoint = animation.Head; pelvisJoint = animation.Pelvis;
            Transform animated = b.AnimatedTransform?.Original;
            if (animated != null && Pelvis != null && Pelvis.IsChildOf(animated)) lifted = b.AnimatedTransform;
            Transform[] all = player.GetComponentsInChildren<Transform>(true).Where(x => x.name.IndexOf("Human", StringComparison.OrdinalIgnoreCase) >= 0).ToArray();
            Transform Find(params string[] endings)
            {
                foreach (string e in endings)
                    foreach (Transform t in all)
                        if (t.name.EndsWith(e, StringComparison.OrdinalIgnoreCase)) return t;
                return null;
            }
            Transform Under(Transform parent, string part)
            {
                if (parent == null) return null;
                foreach (Transform t in parent.GetComponentsInChildren<Transform>(true))
                    if (t != parent && t.name.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0) return t;
                return null;
            }
            Limb Joints(Limb l, string root, string mid, string hand, string point)
            {
                l.Root = animation.Joint(root); l.Mid = animation.Joint(mid); l.Hand = animation.Joint(hand); l.Point = animation.Joint(point);
                return l.Root < 0 || l.Mid < 0 || l.Hand < 0 || l.Point < 0 ? null : l;
            }
            Limb Arm(string s, string r, Transform palm)
            {
                Transform upper = Find("Human" + s + "Upperarm"), lower = Find("Human" + s + "Forearm1");
                if (palm == null) palm = Find("Human" + s + "Palm");
                if (upper == null || lower == null || palm == null) return null;
                // The middle finger's first bone, to point the hand; the index and little fingers' to roll it.
                Transform tip = Find("Human" + s + "Digit31", "Human" + s + "Digit21") ?? (palm.childCount > 0 ? palm.GetChild(0) : null);
                var l = new Limb { Upper = upper, Lower = lower, End = palm, Tip = tip, Index = Find("Human" + s + "Digit21"), Pinky = Find("Human" + s + "Digit51") };
                l.IndexJoint = animation.Joint("index_01_" + r); l.PinkyJoint = animation.Joint("pinky_01_" + r);
                return Joints(l, "upperarm_" + r, "lowerarm_" + r, "hand_" + r, "middle_01_" + r);
            }
            Limb Leg(string s, string r, Transform thigh)
            {
                if (thigh == null) thigh = Find("Human" + s + "Thigh1");
                Transform calf = Under(thigh, "Calf"), foot = Under(thigh, "Foot");
                if (thigh == null || calf == null || foot == null) return null;
                return Joints(new Limb { Upper = thigh, Lower = calf, End = foot, Tip = Under(foot, "Toe") }, "thigh_" + r, "calf_" + r, "foot_" + r, "ball_" + r);
            }
            Limb leftArm = Arm("L", "l", b.LeftPalm), rightArm = Arm("R", "r", b.RightPalm);
            Limb leftLeg = Leg("L", "l", b.LeftThigh1?.Original), rightLeg = Leg("R", "r", b.RightThigh1?.Original);
            if (leftArm != null && rightArm != null) arms = new[] { leftArm, rightArm };
            if (leftLeg != null && rightLeg != null) legs = new[] { leftLeg, rightLeg };

            // The body: hips, then the spine up to the shoulders.
            spine = new[] { b.Spine1 ?? Find("HumanSpine1"), Find("HumanSpine2"), b.Spine3?.Original ?? Find("HumanSpine3") }.Where(x => x != null).Distinct().ToArray();
            neck = b.Neck ?? Find("HumanNeck");
            spineLow = animation.Joint("spine_01"); spineHigh = animation.Joint("spine_03"); neckJoint = animation.Joint("neck_01");
            hipL = animation.Joint("thigh_l"); hipR = animation.Joint("thigh_r");
            shoulderL = animation.Joint("upperarm_l"); shoulderR = animation.Joint("upperarm_r");
            HasBody = arms != null && legs != null && spine.Length > 0 && neck != null && Pelvis != null && Head != null && animation.Has(standingClip)
                      && spineLow >= 0 && spineHigh >= 0 && neckJoint >= 0 && hipL >= 0 && hipR >= 0 && shoulderL >= 0 && shoulderR >= 0;
            if (HasBody)
            {
                // You, standing as you are now, and the animation's rider standing beside the horse.
                hipTilt = Tilt(legs[1].Upper.position - legs[0].Upper.position, spine[0].position - Pelvis.position);
                chestTilt = Tilt(arms[1].Upper.position - arms[0].Upper.position, neck.position - spine[spine.Length - 1].position);
                var stand = new Vector3[animation.JointCount];
                animation.Sample(standingClip, 1, null, 0, stand);
                animHipTilt = Tilt(stand[hipR] - stand[hipL], stand[spineLow] - stand[pelvisJoint]);
                animChestTilt = Tilt(stand[shoulderR] - stand[shoulderL], stand[neckJoint] - stand[spineHigh]);
            }

            var body = new List<Transform>(); var arm = new List<Transform>(); var leg = new List<Transform>();
            if (HasBody) { body.Add(Pelvis); body.AddRange(spine); }
            if (arms != null) foreach (Limb l in arms) { arm.Add(l.Upper); arm.Add(l.Lower); arm.Add(l.End); }
            if (legs != null) foreach (Limb l in legs) { leg.Add(l.Upper); leg.Add(l.Lower); leg.Add(l.End); }
            bodyBones = body.ToArray(); armBones = arm.ToArray(); legBones = leg.ToArray();
            bodyWas = new Quaternion[bodyBones.Length]; armsWas = new Quaternion[armBones.Length]; legsWas = new Quaternion[legBones.Length];

            Plugin.Log.LogInfo($"Rider rig: head {Head?.name}, pelvis {Pelvis?.name}, smoothing {(lifted != null ? "found" : "none")}; " +
                               $"arms {leftArm?.ToString() ?? "missing"} | {rightArm?.ToString() ?? "missing"}; legs {leftLeg?.ToString() ?? "missing"} | {rightLeg?.ToString() ?? "missing"}; " +
                               $"body {(HasBody ? string.Join(" > ", spine.Select(x => x.name)) + " > " + neck.name : "missing")}, " +
                               $"lean hips {hipTilt:0}/{animHipTilt:0} chest {chestTilt:0}/{animChestTilt:0}, roll {(leftArm?.Index != null && leftArm.Pinky != null ? "found" : "none")}");
        }

        // ---- The arms you see -------------------------------------------------------------------------------------

        // With something in your hands, the first-person arms are the ones that came with it (the game's weapon rig),
        // not your body's: those are what the rein hand has to move. Your body's arms are kept to go back to.
        private Transform[] bodyArms;
        private TransformLinks following;

        /// <summary>The arms to pose: those of what's in your hands ('links'), or your body's own (null).</summary>
        public void FollowHands(TransformLinks links)
        {
            if (arms == null || links == following) return;
            if (posed) Restore();
            if (bodyArms == null) bodyArms = new[] { arms[0].Upper, arms[0].Lower, arms[0].End, arms[0].Tip, arms[0].Index, arms[0].Pinky,
                                                    arms[1].Upper, arms[1].Lower, arms[1].End, arms[1].Tip, arms[1].Index, arms[1].Pinky };
            following = links;
            bool found = false;
            if (links != null)
            {
                try
                {
                    found = Use(arms[0], links.GetTransform(ECharacterWeaponBones.HumanLUpperarm), links.GetTransform(ECharacterWeaponBones.HumanLForearm1), links.GetTransform(ECharacterWeaponBones.HumanLPalm))
                          & Use(arms[1], links.GetTransform(ECharacterWeaponBones.HumanRUpperarm), links.GetTransform(ECharacterWeaponBones.HumanRForearm1), links.GetTransform(ECharacterWeaponBones.HumanRPalm));
                }
                catch (Exception e) { Plugin.Log.LogWarning("Arms in your hands: " + e.Message); }
            }
            if (!found)
            {
                for (int i = 0; i < 2; i++)
                {
                    Limb l = arms[i]; int o = i * 6;
                    l.Upper = bodyArms[o]; l.Lower = bodyArms[o + 1]; l.End = bodyArms[o + 2]; l.Tip = bodyArms[o + 3]; l.Index = bodyArms[o + 4]; l.Pinky = bodyArms[o + 5];
                }
            }
            for (int i = 0; i < 2; i++) { armBones[i * 3] = arms[i].Upper; armBones[i * 3 + 1] = arms[i].Lower; armBones[i * 3 + 2] = arms[i].End; }
            Plugin.Log.LogInfo($"Rider rig: arms {(found ? "of what's in your hands" : "of your body")}: {arms[0]} | {arms[1]}");
        }

        private static bool Use(Limb l, Transform upper, Transform lower, Transform palm)
        {
            if (upper == null || lower == null || palm == null) return false;
            Transform Child(params string[] endings)
            {
                foreach (string e in endings)
                    foreach (Transform t in palm.GetComponentsInChildren<Transform>(true))
                        if (t != palm && t.name.EndsWith(e, StringComparison.OrdinalIgnoreCase)) return t;
                return null;
            }
            l.Upper = upper; l.Lower = lower; l.End = palm;
            l.Tip = Child("Digit31", "Digit21") ?? (palm.childCount > 0 ? palm.GetChild(0) : null);
            l.Index = Child("Digit21"); l.Pinky = Child("Digit51");
            return true;
        }

        public static RiderRig Bind(Player player, RiderAnimation animation, string standingClip) => new RiderRig(player, animation, standingClip);

        // ---- The pose ---------------------------------------------------------------------------------------------

        /// <summary>
        /// The animation ('joints' in world space) onto your character, each part by its weight. 'climbing': the whole
        /// figure is the animation's, hung from your head, hands and feet where the animation has them. Otherwise
        /// (in the saddle) the legs are measured from your own hips and scaled to your leg length.
        /// Undo with <see cref="Restore"/> once drawn.
        /// </summary>
        public void Pose(Vector3[] joints, float body, float leftArm, float rightArm, float legWeight, bool climbing, Vector3? reins = null)
        {
            float armWeight = Mathf.Max(leftArm, rightArm);
            if (posed) Restore();
            Remember(bodyBones, bodyWas); Remember(armBones, armsWas); Remember(legBones, legsWas);
            pelvisWas = Pelvis.localPosition;
            posed = true;

            Vector3 head = Head.position;
            if (body > 0 && HasBody)
            {
                if (Frame(joints[hipR] - joints[hipL], joints[spineLow] - joints[pelvisJoint], animHipTilt, out Quaternion want)
                    && Frame(legs[1].Upper.position - legs[0].Upper.position, spine[0].position - Pelvis.position, hipTilt, out Quaternion have))
                    Pelvis.rotation = want * Quaternion.Inverse(have) * Pelvis.rotation;
                Transform top = spine[spine.Length - 1];
                if (Frame(joints[shoulderR] - joints[shoulderL], joints[neckJoint] - joints[spineHigh], animChestTilt, out want)
                    && Frame(arms[1].Upper.position - arms[0].Upper.position, neck.position - top.position, chestTilt, out have))
                {
                    // The same share of the turn at each spine bone adds up to all of it at the chest.
                    Quaternion share = Quaternion.Slerp(Quaternion.identity, want * Quaternion.Inverse(have), 1f / spine.Length);
                    foreach (Transform s in spine) s.rotation = share * s.rotation;
                }
                Blend(bodyBones, bodyWas, body);
                // Your view stays where it is: the body hangs from it.
                Pelvis.position += head - Head.position;
            }
            Vector3 shift = head - joints[headJoint];
            if (armWeight > 0 && HasArms)
            {
                for (int i = 0; i < arms.Length; i++)
                {
                    Limb l = arms[i];
                    float weight = i == 0 ? leftArm : rightArm;
                    if (weight <= 0) continue;
                    Vector3 across = l.IndexJoint >= 0 && l.PinkyJoint >= 0 ? joints[l.PinkyJoint] - joints[l.IndexJoint] : Vector3.zero;
                    Vector3 point = joints[l.Point] - joints[l.Hand], hand = joints[l.Hand] + shift;
                    // The rein hand: its palm (most of the way from wrist to knuckles) on the reins themselves.
                    if (i == 0 && reins.HasValue) hand = reins.Value - point * .8f;
                    Solve(l, hand, joints[l.Mid] + shift, point, across);
                    Blend(armBones, armsWas, weight, i * 3, 3);
                }
            }
            if (legWeight > 0 && HasLegs)
            {
                foreach (Limb l in legs)
                {
                    Vector3 animHip = joints[l.Root], knee = joints[l.Mid], foot = joints[l.Hand];
                    if (climbing) { Solve(l, foot + shift, knee + shift, joints[l.Point] - foot, Vector3.zero); continue; }
                    Vector3 hip = l.Upper.position;
                    float mine = (l.Lower.position - hip).magnitude + (l.End.position - l.Lower.position).magnitude;
                    float theirs = (knee - animHip).magnitude + (foot - knee).magnitude;
                    float s = theirs > .05f ? mine / theirs : 1;
                    Solve(l, hip + (foot - animHip) * s, hip + (knee - animHip) * s, joints[l.Point] - foot, Vector3.zero);
                }
                Blend(legBones, legsWas, legWeight);
            }
            if (Measure) Report = Measured(joints, shift, body, armWeight, legWeight, climbing) + (reins.HasValue ? $", rein hand {(arms[0].End.position - reins.Value).magnitude * 100:0} cm from the reins" : "");
        }

        private string Measured(Vector3[] joints, Vector3 shift, float body, float armWeight, float legWeight, bool climbing)
        {
            string Off(Limb l) => ((l.End.position - (joints[l.Hand] + shift)).magnitude * 100).ToString("0");
            float ground = Owner.Position.y;
            string text = $"posed body {body:0.0} arms {armWeight:0.0} legs {legWeight:0.0}{(climbing ? " climbing" : "")}: cm off, pelvis {(Pelvis.position - (joints[pelvisJoint] + shift)).magnitude * 100:0}";
            if (arms != null) text += $" hands {Off(arms[0])}/{Off(arms[1])}";
            if (legs != null) text += $" feet {Off(legs[0])}/{Off(legs[1])}; feet {legs[0].End.position.y - ground:0.00}/{legs[1].End.position.y - ground:0.00} m up";
            return text + $", head {Head.position.y - ground:0.00}, pelvis {Pelvis.position.y - ground:0.00}";
        }

        /// <summary>Back to the pose the game gave (nothing to do if none was put on).</summary>
        public void Restore()
        {
            if (!posed) return;
            posed = false;
            if (Pelvis == null) return;
            Pelvis.localPosition = pelvisWas;
            Recall(bodyBones, bodyWas); Recall(armBones, armsWas); Recall(legBones, legsWas);
        }

        private static void Remember(Transform[] bones, Quaternion[] was)
        {
            for (int i = 0; i < bones.Length; i++) was[i] = bones[i].localRotation;
        }

        private static void Recall(Transform[] bones, Quaternion[] was)
        {
            for (int i = 0; i < bones.Length; i++) if (bones[i] != null) bones[i].localRotation = was[i];
        }

        private static void Blend(Transform[] bones, Quaternion[] was, float weight, int first = 0, int count = -1)
        {
            if (weight >= 1) return;
            int end = count < 0 ? bones.Length : first + count;
            for (int i = first; i < end; i++) bones[i].localRotation = Quaternion.Slerp(was[i], bones[i].localRotation, weight);
        }

        // ---- The arithmetic ---------------------------------------------------------------------------------------

        // Which way a part of the body faces, from a line across it (left to right) and one up it, the up line first
        // stood upright by the lean it has when the figure is standing.
        private static bool Frame(Vector3 right, Vector3 up, float lean, out Quaternion frame)
        {
            frame = Quaternion.identity;
            if (right.sqrMagnitude < 1e-8f) return false;
            right.Normalize();
            up = Vector3.ProjectOnPlane(up, right);
            if (up.sqrMagnitude < 1e-8f) return false;
            up = Quaternion.AngleAxis(-lean, right) * up.normalized;
            frame = Quaternion.LookRotation(Vector3.Cross(right, up), up);
            return true;
        }

        // How far (degrees, about the line across) an up line leans from upright.
        private static float Tilt(Vector3 right, Vector3 up)
        {
            if (right.sqrMagnitude < 1e-8f) return 0;
            right.Normalize();
            Vector3 upright = Vector3.ProjectOnPlane(Vector3.up, right);
            up = Vector3.ProjectOnPlane(up, right);
            return upright.sqrMagnitude < 1e-6f || up.sqrMagnitude < 1e-8f ? 0 : Vector3.SignedAngle(upright, up, right);
        }

        private static void Solve(Limb l, Vector3 target, Vector3 pole, Vector3 point, Vector3 across)
        {
            Vector3 a = l.Upper.position, b = l.Lower.position, c = l.End.position;
            float l1 = (b - a).magnitude, l2 = (c - b).magnitude;
            if (l1 < 1e-4f || l2 < 1e-4f) return;
            Vector3 to = target - a;
            float d = Mathf.Clamp(to.magnitude, Mathf.Abs(l1 - l2) + 1e-3f, l1 + l2 - 1e-3f);
            Vector3 dir = to.sqrMagnitude > 1e-8f ? to.normalized : (c - a).normalized;
            Vector3 side = pole - a; side -= dir * Vector3.Dot(side, dir);
            if (side.sqrMagnitude < 1e-6f) { side = b - a; side -= dir * Vector3.Dot(side, dir); }
            if (side.sqrMagnitude < 1e-8f) return;
            side.Normalize();
            float x = (l1 * l1 - l2 * l2 + d * d) / (2 * d), h = Mathf.Sqrt(Mathf.Max(0, l1 * l1 - x * x));
            Vector3 joint = a + dir * x + side * h, end = a + dir * d;
            // Upper bone toward the elbow/knee, then turned about itself so the limb bends in that plane (the joint's own
            // hinge, so nothing twists), then the lower bone onto the target.
            l.Upper.rotation = Quaternion.FromToRotation(b - a, joint - a) * l.Upper.rotation;
            Vector3 axis = (joint - a).normalized;
            Vector3 now = Vector3.ProjectOnPlane(l.End.position - l.Lower.position, axis), want = Vector3.ProjectOnPlane(end - joint, axis);
            if (now.sqrMagnitude > 1e-6f && want.sqrMagnitude > 1e-6f)
                l.Upper.rotation = Quaternion.AngleAxis(Vector3.SignedAngle(now, want, axis), axis) * l.Upper.rotation;
            b = l.Lower.position; c = l.End.position;
            l.Lower.rotation = Quaternion.FromToRotation(c - b, end - b) * l.Lower.rotation;
            if (l.Tip == null || point.sqrMagnitude < 1e-6f) return;
            Vector3 tip = l.Tip.position - l.End.position;
            if (tip.sqrMagnitude < 1e-8f) return;
            l.End.rotation = Quaternion.FromToRotation(tip, point) * l.End.rotation;
            // The palm rolled about the way it points, so the knuckles lie as the animation's do.
            if (l.Index == null || l.Pinky == null || across.sqrMagnitude < 1e-8f) return;
            Vector3 along = point.normalized;
            Vector3 mine = Vector3.ProjectOnPlane(l.Pinky.position - l.Index.position, along), theirs = Vector3.ProjectOnPlane(across, along);
            if (mine.sqrMagnitude > 1e-8f && theirs.sqrMagnitude > 1e-8f)
                l.End.rotation = Quaternion.AngleAxis(Vector3.SignedAngle(mine, theirs, along), along) * l.End.rotation;
        }
    }
}
