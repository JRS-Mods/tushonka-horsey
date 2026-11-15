using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Rendering;

namespace TushonkaHorse
{
    public enum Coat { DarkBrown, LightBrown, Spotted, Brown }

    internal sealed class Clip
    {
        public int Frames;
        public float Fps;
        public Pose[] Poses;
        public float Length => (Frames - 1) / Fps;
    }

    /// <summary>One skinned part (body, saddle, reins, head gear, mane) as plain arrays.</summary>
    internal sealed class MeshData
    {
        public string Name;
        public Vector3[] Vertices, Normals;
        public Vector2[] Uv;
        public BoneWeight[] Weights;
        public string[] Kinds;
        public int[][] Triangles;
    }

    /// <summary>horse.bin (tools/convert_blender.py) read into memory: skeleton, parts, every clip.</summary>
    internal sealed class HorseData
    {
        public string[] BoneNames;
        public int[] Parents;
        public Pose[] Rest;
        public MeshData[] Meshes;
        public Dictionary<string, Clip> Clips;
    }

    /// <summary>
    /// The horse's assets, shared by every horse of the session. The file is read on a background thread as the game
    /// starts (it's plain data and plenty of it); the meshes, textures and materials are made on first use and kept, so
    /// only the first raid pays for them.
    /// </summary>
    internal static class HorseAssets
    {
        private const int Magic = 0x31535248; // "HRS1"
        private static Task<HorseData> loading;
        private static string folder;
        private static Mesh[] meshes;
        // One set per look (coat and graphics settings): with Fika, other players' horses can wear other coats at the
        // same time as yours, so a set is never thrown away while a horse could still be using it.
        private static readonly Dictionary<string, Material[][]> looks = new Dictionary<string, Material[][]>();
        public static string ShaderReport { get; private set; } = "";

        internal static void WarmUp(string assetFolder)
        {
            folder = assetFolder;
            if (loading == null) loading = Task.Run(() => Read(Path.Combine(assetFolder, "horse.bin")));
        }

        /// <summary>The file's contents (waits for the background read if it's still going).</summary>
        internal static HorseData Data
        {
            get
            {
                if (loading == null) throw new InvalidOperationException("Horse assets were never loaded.");
                try { return loading.Result; }
                catch (AggregateException e) { throw new InvalidDataException("horse.bin couldn't be read: " + e.InnerException?.Message, e.InnerException); }
            }
        }

        // ---- The file ---------------------------------------------------------------------------------------------

        private static Vector3 V(BinaryReader r) => new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        private static Pose ReadPose(BinaryReader r)
            => new Pose { P = V(r), Q = new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle()), S = V(r) };
        private static int Count(BinaryReader r, int max)
        { int n = r.ReadInt32(); if (n < 0 || n > max) throw new InvalidDataException("Invalid horse asset count: " + n); return n; }
        private static string Text(BinaryReader r) => Encoding.UTF8.GetString(r.ReadBytes(Count(r, 4096)));

        private static HorseData Read(string path)
        {
            using (var r = new BinaryReader(new BufferedStream(File.OpenRead(path), 1 << 20)))
            {
                if (r.ReadInt32() != Magic) throw new InvalidDataException("Unsupported horse.bin version");
                var data = new HorseData();
                int n = Count(r, 512);
                if (n == 0) throw new InvalidDataException("Horse has no skeleton");
                data.BoneNames = new string[n]; data.Parents = new int[n]; data.Rest = new Pose[n];
                for (int i = 0; i < n; i++)
                {
                    data.BoneNames[i] = Text(r); data.Parents[i] = r.ReadInt32();
                    if (data.Parents[i] < -1 || data.Parents[i] >= i) throw new InvalidDataException("Bone parents must precede children");
                    data.Rest[i] = ReadPose(r);
                }
                data.Meshes = new MeshData[Count(r, 64)];
                for (int m = 0; m < data.Meshes.Length; m++) data.Meshes[m] = ReadMesh(r, n);
                int nc = Count(r, 256);
                data.Clips = new Dictionary<string, Clip>(nc);
                for (int c = 0; c < nc; c++)
                {
                    string name = Text(r); int nf = Count(r, 20000); float fps = r.ReadSingle();
                    if (nf < 2 || fps <= 0 || fps > 240) throw new InvalidDataException("Invalid animation timing");
                    var clip = new Clip { Frames = nf, Fps = fps, Poses = new Pose[nf * n] };
                    for (int i = 0; i < clip.Poses.Length; i++) clip.Poses[i] = ReadPose(r);
                    if (Loops(name)) CloseLoop(clip, n);
                    data.Clips[name] = clip;
                }
                if (r.BaseStream.Position != r.BaseStream.Length) throw new InvalidDataException("Unexpected trailing horse data");
                return data;
            }
        }

        private static bool Loops(string name)
            => name.StartsWith("Walk_") || name.StartsWith("Trot_") || name.StartsWith("Run_") || name.StartsWith("Idle_") || name == "Jump_Horse_4_Loop";

        // A cycle that ends a little away from where it begins jerks every stride: what's missing is shared out over it.
        private static void CloseLoop(Clip clip, int boneCount)
        {
            int last = clip.Frames - 1;
            for (int b = 0; b < boneCount; b++)
            {
                Vector3 miss = PoseMath.Log(PoseMath.Mul(clip.Poses[b].Q, PoseMath.Conjugate(clip.Poses[last * boneCount + b].Q)));
                if (miss.sqrMagnitude < .00008f) continue;
                for (int k = 1; k <= last; k++)
                {
                    int at = k * boneCount + b;
                    clip.Poses[at].Q = PoseMath.Mul(PoseMath.Exp(miss * ((float)k / last)), clip.Poses[at].Q);
                }
            }
        }

        private static MeshData ReadMesh(BinaryReader r, int boneCount)
        {
            var mesh = new MeshData { Name = Text(r) };
            int nv = Count(r, 2000000);
            var vertices = new List<Vector3>(nv); var normals = new List<Vector3>(nv);
            var uv = new List<Vector2>(nv); var weights = new List<BoneWeight>(nv);
            for (int i = 0; i < nv; i++)
            {
                vertices.Add(V(r)); normals.Add(V(r)); uv.Add(new Vector2(r.ReadSingle(), r.ReadSingle()));
                var w = new BoneWeight
                {
                    boneIndex0 = r.ReadInt32(), boneIndex1 = r.ReadInt32(), boneIndex2 = r.ReadInt32(), boneIndex3 = r.ReadInt32(),
                    weight0 = r.ReadSingle(), weight1 = r.ReadSingle(), weight2 = r.ReadSingle(), weight3 = r.ReadSingle()
                };
                if ((uint)w.boneIndex0 >= boneCount || (uint)w.boneIndex1 >= boneCount || (uint)w.boneIndex2 >= boneCount || (uint)w.boneIndex3 >= boneCount)
                    throw new InvalidDataException("Bone index outside skeleton");
                weights.Add(w);
            }
            int subs = Count(r, 64);
            mesh.Kinds = new string[subs]; mesh.Triangles = new int[subs][];
            for (int s = 0; s < subs; s++)
            {
                mesh.Kinds[s] = Text(r);
                var indices = new int[Count(r, 12000000)];
                for (int i = 0; i < indices.Length; i++)
                { indices[i] = r.ReadInt32(); if ((uint)indices[i] >= nv) throw new InvalidDataException("Invalid vertex index"); }
                // Hair cards are seen from both sides, which the standard shader doesn't draw: each gets a back face.
                if (mesh.Kinds[s] == "mane") { SoftenHair(indices, vertices, normals); mesh.Triangles[s] = WithBackFaces(indices, vertices, normals, uv, weights); }
                else mesh.Triangles[s] = indices;
            }
            mesh.Vertices = vertices.ToArray(); mesh.Normals = normals.ToArray(); mesh.Uv = uv.ToArray(); mesh.Weights = weights.ToArray();
            return mesh;
        }

        // Hair cards lit by their own flat faces come out as light and dark slabs. Lit instead as one soft mass: every
        // card's normal mostly up and out from the horse's middle, with only a little of the card's own facing left in.
        private static void SoftenHair(int[] indices, List<Vector3> vertices, List<Vector3> normals)
        {
            var done = new HashSet<int>();
            foreach (int v in indices)
            {
                if (!done.Add(v)) continue;
                Vector3 own = normals[v];
                if (own.y < 0) own = -own;
                Vector3 soft = Vector3.up + new Vector3(Mathf.Clamp(vertices[v].x * 4f, -.8f, .8f), 0, 0) + own * .3f;
                normals[v] = soft.normalized;
            }
        }

        private static int[] WithBackFaces(int[] t, List<Vector3> vertices, List<Vector3> normals, List<Vector2> uv, List<BoneWeight> weights)
        {
            var copy = new Dictionary<int, int>();
            var both = new int[t.Length * 2];
            Array.Copy(t, both, t.Length);
            int Back(int v)
            {
                if (copy.TryGetValue(v, out int c)) return c;
                c = vertices.Count;
                // (The same soft normal as the front: both sides of a lock of hair catch the light alike.)
                vertices.Add(vertices[v]); normals.Add(normals[v]); uv.Add(uv[v]); weights.Add(weights[v]);
                return copy[v] = c;
            }
            for (int i = 0; i < t.Length; i += 3)
            { both[t.Length + i] = Back(t[i]); both[t.Length + i + 1] = Back(t[i + 2]); both[t.Length + i + 2] = Back(t[i + 1]); }
            return both;
        }

        // ---- On the graphics card ---------------------------------------------------------------------------------

        private static T Keep<T>(T asset) where T : UnityEngine.Object
        {
            // Ours for the session: the game's sweep of unused assets between raids leaves it alone.
            asset.hideFlags = HideFlags.DontUnloadUnusedAsset;
            return asset;
        }

        /// <summary>The parts' meshes (made once; the bind poses come from the first horse's skeleton at rest).</summary>
        internal static Mesh[] Meshes(Matrix4x4[] bindposes)
        {
            if (meshes != null && meshes.Length > 0 && meshes[0] != null) return meshes;
            HorseData data = Data;
            meshes = new Mesh[data.Meshes.Length];
            for (int m = 0; m < meshes.Length; m++)
            {
                MeshData d = data.Meshes[m];
                var mesh = Keep(new Mesh { name = d.Name, indexFormat = IndexFormat.UInt32 });
                mesh.vertices = d.Vertices; mesh.normals = d.Normals; mesh.uv = d.Uv;
                mesh.boneWeights = d.Weights; mesh.bindposes = bindposes;
                mesh.subMeshCount = d.Kinds.Length;
                for (int s = 0; s < d.Kinds.Length; s++) mesh.SetTriangles(d.Triangles[s], s, false);
                if (Array.IndexOf(d.Kinds, "coat") >= 0) mesh.RecalculateTangents();
                mesh.RecalculateBounds();
                meshes[m] = mesh;
            }
            return meshes;
        }

        /// <summary>Each part's materials for a look (coat and graphics settings); made once per look and kept.</summary>
        internal static Material[][] Materials(Coat coat)
        {
            string wanted = coat + "|2|" + Plugin.NormalMaps.Value + "|" + Plugin.ManeCutoff.Value.ToString("0.00");
            if (looks.TryGetValue(wanted, out Material[][] kept) && kept.Length > 0 && kept[0].Length > 0 && kept[0][0] != null) return kept;
            Shader shader = Shader.Find("Standard") ?? Shader.Find("Legacy Shaders/Diffuse");
            if (shader == null) throw new InvalidOperationException("No supported lit shader found in the game.");
            ShaderReport = "";
            var byKind = new Dictionary<string, Material>();
            HorseData data = Data;
            var materials = new Material[data.Meshes.Length][];
            for (int m = 0; m < materials.Length; m++)
            {
                string[] kinds = data.Meshes[m].Kinds;
                materials[m] = new Material[kinds.Length];
                for (int s = 0; s < kinds.Length; s++)
                {
                    if (!byKind.TryGetValue(kinds[s], out Material material))
                        byKind[kinds[s]] = material = Keep(Make(shader, kinds[s], coat));
                    materials[m][s] = material;
                }
            }
            looks[wanted] = materials;
            return materials;
        }

        private static Texture2D Texture(string file, bool linear = false)
        {
            string path = Path.Combine(folder, file);
            if (!File.Exists(path)) { Plugin.Log.LogWarning("Missing texture " + file); return null; }
            return TextureCache.Load(path, linear);
        }

        private static Material Make(Shader shader, string kind, Coat coat)
        {
            var m = new Material(shader) { name = "Horse " + kind, color = Color.white };
            void Surface(float gloss, float metal)
            {
                if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", gloss);
                if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", metal);
            }
            switch (kind)
            {
                case "coat":
                    m.mainTexture = Texture(coat == Coat.LightBrown ? "coat_lightbrown.jpg" : coat == Coat.Spotted ? "coat_spotted.jpg" : coat == Coat.Brown ? "coat_brown.jpg" : "coat_darkbrown.jpg");
                    Surface(.32f, 0);
                    if (Plugin.NormalMaps.Value && m.HasProperty("_BumpMap"))
                    {
                        Texture2D normal = Texture("coat_n.png", linear: true);
                        if (normal != null) { m.SetTexture("_BumpMap", normal); m.SetFloat("_BumpScale", 1f); m.EnableKeyword("_NORMALMAP"); }
                    }
                    break;
                case "eye": m.mainTexture = Texture("eye.jpg"); Surface(.85f, 0); break;
                case "mane":
                    m.mainTexture = Texture(coat == Coat.LightBrown ? "mane_brown.png" : coat == Coat.Spotted ? "mane_white.png" : "mane_black.png");
                    Surface(.08f, 0);
                    if (shader.name == "Standard")
                    {
                        // Cutout: the hair cards' see-through parts are dropped.
                        m.SetFloat("_Mode", 1); m.SetOverrideTag("RenderType", "TransparentCutout");
                        m.SetInt("_SrcBlend", (int)BlendMode.One); m.SetInt("_DstBlend", (int)BlendMode.Zero); m.SetInt("_ZWrite", 1);
                        m.EnableKeyword("_ALPHATEST_ON"); m.DisableKeyword("_ALPHABLEND_ON"); m.DisableKeyword("_ALPHAPREMULTIPLY_ON");
                        m.SetFloat("_Cutoff", Plugin.ManeCutoff.Value);
                        m.renderQueue = 2450; // AlphaTest
                    }
                    break;
                case "saddle": m.mainTexture = Texture("saddle.jpg"); Surface(.35f, 0); break;
                case "saddle_metal": m.mainTexture = Texture("saddle.jpg"); Surface(.6f, .85f); break;
                case "wood": m.color = new Color(.28f, .16f, .08f); Surface(.3f, 0); break;
                case "leather": m.mainTexture = Texture("reins.jpg"); m.color = new Color(.62f, .5f, .4f); Surface(.22f, 0); break;
                case "metal": m.color = new Color(.62f, .62f, .6f); Surface(.65f, .9f); break;
                default: m.color = new Color(.3f, .2f, .12f); Surface(.3f, 0); break;
            }
            ShaderReport += kind + "=" + shader.name + (m.IsKeywordEnabled("_ALPHATEST_ON") ? "(cutout)" : "") + " ";
            return m;
        }
    }
}
