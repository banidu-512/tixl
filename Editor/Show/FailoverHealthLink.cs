#nullable enable
using System;
using System.Buffers;
using System.Text.Json;
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
        _pingBuffer.Clear();
        _pingWriter.Reset(_pingBuffer);
        _pingWriter.WriteStartObject();
        _pingWriter.WriteString("t", PingMessageType);
        _pingWriter.WriteString("id", snapshot.NodeId);
        _pingWriter.WriteNumber("prio", snapshot.Priority);
        _pingWriter.WriteBoolean("active", snapshot.AdvertisesActive);
        _pingWriter.WriteNumber("epoch", snapshot.Epoch);
        _pingWriter.WriteNumber("seq", snapshot.Seq);
        _pingWriter.WriteNumber("frame", snapshot.Frame);
        _pingWriter.WriteNumber("health", snapshot.Health);
        _pingWriter.WriteBoolean("armed", snapshot.Armed);
        _pingWriter.WriteBoolean("standdown", snapshot.Standdown);
        _pingWriter.WriteNumber("mute", snapshot.Mute);
        _pingWriter.WriteEndObject();
        _pingWriter.Flush();
        collab.BroadcastEncodedJson(_pingBuffer.WrittenSpan);
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
        {
            _ackBuffer.Clear();
            _ackWriter.Reset(_ackBuffer);
            _ackWriter.WriteStartObject();
            _ackWriter.WriteString("t", AckMessageType);
            _ackWriter.WriteNumber("seq", snapshot.Seq);
            _ackWriter.WriteString("id", FailoverArbiter.NodeIdText);
            _ackWriter.WriteEndObject();
            _ackWriter.Flush();
            collab.SendEncodedToHost(_ackBuffer.WrittenSpan);
        }
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

    /** Reused on the UI thread: the ping runs 20 times a second and must not feed the garbage collector. */
    private static readonly ArrayBufferWriter<byte> _pingBuffer = new(256);

    private static readonly Utf8JsonWriter _pingWriter = new(_pingBuffer);
    private static readonly ArrayBufferWriter<byte> _ackBuffer = new(128);
    private static readonly Utf8JsonWriter _ackWriter = new(_ackBuffer);
}
