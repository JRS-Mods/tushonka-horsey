using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace TushonkaHorse
{
    /// <summary>
    /// The kit's rider animations (rider.bin, tools/convert_blender.py) as joint positions in the horse model's space:
    /// where the rider's pelvis, head, hands, knees and feet are on (or beside) the horse at each frame. These are put
    /// onto the game's own character by RiderRig, so no skeleton has to match.
    /// </summary>
    internal sealed class RiderAnimation
    {
        private sealed class Clip { public int Frames; public float Fps; public Vector3[] P; }
        private const int Magic = 0x31524452; // "RDR1"
        private const string Idle = "Idle_Rider_4";
        private readonly Dictionary<string, Clip> clips = new Dictionary<string, Clip>();
        private readonly Dictionary<string, int> index = new Dictionary<string, int>();
        private readonly Dictionary<string, string> forHorse = new Dictionary<string, string>();

        public int JointCount { get; private set; }
        // The joints the placement code asks for every frame.
        public int Pelvis { get; private set; }
        public int Head { get; private set; }
        public int LeftFoot { get; private set; }
        public int RightFoot { get; private set; }

        public static RiderAnimation Load(string path)
        {
            var a = new RiderAnimation();
            using (var r = new BinaryReader(new BufferedStream(File.OpenRead(path), 1 << 18)))
            {
                if (r.ReadInt32() != Magic) throw new InvalidDataException("Unsupported rider.bin version");
                int n = r.ReadInt32();
                if (n <= 0 || n > 256) throw new InvalidDataException("Bad rider joint count");
                a.JointCount = n;
                for (int i = 0; i < n; i++) a.index[Encoding.UTF8.GetString(r.ReadBytes(r.ReadInt32()))] = i;
                int nc = r.ReadInt32();
                for (int c = 0; c < nc; c++)
                {
                    string name = Encoding.UTF8.GetString(r.ReadBytes(r.ReadInt32()));
                    int frames = r.ReadInt32(); float fps = r.ReadSingle();
                    if (frames < 2 || frames > 20000 || fps <= 0) throw new InvalidDataException("Bad rider clip " + name);
                    var p = new Vector3[frames * n];
                    for (int i = 0; i < p.Length; i++) p[i] = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                    a.clips[name] = new Clip { Frames = frames, Fps = fps, P = p };
                }
                if (r.BaseStream.Position != r.BaseStream.Length) throw new InvalidDataException("Unexpected trailing rider data");
            }
            a.Pelvis = a.Need("pelvis"); a.Head = a.Need("head"); a.LeftFoot = a.Need("foot_l"); a.RightFoot = a.Need("foot_r");
            if (!a.Has(Idle)) throw new InvalidDataException("rider.bin has no " + Idle);
            return a;
        }

        private int Need(string joint) => index.TryGetValue(joint, out int i) ? i : throw new InvalidDataException("rider.bin has no joint " + joint);

        public bool Has(string clip) => clip != null && clips.ContainsKey(clip);
        public int Joint(string name) => index.TryGetValue(name, out int i) ? i : -1;
        public float Length(string clip) { Clip c = clips[clip]; return (c.Frames - 1) / c.Fps; }

        /// <summary>The joints at a point (0-1) of a clip, optionally mixed with a second clip at the same point.</summary>
        public void Sample(string clip, float progress, string layer, float weight, Vector3[] into)
        {
            Read(clips[clip], progress, into, 1, false);
            if (layer != null && weight > .001f && clips.TryGetValue(layer, out Clip other)) Read(other, progress, into, weight, true);
        }

        private void Read(Clip c, float progress, Vector3[] into, float weight, bool mix)
        {
            int n = JointCount;
            float f = Mathf.Clamp01(progress) * (c.Frames - 1);
            int a = Mathf.Min((int)f, c.Frames - 2);
            float t = f - a;
            int row = a * n, next = row + n;
            for (int j = 0; j < n; j++)
            {
                Vector3 p = Vector3.LerpUnclamped(c.P[row + j], c.P[next + j], t);
                into[j] = mix ? Vector3.LerpUnclamped(into[j], p, weight) : p;
            }
        }

        /// <summary>The rider clip that goes with a horse clip (the kit made them in pairs); remembered once worked out.</summary>
        public string ForHorse(string horseClip)
        {
            if (horseClip == null) return Idle;
            if (forHorse.TryGetValue(horseClip, out string name)) return name;
            if (horseClip.StartsWith("Idle_Horse_4")) name = Idle;
            else if (horseClip.StartsWith("Walk_Backwards_Horse_4")) name = "Walk_Backwards_Character";
            else name = horseClip.Replace("_Horse_4", "_Rider_4");
            return forHorse[horseClip] = Has(name) ? name : Idle;
        }
    }
}
