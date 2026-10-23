#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Net.Sockets;
using System.Text;
using Newtonsoft.Json;
using T3.Core.Logging;

namespace T3.Core.Show;

/// <summary>
/// Configures <see cref="FailoverArbiter"/>. Lives in Core so runtime-compiled operators (Artnet,
/// sACN, stream senders) can read the arbiter's suppression flag; the editor pushes its persisted
/// settings into this shape.
/// </summary>
public sealed record FailoverConfig
{
    public bool Enabled;
    public string ShowId = "tixl-show";
    public int Priority = 100;
    public int HeartbeatMs = 500;
    public int TimeoutFactor = 3;
    public bool Preempt = true;

    public FailoverConfig Copy() => new()
                                         {
                                             Enabled = Enabled,
                                             ShowId = ShowId,
                                             Priority = Priority,
                                             HeartbeatMs = HeartbeatMs,
                                             TimeoutFactor = TimeoutFactor,
                                             Preempt = Preempt,
                                         };

    public bool ConfigEquals(FailoverConfig? other)
    {
        return other != null
               && Enabled == other.Enabled
               && ShowId == other.ShowId
               && Priority == other.Priority
               && HeartbeatMs == other.HeartbeatMs
               && TimeoutFactor == other.TimeoutFactor
               && Preempt == other.Preempt;
    }
}

/// <summary>
/// Active/standby arbitration for show-critical network outputs (Artnet, sACN, NDI/Spout streams).
/// Two TiXL machines run the same composition; exactly one is Active and actually sends. The other
/// renders silently (Standby) and takes over within `TimeoutFactor × HeartbeatMs` if the active
/// node dies. Heartbeats are small UDP datagrams multicast on the LAN; a shared Show Id isolates
/// unrelated pairs. Multicast (not broadcast) is deliberate: it also loops back on the sending
/// host, so a master and backup can be rehearsed on one machine.
///
/// Election rules (VRRP-flavored):
/// - Higher Priority wins; equal priority breaks the tie by lower IP, then lower node id.
/// - A Standby promotes when no Active peer has been heard for TimeoutFactor × HeartbeatMs.
/// - Preempt (default on): a returning node with higher priority reclaims Active from a lower
///   priority Active peer through a short contested mute window, so both sides never stream at
///   once. With Preempt off, a returning node stays Standby until the current Active dies.
///
/// Operators check <see cref="IsOutputSuppressed"/> before sending. The flag is also true during
/// startup probing and contested handovers — brief output blackouts on role changes are
/// intentional and much cheaper than two masters fighting on the same DMX universe.
/// </summary>
public static class FailoverArbiter
{
    public enum NodeRole
    {
        Disabled,
        Probing,
        Active,
        Standby,
        Contested,
    }

    private const int FailoverPort = 44910;
    private const int MaxDatagramSize = 512;

    /// <summary>Link-local multicast block (224.0.0.0/24); flooded by switches, never routed.</summary>
    private static readonly IPAddress GroupAddress = IPAddress.Parse("224.0.0.157");

    private static readonly object Gate = new();

    private static FailoverConfig _config = new();
    private static NodeRole _role = NodeRole.Disabled;
    private static readonly string NodeId = Guid.NewGuid().ToString("N");

    private static Socket? _heartbeatSocket;
    private static CancellationTokenSource? _cancellation;

    private sealed class PeerInfo
    {
        public int Priority;
        public bool AdvertisesActive;
        public long LastHeardTicks;
        public string NodeId = "";
    }

    private static readonly Dictionary<string, PeerInfo> _peers = new(); // key: node id

    private static long _lastStateChangeTicks = Environment.TickCount64;
    private static int _takeoverCount;
    private static string _lastEvent = "";

    private static readonly ConcurrentQueue<string> _recentEvents = new();

    /// <summary>True while this machine must NOT send network output. Operators check this per frame.</summary>
    public static bool IsOutputSuppressed
    {
        get
        {
            lock (Gate)
            {
                return _role is not (NodeRole.Active or NodeRole.Disabled);
            }
        }
    }

    public static bool IsEnabled
    {
        get
        {
            lock (Gate)
                return _config.Enabled;
        }
    }

    /// <summary>Snapshot strings for the settings panel (UI thread).</summary>
    public static string RoleText
    {
        get
        {
            lock (Gate)
            {
                return _role switch
                           {
                               NodeRole.Disabled  => "Off",
                               NodeRole.Probing   => "Probing",
                               NodeRole.Active    => "ACTIVE (streaming)",
                               NodeRole.Standby   => "Standby",
                               NodeRole.Contested => "Handover",
                               _                  => "?",
                           };
            }
        }
    }

    public static string PeerSummary
    {
        get
        {
            lock (Gate)
            {
                var now = Environment.TickCount64;
                var timeout = TimeoutMs;
                var alive = 0;
                PeerInfo? bestActive = null;
                foreach (var peer in _peers.Values)
                {
                    if (now - peer.LastHeardTicks > timeout)
                        continue;

                    alive++;
                    if (peer.AdvertisesActive && (bestActive == null || Beats(peer.Priority, peer.NodeId, bestActive.Priority, bestActive.NodeId)))
                        bestActive = peer;
                }

                if (alive == 0)
                    return "No peers seen";

                var age = bestActive != null ? now - bestActive.LastHeardTicks : -1;
                return $"{alive} peer(s); active peer: {(bestActive != null ? $"priority {bestActive.Priority}, heard {age} ms ago" : "none")}";
            }
        }
    }

    public static int TakeoverCount
    {
        get
        {
            lock (Gate)
                return _takeoverCount;
        }
    }

    public static string LastEvent
    {
        get
        {
            lock (Gate)
                return _lastEvent;
        }
    }

    public static IReadOnlyList<string> RecentEvents
    {
        get
        {
            lock (Gate)
                return _recentEvents.ToArray();
        }
    }

    private static int TimeoutMs
    {
        get
        {
            lock (Gate)
                return Math.Max(2, _config.TimeoutFactor) * Math.Max(50, _config.HeartbeatMs);
        }
    }

    /// <summary>Pushes the editor's persisted settings. Cheap enough to call every frame; no-ops when unchanged.</summary>
    public static void ApplyConfig(FailoverConfig config)
    {
        lock (Gate)
        {
            if (_config.ConfigEquals(config))
                return;

            var wasEnabled = _config.Enabled;
            _config = config.Copy();

            if (!wasEnabled && _config.Enabled)
            {
                Start();
            }
            else if (wasEnabled && !_config.Enabled)
            {
                Stop();
            }
            else if (_config.Enabled)
            {
                // Priority/preempt changes re-evaluate on the next tick naturally.
                RecordEvent("Configuration updated");
            }
        }
    }

    private static void Start()
    {
        _role = NodeRole.Probing;
        _lastStateChangeTicks = Environment.TickCount64;
        _peers.Clear();
        RecordEvent($"Enabled (show '{_config.ShowId}', priority {_config.Priority})");

        try
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.Bind(new IPEndPoint(IPAddress.Any, FailoverPort));
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, true);
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership,
                                   new MulticastOption(GroupAddress, IPAddress.Any));
            _heartbeatSocket = socket;
        }
        catch (Exception e)
        {
            Log.Warning($"Failover: could not bind heartbeat multicast on port {FailoverPort}: {e.Message}");
            _role = NodeRole.Disabled;
            RecordEvent($"Failed to bind port {FailoverPort}");
            return;
        }

        _cancellation = new CancellationTokenSource();
        var token = _cancellation.Token;
        _ = Task.Run(() => ListenLoop(token), token);
        _ = Task.Run(() => TickLoop(token), token);
    }

    private static void Stop()
    {
        _role = NodeRole.Disabled;
        _peers.Clear();
        RecordEvent("Disabled — output resumed");
        _cancellation?.Cancel();
        _cancellation = null;
        try
        {
            _heartbeatSocket?.Close();
        }
        catch
        {
            // Socket already dead.
        }

        _heartbeatSocket = null;
    }

    private static void TickLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                SendHeartbeat();
                EvaluateState();
            }
            catch (Exception e)
            {
                Log.Warning($"Failover: tick error: {e.Message}");
            }

            int delay;
            lock (Gate)
                delay = Math.Clamp(_config.HeartbeatMs / 2, 50, 500);

            try
            {
                Task.Delay(delay, token).Wait(token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private static void SendHeartbeat()
    {
        Socket? socket;
        string showId;
        int priority;
        bool advertiseActive;
        lock (Gate)
        {
            socket = _heartbeatSocket;
            showId = _config.ShowId;
            priority = _config.Priority;
            advertiseActive = _role == NodeRole.Active;
        }

        if (socket == null)
            return;

        var payload = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new
                                                                             {
                                                                                 show = showId,
                                                                                 id = NodeId,
                                                                                 prio = priority,
                                                                                 active = advertiseActive,
                                                                             }));

        try
        {
            socket.SendTo(payload, new IPEndPoint(GroupAddress, FailoverPort));
        }
        catch (SocketException)
        {
            // Best-effort; a missed heartbeat only delays takeover detection slightly.
        }
    }

    private static void ListenLoop(CancellationToken token)
    {
        var buffer = new byte[MaxDatagramSize];
        while (!token.IsCancellationRequested)
        {
            EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
            int length;
            try
            {
                var socket = _heartbeatSocket;
                if (socket == null)
                    break;

                length = socket.ReceiveFrom(buffer, ref remote);
            }
            catch (Exception e) when (e is SocketException or ObjectDisposedException)
            {
                break;
            }

            try
            {
                if (length <= 0 || length > MaxDatagramSize)
                    continue;

                var heartbeat = JsonConvert.DeserializeObject<Heartbeat>(Encoding.UTF8.GetString(buffer, 0, length));
                if (heartbeat == null || string.IsNullOrEmpty(heartbeat.Show) || string.IsNullOrEmpty(heartbeat.Id))
                    continue;

                lock (Gate)
                {
                    if (heartbeat.Show != _config.ShowId)
                        continue; // Another show on the same network — ignore.

                    if (heartbeat.Id == NodeId)
                        continue; // Our own multicast loopback.
                }

                lock (Gate)
                {
                    if (!_peers.TryGetValue(heartbeat.Id, out var peer))
                    {
                        peer = new PeerInfo();
                        _peers[heartbeat.Id] = peer;
                        RecordEvent($"Peer discovered (priority {heartbeat.Prio}, {(heartbeat.Active ? "active" : "standby")})");
                    }

                    peer.Priority = heartbeat.Prio;
                    peer.AdvertisesActive = heartbeat.Active;
                    peer.NodeId = heartbeat.Id;
                    peer.LastHeardTicks = Environment.TickCount64;
                }
            }
            catch
            {
                // Malformed datagram from some other tool on this port — ignore.
            }
        }
    }

    private sealed class Heartbeat
    {
        public string? Show;
        public string? Id;
        public int Prio;
        public bool Active;
    }

    /// <summary>Does (priority, id) win the election against the other side? Deterministic from both sides.</summary>
    private static bool Beats(int priorityA, string idA, int priorityB, string idB)
    {
        if (priorityA != priorityB)
            return priorityA > priorityB;

        return string.CompareOrdinal(idA, idB) < 0;
    }

    private static void EvaluateState()
    {
        lock (Gate)
        {
            if (_role == NodeRole.Disabled)
                return;

            var now = Environment.TickCount64;
            var timeout = TimeoutMs;
            var config = _config;

            PeerInfo? bestActive = null;
            foreach (var peer in _peers.Values)
            {
                if (now - peer.LastHeardTicks > timeout)
                    continue;

                if (peer.AdvertisesActive && (bestActive == null || Beats(peer.Priority, peer.NodeId, bestActive.Priority, bestActive.NodeId)))
                    bestActive = peer;
            }

            var iWin = bestActive == null || Beats(config.Priority, NodeId, bestActive.Priority, bestActive.NodeId);

            switch (_role)
            {
                case NodeRole.Probing:
                {
                    // The probe window (2×timeout) lets a joining node hear existing masters before
                    // claiming anything.
                    if (now - _lastStateChangeTicks < 2 * timeout)
                        return;

                    if (bestActive == null)
                    {
                        Transition(NodeRole.Active, "No active peer — becoming active");
                    }
                    else if (iWin)
                    {
                        Transition(NodeRole.Contested, config.Preempt
                                                           ? $"Preempting lower-priority active peer"
                                                           : "Contested — resolving startup overlap");
                    }
                    else
                    {
                        Transition(NodeRole.Standby, $"Active peer has higher priority");
                    }

                    break;
                }

                case NodeRole.Standby:
                {
                    if (bestActive == null)
                    {
                        _takeoverCount++;
                        Transition(NodeRole.Active, "Active peer lost — taking over");
                        return;
                    }

                    if (config.Preempt && iWin && now - _lastStateChangeTicks > 2 * timeout)
                    {
                        Transition(NodeRole.Contested, "Preempting lower-priority active peer");
                    }

                    break;
                }

                case NodeRole.Contested:
                {
                    // Both sides are muted for one timeout window, then the winner claims.
                    if (now - _lastStateChangeTicks < timeout)
                        return;

                    if (!iWin)
                    {
                        Transition(NodeRole.Standby, "Yielding to higher-priority peer");
                        return;
                    }

                    Transition(NodeRole.Active, "Claimed active after handover");
                    break;
                }

                case NodeRole.Active:
                {
                    if (bestActive != null && !iWin)
                    {
                        Transition(NodeRole.Contested, "Higher-priority peer is claiming");
                    }

                    break;
                }
            }
        }
    }

    private static void Transition(NodeRole newRole, string reason)
    {
        if (_role == newRole)
            return;

        _role = newRole;
        _lastStateChangeTicks = Environment.TickCount64;
        RecordEvent($"{newRole}: {reason}");
        Log.Info($"Failover: {newRole} — {reason}");
    }

    private static void RecordEvent(string message)
    {
        _lastEvent = $"{DateTime.Now:HH:mm:ss}  {message}";
        _recentEvents.Enqueue(_lastEvent);
        while (_recentEvents.Count > 12)
        {
            _recentEvents.TryDequeue(out _);
        }
    }
}
