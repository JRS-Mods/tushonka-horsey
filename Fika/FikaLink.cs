using System;
using System.Collections.Generic;
using EFT;
using Fika.Core.Main.Players;
using Fika.Core.Main.Utils;
using Fika.Core.Modding;
using Fika.Core.Modding.Events;
using Fika.Core.Networking;
using Fika.Core.Networking.LiteNetLib;
using Fika.Core.Networking.LiteNetLib.Utils;
using UnityEngine;

namespace TushonkaHorse.Fika
{
    internal sealed class FikaLink : IHorseLink
    {
        private const int HelloTries = 3;
        private const float HelloEvery = 4f;
        private readonly HashSet<NetPeer> peers = new HashSet<NetPeer>();
        private readonly Dictionary<int, NetPeer> owners = new Dictionary<int, NetPeer>();
        private IFikaNetworkManager network;
        private bool host, welcomed, refused, toldSilent;
        private int helloTries;
        private float nextHello;

        public bool Online
        {
            get
            {
                if (network == null) return false;
                if (host || welcomed) return true;
                SayHello();
                return false;
            }
        }

        public bool Headless => FikaBackendUtils.IsHeadless;

        public FikaLink()
        {
            FikaEventDispatcher.SubscribeEvent<FikaNetworkManagerCreatedEvent>(e => Safely(() => Created(e)));
            FikaEventDispatcher.SubscribeEvent<FikaNetworkManagerDestroyedEvent>(e => Safely(() => Destroyed(e)));
            FikaEventDispatcher.SubscribeEvent<PeerDisconnectedEvent>(e => Safely(() => Left(e)));
            FikaEventDispatcher.SubscribeEvent<GameWorldStartedEvent>(_ => Safely(() =>
            {
                helloTries = 0;
                nextHello = 0;
                toldSilent = false;
                if (network != null && !host) SayHello();
            }));
        }

        private static void Safely(Action action)
        {
            try { action(); }
            catch (Exception e) { Plugin.Log.LogWarning("Horse sync: " + e); }
        }

        public int NetIdOf(Player player) => (player as FikaPlayer)?.NetId ?? -1;

        private void Created(FikaNetworkManagerCreatedEvent e)
        {
            Reset();
            network = e.Manager;
            host = network is FikaServer;
            network.RegisterPacket<HorseHelloPacket, NetPeer>((packet, from) => Safely(() => Hello(packet, from)));
            network.RegisterPacket<HorseStatePacket, NetPeer>((packet, from) => Safely(() => State(packet, from)));
        }

        private void Destroyed(FikaNetworkManagerDestroyedEvent e)
        {
            if (e.Manager != network) return;
            network.UnregisterPacket<HorseHelloPacket>();
            network.UnregisterPacket<HorseStatePacket>();
            Reset();
            HorseNet.RaidOver();
        }

        private void Left(PeerDisconnectedEvent e)
        {
            if (e.NetworkManager != network) return;
            peers.Remove(e.Peer);
            var gone = new List<int>();
            foreach (KeyValuePair<int, NetPeer> pair in owners)
                if (pair.Value == e.Peer) gone.Add(pair.Key);
            foreach (int owner in gone)
            {
                owners.Remove(owner);
                HorseNet.Gone(owner);
            }
        }

        private void Reset()
        {
            network = null;
            host = welcomed = refused = toldSilent = false;
            helloTries = 0;
            nextHello = 0;
            peers.Clear();
            owners.Clear();
        }

        private void SayHello()
        {
            if (network == null || Time.time < nextHello) return;
            if (helloTries >= HelloTries)
            {
                if (!refused && !toldSilent)
                {
                    toldSilent = true;
                    HorseNet.NotShared($"Other players' horses won't show this raid: the host didn't answer. Everyone needs Tushonka Horse {Plugin.Version}, including a headless host.");
                }
                return;
            }
            helloTries++;
            nextHello = Time.time + HelloEvery;
            var packet = new HorseHelloPacket { Version = Plugin.Version };
            network.SendData(ref packet, DeliveryMethod.ReliableOrdered, false);
        }

        private void Hello(HorseHelloPacket packet, NetPeer from)
        {
            if (host)
            {
                bool same = packet.Version == Plugin.Version;
                var answer = new HorseHelloPacket { Version = Plugin.Version, Welcome = same };
                network.SendDataToPeer(ref answer, DeliveryMethod.ReliableOrdered, from);
                if (same) peers.Add(from);
                else HorseNet.NotShared($"A player has Tushonka Horse {packet.Version} and this host has {Plugin.Version}; their horses won't be shared.");
                return;
            }
            if (packet.Welcome)
            {
                welcomed = true;
                Plugin.Log.LogInfo("The host has the same Tushonka Horse version: horses are shared.");
            }
            else if (!welcomed && !refused)
            {
                refused = true;
                HorseNet.NotShared($"The host has Tushonka Horse {packet.Version}, but you have {Plugin.Version}; horses won't be shared.");
            }
        }

        public void Send(HorseState state, bool reliable)
        {
            var packet = new HorseStatePacket { State = state };
            DeliveryMethod delivery = reliable ? DeliveryMethod.ReliableOrdered : DeliveryMethod.Sequenced;
            if (host) Relay(ref packet, delivery, null);
            else network.SendData(ref packet, delivery, false);
        }

        private void State(HorseStatePacket packet, NetPeer from)
        {
            if (host)
            {
                if (!peers.Contains(from)) return;
                owners[packet.State.Owner] = from;
                Relay(ref packet, packet.State.Spawned ? DeliveryMethod.Sequenced : DeliveryMethod.ReliableOrdered, from);
            }
            if (!Headless) HorseNet.Received(packet.State);
        }

        private void Relay(ref HorseStatePacket packet, DeliveryMethod delivery, NetPeer except)
        {
            foreach (NetPeer peer in peers)
                if (peer != except) network.SendDataToPeer(ref packet, delivery, peer);
        }
    }

    public struct HorseHelloPacket : INetSerializable
    {
        public string Version;
        public bool Welcome;
        public readonly void Serialize(NetDataWriter writer)
        {
            writer.Put(Version ?? "", 0);
            writer.Put(Welcome);
        }
        public void Deserialize(NetDataReader reader)
        {
            Version = reader.GetString();
            Welcome = reader.GetBool();
        }
    }

    public struct HorseStatePacket : INetSerializable
    {
        public HorseState State;
        public readonly void Serialize(NetDataWriter writer)
        {
            writer.Put(State.Owner);
            writer.PutUnmanaged(State.Position);
            writer.Put(State.Yaw);
            writer.Put(State.Pitch);
            writer.Put(State.Scale);
            writer.Put(State.Coat);
            writer.Put(State.Clip ?? "", 0);
            writer.Put(State.Layer ?? "", 0);
            writer.Put(State.LayerWeight);
            writer.Put(State.Progress);
            writer.Put(State.Playback);
            writer.Put(State.RootY);
            writer.Put((byte)((State.Spawned ? 1 : 0) | (State.Looping ? 2 : 0)));
        }
        public void Deserialize(NetDataReader reader)
        {
            State.Owner = reader.GetInt();
            State.Position = reader.GetUnmanaged<Vector3>();
            State.Yaw = reader.GetFloat();
            State.Pitch = reader.GetFloat();
            State.Scale = reader.GetFloat();
            State.Coat = reader.GetByte();
            State.Clip = Named(reader.GetString());
            State.Layer = Named(reader.GetString());
            State.LayerWeight = reader.GetFloat();
            State.Progress = reader.GetFloat();
            State.Playback = reader.GetFloat();
            State.RootY = reader.GetFloat();
            byte flags = reader.GetByte();
            State.Spawned = (flags & 1) != 0;
            State.Looping = (flags & 2) != 0;
        }
        private static string Named(string value) => string.IsNullOrEmpty(value) ? null : value;
    }
}
