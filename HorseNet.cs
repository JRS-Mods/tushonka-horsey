using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx.Bootstrap;
using Comfort.Common;
using EFT;
using EFT.Communications;
using UnityEngine;

namespace TushonkaHorse
{
    public struct HorseState
    {
        public int Owner;
        public bool Spawned;
        public Vector3 Position;
        public float Yaw, Pitch, Scale;
        public byte Coat;
        public string Clip, Layer;
        public float LayerWeight, Progress, Playback, RootY;
        public bool Looping;
    }

    public interface IHorseLink
    {
        bool Online { get; }
        bool Headless { get; }
        int NetIdOf(Player player);
        void Send(HorseState state, bool reliable);
    }

    internal static class HorseNet
    {
        private const string FikaId = "com.fika.core";
        internal const string HeadlessId = "com.fika.headless";
        private const string LinkFile = "TushonkaHorse.Fika.dll";
        private const string LinkType = "TushonkaHorse.Fika.FikaLink";
        private static readonly Dictionary<int, HorseReplica> replicas = new Dictionary<int, HorseReplica>();
        private static IHorseLink link;
        private static bool toldNotShared, cantShow;

        internal static bool Online
        {
            get
            {
                try { return link != null && link.Online; }
                catch (Exception e) { Plugin.Log.LogWarning("Horse sync: " + e.Message); return false; }
            }
        }

        internal static bool Headless
        {
            get
            {
                // The Fika.Headless plugin itself (even if the link didn't load), or Fika's own flag.
                if (Chainloader.PluginInfos.ContainsKey(HeadlessId)) return true;
                if (link == null) return false;
                try { return link.Headless; }
                catch { return false; }
            }
        }

        internal static void Load()
        {
            if (!Chainloader.PluginInfos.ContainsKey(FikaId)) return;
            try
            {
                string file = Path.Combine(Plugin.AssetFolder, LinkFile);
                if (!File.Exists(file))
                {
                    Plugin.Log.LogWarning($"Fika is installed but {LinkFile} is missing: horses will only appear locally.");
                    return;
                }
                AppDomain.CurrentDomain.AssemblyResolve += AlreadyLoaded;
                link = (IHorseLink)Activator.CreateInstance(Assembly.LoadFrom(file).GetType(LinkType, true), true);
                Plugin.Log.LogInfo(Headless ? "Fika headless host found: relaying horses." : "Fika found: horses are shared with other players.");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("The Fika horse link couldn't start (the horse still works locally): " + e);
                link = null;
            }
        }

        private static Assembly AlreadyLoaded(object sender, ResolveEventArgs args)
        {
            string wanted = new AssemblyName(args.Name).Name;
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                if (assembly.GetName().Name == wanted) return assembly;
            return null;
        }

        internal static void Send(HorseState state, Player owner, bool reliable)
        {
            if (!Online || owner == null) return;
            try
            {
                state.Owner = link.NetIdOf(owner);
                if (state.Owner >= 0) link.Send(state, reliable);
            }
            catch (Exception e) { Plugin.Log.LogWarning("Horse sync: " + e.Message); }
        }

        internal static void Gone(Player owner)
        {
            Send(new HorseState { Spawned = false }, owner, true);
        }

        public static void Received(HorseState state)
        {
            if (Headless || state.Owner < 0 || state.Owner == MyNetId()) return;
            Show(state, state.Owner);
        }

        /// <summary>
        /// The self test's stand-in for another player: your own horse's state shown as a remote horse would be
        /// (null takes it away), so the replica can be checked without a second game.
        /// </summary>
        internal const int EchoKey = int.MinValue;
        internal static void Echo(HorseState? state)
        {
            if (state == null) { Remove(EchoKey); return; }
            HorseState s = state.Value;
            s.Owner = EchoKey;
            Show(s, EchoKey);
        }

        /// <summary>Other players' horses shown here (for the self test).</summary>
        internal static string RemoteReport
        {
            get
            {
                var parts = new List<string>();
                foreach (KeyValuePair<int, HorseReplica> pair in replicas)
                    if (pair.Key != EchoKey && pair.Value != null)
                        parts.Add($"{pair.Key}:{pair.Value.Clip}@{pair.Value.transform.position} seen {pair.Value.Seen}");
                return parts.Count == 0 ? "none" : string.Join(", ", parts);
            }
        }

        internal static HorseReplica EchoReplica => replicas.TryGetValue(EchoKey, out HorseReplica r) ? r : null;

        private static void Show(HorseState state, int key)
        {
            if (!state.Spawned)
            {
                Remove(key);
                return;
            }
            if (cantShow || !Singleton<GameWorld>.Instantiated) return;
            GameWorld world = Singleton<GameWorld>.Instance;
            if (world == null || world is HideoutGameWorld) return;
            if (!replicas.TryGetValue(key, out HorseReplica replica) || replica == null)
            {
                try { replica = HorseReplica.Create(world.transform, state); }
                catch (Exception e)
                {
                    // Don't try again 20 times a second (a missing or broken horse.bin won't mend itself mid-raid).
                    cantShow = true;
                    Plugin.Log.LogError("Other players' horses can't be shown: " + e);
                    return;
                }
                replicas[key] = replica;
            }
            replica.Receive(state);
        }

        public static void Gone(int owner) => Remove(owner);

        private static void Remove(int owner)
        {
            if (replicas.TryGetValue(owner, out HorseReplica replica) && replica != null)
                UnityEngine.Object.Destroy(replica.gameObject);
            replicas.Remove(owner);
        }

        public static void RaidOver()
        {
            foreach (HorseReplica replica in replicas.Values)
                if (replica != null) UnityEngine.Object.Destroy(replica.gameObject);
            replicas.Clear();
            toldNotShared = cantShow = false;
        }

        public static void NotShared(string why)
        {
            Plugin.Log.LogWarning(why);
            if (toldNotShared || Headless) return;
            toldNotShared = true;
            try { NotificationManager.DisplayWarningNotification(why); }
            catch { }
        }

        private static int MyNetId()
        {
            if (link == null || !Singleton<GameWorld>.Instantiated) return -1;
            Player player = Singleton<GameWorld>.Instance?.MainPlayer;
            return player != null ? link.NetIdOf(player) : -1;
        }
    }

    internal sealed class HorseReplica : MonoBehaviour
    {
        private HorseModel model;
        private HorseState state;
        private Vector3 fromPosition;
        private float fromYaw, fromPitch, receivedAt, interval = .05f, lastReceived, animationProgress;

        internal static HorseReplica Create(Transform parent, HorseState initial)
        {
            var go = new GameObject($"Tushonka Horse (remote {initial.Owner})");
            go.transform.SetParent(parent, true);
            HorseReplica replica = go.AddComponent<HorseReplica>();
            try { replica.model = new HorseModel(go.transform, Mathf.Clamp(initial.Scale, .8f, 1.25f), ValidCoat(initial.Coat)); }
            catch { Destroy(go); throw; }
            replica.state = initial;
            replica.fromPosition = initial.Position;
            replica.fromYaw = initial.Yaw;
            replica.fromPitch = initial.Pitch;
            replica.animationProgress = initial.Progress;
            go.transform.SetPositionAndRotation(initial.Position, Quaternion.Euler(initial.Pitch, initial.Yaw, 0));
            Plugin.Log.LogInfo($"Remote horse {initial.Owner} appeared.");
            return replica;
        }

        internal string Seen => model == null ? "-" : model.Seen + (model.MaterialsIntact ? "" : " MATERIALS LOST");
        internal string Clip => model == null ? "-" : model.Current;

        private static Coat ValidCoat(byte value) => value <= (byte)Coat.Brown ? (Coat)value : Coat.DarkBrown;

        internal void Receive(HorseState next)
        {
            float now = Time.time;
            fromPosition = transform.position;
            Vector3 angles = transform.eulerAngles;
            fromYaw = angles.y;
            fromPitch = angles.x > 180 ? angles.x - 360 : angles.x;
            if (lastReceived > 0) interval = Mathf.Clamp(now - lastReceived, .03f, .2f);
            lastReceived = receivedAt = now;
            if (state.Clip != next.Clip) animationProgress = next.Progress;
            else
            {
                float delta = next.Progress - animationProgress;
                if (next.Looping) delta -= Mathf.Round(delta);
                animationProgress += delta * .65f;
                if (next.Looping) animationProgress -= Mathf.Floor(animationProgress);
            }
            state = next;
        }

        private void Update()
        {
            if (model == null) return;
            float dt = Time.deltaTime;
            float t = Mathf.Clamp01((Time.time - receivedAt) / Mathf.Max(.03f, interval));
            transform.SetPositionAndRotation(Vector3.Lerp(fromPosition, state.Position, t),
                Quaternion.Euler(Mathf.LerpAngle(fromPitch, state.Pitch, t), Mathf.LerpAngle(fromYaw, state.Yaw, t), 0));
            if (!string.IsNullOrEmpty(state.Clip) && model.Has(state.Clip))
            {
                float length = Mathf.Max(.01f, model.Length(state.Clip));
                animationProgress += dt * state.Playback / length;
                animationProgress = state.Looping ? animationProgress - Mathf.Floor(animationProgress) : Mathf.Clamp01(animationProgress);
                model.TickAt(state.Clip, state.Layer, state.LayerWeight, animationProgress, state.Playback, dt, state.Looping);
                model.Root.localPosition = new Vector3(0, state.RootY, 0);
            }
        }
    }
}
