#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using System.Net.Sockets;
using System.Text;
using Newtonsoft.Json;
using T3.Core.Logging;
using T3.Core.Utils;

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

    /// <summary>Heartbeat interval. A heartbeat goes out every half interval, so this also sets the
    /// cadence at which the takeover timeout is measured in multiples of.</summary>
    public int HeartbeatMs = 100;

    public int TimeoutFactor = 3;
    public bool Preempt = true;

    /// <summary>How long this node may go without producing a rendered output frame before it steps
    /// down as broken. This is what turns "alive but black" into a takeover. 0 disables the check.</summary>
    public int StallMs = 300;

    /// <summary>Renew a short lease with the standby and mute immediately when it is lost, so a
    /// promotion can never put two masters on the wire. Disable only when running without a standby.</summary>
    public bool Fencing = true;

    /// <summary>TCP port of the collaboration session the active node hosts for its standbys.
    /// The editor pushes the collab settings' port; the default mirrors CollabProtocol.DefaultTcpPort
    /// (Core cannot reference the editor protocol).</summary>
    public int CollabPort = 44700;

    /// <summary>Adapter to send and receive heartbeats on. Empty = every usable interface.</summary>
    public string NetworkInterfaceName = "";

    public FailoverConfig Copy() => new()
                                         {
                                             Enabled = Enabled,
                                             ShowId = ShowId,
                                             Priority = Priority,
                                             HeartbeatMs = HeartbeatMs,
                                             TimeoutFactor = TimeoutFactor,
                                             Preempt = Preempt,
                                             StallMs = StallMs,
                                             Fencing = Fencing,
                                             CollabPort = CollabPort,
                                             NetworkInterfaceName = NetworkInterfaceName,
                                         };

    public bool ConfigEquals(FailoverConfig? other)
    {
        return other != null
               && Enabled == other.Enabled
               && ShowId == other.ShowId
               && Priority == other.Priority
               && HeartbeatMs == other.HeartbeatMs
               && TimeoutFactor == other.TimeoutFactor
               && Preempt == other.Preempt
               && StallMs == other.StallMs
               && Fencing == other.Fencing
               && CollabPort == other.CollabPort
               && string.Equals(NetworkInterfaceName, other.NetworkInterfaceName, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Active/standby arbitration for show-critical network outputs (Artnet, sACN, NDI/Spout streams).
/// Two TiXL machines run the same composition; exactly one is Active and actually sends. The other
/// renders silently (Standby) and takes over when the active node stops producing or dies.
///
/// Detection uses two independent principles:
/// - <b>Declared failure</b>: the active node watches itself. A frozen render (<see cref="NotifyFrameAdvanced"/>)
///   or an output path error (<see cref="ReportOutputFault"/>) steps it down and announces that on the next
///   heartbeat, so the standby promotes in one frame. This is the instant path and it cannot be a false
///   positive — the failing machine is the one deciding.
/// - <b>Fenced inference</b>: silence alone is ambiguous (death, cable, IGMP filtering, a frozen render).
///   The standby promotes at <c>TimeoutFactor × HeartbeatMs</c>, but only after the active node has had
///   time to mute itself under the lease below. A takeover therefore costs at most a short blackout, never
///   two masters on the same DMX universe.
///
/// Lease/fencing: heartbeats carry a sequence number, and every peer answers with an ack for the sequence
/// it heard. A node that was Active while an armed standby was present mutes itself at <see cref="MuteMs"/>
/// without an ack — before the standby's timeout can expire. Because an ack can only be produced by a node
/// that received the heartbeat, a failure in either direction ends with the old master muted. A standby
/// that exited cleanly (socket closed while its heartbeats stopped) disarms the lease, so the remaining
/// machine keeps streaming.
///
/// Election rules (VRRP-flavored, plus a term number):
/// - Higher Priority wins; equal priority breaks the tie by lower node id.
/// - Every claim increments <c>epoch</c> above the highest epoch heard. A node that hears a strictly newer
///   claim while Active mutes and contests, so a partition heal can never leave two streaming nodes.
/// - A node that just stopped being Active holds down (does not preempt) for <see cref="StanddownMs"/>,
///   and a node whose own output is broken never claims.
///
/// Operators check <see cref="IsOutputSuppressed"/> before sending. The flag is also true during
/// startup probing, contested handovers and while fenced — brief output blackouts on role changes are
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

        /// <summary>This node's own output pipeline is broken; it is muted and must not claim.</summary>
        Faulted,

        /// <summary>The standby stopped answering while this node was Active. Muted until the lease is
        /// renewed or the operator resumes solo output — this is what prevents a partition from producing
        /// two masters.</summary>
        Fenced,
    }

    public enum OutputHealth
    {
        Ok = 0,
        Stalled = 1,
        Faulted = 2,
    }

    /// <summary>Everything one node advertises about itself. Shared by the UDP heartbeat, the collaboration
    /// health ping and the settings panel, so all channels tell the same story.</summary>
    public readonly record struct NodeSnapshot(string NodeId,
                                               int Priority,
                                               bool AdvertisesActive,
                                               long Epoch,
                                               long Seq,
                                               long Frame,
                                               int Health,
                                               bool Armed,
                                               bool Standdown,
                                               int Mute);

    private const int FailoverPort = 44910;
    private const int MaxDatagramSize = 512;

    /// <summary>A show has a handful of nodes; anything beyond this is a flood of forged ids.</summary>
    private const int MaxTrackedPeers = 64;

    /// <summary>How long a fault is held before a recovered node may rejoin the election.</summary>
    private const int RecoveryMs = 2000;

    /// <summary>No preemption for this long after a node stopped being Active. Stops a machine that
    /// rebooted (or faulted and recovered) from yanking a working show back to itself instantly.</summary>
    private const int StanddownMs = 5000;

    /// <summary>Link-local multicast block (224.0.0.0/24); flooded by switches, never routed.</summary>
    private static readonly IPAddress GroupAddress = IPAddress.Parse("224.0.0.157");

    private static readonly object Gate = new();

    private static FailoverConfig _config = new();

    /// <summary>Written under <see cref="Gate"/>; volatile so the per-frame suppression check needs no lock.</summary>
    private static volatile NodeRole _role = NodeRole.Disabled;
    private static readonly string NodeId = Guid.NewGuid().ToString("N");

    /// <summary>
    /// One receive/send socket per usable IPv4 interface. Membership pinned per interface is what
    /// makes the arbiter work on multi-homed machines: with a single IPAddress.Any membership the
    /// OS picks one default route and heartbeat datagrams arriving on every other NIC (WSL, Hyper-V,
    /// Docker, VPN adapters) are silently dropped — the classic cause of a false "no active peer"
    /// and a dual-active pair streaming the same DMX universe.
    /// </summary>
    private static readonly List<Socket> _heartbeatSockets = new();
    private static CancellationTokenSource? _cancellation;

    private sealed class PeerInfo
    {
        public int Priority;
        public bool AdvertisesActive;
        public long LastHeardTicks;
        public string NodeId = "";

        /// <summary>Source address of the peer's heartbeats plus the collab port it advertises,
        /// so a standby knows where to follow the active master.</summary>
        public string Address = "";
        public int CollabPort;
        public string Host = "";

        // Health/election facts the peer advertises.
        public long Epoch;
        public OutputHealth Health = OutputHealth.Ok;
        public bool Armed;
        public bool Standdown;
        public int MuteMs;
        public long LastAckedSeq;

        /// <summary>True once this peer advertised itself Active, so a later yield can be told from a
        /// peer that was never a master.</summary>
        public bool WasActive;
    }

    private static readonly Dictionary<string, PeerInfo> _peers = new(); // key: node id
    private static string _lastPeerHost = "";

    private static long _lastStateChangeTicks = Environment.TickCount64;
    private static int _takeoverCount;
    private static string _lastEvent = "";

    private static readonly ConcurrentQueue<string> _recentEvents = new();

    // --- elections -------------------------------------------------------------------------
    /// <summary>Term of this node's current/last claim. A claim is only valid above the highest epoch heard.</summary>
    private static long _epoch;
    private static long _maxSeenEpoch;
    private static long _standdownUntilTicks;
    private static bool _forceTakeover;

    // --- liveness / lease ------------------------------------------------------------------
    private static long _heartbeatSeq;
    private static long _lastAckTicks;
    private static long _lastPartnerPresenceTicks;

    /// <summary>True while an armed peer (one that could promote) has been heard since this node became
    /// Active. Reset when it exits cleanly, so a dead standby never mutes the master.</summary>
    private static bool _armedPartnerPresent;
    private static bool _partnerExitedCleanly;

    /// <summary>When the collaboration session to the other machine was lost. Used together with the
    /// heartbeat silence to tell "the process exited" from "only the session broke".</summary>
    private static long _sessionClosedTicks;

    /// <summary>The last time a peer that advertises itself Active was heard, on any channel.</summary>
    private static long _lastActivePeerHeardTicks;
    private static int _lastActivePeerMuteMs;

    /// <summary>When the last active peer announced that it yielded (fault, step-down, fence). A declared
    /// handover promotes immediately; it must not wait for the silence timeout.</summary>
    private static long _lastActivePeerYieldTicks;

    /// <summary>Set while Fenced. The per-frame output gate reads it without taking the lock.</summary>
    private static int _leaseExpired;

    // --- self health -----------------------------------------------------------------------
    private static long _lastFaultReportTicks;
    private static string _faultReason = "";
    private static bool _frameClockStarted;
    private static long _lastFrameAdvanceTicks;
    private static long _frameCounter;

    /// <summary>
    /// Node-id derived claim offset: two machines that start together would otherwise cross their claim
    /// heartbeats and both stream for one evaluation tick. The offset separates the claims by a few tens
    /// of milliseconds, and the first claim is answered immediately by the other node.
    /// </summary>
    private static readonly int ClaimJitterMs = (NodeId.GetHashCode() & 0x7FFFFFFF) % 60;

    private static bool _sendPending;

    /// <summary>Send failures across all heartbeat sockets in a row; triggers a re-bind when the NIC moved.</summary>
    private static int _consecutiveSendFailures;
    private static long _nextSocketCheckTicks;

    /// <summary>True while this machine must NOT send network output. Operators check this per frame.</summary>
    public static bool IsOutputSuppressed
    {
        get
        {
            var role = _role;
            if (role is NodeRole.Disabled)
                return false;

            if (role is not NodeRole.Active)
                return true;

            // The tick loop may not have moved the role to Fenced yet; the lease flag is written first.
            return Volatile.Read(ref _leaseExpired) != 0;
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

    public static NodeRole Role
    {
        get
        {
            lock (Gate)
                return _role;
        }
    }

    public static string NodeIdText => NodeId;

    /// <summary>
    /// Where the best live active peer advertises its collaboration session — the source address
    /// of its heartbeats plus the collab port from the payload — or null while none is heard.
    /// The editor's failover link dials this so the standby follows the active master.
    /// </summary>
    public static (string Address, int Port)? ActivePeerEndpoint
    {
        get
        {
            lock (Gate)
            {
                var now = Environment.TickCount64;
                var timeout = TimeoutMs;
                PeerInfo? bestActive = null;
                foreach (var peer in _peers.Values)
                {
                    if (now - peer.LastHeardTicks > timeout)
                        continue;

                    if (peer.AdvertisesActive
                        && (bestActive == null || Beats(peer.Priority, peer.NodeId, bestActive.Priority, bestActive.NodeId)))
                        bestActive = peer;
                }

                if (bestActive == null || bestActive.Address.Length == 0 || bestActive.CollabPort <= 0)
                    return null;

                return (bestActive.Address, bestActive.CollabPort);
            }
        }
    }

    /// <summary>Source addresses of the peers whose heartbeats are currently heard.</summary>
    public static void GetLivePeerAddresses(List<string> addresses)
    {
        lock (Gate)
        {
            var now = Environment.TickCount64;
            var timeout = TimeoutMs;
            foreach (var peer in _peers.Values)
            {
                if (now - peer.LastHeardTicks <= timeout && peer.Address.Length > 0)
                    addresses.Add(peer.Address);
            }
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
                               NodeRole.Faulted   => "FAULT (output suppressed)",
                               NodeRole.Fenced    => "FENCED (partner unreachable)",
                               _                  => "?",
                           };
            }
        }
    }

    /// <summary>Self health, for the settings panel.</summary>
    public static string HealthText
    {
        get
        {
            lock (Gate)
            {
                return ComputeHealth(Environment.TickCount64) switch
                           {
                               OutputHealth.Ok      => "OK",
                               OutputHealth.Stalled => $"STALLED ({Environment.TickCount64 - _lastFrameAdvanceTicks} ms without a frame)",
                               _                    => string.IsNullOrEmpty(_faultReason) ? "FAULT" : $"FAULT: {_faultReason}",
                           };
            }
        }
    }

    /// <summary>Lease state, for the settings panel.</summary>
    public static string LeaseText
    {
        get
        {
            lock (Gate)
            {
                if (!_config.Fencing)
                    return "Fencing off";

                if (!_armedPartnerPresent)
                    return "No armed partner";

                if (_partnerExitedCleanly)
                    return "Partner exited cleanly";

                var age = Environment.TickCount64 - _lastAckTicks;
                return $"Lease renewed {age} ms ago (mute at {MuteMs} ms)";
            }
        }
    }

    /// <summary>
    /// The machine name of the partner node heard most recently, or null. A stream sender uses it to tell its
    /// receivers where to fail over: a network source is named after the machine that sends it.
    /// </summary>
    public static string? PeerHostName
    {
        get
        {
            lock (Gate)
            {
                var now = Environment.TickCount64;
                var timeout = TimeoutMs;
                PeerInfo? best = null;
                foreach (var peer in _peers.Values)
                {
                    if (now - peer.LastHeardTicks > timeout || string.IsNullOrEmpty(peer.Host))
                        continue;

                    if (best == null || peer.LastHeardTicks > best.LastHeardTicks)
                        best = peer;
                }

                // A partner that is gone right now is still the one to fail over to: a node that took over from
                // a dead master must point its receivers back at it, so they return when the master comes back.
                return best?.Host ?? (_lastPeerHost.Length > 0 ? _lastPeerHost : null);
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
                var where = bestActive != null && bestActive.Address.Length > 0 ? $" at {bestActive.Address}," : "";
                var state = bestActive != null
                                ? $"priority {bestActive.Priority}, epoch {bestActive.Epoch}, {HealthLabel(bestActive.Health)},{where} heard {age} ms ago"
                                : "none";
                return $"{alive} peer(s); active peer: {state}";
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

    /// <summary>Everything this node advertises, for the collab health ping and for tests.</summary>
    public static NodeSnapshot GetSnapshot()
    {
        lock (Gate)
            return GetSnapshotLocked(Environment.TickCount64);
    }

    private static NodeSnapshot GetSnapshotLocked(long now)
    {
        var health = ComputeHealth(now);
        var advertiseActive = _role is NodeRole.Active or NodeRole.Contested;
        var standdown = !advertiseActive && now < _standdownUntilTicks;
        var armed = health == OutputHealth.Ok
                    && !standdown
                    && IsArmedRole();

        return new NodeSnapshot(NodeId, _config.Priority, advertiseActive, _epoch, _heartbeatSeq,
                                _frameCounter, (int)health, armed, standdown, MuteMs);
    }

    private static bool IsArmedRole() => _role is NodeRole.Standby or NodeRole.Probing;

    private static int TimeoutMs
    {
        get
        {
            lock (Gate)
                return Math.Max(2, _config.TimeoutFactor) * Math.Max(50, _config.HeartbeatMs);
        }
    }

    private static int SendCadenceMs => Math.Clamp(_config.HeartbeatMs / 2, 20, 500);

    /// <summary>How long an Active node waits for an ack before it fences itself. Always shorter than the
    /// standby's takeover timeout, so the old master is muted before a promotion can happen.</summary>
    private static int MuteMs
    {
        get
        {
            var timeout = TimeoutMs;
            return Math.Max(50, timeout - Math.Max(2 * SendCadenceMs, timeout / 3));
        }
    }

    // -------------------------------------------------------------------------------------------
    // Editor-facing control
    // -------------------------------------------------------------------------------------------

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

    /// <summary>
    /// Called once per rendered frame, before any output is evaluated. Detects a render stall at its
    /// boundary: a node that comes back from a freeze must not send a single frame while an old role
    /// says Active — that is the zombie-master hazard. The gap check demotes before the outputs run.
    /// </summary>
    public static void NotifyFrameAdvanced()
    {
        var now = Environment.TickCount64;
        lock (Gate)
        {
            if (_role == NodeRole.Disabled)
            {
                _frameClockStarted = true;
                _lastFrameAdvanceTicks = now;
                _frameCounter++;
                return;
            }

            var gap = _frameClockStarted ? now - _lastFrameAdvanceTicks : 0;
            _frameClockStarted = true;
            _lastFrameAdvanceTicks = now;
            _frameCounter++;

            var stallMs = EffectiveStallMs;
            if (stallMs > 0 && gap > stallMs && _role == NodeRole.Active)
            {
                _lastFaultReportTicks = now;
                _faultReason = $"render stalled for {gap} ms";
                Transition(NodeRole.Faulted, $"Render stalled for {gap} ms — stepping down");
            }
        }

        FlushPendingSend();
    }

    /// <summary>
    /// Called by an output path that cannot send (socket failures, a dead stream sender, ...). A hard
    /// fault: the node mutes and announces it, and the standby promotes on the next heartbeat. Callers
    /// should already have debounced (a sustained failure, not a single dropped packet).
    /// </summary>
    public static void ReportOutputFault(string reason)
    {
        var now = Environment.TickCount64;
        lock (Gate)
        {
            if (_role == NodeRole.Faulted && now - _lastFaultReportTicks < 250)
            {
                _lastFaultReportTicks = now;
                return;
            }

            _lastFaultReportTicks = now;
            _faultReason = reason;

            if (_role is NodeRole.Active or NodeRole.Standby or NodeRole.Probing or NodeRole.Contested)
                Transition(NodeRole.Faulted, $"Output fault: {reason}");
        }

        FlushPendingSend();
    }

    /// <summary>Operator: step down as the active node. The standby promotes on the next heartbeat.</summary>
    public static void RequestStepDown(string reason)
    {
        lock (Gate)
        {
            if (_role is NodeRole.Active or NodeRole.Contested)
                Transition(NodeRole.Standby, $"Operator: {reason}");
        }

        FlushPendingSend();
    }

    /// <summary>Operator: on a standby, claim the active role from the current master. Both sides mute for
    /// one contested window, then this node claims with a newer epoch and the old master yields.</summary>
    public static void RequestTakeover(string reason)
    {
        lock (Gate)
        {
            if (_role != NodeRole.Standby || ComputeHealth(Environment.TickCount64) != OutputHealth.Ok)
                return;

            _forceTakeover = true;
            _standdownUntilTicks = 0;
            BeginClaim(NodeRole.Contested, $"Operator requested takeover: {reason}");
        }

        FlushPendingSend();
    }

    /// <summary>Operator override: a fenced master resumes streaming even though its standby stays silent.
    /// Only use it when the standby is known to be gone; a live-but-partitioned standby would then be a
    /// second master on the wire.</summary>
    public static void RequestResumeSolo(string reason)
    {
        lock (Gate)
        {
            if (_role != NodeRole.Fenced)
                return;

            _armedPartnerPresent = false;
            _partnerExitedCleanly = true;
            _standdownUntilTicks = 0;
            Transition(NodeRole.Probing, $"Operator: {reason}");
        }

        FlushPendingSend();
    }

    // -------------------------------------------------------------------------------------------
    // Collaboration health channel (TCP). The lease and the liveness evidence use both channels,
    // so a broken multicast path alone can never cause a failover or a fence.
    // -------------------------------------------------------------------------------------------

    /// <summary>Standby side: the active machine's periodic health ping over the collab session.</summary>
    public static void NotifyPeerPing(NodeSnapshot snapshot)
    {
        if (string.IsNullOrEmpty(snapshot.NodeId) || snapshot.NodeId == NodeId)
            return;

        lock (Gate)
        {
            if (!_config.Enabled)
                return;

            var now = Environment.TickCount64;
            var peer = GetOrAddPeer(snapshot.NodeId);
            if (peer == null)
                return;

            ApplyPeerFacts(peer, snapshot.Priority, snapshot.AdvertisesActive, snapshot.Epoch,
                           snapshot.Health, snapshot.Armed, snapshot.Standdown, snapshot.Mute, now);

            _maxSeenEpoch = Math.Max(_maxSeenEpoch, snapshot.Epoch);
            _lastPartnerPresenceTicks = now;
            _sessionClosedTicks = 0;

            if (snapshot.AdvertisesActive)
            {
                _lastActivePeerHeardTicks = now;
                if (snapshot.Mute > 0)
                    _lastActivePeerMuteMs = snapshot.Mute;
            }

            if (!snapshot.AdvertisesActive)
            {
                if (snapshot.Armed)
                {
                    _armedPartnerPresent = true;
                    _partnerExitedCleanly = false;
                }
                else if (snapshot.Health != 0)
                {
                    _armedPartnerPresent = false;
                }
            }
        }
    }

    /// <summary>Active side: a peer acknowledged a heartbeat over the collab session.</summary>
    public static void NotifyPeerAck(long seq, string nodeId)
    {
        if (seq <= 0 || string.IsNullOrEmpty(nodeId))
            return;

        lock (Gate)
        {
            if (!_config.Enabled || _heartbeatSeq - seq > 10)
                return;

            var now = Environment.TickCount64;
            _lastPartnerPresenceTicks = now;

            // Any known participant may renew the lease. Do not require it to be "armed": a peer that
            // just yielded is in stand-down and will become able to claim again before its hold expires,
            // so the master must still see its acks.
            if (_peers.ContainsKey(nodeId))
                _lastAckTicks = now;
        }
    }

    /// <summary>
    /// The collaboration session to the other machine ended. On the host this disarms the lease only when
    /// the partner's heartbeats stopped at the same moment (a clean process exit); a session that dies
    /// long after the heartbeats did is a network partition, and the lease must stay armed.
    /// On a standby it starts the fast-promotion guard.
    /// </summary>
    public static void NotifyPeerSessionLost(bool iAmHost)
    {
        var now = Environment.TickCount64;
        lock (Gate)
        {
            if (!_config.Enabled)
                return;

            if (iAmHost)
            {
                if (now - _lastPartnerPresenceTicks < 300 && _role is NodeRole.Active or NodeRole.Fenced)
                {
                    _armedPartnerPresent = false;
                    _partnerExitedCleanly = true;
                    RecordEvent("Standby session closed — lease disarmed");
                }
            }
            else
            {
                _sessionClosedTicks = now;
            }
        }

        FlushPendingSend();

        // A session close can unblock a fenced master; evaluate now instead of on the next tick.
        EvaluateState();
    }

    private static int EffectiveStallMs => _config.StallMs <= 0 ? 0 : Math.Max(50, _config.StallMs);

    private static OutputHealth ComputeHealth(long now)
    {
        var stallMs = EffectiveStallMs;
        if (stallMs > 0 && _frameClockStarted && now - _lastFrameAdvanceTicks > stallMs)
            return OutputHealth.Stalled;

        if (now - _lastFaultReportTicks < RecoveryMs)
            return OutputHealth.Faulted;

        return OutputHealth.Ok;
    }

    private static string HealthLabel(OutputHealth health) => health switch
                                                                  {
                                                                      OutputHealth.Ok      => "ok",
                                                                      OutputHealth.Stalled => "stalled",
                                                                      _                    => "faulted",
                                                                  };

    private static void FlushPendingSend()
    {
        bool pending;
        lock (Gate)
        {
            pending = _sendPending;
            _sendPending = false;
        }

        if (pending)
            SendHeartbeat();
    }

    // -------------------------------------------------------------------------------------------
    // Lifecycle
    // -------------------------------------------------------------------------------------------

    private static void Start()
    {
        _role = NodeRole.Probing;
        _lastStateChangeTicks = Environment.TickCount64;
        _peers.Clear();
        _epoch = 0;
        _maxSeenEpoch = 0;
        _heartbeatSeq = 0;
        _lastAckTicks = 0;
        _lastPartnerPresenceTicks = 0;
        _armedPartnerPresent = false;
        _partnerExitedCleanly = false;
        _sessionClosedTicks = 0;
        _lastActivePeerHeardTicks = 0;
        _lastActivePeerMuteMs = 0;
        _lastActivePeerYieldTicks = 0;
        _standdownUntilTicks = 0;
        _forceTakeover = false;
        _lastFaultReportTicks = 0;
        _faultReason = "";
        _frameClockStarted = false;
        _lastFrameAdvanceTicks = 0;
        _frameCounter = 0;
        _consecutiveSendFailures = 0;
        _nextSocketCheckTicks = 0;
        Volatile.Write(ref _leaseExpired, 0);
        RecordEvent($"Enabled (show '{_config.ShowId}', priority {_config.Priority})");

        _cancellation = new CancellationTokenSource();
        var token = _cancellation.Token;
        int socketCount;
        try
        {
            lock (Gate)
                socketCount = TryBindSocketsLocked();
        }
        catch (Exception e)
        {
            Log.Warning($"Failover: could not bind heartbeat multicast on port {FailoverPort}: {e.Message}");
            socketCount = 0;
        }

        if (socketCount == 0)
        {
            // Keep the tick loop alive: the bind is retried, so a port conflict or a NIC that is not
            // up yet heals without toggling failover. Until then this node streams solo, exactly as
            // a machine that never enabled failover would.
            _role = NodeRole.Disabled;
            RecordEvent($"No heartbeat sockets — retrying every 2 s (port {FailoverPort})");
        }

        StartListeners(token);
        _ = Task.Run(() => TickLoop(token), token);
    }

    /// <summary>
    /// Binds one heartbeat socket per usable interface. Caller holds <see cref="Gate"/> and has closed
    /// the previous sockets. Returns how many sockets could be created.
    /// </summary>
    private static int TryBindSocketsLocked()
    {
        // Join the heartbeat group on every real interface, not just the OS default route:
        // multi-homed machines (WSL/Hyper-V/VPN adapters) otherwise never hear peers on the
        // show NIC. Loopback keeps one-PC master/backup rehearsal working via MulticastLoopback.
        // An explicitly selected adapter restricts heartbeats to that NIC instead.
        var pinnedInterface = string.IsNullOrWhiteSpace(_config.NetworkInterfaceName)
                                  ? null
                                  : LanInterfaces.Find(_config.NetworkInterfaceName);

        if (pinnedInterface != null && !pinnedInterface.SupportsMulticast)
        {
            Log.Warning($"Failover: adapter '{pinnedInterface.Name}' has no multicast support; using all interfaces");
            pinnedInterface = null;
        }

        var interfaces = new List<IPAddress>();
        if (pinnedInterface != null)
        {
            interfaces.Add(pinnedInterface.Address);
            RecordEvent($"Heartbeat interface {pinnedInterface.Name} ({pinnedInterface.Address})");
        }
        else
        {
            try
            {
                foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (networkInterface.OperationalStatus != OperationalStatus.Up
                        || networkInterface.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel
                        || !networkInterface.SupportsMulticast)
                        continue;

                    var ipv4 = networkInterface.GetIPProperties().UnicastAddresses
                                          .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork)?.Address;
                    if (ipv4 != null)
                        interfaces.Add(ipv4);
                }
            }
            catch (Exception e)
            {
                Log.Warning($"Failover: interface enumeration failed ({e.Message}); using default route");
            }
        }

        if (interfaces.Count == 0)
            interfaces.Add(IPAddress.Any);

        if (pinnedInterface != null)
        {
            // Pinned adapter. The socket binds the multicast GROUP address, not the interface's
            // unicast IP: some Linux kernels (verified on Debian 13) never deliver multicast to
            // a socket bound to a specific unicast address, which deafened a pinned standby into
            // promoting itself against a live master. Membership still restricts receiving to the
            // chosen NIC, and IP_MULTICAST_IF pins the outgoing interface, so heartbeats both
            // ways stay on that adapter.
            try
            {
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                // Windows delivers multicast to a socket bound to the interface's unicast address;
                // Debian 13 (verified) never does and only answers a group-address bind, where a
                // unicast bind would deafen a pinned standby into promoting itself against a live
                // master. Membership still restricts receiving to the chosen NIC, and
                // IP_MULTICAST_IF pins the outgoing interface, so heartbeats stay on that adapter.
                var bindAddress = OperatingSystem.IsLinux() ? GroupAddress : pinnedInterface.Address;
                socket.Bind(new IPEndPoint(bindAddress, FailoverPort));
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, true);
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership,
                                       new MulticastOption(GroupAddress, pinnedInterface.Address));
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface,
                                       pinnedInterface.Address.GetAddressBytes());
                _heartbeatSockets.Add(socket);
                RecordEvent($"Heartbeat socket on {pinnedInterface.Name} ({pinnedInterface.Address})");
            }
            catch (Exception e)
            {
                Log.Warning($"Failover: could not bind {pinnedInterface.Address}:{FailoverPort}: {e.Message}");
            }
        }
        else
        {
            foreach (var address in interfaces)
            {
                try
                {
                    var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                    socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                    socket.Bind(new IPEndPoint(address, FailoverPort));
                    socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, true);
                    socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership,
                                           new MulticastOption(GroupAddress, address));
                    _heartbeatSockets.Add(socket);
                    RecordEvent($"Heartbeat socket on {address}");
                }
                catch (Exception e)
                {
                    Log.Warning($"Failover: could not bind {address}:{FailoverPort}: {e.Message}");
                }
            }

            // Supplementary default-route socket: on Linux a specific-IP bind never receives
            // multicast looped back from a local sender (verified), which would break the
            // one-PC master/backup rehearsal. The Any bind still does. Duplicates are
            // harmless — peers are deduped by node id.
            try
            {
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                socket.Bind(new IPEndPoint(IPAddress.Any, FailoverPort));
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, true);
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership,
                                       new MulticastOption(GroupAddress, IPAddress.Any));
                _heartbeatSockets.Add(socket);
            }
            catch (Exception e)
            {
                Log.Warning($"Failover: could not bind default-route heartbeat socket: {e.Message}");
            }
        }

        return _heartbeatSockets.Count;
    }

    private static void StartListeners(CancellationToken token)
    {
        Socket[] sockets;
        lock (Gate)
            sockets = _heartbeatSockets.ToArray();

        foreach (var socket in sockets)
            _ = Task.Run(() => ListenLoop(socket, token), token);
    }

    private static void CloseSocketsLocked()
    {
        foreach (var socket in _heartbeatSockets)
        {
            try
            {
                socket.Close();
            }
            catch
            {
                // Already dead.
            }
        }

        _heartbeatSockets.Clear();
    }

    /// <summary>
    /// Self-healing for the transport itself: re-binds the heartbeat sockets when none could be bound
    /// (port conflict at startup, adapter not up yet) or when every send has been failing (NIC reset,
    /// dock unplugged, VPN route change).  The role is kept; the election runs on the fresh sockets.
    /// </summary>
    private static void EnsureSockets()
    {
        var now = Environment.TickCount64;
        if (now < _nextSocketCheckTicks)
            return;

        _nextSocketCheckTicks = now + 2000;

        lock (Gate)
        {
            if (_cancellation == null)
                return;

            if (_heartbeatSockets.Count > 0 && _consecutiveSendFailures < 40)
                return;
        }

        RebindSockets();
    }

    private static void RebindSockets()
    {
        CancellationToken token;
        bool recoveredFromDisabled;
        int socketCount;
        lock (Gate)
        {
            if (_cancellation == null)
                return;

            token = _cancellation.Token;
            recoveredFromDisabled = _role == NodeRole.Disabled && _config.Enabled;
            CloseSocketsLocked();
            socketCount = TryBindSocketsLocked();
            _consecutiveSendFailures = 0;

            if (socketCount > 0 && recoveredFromDisabled)
            {
                _role = NodeRole.Probing;
                _lastStateChangeTicks = Environment.TickCount64;
                RecordEvent("Heartbeat network recovered — rejoining the election");
            }
        }

        if (socketCount == 0)
            return;

        StartListeners(token);
        Log.Info("Failover: heartbeat sockets re-bound");
    }

    private static void Stop()
    {
        SendBye();
        _role = NodeRole.Disabled;
        _peers.Clear();
        _armedPartnerPresent = false;
        _consecutiveSendFailures = 0;
        _nextSocketCheckTicks = 0;
        Volatile.Write(ref _leaseExpired, 0);
        RecordEvent("Disabled — output resumed");
        _cancellation?.Cancel();
        _cancellation = null;
        Socket[] sockets;
        lock (Gate)
        {
            sockets = _heartbeatSockets.ToArray();
            _heartbeatSockets.Clear();
        }

        foreach (var socket in sockets)
        {
            try
            {
                socket.Close();
            }
            catch
            {
                // Socket already dead.
            }
        }
    }

    private static void TickLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                SendHeartbeat();
                EvaluateState();
                EnsureSockets();
            }
            catch (Exception e)
            {
                Log.Warning($"Failover: tick error: {e.Message}");
            }

            int delay;
            lock (Gate)
                delay = SendCadenceMs;

            try
            {
                if (token.WaitHandle.WaitOne(delay))
                    break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
        }
    }

    // -------------------------------------------------------------------------------------------
    // UDP heartbeat / ack
    // -------------------------------------------------------------------------------------------

    private static void SendHeartbeat()
    {
        Socket[] sockets;
        byte[] payload;
        lock (Gate)
        {
            sockets = _heartbeatSockets.ToArray();
            if (sockets.Length == 0 || !_config.Enabled)
                return;

            var snapshot = GetSnapshotLocked(Environment.TickCount64);
            _heartbeatSeq++;
            payload = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new
                                                                             {
                                                                                 t = "hb",
                                                                                 show = _config.ShowId,
                                                                                 id = NodeId,
                                                                                 prio = snapshot.Priority,
                                                                                 active = snapshot.AdvertisesActive,
                                                                                 port = _config.CollabPort,
                                                                                 host = Environment.MachineName,
                                                                                 seq = _heartbeatSeq,
                                                                                 epoch = snapshot.Epoch,
                                                                                 frame = snapshot.Frame,
                                                                                 health = snapshot.Health,
                                                                                 armed = snapshot.Armed,
                                                                                 standdown = snapshot.Standdown,
                                                                                 mute = snapshot.Mute,
                                                                             }));
        }

        // One copy per interface: heartbeats are ~200 bytes, and peers that only share one NIC
        // with us still get every datagram. Duplicates are harmless (peers key by node id).
        var sentAny = false;
        foreach (var socket in sockets)
        {
            try
            {
                socket.SendTo(payload, new IPEndPoint(GroupAddress, FailoverPort));
                sentAny = true;
            }
            catch (Exception e) when (e is SocketException or ObjectDisposedException)
            {
                // Interface vanished (cable out, adapter reset); the remaining sockets still send.
            }
        }

        lock (Gate)
            _consecutiveSendFailures = sentAny ? 0 : _consecutiveSendFailures + 1;
    }

    private static void SendBye()
    {
        Socket[] sockets;
        byte[] payload;
        lock (Gate)
        {
            sockets = _heartbeatSockets.ToArray();
            if (sockets.Length == 0)
                return;

            payload = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new
                                                                             {
                                                                                 t = "bye",
                                                                                 show = _config.ShowId,
                                                                                 id = NodeId,
                                                                                 host = Environment.MachineName,
                                                                             }));
        }

        foreach (var socket in sockets)
        {
            try
            {
                socket.SendTo(payload, new IPEndPoint(GroupAddress, FailoverPort));
            }
            catch (Exception e) when (e is SocketException or ObjectDisposedException)
            {
                // Best effort on the way out.
            }
        }
    }

    private static void SendAck(string showId, string targetId, long seq)
    {
        Socket[] sockets;
        lock (Gate)
            sockets = _heartbeatSockets.ToArray();
        if (sockets.Length == 0)
            return;

        var payload = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new
                                                                             {
                                                                                 t = "ack",
                                                                                 show = showId,
                                                                                 id = NodeId,
                                                                                 to = targetId,
                                                                                 ack = seq,
                                                                             }));

        // Ack is multicast too: a pinned Linux socket binds the group address and would never see a
        // unicast reply. Receivers filter by `to`, so extra copies are ignored.
        foreach (var socket in sockets)
        {
            try
            {
                socket.SendTo(payload, new IPEndPoint(GroupAddress, FailoverPort));
            }
            catch (Exception e) when (e is SocketException or ObjectDisposedException)
            {
                // Best effort.
            }
        }
    }

    private static void ListenLoop(Socket listenSocket, CancellationToken token)
    {
        var buffer = new byte[MaxDatagramSize];
        while (!token.IsCancellationRequested)
        {
            EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
            int length;
            try
            {
                length = listenSocket.ReceiveFrom(buffer, ref remote);
            }
            catch (SocketException e) when (e.SocketErrorCode is SocketError.MessageSize or SocketError.ConnectionReset
                                                 && !token.IsCancellationRequested)
            {
                // A foreign oversized datagram, or an ICMP reset echoed back to this UDP socket, must not
                // end the listener: a deaf node promotes itself against a live master.
                continue;
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

                var senderAddress = (remote as IPEndPoint)?.Address.ToString() ?? "";

                string showId;
                lock (Gate)
                    showId = _config.ShowId;

                if (heartbeat.Show != showId)
                    continue; // Another show on the same network — ignore.

                if (heartbeat.Id == NodeId)
                    continue; // Our own multicast loopback.

                switch (heartbeat.T)
                {
                    case "ack":
                        HandleAck(heartbeat);
                        continue;

                    case "bye":
                        HandleBye(heartbeat);
                        continue;
                }

                var ackSeq = HandleHeartbeat(heartbeat, senderAddress);
                if (ackSeq > 0)
                    SendAck(showId, heartbeat.Id!, ackSeq);

                // Evaluate now instead of waiting for the next tick: a competing claim must be answered
                // within the same millisecond, or both nodes stream until one's next evaluation.
                EvaluateState();
            }
            catch
            {
                // Malformed datagram from some other tool on this port — ignore.
            }
        }
    }

    /// <summary>Returns the sequence number to acknowledge, or 0 when none is needed.</summary>
    private static long HandleHeartbeat(Heartbeat heartbeat, string senderAddress)
    {
        var now = Environment.TickCount64;
        lock (Gate)
        {
            var peer = GetOrAddPeer(heartbeat.Id!, senderAddress, heartbeat.Port);
            if (peer == null)
                return 0;

            ApplyPeerFacts(peer, heartbeat.Prio, heartbeat.Active, heartbeat.Epoch,
                           heartbeat.Health, heartbeat.Armed, heartbeat.Standdown, heartbeat.Mute, now);

            peer.Address = senderAddress;
            peer.CollabPort = heartbeat.Port;
            peer.Host = heartbeat.Host ?? "";
            if (!string.IsNullOrEmpty(peer.Host))
                _lastPeerHost = peer.Host;

            _maxSeenEpoch = Math.Max(_maxSeenEpoch, heartbeat.Epoch);
            _lastPartnerPresenceTicks = now;
            _partnerExitedCleanly = false;

            if (heartbeat.Active)
            {
                _lastActivePeerHeardTicks = now;
                if (heartbeat.Mute > 0)
                    _lastActivePeerMuteMs = heartbeat.Mute;
            }

            if (!heartbeat.Active)
            {
                if (heartbeat.Armed)
                {
                    _armedPartnerPresent = true;
                }
                else if (heartbeat.Health != 0)
                {
                    // A broken standby cannot promote; its silence must not mute the show.
                    _armedPartnerPresent = false;
                }
            }

            // Answer every fresh heartbeat with the sequence it carries. That ack is the sender's lease:
            // it proves somebody heard the heartbeat, in the direction the heartbeat travelled.
            if (heartbeat.Seq > peer.LastAckedSeq)
            {
                peer.LastAckedSeq = heartbeat.Seq;
                return heartbeat.Seq;
            }

            return 0;
        }
    }

    private static void HandleAck(Heartbeat heartbeat)
    {
        lock (Gate)
        {
            if (heartbeat.To != NodeId)
                return;

            if (_heartbeatSeq - heartbeat.Ack > 10 || heartbeat.Ack > _heartbeatSeq)
                return;

            var now = Environment.TickCount64;
            _lastPartnerPresenceTicks = now;

            // See NotifyPeerAck: a known peer renews the lease, whether or not it is currently armed.
            if (_peers.ContainsKey(heartbeat.Id!))
                _lastAckTicks = now;
        }
    }

    private static void HandleBye(Heartbeat heartbeat)
    {
        lock (Gate)
        {
            if (_peers.Remove(heartbeat.Id!))
                RecordEvent($"Peer left ({heartbeat.Host})");

            if (_peers.Count == 0)
            {
                _armedPartnerPresent = false;
                _partnerExitedCleanly = true;
            }
        }

        FlushPendingSend();
    }

    private static PeerInfo? GetOrAddPeer(string id, string address = "", int collabPort = 0)
    {
        if (_peers.TryGetValue(id, out var peer))
            return peer;

        if (_peers.Count >= MaxTrackedPeers)
            return null;

        peer = new PeerInfo
                   {
                       NodeId = id,
                       Address = address,
                       CollabPort = collabPort,
                   };
        _peers[id] = peer;
        RecordEvent("Peer discovered");
        return peer;
    }

    private static void ApplyPeerFacts(PeerInfo peer,
                                       int priority,
                                       bool advertisesActive,
                                       long epoch,
                                       int health,
                                       bool armed,
                                       bool standdown,
                                       int mute,
                                       long now)
    {
        peer.Priority = priority;
        peer.AdvertisesActive = advertisesActive;
        peer.LastHeardTicks = now;
        peer.Epoch = epoch;
        peer.Health = (OutputHealth)Math.Clamp(health, 0, 2);
        peer.Armed = armed;
        peer.Standdown = standdown;
        peer.MuteMs = mute;

        if (advertisesActive)
        {
            peer.WasActive = true;
        }
        else if (peer.WasActive)
        {
            // A master that stopped advertising itself active declared a handover: a fault, a step-down
            // or a fence. Promote now instead of waiting for its heartbeat to time out.
            peer.WasActive = false;
            _lastActivePeerYieldTicks = now;
        }
    }

    private sealed class Heartbeat
    {
        [JsonProperty("t")]
        public string? T { get; set; }

        public string? Show { get; set; }
        public string? Id { get; set; }
        public int Prio { get; set; }
        public bool Active { get; set; }
        public int Port { get; set; }
        public string? Host { get; set; }

        public long Seq { get; set; }
        public long Epoch { get; set; }
        public long Frame { get; set; }
        public int Health { get; set; }
        public bool Armed { get; set; }
        public bool Standdown { get; set; }
        public int Mute { get; set; }

        /// <summary>Ack recipient node id.</summary>
        public string? To { get; set; }

        /// <summary>Acknowledged heartbeat sequence.</summary>
        public long Ack { get; set; }
    }

    // -------------------------------------------------------------------------------------------
    // Election
    // -------------------------------------------------------------------------------------------

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
            EvaluateStateLocked();

        FlushPendingSend();
    }

    /// <summary>Caller holds <see cref="Gate"/>. Early returns are fine: the caller flushes any pending heartbeat.</summary>
    private static void EvaluateStateLocked()
    {
        if (_role == NodeRole.Disabled)
            return;

        var now = Environment.TickCount64;
        var timeout = TimeoutMs;
        var config = _config;

        // Peers that stopped sending can never win an election again. Without this the table
        // grows for the life of the process: every restarted node mints a new id, and any LAN
        // sender can add entries at will.
        PruneStalePeers(now, timeout);

        var health = ComputeHealth(now);
        var standdown = now < _standdownUntilTicks;

        PeerInfo? bestActive = null;
        PeerInfo? bestEligible = null;
        foreach (var peer in _peers.Values)
        {
            if (now - peer.LastHeardTicks > timeout)
                continue;

            if (peer.Health == OutputHealth.Ok
                && !peer.Standdown
                && (bestEligible == null || Beats(peer.Priority, peer.NodeId, bestEligible.Priority, bestEligible.NodeId)))
                bestEligible = peer;

            if (peer.AdvertisesActive
                && (bestActive == null || Beats(peer.Priority, peer.NodeId, bestActive.Priority, bestActive.NodeId)))
                bestActive = peer;
        }

        var iWin = bestActive == null || Beats(config.Priority, NodeId, bestActive.Priority, bestActive.NodeId);
        var outrankedByEligible = bestEligible != null && Beats(bestEligible.Priority, bestEligible.NodeId, config.Priority, NodeId);
        var superseded = bestActive != null && bestActive.Epoch > _epoch;

        // A broken node mutes and never claims. The standby keeps following the master while faulted.
        // A frame stall only faults a node that streams or is about to: a standby or probing node stalls
        // whenever it switches roles (dropping a sender, joining a session), and faulting it then would
        // hold it out of the election for seconds right when the master may be about to fail.
        var stallOnly = health == OutputHealth.Stalled;
        var canFault = _role is NodeRole.Active or NodeRole.Contested or NodeRole.Fenced
                       || (!stallOnly && _role is NodeRole.Standby or NodeRole.Probing);
        if (health != OutputHealth.Ok && canFault)
        {
            if (health == OutputHealth.Stalled)
            {
                _lastFaultReportTicks = now;
                _faultReason = "output frames stalled";
            }

            Transition(NodeRole.Faulted,
                       health == OutputHealth.Stalled
                           ? "Output frames stalled"
                           : string.IsNullOrEmpty(_faultReason) ? "Output fault" : $"Output fault: {_faultReason}");
            return;
        }

        // Lease: an Active node that was relying on an armed standby must mute before that standby can
        // promote. Renewal comes from an ack on either channel, so a single broken path never fences.
        var leaseLost = _role == NodeRole.Active
                        && config.Fencing
                        && _armedPartnerPresent
                        && !_partnerExitedCleanly
                        && now - _lastAckTicks > MuteMs;

        if (leaseLost)
        {
            Volatile.Write(ref _leaseExpired, 1);
            Transition(NodeRole.Fenced, $"Standby silent for {now - _lastAckTicks} ms — fencing output");
            return;
        }

        switch (_role)
        {
            case NodeRole.Probing:
            {
                // The probe window (2×timeout) lets a joining node hear existing masters before
                // claiming anything; the per-node offset keeps simultaneous joiners from crossing claims.
                if (now - _lastStateChangeTicks < 2 * timeout + ClaimJitterMs)
                    return;

                // A peer that outranks us can claim at any moment - including one that is still
                // probing or already contested, neither of which advertises itself as active.
                // Yielding here is what stops two nodes that start together from both claiming
                // at the probe deadline (each saw no active peer).
                if (outrankedByEligible)
                {
                    Transition(NodeRole.Standby, bestActive != null
                                                     ? "Active peer has higher priority"
                                                     : "Higher-priority peer is present");
                    return;
                }

                if (bestActive == null && (!standdown || bestEligible == null))
                {
                    // With no eligible partner around there is nobody this claim could steal the role
                    // from: a solo machine or a pair whose partner is broken must heal itself without
                    // waiting out the stand-down.
                    ReturnToActive("No active peer — becoming active");
                }
                else if (bestActive == null)
                {
                    // We just stepped down; the partner gets the first chance to claim.
                    Transition(NodeRole.Standby, "Holding down after step-down");
                }
                else if (superseded)
                {
                    Transition(NodeRole.Standby, "A newer claim is active");
                }
                else if (config.Preempt && !standdown)
                {
                    BeginClaim(NodeRole.Contested, "Preempting lower-priority active peer");
                }
                else
                {
                    // An active peer that outranked us was handled above, so this is a lower
                    // priority active we would win against - but preempt is off, or we just
                    // stepped down: a returning node must not take the role from a live active.
                    Transition(NodeRole.Standby, standdown
                                                     ? "Holding down after step-down"
                                                     : "Active peer is present — preempt is off");
                }

                break;
            }

            case NodeRole.Standby:
            {
                if (bestActive != null)
                {
                    if (config.Preempt
                        && iWin
                        && !standdown
                        && !outrankedByEligible
                        && now - _lastStateChangeTicks > 2 * timeout)
                    {
                        BeginClaim(NodeRole.Contested, "Preempting lower-priority active peer");
                    }

                    return;
                }

                // A higher-ranked node that is alive but has not claimed yet (still probing
                // or contested) will; promoting now puts two nodes on the wire.
                if (outrankedByEligible)
                    return;

                // We just stepped down; give a live partner a chance to claim before taking the role back.
                // With no eligible partner there is nothing to wait for, so a solo machine heals at once.
                if (standdown && bestEligible != null)
                    return;

                // An explicit yield (fault, operator step-down, fence) is not a guess; promote now.
                if (now - _lastActivePeerYieldTicks < 2000)
                {
                    _takeoverCount++;
                    ReturnToActive("Master announced a handover — taking over");
                    return;
                }

                // A master whose process exited closes its session and stops heartbeating at the
                // same instant. That is not a partition: promote without waiting for the timeout.
                var sessionClosedFast = _sessionClosedTicks != 0
                                        && _lastActivePeerHeardTicks != 0
                                        && now - _sessionClosedTicks >= 2 * SendCadenceMs
                                        && now - _lastActivePeerHeardTicks >= 2 * SendCadenceMs;
                if (sessionClosedFast)
                {
                    _takeoverCount++;
                    ReturnToActive("Master session closed — taking over");
                    return;
                }

                // Honor the slowest of the two deadlines: our own timeout and the master's own
                // fence deadline plus one heartbeat of margin. With matching configs this is just
                // the timeout; with mismatched configs it still cannot overlap.
                var wait = timeout;
                if (_lastActivePeerMuteMs > 0)
                    wait = Math.Max(wait, _lastActivePeerMuteMs + 2 * SendCadenceMs + 50);

                if (now - _lastActivePeerHeardTicks < wait)
                    return;

                _takeoverCount++;
                ReturnToActive("Active peer lost — taking over");
                return;
            }

            case NodeRole.Contested:
            {
                // Both sides are muted for one timeout window, then the winner claims.
                if (now - _lastStateChangeTicks < timeout)
                    return;

                if (superseded)
                {
                    _forceTakeover = false;
                    Transition(NodeRole.Standby, "Yielding to a newer claim");
                    return;
                }

                if (!_forceTakeover && (outrankedByEligible || !iWin))
                {
                    _forceTakeover = false;
                    Transition(NodeRole.Standby, "Yielding to higher-priority peer");
                    return;
                }

                _forceTakeover = false;
                ReturnToActive("Claimed active after handover");
                break;
            }

            case NodeRole.Active:
            {
                if (superseded)
                {
                    Transition(NodeRole.Contested, "A newer claim is active");
                }
                else if (bestActive != null && !iWin)
                {
                    Transition(NodeRole.Contested, "Higher-priority peer is claiming");
                }

                break;
            }

            case NodeRole.Faulted:
            {
                if (health == OutputHealth.Ok)
                    Transition(NodeRole.Probing, "Output healthy again — rejoining");

                break;
            }

            case NodeRole.Fenced:
            {
                if (!config.Fencing)
                {
                    Transition(NodeRole.Probing, "Fencing disabled — resuming");
                    break;
                }

                // The standby answered again: the path is back, rejoin the election (muted while probing).
                if (now - _lastAckTicks < MuteMs)
                {
                    Volatile.Write(ref _leaseExpired, 0);
                    Transition(NodeRole.Probing, "Standby is answering again — rejoining");
                }
                else if (_partnerExitedCleanly)
                {
                    Volatile.Write(ref _leaseExpired, 0);
                    Transition(NodeRole.Probing, "Standby exited — resuming");
                }

                break;
            }
        }
    }

    /// <summary>Entering Active: a fresh lease window and a clean armed-partner slate.</summary>
    private static void ReturnToActive(string reason)
    {
        _epoch = _maxSeenEpoch + 1;
        _maxSeenEpoch = _epoch;
        _lastAckTicks = Environment.TickCount64;
        _armedPartnerPresent = false;
        _sessionClosedTicks = 0;
        _lastActivePeerYieldTicks = 0;
        _standdownUntilTicks = 0; // a claimed node has no reason to hold down; leaving Active sets it again
        Volatile.Write(ref _leaseExpired, 0);
        Transition(NodeRole.Active, reason);
    }

    private static void BeginClaim(NodeRole role, string reason)
    {
        _epoch = _maxSeenEpoch + 1;
        _maxSeenEpoch = _epoch;
        Transition(role, reason);
    }

    /// <summary>Drops peers unheard for many timeouts; a returning node re-announces itself. Caller holds Gate.</summary>
    private static void PruneStalePeers(long now, int timeout)
    {
        if (_peers.Count == 0)
            return;

        _stalePeerIds.Clear();
        foreach (var (id, peer) in _peers)
        {
            if (now - peer.LastHeardTicks > 10L * timeout)
                _stalePeerIds.Add(id);
        }

        foreach (var id in _stalePeerIds)
            _peers.Remove(id);
    }

    private static readonly List<string> _stalePeerIds = [];

    private static void Transition(NodeRole newRole, string reason)
    {
        if (_role == newRole)
            return;

        var leavingActive = _role == NodeRole.Active && newRole != NodeRole.Active;
        _role = newRole;
        _lastStateChangeTicks = Environment.TickCount64;

        // The per-frame output gate reads this without the lock: only a fenced Active node is muted by the lease.
        Volatile.Write(ref _leaseExpired, newRole == NodeRole.Fenced ? 1 : 0);

        if (leavingActive)
            _standdownUntilTicks = _lastStateChangeTicks + StanddownMs;

        RecordEvent($"{newRole}: {reason}");
        Log.Info($"Failover: {newRole} — {reason}");
        _sendPending = true;
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
