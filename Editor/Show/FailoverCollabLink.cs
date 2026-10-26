#nullable enable
using System;
using T3.Core.Logging;
using T3.Core.Show;
using T3.Editor.Collab;
using T3.Editor.Gui.UiHelpers;

namespace T3.Editor.Show;

/// <summary>
/// Wires the failover arbiter's role to a collaboration session, so the standby machine follows
/// the active one automatically: while this node is Active it hosts the session, while it is
/// Standby it joins the active peer's session and receives the same live updates a manual collab
/// session carries (composition snapshots and edit ops). That is what makes a takeover useful —
/// a promoted backup continues the master's show instead of its own stale composition — and what
/// hands the session back when the master returns: the returning master preempts the output
/// (arbiter) and this backup steps down from hosting and rejoins the master as a client.
///
/// Failover owns the collab session while it is enabled: manual host/join choices are reconciled
/// away on the next tick. The link only acts on settled roles (Active/Standby) — Probing and the
/// contested handover window are left alone so it cannot thrash against an election in flight.
/// </summary>
internal sealed class FailoverCollabLink
{
    public static FailoverCollabLink Instance { get; } = new();

    /// <summary>What the link is doing, for the failover settings panel.</summary>
    public string StatusText { get; private set; } = "Idle (failover off)";

    public void Update()
    {
        var collab = CollabManager.Instance;

        if (!FailoverArbiter.IsEnabled)
        {
            StatusText = "Idle (failover off)";
            _connectWatchTicks = 0;
            return;
        }

        // A dial that never completes — a dead master whose host drops instead of refuses SYNs —
        // parks the session in Connecting for the OS connect timeout, which is minutes. Retire it
        // so the current role's hosting/following can proceed.
        if (collab.State == CollabManager.SessionState.Connecting)
        {
            if (_connectWatchTicks == 0)
            {
                _connectWatchTicks = Environment.TickCount64;
            }
            else if (Environment.TickCount64 - _connectWatchTicks > StaleConnectTimeoutMs)
            {
                Log.Info("Failover: the pending session dial never completed — abandoning it");
                collab.Leave();
                _dialedHost = "";
                _connectWatchTicks = 0;
                _nextActionTicks = Environment.TickCount64 + RetryCooldownMs;
            }
        }
        else
        {
            _connectWatchTicks = 0;
        }

        // Cooldown between transitions: TCP teardown, the leaving session's cleanup and the
        // returning master opening its own listener all need a beat. One transition per tick.
        if (Environment.TickCount64 < _nextActionTicks)
            return;

        switch (FailoverArbiter.Role)
        {
            case FailoverArbiter.NodeRole.Active:
                DriveHosting(collab);
                break;

            case FailoverArbiter.NodeRole.Standby:
                DriveFollowing(collab);
                break;

            // Probing / Contested: an election or handover is in flight — wait it out.
        }
    }

    private void DriveHosting(CollabManager collab)
    {
        if (collab.State == CollabManager.SessionState.Hosting)
        {
            StatusText = "Master — hosting the sync session";
            return;
        }

        if (collab.State == CollabManager.SessionState.Connecting)
        {
            StatusText = "Waiting for a pending connection to settle...";
            return;
        }

        if (collab.State == CollabManager.SessionState.Connected)
        {
            // We just took over from a master we were still following: drop the client link;
            // hosting starts on the next tick after the cooldown.
            StatusText = "Taking over the sync session...";
            collab.Leave();
            _nextActionTicks = Environment.TickCount64 + TransitionCooldownMs;
            return;
        }

        var settings = UserSettings.Config;
        Log.Info($"Failover: this node is active — hosting the collab session on port {settings.CollabPort}");
        collab.StartHost(settings.CollabPort,
                         settings.CollabPassword,
                         enableLanDiscovery: false,
                         projectName: CollabManager.ConfiguredShareProject,
                         interfaceName: settings.NetworkInterface);
        StatusText = collab.State == CollabManager.SessionState.Hosting
                         ? "Master — hosting the sync session"
                         : "Hosting failed — retrying";
        _nextActionTicks = Environment.TickCount64 + (collab.State == CollabManager.SessionState.Hosting
                                                          ? TransitionCooldownMs
                                                          : RetryCooldownMs);
    }

    private void DriveFollowing(CollabManager collab)
    {
        var endpoint = FailoverArbiter.ActivePeerEndpoint;
        if (endpoint == null)
        {
            StatusText = "Standby — no active master heard yet";
            return;
        }

        var (address, port) = endpoint.Value;

        // Already following the current master: nothing to do.
        if (collab.State == CollabManager.SessionState.Connected && _dialedHost == $"{address}:{port}")
        {
            StatusText = $"Standby — following {address}:{port}";
            return;
        }

        if (collab.State == CollabManager.SessionState.Connecting)
        {
            StatusText = "Connecting to the master...";
            return;
        }

        if (collab.IsSessionActive)
        {
            // A session we must not hold while standby: the returning master preempted us while
            // we were hosting, or our old link points at a master that died. Step down and follow
            // the current one.
            StatusText = "Stepping down to follow the master...";
            collab.Leave();
            _dialedHost = "";
            _nextActionTicks = Environment.TickCount64 + TransitionCooldownMs;
            return;
        }

        Log.Info($"Failover: this node is standby — joining the master's session at {address}:{port}");
        collab.JoinDirect(address, port, UserSettings.Config.CollabPassword);
        var dialSucceeded = collab.State == CollabManager.SessionState.Connecting;
        _dialedHost = dialSucceeded ? $"{address}:{port}" : "";
        StatusText = dialSucceeded
                         ? $"Standby — connecting to {address}:{port}"
                         : "Join failed — retrying";
        _nextActionTicks = Environment.TickCount64 + (dialSucceeded ? TransitionCooldownMs : RetryCooldownMs);
    }

    /// <summary>host:port the link itself last dialed, so a live session can be matched against the current active peer.</summary>
    private string _dialedHost = "";

    private long _nextActionTicks;

    /// <summary>When the current Connecting state was first seen, for the stale-dial guard.</summary>
    private long _connectWatchTicks;

    private const int TransitionCooldownMs = 1000;
    private const int RetryCooldownMs = 2000;
    private const int StaleConnectTimeoutMs = 15000;
}
