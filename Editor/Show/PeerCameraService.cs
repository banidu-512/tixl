#nullable enable
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using T3.Core.DataTypes;
using T3.Core.Logging;
using T3.Core.Output.Streaming;
using T3.Core.Video;
using T3.Editor.Collab;

namespace T3.Editor.Show;

/// <summary>
/// Lets the TiXL clients of a collaboration, boygroup or failover session use each other's cameras. Every client
/// announces the cameras connected to it; a video input that selected "Camera @ Machine" asks that machine for
/// the stream, which opens the camera and publishes it over OMT, and the answer reaches the input through
/// <see cref="RemoteVideoInputBus"/>. Runs once per frame on the UI thread and does nothing outside a session.
/// </summary>
internal sealed class PeerCameraService
{
    public const string AnnounceType = "peerCameras";
    public const string RequestType = "peerCameraRequest";
    public const string SourceType = "peerCameraSource";

    public static PeerCameraService Instance { get; } = new();

    /// <summary>Runs once per frame, after the session managers updated and before the graph evaluates.</summary>
    public void Update()
    {
        CollectSessions();
        if (_sessions.Count == 0)
        {
            Reset();
            return;
        }

        _wasInSession = true;
        var now = Environment.TickCount64;
        RefreshLocalCameras(now);
        Announce(now);

        if (now >= _nextPruneTicks)
        {
            _nextPruneTicks = now + 2000;
            PeerCameraDirectory.PruneOlderThan(AnnounceLifetimeMs);
            ForgetUnknownRoutes();
        }

        PumpRequests(now);
        PumpShares(now);
    }

    /// <summary>Handles the three session messages; called by the collab and boygroup managers for their sessions.</summary>
    public void HandleMessage(CollabTransport? transport, bool isHost, CollabPeerConnection peer, string type, JObject json)
    {
        if (transport == null || (isHost && !peer.IsAuthenticated))
            return;

        var localMachine = PeerCameraDirectory.LocalMachine;
        switch (type)
        {
            case AnnounceType:
            {
                var machine = Text(json, "machine");
                if (machine.Length == 0 || string.Equals(machine, localMachine, StringComparison.OrdinalIgnoreCase))
                    return;

                _announcedScratch.Clear();
                if (json["cameras"] is JArray cameras)
                {
                    foreach (var camera in cameras)
                    {
                        var name = camera.Value<string>();
                        if (!string.IsNullOrEmpty(name))
                            _announcedScratch.Add(name);
                    }
                }

                PeerCameraDirectory.SetMachine(machine, _announcedScratch);
                var isNewMachine = !_routes.ContainsKey(machine);
                _routes[machine] = new Route(transport, peer);

                if (isHost)
                {
                    Forward(transport, peer, json);
                    if (isNewMachine && _sessionStates.TryGetValue(transport, out var session))
                        session.NextAnnounceTicks = 0;
                }

                return;
            }

            case RequestType:
            {
                var target = Text(json, "to");
                if (string.Equals(target, localMachine, StringComparison.OrdinalIgnoreCase))
                    RegisterRequest(Text(json, "camera"), Text(json, "from"));
                else if (isHost)
                    RouteTo(target, json);

                return;
            }

            case SourceType:
            {
                var target = Text(json, "to");
                if (string.Equals(target, localMachine, StringComparison.OrdinalIgnoreCase))
                    RegisterAnswer(json);
                else if (isHost)
                    RouteTo(target, json);

                return;
            }
        }
    }

    /// <summary>One line per part for the debug bridge.</summary>
    public JObject Describe()
    {
        var shares = new JArray();
        foreach (var (camera, share) in _shares)
        {
            shares.Add(new JObject
                           {
                               ["camera"] = camera,
                               ["status"] = share.Status,
                               ["address"] = share.Sender?.AdvertisedAddress ?? "",
                               ["requesters"] = share.RequesterCount,
                           });
        }

        var known = new List<string>();
        PeerCameraDirectory.AppendReferences(known);

        return new JObject
                   {
                       ["machine"] = PeerCameraDirectory.LocalMachine,
                       ["sessions"] = _sessions.Count,
                       ["local"] = new JArray(_localCameras),
                       ["known"] = new JArray(known),
                       ["wants"] = _wantStates.Count,
                       ["peerSources"] = _peerSources.Count,
                       ["shares"] = shares,
                   };
    }

    private void CollectSessions()
    {
        _sessions.Clear();
        var collab = CollabManager.Instance;
        if (collab.PeerSessionTransport is { } collabTransport)
            _sessions.Add(Track(collabTransport, collab.PeerSessionIsHost));

        var boygroup = BoygroupManager.Instance;
        if (boygroup.PeerSessionTransport is { } boygroupTransport)
            _sessions.Add(Track(boygroupTransport, boygroup.PeerSessionIsHost));

        if (_sessionStates.Count > _sessions.Count)
        {
            _staleTransports.Clear();
            foreach (var transport in _sessionStates.Keys)
            {
                var live = false;
                foreach (var session in _sessions)
                {
                    if (ReferenceEquals(session.Transport, transport))
                        live = true;
                }

                if (!live)
                    _staleTransports.Add(transport);
            }

            foreach (var transport in _staleTransports)
                _sessionStates.Remove(transport);
        }
    }

    private SessionState Track(CollabTransport transport, bool isHost)
    {
        if (!_sessionStates.TryGetValue(transport, out var state))
        {
            state = new SessionState(transport);
            _sessionStates[transport] = state;
        }

        state.IsHost = isHost;
        return state;
    }

    /// <summary>The camera list comes from the capture package; asked in the background, since a first device scan can take a while.</summary>
    private void RefreshLocalCameras(long now)
    {
        if (now < _nextCameraScanTicks || _scanning)
            return;

        var provider = PeerCameraRegistry.Provider;
        if (provider == null)
        {
            _nextCameraScanTicks = now + CameraScanIntervalMs;
            _localCameras = [];
            return;
        }

        _scanning = true;
        _nextCameraScanTicks = now + CameraScanIntervalMs;
        _ = Task.Run(() =>
                     {
                         try
                         {
                             var names = provider.GetCameraNames().ToArray();
                             if (!names.AsSpan().SequenceEqual(_localCameras))
                             {
                                 _localCameras = names;
                                 _camerasChanged = true;
                             }
                         }
                         catch (Exception e)
                         {
                             Log.Debug($"Peer cameras: listing the local cameras failed: {e.Message}");
                         }
                         finally
                         {
                             _scanning = false;
                         }
                     });
    }

    private void Announce(long now)
    {
        // A changed camera list goes out at once instead of with the next periodic announcement.
        var camerasChanged = _camerasChanged;
        _camerasChanged = false;
        foreach (var session in _sessions)
        {
            if (camerasChanged)
                session.NextAnnounceTicks = 0;

            if (now < session.NextAnnounceTicks)
                continue;

            session.NextAnnounceTicks = now + AnnounceIntervalMs;
            var message = new JObject
                              {
                                  ["t"] = AnnounceType,
                                  ["machine"] = PeerCameraDirectory.LocalMachine,
                                  ["cameras"] = new JArray(_localCameras),
                              };
            SendToAll(session.Transport, session.IsHost, message);
        }
    }

    /// <summary>Consumer side: asks the owning machines for the cameras inputs selected and hands the answers to the video bus.</summary>
    private void PumpRequests(long now)
    {
        _wants.Clear();
        PeerCameraBus.CollectWants(_wants);
        if (_wants.Count == 0 && _wantStates.Count == 0 && _lastSourcesHash == 0)
            return;

        _peerSources.Clear();
        var hash = _wants.Count;
        foreach (var want in _wants)
        {
            if (!_wantStates.TryGetValue(want.Key, out var state) || state.Machine != want.Machine || state.Camera != want.Camera)
            {
                state = new WantState(want.Machine, want.Camera);
                _wantStates[want.Key] = state;
            }

            state.Seen = true;
            if (now >= state.NextRequestTicks && _routes.TryGetValue(want.Machine, out var route) && !route.Peer.SessionClosed)
            {
                state.NextRequestTicks = now + RequestIntervalMs;
                route.Transport.SendJson(route.Peer,
                                         new JObject
                                             {
                                                 ["t"] = RequestType,
                                                 ["to"] = want.Machine,
                                                 ["from"] = PeerCameraDirectory.LocalMachine,
                                                 ["camera"] = want.Camera,
                                             });
            }

            var address = "";
            var status = _routes.ContainsKey(want.Machine) ? state.WaitingText : state.NotConnectedText;
            if (_answers.TryGetValue((want.Machine, want.Camera), out var answer) && now - answer.ReceivedTicks <= AnswerLifetimeMs)
            {
                address = answer.Address;
                status = answer.Status;
            }

            _peerSources.Add(new RemoteVideoSource(want.Key, address, status));
            hash = HashCode.Combine(hash, want.Key, address, status);
        }

        if (_wantStates.Count != _wants.Count)
        {
            _staleKeys.Clear();
            foreach (var (key, state) in _wantStates)
            {
                if (!state.Seen)
                    _staleKeys.Add(key);

                state.Seen = false;
            }

            foreach (var key in _staleKeys)
                _wantStates.Remove(key);
        }
        else
        {
            foreach (var state in _wantStates.Values)
                state.Seen = false;
        }

        if (hash != _lastSourcesHash)
        {
            _lastSourcesHash = hash;
            RemoteVideoInputBus.SetPeerSources(_peerSources);
        }

        // A boygroup render node already pumps the bus every frame.
        if (_peerSources.Count > 0 && BoygroupManager.Instance.State != BoygroupManager.BoygroupState.Client)
            RemoteVideoInputBus.Pump();
    }

    /// <summary>Provider side: keeps one capture and OMT sender per requested camera, publishes frames and answers the requesters.</summary>
    private void PumpShares(long now)
    {
        if (_requests.Count == 0 && _shares.Count == 0)
            return;

        // Open a share for every camera someone asked for recently.
        foreach (var (camera, requesters) in _requests)
        {
            if (!_shares.ContainsKey(camera) && HasLiveRequester(requesters, now))
                _shares[camera] = OpenShare(camera);
        }

        _staleKeys.Clear();
        foreach (var (camera, share) in _shares)
        {
            if (!_requests.TryGetValue(camera, out var requesters) || !HasLiveRequester(requesters, now))
            {
                _staleKeys.Add(camera);
                continue;
            }

            if (share.Capture == null)
            {
                if (now >= share.RetryTicks)
                    _reopenKeys.Add(camera);

                Reply(share, camera, requesters, now);
                continue;
            }

            try
            {
                var texture = share.Capture.Update();
                if (texture != null && share.Sender != null)
                {
                    share.Sender.Send(texture);
                    share.Status = "live";
                }
                else if (texture == null && share.Capture.Status != "live")
                {
                    share.Status = share.Capture.Status;
                }
            }
            catch (Exception e)
            {
                share.Status = $"streaming failed: {e.Message}";
            }

            share.RequesterCount = CountLive(requesters, now);
            Reply(share, camera, requesters, now);
        }

        foreach (var camera in _reopenKeys)
        {
            _shares[camera].Dispose();
            _shares[camera] = OpenShare(camera);
        }

        _reopenKeys.Clear();

        foreach (var camera in _staleKeys)
        {
            _shares[camera].Dispose();
            _shares.Remove(camera);
            _requests.Remove(camera);
            Log.Debug($"Peer cameras: stopped sharing '{camera}' - nobody asked for it anymore");
        }
    }

    private Share OpenShare(string camera)
    {
        var share = new Share();
        var provider = PeerCameraRegistry.Provider;
        if (provider == null)
        {
            share.Status = "this machine has no camera support loaded";
            share.RetryTicks = Environment.TickCount64 + ShareRetryMs;
            return share;
        }

        try
        {
            var capture = provider.Open(camera, out var error);
            if (capture == null)
            {
                share.Status = error ?? "the camera could not be opened";
                share.RetryTicks = Environment.TickCount64 + ShareRetryMs;
                return share;
            }

            var streamProvider = OutputStreamRegistry.TryGetProvider(RemoteVideoInputBus.PreferredTransportKind);
            if (streamProvider == null)
            {
                capture.Dispose();
                share.Status = $"the {RemoteVideoInputBus.PreferredTransportKind} package is not loaded on this machine";
                share.RetryTicks = Environment.TickCount64 + ShareRetryMs;
                return share;
            }

            var sender = streamProvider.CreateSender($"TiXL camera {camera}");
            sender.Configure(new OutputStreamSettings(60, false));
            share.Capture = capture;
            share.Sender = sender;
            share.Status = "starting";
            Log.Info($"Peer cameras: sharing '{camera}' over {RemoteVideoInputBus.PreferredTransportKind}");
        }
        catch (Exception e)
        {
            share.Status = $"could not share the camera: {e.Message}";
            share.RetryTicks = Environment.TickCount64 + ShareRetryMs;
        }

        return share;
    }

    private void Reply(Share share, string camera, Dictionary<string, long> requesters, long now)
    {
        if (now < share.NextReplyTicks)
            return;

        share.NextReplyTicks = now + ReplyIntervalMs;
        var address = share.Sender?.AdvertisedAddress ?? "";
        foreach (var (requester, lastTicks) in requesters)
        {
            if (now - lastTicks > RequestLifetimeMs)
                continue;

            RouteTo(requester, new JObject
                                   {
                                       ["t"] = SourceType,
                                       ["to"] = requester,
                                       ["from"] = PeerCameraDirectory.LocalMachine,
                                       ["camera"] = camera,
                                       ["address"] = address,
                                       ["status"] = share.Status,
                                   });
        }
    }

    private void RegisterRequest(string camera, string requester)
    {
        if (camera.Length == 0 || requester.Length == 0)
            return;

        if (!_requests.TryGetValue(camera, out var requesters))
        {
            requesters = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            _requests[camera] = requesters;
        }

        requesters[requester] = Environment.TickCount64;
    }

    private void RegisterAnswer(JObject json)
    {
        var machine = Text(json, "from");
        var camera = Text(json, "camera");
        if (machine.Length == 0 || camera.Length == 0)
            return;

        _answers[(machine, camera)] = new Answer(Text(json, "address"), Text(json, "status"), Environment.TickCount64);
    }

    private static bool HasLiveRequester(Dictionary<string, long> requesters, long now) => CountLive(requesters, now) > 0;

    private static int CountLive(Dictionary<string, long> requesters, long now)
    {
        var count = 0;
        foreach (var lastTicks in requesters.Values)
        {
            if (now - lastTicks <= RequestLifetimeMs)
                count++;
        }

        return count;
    }

    /// <summary>Sends a message toward a machine: straight to its peer when we host the session, otherwise via the host.</summary>
    private void RouteTo(string machine, JObject json)
    {
        if (_routes.TryGetValue(machine, out var route) && !route.Peer.SessionClosed)
            route.Transport.SendJson(route.Peer, json);
    }

    private static void SendToAll(CollabTransport transport, bool isHost, JObject message)
    {
        foreach (var peer in transport.Peers)
        {
            if (!peer.SessionClosed && (!isHost || peer.IsAuthenticated))
                transport.SendJson(peer, message);
        }
    }

    /// <summary>Host: passes an announcement on to every participant except the one it came from.</summary>
    private static void Forward(CollabTransport transport, CollabPeerConnection from, JObject json)
    {
        foreach (var peer in transport.Peers)
        {
            if (!ReferenceEquals(peer, from) && !peer.SessionClosed && peer.IsAuthenticated)
                transport.SendJson(peer, json);
        }
    }

    private void ForgetUnknownRoutes()
    {
        _staleKeys.Clear();
        foreach (var (machine, route) in _routes)
        {
            if (route.Peer.SessionClosed || !PeerCameraDirectory.IsMachineKnown(machine))
                _staleKeys.Add(machine);
        }

        foreach (var machine in _staleKeys)
            _routes.Remove(machine);
    }

    /// <summary>The session ended: everything that depended on it goes.</summary>
    private void Reset()
    {
        if (!_wasInSession)
            return;

        _wasInSession = false;
        foreach (var share in _shares.Values)
            share.Dispose();

        _shares.Clear();
        _requests.Clear();
        _answers.Clear();
        _routes.Clear();
        _wantStates.Clear();
        _sessionStates.Clear();
        _lastSourcesHash = 0;
        PeerCameraDirectory.Clear();
        RemoteVideoInputBus.SetPeerSources([]);
    }

    private static string Text(JObject json, string key) => json[key]?.Value<string>() ?? "";

    private sealed class SessionState(CollabTransport transport)
    {
        public readonly CollabTransport Transport = transport;
        public bool IsHost;
        public long NextAnnounceTicks;
    }

    private readonly record struct Route(CollabTransport Transport, CollabPeerConnection Peer);

    private readonly record struct Answer(string Address, string Status, long ReceivedTicks);

    private sealed class WantState(string machine, string camera)
    {
        public readonly string Machine = machine;
        public readonly string Camera = camera;
        public readonly string WaitingText = $"Waiting for the camera on {machine}";
        public readonly string NotConnectedText = $"{machine} is not connected";
        public long NextRequestTicks;
        public bool Seen;
    }

    private sealed class Share : IDisposable
    {
        public IPeerCameraCapture? Capture;
        public IOutputStreamSender? Sender;
        public string Status = "";
        public int RequesterCount;
        public long NextReplyTicks;
        public long RetryTicks;

        public void Dispose()
        {
            Capture?.Dispose();
            Capture = null;
            try
            {
                Sender?.Dispose();
            }
            catch (Exception e)
            {
                Log.Debug($"Peer cameras: closing a camera stream failed: {e.Message}");
            }

            Sender = null;
        }
    }

    private const int AnnounceIntervalMs = 5000;
    private const int AnnounceLifetimeMs = 15000;
    private const int CameraScanIntervalMs = 10000;
    private const int RequestIntervalMs = 2000;
    private const int RequestLifetimeMs = 6000;
    private const int AnswerLifetimeMs = 6000;
    private const int ReplyIntervalMs = 2000;
    private const int ShareRetryMs = 3000;

    private readonly List<SessionState> _sessions = [];
    private readonly Dictionary<CollabTransport, SessionState> _sessionStates = [];
    private readonly List<CollabTransport> _staleTransports = [];
    private readonly Dictionary<string, Route> _routes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string Machine, string Camera), Answer> _answers = [];
    private readonly Dictionary<string, WantState> _wantStates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Share> _shares = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, long>> _requests = new(StringComparer.Ordinal);
    private readonly List<PeerCameraBus.Want> _wants = [];
    private readonly List<RemoteVideoSource> _peerSources = [];
    private readonly List<string> _staleKeys = [];
    private readonly List<string> _reopenKeys = [];
    private readonly List<string> _announcedScratch = [];
    private volatile string[] _localCameras = [];
    private volatile bool _scanning;
    private volatile bool _camerasChanged;
    private bool _wasInSession;
    private long _nextCameraScanTicks;
    private long _nextPruneTicks;
    private int _lastSourcesHash;
}
