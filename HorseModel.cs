using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;

namespace TushonkaHorse
{
    /// <summary>
    /// One horse as drawn: its own skeleton and renderers over the shared meshes and materials (HorseAssets), and its
    /// animation, played with inertial blending between clips: the new clip plays at once and the difference from the
    /// pose the horse was really in (and how fast that was moving) dies away over the blend.
    /// </summary>
    internal sealed class HorseModel
    {
        private readonly Transform[] bones;
        private readonly Dictionary<string, Transform> byName = new Dictionary<string, Transform>();
        private readonly Dictionary<string, Clip> clips;
        private readonly Pose[] displayed, previous;
        private readonly Vector3[] offsetTurn, turnRate, offsetSlide, slideRate;
        private readonly int[] legBones;
        private readonly Transform[] lookChain;
        private static readonly string[] LookBones = { "Neck_3", "Neck_4", "Neck_5", "Neck_6", "Head" };
        private static readonly float[] LookShare = { .12f, .16f, .2f, .24f, .28f };

        private const float DefaultBlend = .3f, MaxTurnRate = 20f, MaxSlideRate = 5f;
        private Clip clip;
        private float time, sinceSwitch, settleRate, nextBlend = DefaultBlend, lastDt = 1 / 60f;
        private bool settling;
        private float lookYaw, wantYaw;

        public Transform Root { get; }
        public string Current { get; private set; }
        public float Progress { get; private set; }
        // The turning clip mixed in, and how much (what the rider's legs follow).
        public string Layer { get; private set; }
        public float LayerWeight { get; private set; }
        public float Playback { get; private set; }
        public bool Looping { get; private set; }

        /// <summary>
        /// How far the clip has the whole body above (or below) where it stands at rest, in the model's own units. The
        /// jump, fall and landing clips rise by themselves; the horse's real height in the air comes from its motion.
        /// </summary>
        public float RootLift => displayed[0].P.y - restHeight;
        private readonly float restHeight;

        private readonly List<Renderer> renderers = new List<Renderer>();

        public Transform Bone(string name) => byName.TryGetValue(name, out Transform t) ? t : null;
        public float Length(string name) => clips[name].Length;
        public bool Has(string name) => clips.ContainsKey(name);

        public HorseModel(Transform parent, float scale, Coat coat)
        {
            HorseData data = HorseAssets.Data;
            clips = data.Clips;
            var visual = new GameObject("Tushonka horse");
            visual.transform.SetParent(parent, false);
            Root = visual.transform;
            try
            {
                int n = data.BoneNames.Length;
                bones = new Transform[n]; displayed = new Pose[n]; previous = new Pose[n];
                offsetTurn = new Vector3[n]; turnRate = new Vector3[n]; offsetSlide = new Vector3[n]; slideRate = new Vector3[n];
                var legs = new List<int>();
                for (int i = 0; i < n; i++)
                {
                    int p = data.Parents[i];
                    Transform bone = new GameObject(data.BoneNames[i]).transform;
                    bone.SetParent(p < 0 ? Root : bones[p], false);
                    bones[i] = bone; byName[data.BoneNames[i]] = bone;
                    Pose rest = data.Rest[i];
                    bone.localPosition = rest.P; bone.localRotation = rest.Q; bone.localScale = rest.S;
                    displayed[i] = previous[i] = rest;
                    if (data.BoneNames[i].Contains("_Leg_")) legs.Add(i);
                }
                legBones = legs.ToArray();
                restHeight = data.Rest[0].P.y;
                var chain = new List<Transform>();
                foreach (string name in LookBones) { Transform t = Bone(name); if (t != null) chain.Add(t); }
                lookChain = chain.ToArray();

                var bind = new Matrix4x4[n];
                Matrix4x4 toWorld = Root.localToWorldMatrix;
                for (int i = 0; i < n; i++) bind[i] = bones[i].worldToLocalMatrix * toWorld;
                Mesh[] meshes = HorseAssets.Meshes(bind);
                Material[][] materials = HorseAssets.Materials(coat);
                for (int m = 0; m < meshes.Length; m++)
                {
                    var go = new GameObject(meshes[m].name);
                    go.transform.SetParent(Root, false);
                    var renderer = go.AddComponent<SkinnedMeshRenderer>();
                    renderer.sharedMesh = meshes[m]; renderer.bones = bones; renderer.rootBone = bones[0]; renderer.sharedMaterials = materials[m];
                    renderer.quality = SkinQuality.Bone4; renderer.shadowCastingMode = ShadowCastingMode.On;
                    renderer.receiveShadows = true;
                    renderers.Add(renderer);
                    // Wide enough for every pose (a rearing jump included), so it isn't skinned while nobody can see it.
                    // The box is in the root bone's own space, which the model has at 1/100 scale: 7 m across in the world.
                    renderer.updateWhenOffscreen = false;
                    float unit = Mathf.Max(1e-4f, Mathf.Abs(data.Rest[0].S.x));
                    renderer.localBounds = new Bounds(Vector3.zero, Vector3.one * (7f / unit));
                }
                Root.localScale = Vector3.one * scale;
            }
            catch { UnityEngine.Object.Destroy(visual); throw; }
        }

        /// <summary>Clips the mod plays by name must be in the file.</summary>
        public void Require(params string[] names)
        {
            foreach (string name in names)
                if (!clips.ContainsKey(name)) throw new InvalidDataException("Missing animation: " + name);
        }

        // ---- Animation ------------------------------------------------------------------------------------------

        // Scale is hardly ever animated: written only when it changes.
        private void Apply(int i, in Pose p)
        {
            Transform bone = bones[i];
            bone.localPosition = p.P; bone.localRotation = p.Q;
            if (p.S != displayed[i].S) bone.localScale = p.S;
        }

        /// <summary>How long the next change of clip takes to settle.</summary>
        public void NextBlend(float seconds) { nextBlend = seconds; }

        /// <summary>The next Tick starts its clip from the beginning, even if it's the one playing.</summary>
        public void Restart() { Current = null; }

        private Pose Sample(Clip c, float frame, int bone)
        {
            int first = (int)frame, n = bones.Length;
            return Pose.Blend(c.Poses[first * n + bone], c.Poses[(first + 1) * n + bone], frame - first);
        }

        private Pose Mixed(Clip c, float frame, Clip other, float weight, int bone)
        {
            Pose p = Sample(c, frame, bone);
            if (other == null) return p;
            float along = frame / (c.Frames - 1);
            return Pose.Blend(p, Sample(other, Mathf.Min(along * (other.Frames - 1), other.Frames - 1.001f), bone), weight);
        }

        private Clip LayerClip(string layer, float weight)
            => layer != null && weight > 0.001f && clips.TryGetValue(layer, out Clip other) ? other : null;

        private void Begin(string name, string layer, float weight, float start)
        {
            Clip to = clips[name];
            Clip other = LayerClip(layer, weight);
            float last = to.Frames - 1;
            float frame = Mathf.Max(0, Mathf.Min(start * to.Fps, last - 1.001f)), next = Mathf.Min(frame + 1, last - .001f);
            for (int i = 0; i < bones.Length; i++)
            {
                Pose at = Mixed(to, frame, other, weight, i), then = Mixed(to, next, other, weight, i);
                Quaternion now = displayed[i].Q;
                offsetTurn[i] = PoseMath.Log(PoseMath.Mul(now, PoseMath.Conjugate(at.Q)));
                offsetSlide[i] = displayed[i].P - at.P;
                // How the pose was moving against how the clip moves: carried over, so the change doesn't jolt the bones.
                Vector3 was = PoseMath.Log(PoseMath.Mul(now, PoseMath.Conjugate(previous[i].Q))) / lastDt;
                Vector3 goes = PoseMath.Log(PoseMath.Mul(then.Q, PoseMath.Conjugate(at.Q))) * to.Fps;
                turnRate[i] = Vector3.ClampMagnitude(was - goes, MaxTurnRate);
                slideRate[i] = Vector3.ClampMagnitude((displayed[i].P - previous[i].P) / lastDt - (then.P - at.P) * to.Fps, MaxSlideRate);
            }
            // A critically damped settle is 95% done after 4.74 / rate.
            settleRate = 4.74f / Mathf.Max(nextBlend, .02f); sinceSwitch = 0; settling = true;
            nextBlend = DefaultBlend;
            Current = name; clip = to; time = start;
        }

        /// <summary>Plays a clip on (starting it if it isn't the one playing). True once a non-looping clip has ended.</summary>
        public bool Tick(string name, float speed, float dt, bool loop = true) => Tick(name, null, 0, speed, dt, loop);

        public bool Tick(string name, string layer, float weight, float speed, float dt, bool loop = true)
        {
            if (Current != name) Begin(name, layer, weight, 0);
            Playback = speed;
            Looping = loop;
            float last = clip.Frames - 1, length = clip.Length;
            time += dt * speed;
            bool done = false;
            if (loop) { time %= length; if (time < 0) time += length; }
            else if (time >= length) { time = length; done = true; }
            float frame = Mathf.Clamp(time * clip.Fps, 0, last - 0.001f);
            Progress = frame / last;
            Clip other = LayerClip(layer, weight);
            Layer = other != null ? layer : null; LayerWeight = other != null ? weight : 0;
            float keep = 0, keepRate = 0;
            if (settling)
            {
                sinceSwitch += dt;
                float t = settleRate * sinceSwitch, fade = Mathf.Exp(-t);
                keep = (1 + t) * fade; keepRate = sinceSwitch * fade;
                if (t > 7) settling = false;
            }
            for (int i = 0; i < bones.Length; i++)
            {
                Pose p = Mixed(clip, frame, other, weight, i);
                if (settling)
                {
                    p.Q = PoseMath.Mul(PoseMath.Exp(offsetTurn[i] * keep + turnRate[i] * keepRate), p.Q);
                    p.P += offsetSlide[i] * keep + slideRate[i] * keepRate;
                }
                Apply(i, p);
                previous[i] = displayed[i]; displayed[i] = p;
            }
            if (dt > 0) lastDt = Mathf.Max(dt, .005f);
            TurnHead(dt);
            return done;
        }

        /// <summary>Displays an externally clocked animation, used by Fika replicas.</summary>
        public void TickAt(string name, string layer, float weight, float progress, float playback, float dt, bool looping)
        {
            if (string.IsNullOrEmpty(name) || !clips.ContainsKey(name)) return;
            Clip wanted = clips[name];
            float at = Mathf.Clamp01(progress) * wanted.Length;
            if (Current != name) Begin(name, layer, weight, at);
            time = at - dt * playback;
            Tick(name, layer, weight, playback, dt, looping);
        }

        /// <summary>A gait: taken up from the frame of its cycle where the legs are closest to where they are now.</summary>
        public void TickGait(string name, string lean, float weight, float speed, float dt)
        {
            if (Current != name)
            {
                Clip to = clips[name];
                NextBlend(.28f);
                Begin(name, lean, weight, ClosestFrame(to) / to.Fps);
            }
            Tick(name, lean, weight, speed, dt);
        }

        private int ClosestFrame(Clip c)
        {
            int best = 0, count = bones.Length;
            float least = float.MaxValue;
            for (int k = 0; k < c.Frames - 1; k++)
            {
                float apart = 0;
                foreach (int b in legBones)
                {
                    Quaternion a = c.Poses[k * count + b].Q, d = displayed[b].Q;
                    float dot = a.x * d.x + a.y * d.y + a.z * d.z + a.w * d.w;
                    apart += 1 - dot * dot;
                }
                if (apart < least) { least = apart; best = k; }
            }
            return best;
        }

        /// <summary>Turns the neck and head toward a side (degrees, right positive) on top of the animation, this frame.</summary>
        public void LookAt(float yaw) { wantYaw = Mathf.Clamp(yaw, -60, 60); }

        private void TurnHead(float dt)
        {
            lookYaw += (wantYaw - lookYaw) * (1 - Mathf.Exp(-6 * dt));
            wantYaw = 0;
            if (Mathf.Abs(lookYaw) < .2f) return;
            Vector3 up = Root.up;
            for (int i = 0; i < lookChain.Length; i++)
                lookChain[i].rotation = Quaternion.AngleAxis(lookYaw * LookShare[i], up) * lookChain[i].rotation;
        }
    }
}
