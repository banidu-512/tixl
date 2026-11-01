#nullable enable
using System;
using Newtonsoft.Json.Linq;
using T3.Core.Show;
using T3.Editor.Collab;

namespace T3.Editor.Show;

/// <summary>
/// Carries the failover health/lease channel over the collaboration session. The active machine pings at
/// the heartbeat cadence; the standby answers every ping with the sequence it heard. Two independent
/// channels — multicast UDP and this TCP session — are deliberate: a broken multicast path (IGMP, Wi-Fi,
/// a pinned Linux socket) must never look like a dead master, and a broken session must never look like
/// a dead master either. Only when both fall silent does the timeout decide.
/// </summary>
internal static class FailoverHealthLink
{
    public const string PingMessageType = "failoverPing";
    public const string AckMessageType = "failoverAck";

    /// <summary>Active side, called every editor frame while hosting.</summary>
    public static void Tick(CollabManager collab)
    {
        if (!FailoverArbiter.IsEnabled || FailoverArbiter.Role != FailoverArbiter.NodeRole.Active)
            return;

        var now = Environment.TickCount64;
        if (now < _nextPingTicks)
            return;

        _nextPingTicks = now + PingIntervalMs;

        if (collab.State != CollabManager.SessionState.Hosting || collab.Peers.Count == 0)
            return;

        var snapshot = FailoverArbiter.GetSnapshot();
        collab.BroadcastJson(new
                                 {
                                     t = PingMessageType,
                                     id = snapshot.NodeId,
                                     prio = snapshot.Priority,
                                     active = snapshot.AdvertisesActive,
                                     epoch = snapshot.Epoch,
                                     seq = snapshot.Seq,
                                     frame = snapshot.Frame,
                                     health = snapshot.Health,
                                     armed = snapshot.Armed,
                                     standdown = snapshot.Standdown,
                                     mute = snapshot.Mute,
                                 });
    }

    /// <summary>Standby side: the active node's liveness/lease ping.</summary>
    public static void ApplyPing(JObject message, CollabManager collab)
    {
        if (!FailoverArbiter.IsEnabled)
            return;

        var snapshot = new FailoverArbiter.NodeSnapshot(message.Value<string>("id") ?? "",
                                                        message.Value<int>("prio"),
                                                        message.Value<bool>("active"),
                                                        message.Value<long>("epoch"),
                                                        message.Value<long>("seq"),
                                                        message.Value<long>("frame"),
                                                        message.Value<int>("health"),
                                                        message.Value<bool>("armed"),
                                                        message.Value<bool>("standdown"),
                                                        message.Value<int>("mute"));
        FailoverArbiter.NotifyPeerPing(snapshot);

        // The ack is the active node's lease: it proves this machine can hear it.
        if (collab.State == CollabManager.SessionState.Connected)
            collab.SendToHost(new { t = AckMessageType, seq = snapshot.Seq, id = FailoverArbiter.NodeIdText });
    }

    /// <summary>Active side: a standby acknowledged a ping (or a UDP heartbeat) over the session.</summary>
    public static void ApplyAck(JObject message)
    {
        if (!FailoverArbiter.IsEnabled)
            return;

        FailoverArbiter.NotifyPeerAck(message.Value<long>("seq"), message.Value<string>("id") ?? "");
    }

    private const long PingIntervalMs = 50;
    private static long _nextPingTicks;
}
