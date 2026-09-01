#nullable enable
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Lib.laser;
using T3.Core.DataTypes;
using T3.Core.Logging;
using T3.Core.Operator;
using T3.Core.Operator.Attributes;
using T3.Core.Operator.Slots;
using T3.Core.Utils;

namespace Lib.laser.output;

[Guid("7B3F8E2D-1A4C-4F5D-9E6A-2B7C8D9E0F1A")]
internal sealed class CITPLaser : Instance<CITPLaser>, IStatusProvider, ICustomDropdownHolder, IDisposable
{
    #region CITP Protocol Constants (ASCII cookies read as big-endian, integers are little-endian per spec)
    private const uint CITP_COOKIE = 0x43495450; // "CITP"
    private const uint PINF_COOKIE = 0x50494E46; // "PINF"
    private const uint PLOC_COOKIE = 0x504C6F63; // "PLoc"
    private const uint PNAM_COOKIE = 0x504E616D; // "PNam"
    private const uint PTYP_COOKIE = 0x50547970; // "PTyp"
    private const uint CAEX_COOKIE = 0x43414558; // "CAEX"

    private enum CaexContentType : uint
    {
        GetLaserFeedList = 0x00030100,
        LaserFeedList = 0x00030101,
        LaserFeedControl = 0x00030102,
        LaserFeedFrame = 0x00030200,
        EnterShow = 0x00020100
    }

    // Verified working with Capture: 224.0.0.180:4809
    private static readonly IPAddress MulticastAddress = IPAddress.Parse("224.0.0.180");
    private const int MulticastPort = 4809;
    #endregion

    #region Inputs
    [Input(Guid = "8A4B9F3E-2B5D-5E6F-0F7B-3C8D9E0F1A2B")]
    public readonly InputSlot<bool> Enable = new();

    [Input(Guid = "9B5C0A4F-3C6E-6F7A-1A8C-4D9E0F1A2B3C")]
    public readonly InputSlot<string> LocalIpAddress = new();

    [Input(Guid = "0C6D1B5A-4D7F-7A8B-2B9D-5E0F1A2B3C4D")]
    public readonly MultiInputSlot<StructuredList> LaserPointsFeeds = new();

    [Input(Guid = "1D7E2C6B-5E8A-8B9C-3C0E-6F1A2B3C4D5E")]
    public readonly InputSlot<float> ScaleX = new(1.0f / 16.0f);

    [Input(Guid = "2E8F3D7C-6F9A-9A0B-4D1F-7A2B3C4D5E6F")]
    public readonly InputSlot<float> OffsetX = new(0f);

    [Input(Guid = "3F9A4E8D-7A0B-0B1C-5C2A-8A3C4D5E6F7A")]
    public readonly InputSlot<float> ScaleY = new(1.0f / 16.0f);

    [Input(Guid = "4A0B5F9E-8A1C-1C2D-6D3A-9B4D5E6F7A8B")]
    public readonly InputSlot<float> OffsetY = new(0f);

    [Input(Guid = "5B1C6A0F-9A2D-2D3E-7A4B-0B5E6F7A8B9C")]
    public readonly InputSlot<bool> PrintToLog = new();

    [Input(Guid = "6C2D7B10-0B3E-3E4F-8B5C-1C6F7A8B9C9D")]
    public readonly InputSlot<string> FeedName = new("Laser Feed");

    [Input(Guid = "7D3E8C21-1C4F-4F5A-9C6D-2D7A8B9C9D9E")]
    public readonly InputSlot<int> SourceKey = new(0);
    
    [Input(Guid = "8E4F9D32-2D5E-4F6A-9B7C-4D8E9F0A1B2C")]
    public readonly InputSlot<bool> ReconnectTrigger = new();

    [Input(Guid = "9F5A0B43-3E6F-5A7B-0C8D-5E9F0A1B2C3D")]
    public readonly InputSlot<int> CapturePort = new(0);
    #endregion

    #region Outputs
    [Output(Guid = "A1B2C3D4-E5F6-7890-ABCD-EF123456789A")]
    public readonly Slot<bool> IsConnected = new();

    [Output(Guid = "B2C3D4E5-F6A7-8901-BCDE-F123456789AB")]
    public readonly Slot<int> ActiveFeeds = new();

    [Output(Guid = "C3D4E5F6-A7B8-9012-CDEF-0123456789AB")]
    public readonly Slot<bool> FeedActive = new();

    [Output(Guid = "D5E6F7A8-B9C0-1234-CDEF-123456789ABC")]
    public readonly Slot<int> RequiredFps = new();

    [Output(Guid = "D4E5F6A7-A8B9-0123-CDEF-123456789ABC", DirtyFlagTrigger = DirtyFlagTrigger.Animated)]
    public readonly Slot<string> StatusMessage = new();

    [Output(Guid = "E5F6A7B8-B9C0-1234-DEF0-23456789ABCD")]
    public readonly Slot<Command> Command = new();
    #endregion

    #region Private Fields
    private readonly object _connectionLock = new();
    private readonly object _statusLock = new();

    private UdpClient? _multicastListener;
    private TcpClient? _tcpClient;
    private NetworkStream? _tcpStream;
    private Thread? _udpReceiveThread;
    private Thread? _tcpReceiveThread;
    private Thread? _sendThread;
    private CancellationTokenSource? _cts;

    private volatile bool _feedActive;
    private volatile int _requiredFps;

    private readonly ConcurrentQueue<(int feedIndex, LaserPoint[] points, float scaleX, float offsetX, float scaleY, float offsetY)> _frameQueue = new();

    private string _captureIp = string.Empty;
    private int _captureTcpPort;
    private uint _sourceKey;
    private uint _frameSequenceNo;
    private bool _printToLog;
    private int _lastCapturePort;
    private string _lastLocalIp = string.Empty;
    private bool _lastEnable;
    private bool _disposed;
    private string _statusMessage = "Disconnected";
    private IStatusProvider.StatusLevel _statusLevel = IStatusProvider.StatusLevel.Notice;
    private static List<NetworkAdapterInfo> _networkInterfaces = GetNetworkInterfaces();
    private bool _reconnectTrigger;
    private IPAddress? _selectedLocalIp;
    #endregion

    #region Network Adapter Info
    private sealed record NetworkAdapterInfo(IPAddress IpAddress, string Name)
    {
        public string DisplayName => $"{Name}: {IpAddress}";
    }

    private static List<NetworkAdapterInfo> GetNetworkInterfaces()
    {
        var list = new List<NetworkAdapterInfo> { new(IPAddress.Loopback, "Localhost") };
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus == OperationalStatus.Up && ni.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                {
                    foreach (var ip in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (ip.Address.AddressFamily == AddressFamily.InterNetwork)
                        {
                            list.Add(new NetworkAdapterInfo(ip.Address, ni.Name));
                        }
                    }
                }
            }
        }
        catch { /* ignore */ }
        return list;
    }
    #endregion

    #region Constructor & Dispose
    public CITPLaser()
    {
        Command.UpdateAction += Update;
        Command.Value = new Command();
        IsConnected.UpdateAction += Update;
        ActiveFeeds.UpdateAction += Update;
        FeedActive.UpdateAction += Update;
        RequiredFps.UpdateAction += Update;
        StatusMessage.UpdateAction += Update;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopCitpClient(false);
    }
    #endregion

    #region IStatusProvider
    public IStatusProvider.StatusLevel GetStatusLevel()
    {
        lock (_statusLock) return _statusLevel;
    }

    public string GetStatus()
    {
        lock (_statusLock) return _statusMessage;
    }

    public string GetStatusMessage() => GetStatus();
    #endregion

    #region ICustomDropdownHolder
    string ICustomDropdownHolder.GetValueForInput(Guid inputId)
    {
        if (inputId == LocalIpAddress.Id) return LocalIpAddress.Value ?? string.Empty;
        return string.Empty;
    }

    IEnumerable<string> ICustomDropdownHolder.GetOptionsForInput(Guid inputId)
    {
        if (inputId == LocalIpAddress.Id)
        {
            _networkInterfaces = GetNetworkInterfaces();
            foreach (var a in _networkInterfaces) yield return a.DisplayName;
        }
    }

    void ICustomDropdownHolder.HandleResultForInput(Guid inputId, string? selected, bool isAListItem)
    {
        if (string.IsNullOrEmpty(selected) || !isAListItem) return;
        if (inputId == LocalIpAddress.Id)
        {
            var found = _networkInterfaces.FirstOrDefault(i => i.DisplayName == selected);
            if (found != null)
                LocalIpAddress.SetTypedInputValue(found.IpAddress.ToString());
        }
    }
    #endregion

    #region Update Method
    private void Update(EvaluationContext context)
    {
        var enable = Enable.GetValue(context);
        var localIpString = LocalIpAddress.GetValue(context);
        var printToLog = PrintToLog.GetValue(context);
        var sourceKeyParam = SourceKey.GetValue(context);
        var scaleX = ScaleX.GetValue(context);
        var scaleY = ScaleY.GetValue(context);
        var offsetX = OffsetX.GetValue(context);
        var offsetY = OffsetY.GetValue(context);
        var reconnectTriggered = MathUtils.WasTriggered(ReconnectTrigger.GetValue(context), ref _reconnectTrigger);
        var capturePort = CapturePort.GetValue(context);

        _printToLog = printToLog;
        _sourceKey = sourceKeyParam > 0 ? (uint)sourceKeyParam : (uint)Random.Shared.Next();

        if (enable != _lastEnable || localIpString != _lastLocalIp || reconnectTriggered || capturePort != _lastCapturePort)
        {
            if (enable && !string.IsNullOrEmpty(localIpString))
            {
                if (IPAddress.TryParse(localIpString, out var localIp))
                {
                    _selectedLocalIp = localIp;
                    
                    if (capturePort > 0)
                    {
                        lock (_connectionLock)
                        {
                            _captureIp = "127.0.0.1";
                            _captureTcpPort = capturePort;
                        }
                    }
                    
                    if (reconnectTriggered && _printToLog)
                        Log.Debug("CITP Laser: Reconnect triggered.", this);
                    StartCitpClient(localIp);
                }
            }
            else StopCitpClient(false);

            _lastEnable = enable;
            _lastLocalIp = localIpString ?? string.Empty;
            _lastCapturePort = capturePort;
        }

        bool isConnected = false;
        lock (_connectionLock)
        {
            isConnected = _tcpClient != null && _tcpClient.Connected;
        }

        if (isConnected)
        {
            var feedCount = 0;
            var inputs = LaserPointsFeeds.CollectedInputs;
            for (int i = 0; i < inputs.Count; i++)
            {
                var points = inputs[i].GetValue(context);
                if (points is StructuredList<LaserPoint> laserPoints && laserPoints.NumElements > 0)
                {
                    _frameQueue.Enqueue((i, laserPoints.TypedElements, scaleX, offsetX, scaleY, offsetY));
                    feedCount++;
                }
            }
            ActiveFeeds.Value = feedCount;
        }
        else 
        {
            ActiveFeeds.Value = 0;
        }

        IsConnected.Value = isConnected;
        FeedActive.Value = _feedActive;
        RequiredFps.Value = _requiredFps;
        StatusMessage.Value = GetStatus();
        Command.DirtyFlag.Clear();
    }
    #endregion

    #region CITP Client Lifecycle
    private void StartCitpClient(IPAddress localIp)
    {
        try
        {
            StopCitpClient(keepManualSettings: true);

            lock (_connectionLock)
            {
                _cts = new CancellationTokenSource();
                _feedActive = false;
                _requiredFps = 0;

                // FORCING DIRECT CONNECTION TO CAPTURE
                // Bypassing UDP discovery entirely to test handshake
                if (_printToLog)
                    Log.Debug("CITP Laser: Bypassing discovery. Connecting direct to 10.0.0.233:47999.", this);
                
                _captureIp = "10.0.0.233";
                _captureTcpPort = 47999;
                
                ConnectToCapture();
            }
            SetStatus($"Connected (Discovery bypassed) to 127.0.0.1:47999", IStatusProvider.StatusLevel.Notice);
        }
        catch (Exception ex)
        {
            SetStatus($"Failed to start CITP client: {ex.Message}", IStatusProvider.StatusLevel.Error);
        }
    }

    private void StopCitpClient(bool keepManualSettings)
    {
        Thread? udpThreadToJoin = null;
        Thread? tcpThreadToJoin = null;
        Thread? sendThreadToJoin = null;

        lock (_connectionLock)
        {
            _cts?.Cancel();

            if (_multicastListener != null)
            {
                try { _multicastListener.Close(); } catch { /* ignore */ }
                _multicastListener = null;
            }

            StopTcpConnectionInternal();

            udpThreadToJoin = _udpReceiveThread;
            tcpThreadToJoin = _tcpReceiveThread;
            sendThreadToJoin = _sendThread;

            _udpReceiveThread = null;
            _tcpReceiveThread = null;
            _sendThread = null;

            if (!keepManualSettings)
            {
                _captureIp = string.Empty;
                _captureTcpPort = 0;
            }
            
            _feedActive = false;
            _requiredFps = 0;
            
            _frameQueue.Clear();
            SetStatus("Disconnected", IStatusProvider.StatusLevel.Notice);
        }

        if (udpThreadToJoin != null && Thread.CurrentThread != udpThreadToJoin)
            udpThreadToJoin.Join(1000);
        if (tcpThreadToJoin != null && Thread.CurrentThread != tcpThreadToJoin)
            tcpThreadToJoin.Join(1000);
        if (sendThreadToJoin != null && Thread.CurrentThread != sendThreadToJoin)
            sendThreadToJoin.Join(1000);
    }
    #endregion

    #region UDP Receive Loop – discover Capture via PINF/PLoc
    private bool CheckTcpConnected()
    {
        lock (_connectionLock)
        {
            return _tcpClient != null && _tcpClient.Connected;
        }
    }

    private void UdpReceiveLoop()
    {
        CancellationToken token;
        UdpClient? listener;
        lock (_connectionLock)
        {
            if (_cts == null || _multicastListener == null) return;
            token = _cts.Token;
            listener = _multicastListener;
        }

        var remoteEndPoint = new IPEndPoint(IPAddress.Any, 0);

        while (!token.IsCancellationRequested)
        {
            try
            {
                if (CheckTcpConnected()) 
                {
                    // If we're already connected, we will still block on Receive() to clear the socket buffer
                }

                var bytes = listener.Receive(ref remoteEndPoint);
                if (token.IsCancellationRequested) break;
                if (CheckTcpConnected()) continue; // Ignore discovery if already connected

                if (bytes.Length < 16) continue;
                
                var citpCookie = ReadBigEndianUint(bytes, 0);
                if (citpCookie != CITP_COOKIE) continue;

                var remoteIp = remoteEndPoint.Address;
                if (remoteIp.IsIPv4MappedToIPv6) remoteIp = remoteIp.MapToIPv4();

                // If a local IP is selected, prefer discovery packets from the same subnet
                if (_selectedLocalIp != null && !IPAddress.IsLoopback(remoteIp))
                {
                    var localBytes = _selectedLocalIp.GetAddressBytes();
                    var remoteBytes = remoteIp.GetAddressBytes();
                    if (localBytes.Length == 4 && remoteBytes.Length == 4 && localBytes[0] != remoteBytes[0])
                    {
                        continue; 
                    }
                }

                int pInfOffset = -1;
                int pLocOffset = -1;
                for (int i = 12; i <= Math.Min(bytes.Length - 4, 24); i += 4)
                {
                    var val = ReadBigEndianUint(bytes, i);
                    if (val == PINF_COOKIE) pInfOffset = i;
                    if (val == PLOC_COOKIE) pLocOffset = i;
                }

                if (pInfOffset != -1)
                {
                    for (int i = pInfOffset + 4; i <= bytes.Length - 8; i++)
                    {
                        if (ReadBigEndianUint(bytes, i) == PLOC_COOKIE)
                        {
                            TryConnect(remoteIp, i, bytes);
                            break;
                        }
                    }
                }
                else if (pLocOffset != -1)
                {
                    TryConnect(remoteIp, pLocOffset, bytes);
                }
            }
            catch (SocketException ex)
            {
                if (ex.SocketErrorCode == SocketError.TimedOut)
                {
                    // If no PLoc multicast received within timeout, attempt direct connection to localhost Capture
                    if (!CheckTcpConnected() && !token.IsCancellationRequested)
                    {
                        lock (_connectionLock)
                        {
                            if (_captureTcpPort == 0)
                            {
                                if (_printToLog)
                                    Log.Debug("CITP Laser: UDP discovery timed out. Attempting automatic direct localhost connection (127.0.0.1:51245)...", this);
                                _captureIp = "127.0.0.1";
                                _captureTcpPort = 51245;
                            }
                            else
                            {
                                if (_printToLog)
                                    Log.Debug($"CITP Laser: UDP discovery timed out. Using manual connection settings {_captureIp}:{_captureTcpPort}", this);
                            }
                        }
                        ConnectToCapture();
                    }
                    continue;
                }
                break;
            }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested)
                {
                    if (_printToLog) Log.Warning($"CITP Laser: UDP error - {ex.Message}", this);
                    Thread.Sleep(100);
                }
            }
        }
    }

    private void TryConnect(IPAddress address, int cookieIndex, byte[] buffer)
    {
        if (cookieIndex + 5 >= buffer.Length) return;
        
        var tcpPort = (ushort)(buffer[cookieIndex + 4] | (buffer[cookieIndex + 5] << 8)); // Little-endian per CITP spec
        if (tcpPort == 0) return;

        int offset = cookieIndex + 6;
        var peerType = ReadUcs1String(buffer, ref offset);
        var peerName = ReadUcs1String(buffer, ref offset);

        lock (_connectionLock)
        {
            if (_tcpClient != null && _tcpClient.Connected) return;
            _captureIp = address.ToString();
            _captureTcpPort = tcpPort;
        }

        if (_printToLog)
            Log.Debug($"CITP Laser: Attempting connection to {peerName} ({peerType}) at {_captureIp}:{_captureTcpPort}", this);

        ConnectToCapture();
    }
    #endregion

    #region TCP Client Connection
    private void ConnectToCapture()
    {
        string targetIp;
        int targetPort;
        lock (_connectionLock)
        {
            targetIp = _captureIp;
            targetPort = _captureTcpPort;
        }

        if (string.IsNullOrEmpty(targetIp) || targetPort == 0) return;
        if (!IPAddress.TryParse(targetIp, out var ip)) return;

        try
        {
            StopTcpConnection(); // Safely clear old connection without stopping UDP

            var tcpClient = new TcpClient();
            tcpClient.Connect(ip, targetPort);
            var tcpStream = tcpClient.GetStream();

            lock (_connectionLock)
            {
                if (_cts == null || _cts.IsCancellationRequested)
                {
                    tcpStream.Close();
                    tcpClient.Close();
                    return;
                }

                _tcpClient = tcpClient;
                _tcpStream = tcpStream;

                SetStatus($"Connected to Capture at {targetIp}:{targetPort}", IStatusProvider.StatusLevel.Success);
                if (_printToLog) Log.Debug($"CITP Laser: TCP connected to {targetIp}:{targetPort}", this);

                _tcpReceiveThread = new Thread(TcpReceiveLoop)
                {
                    IsBackground = true,
                    Name = "CITP_TCP_Receiver"
                };
                _tcpReceiveThread.Start();
            }

            // Send handshake first
            SendPeerName();
            SendPeerType();
            SendLaserFeedList();
        }
        catch (Exception ex)
        {
            if (_printToLog) Log.Debug($"CITP Laser: Connection to {ip}:{targetPort} refused - {ex.Message}", this);
            SetStatus($"Discovery: {ip} refused connection", IStatusProvider.StatusLevel.Notice);
            StopTcpConnection();
        }
    }

    private void StopTcpConnection()
    {
        Thread? tcpThreadToJoin = null;
        lock (_connectionLock)
        {
            StopTcpConnectionInternal();
            
            tcpThreadToJoin = _tcpReceiveThread;
            _tcpReceiveThread = null;
            
            _feedActive = false;
            _requiredFps = 0;
            _frameQueue.Clear();
            
            // Set status to listening since UDP is still active and waiting for next broadcast
            if (_cts != null && !_cts.IsCancellationRequested)
            {
                SetStatus($"Listening for Capture on {MulticastAddress}:{MulticastPort}", IStatusProvider.StatusLevel.Notice);
            }
        }

        if (tcpThreadToJoin != null && Thread.CurrentThread != tcpThreadToJoin)
            tcpThreadToJoin.Join(1000);
    }

    private void StopTcpConnectionInternal()
    {
        try { _tcpStream?.Close(); } catch { /* ignore */ }
        _tcpStream = null;

        try { _tcpClient?.Close(); } catch { /* ignore */ }
        _tcpClient = null;
    }
    #endregion

    #region TCP Receive Loop – handle CAEX messages from Capture
    private void TcpReceiveLoop()
    {
        CancellationToken token;
        TcpClient? client;
        NetworkStream? stream;
        
        lock (_connectionLock)
        {
            if (_cts == null || _tcpClient == null || _tcpStream == null) return;
            token = _cts.Token;
            client = _tcpClient;
            stream = _tcpStream;
        }

        var buffer = new byte[4096];
        while (!token.IsCancellationRequested && client.Connected)
        {
            try
            {
                var bytesRead = stream.Read(buffer, 0, buffer.Length);
                if (bytesRead == 0) 
                {
                    if (_printToLog) Log.Debug("CITP Laser: TCP connection closed by remote host.", this);
                    StopTcpConnection();
                    break;
                }

                if (bytesRead < 24) continue; // 20-byte CITP header + 4-byte layer content type minimum

                if (_printToLog)
                {
                    var hex = string.Join(" ", buffer.Take(Math.Min(bytesRead, 32)).Select(b => b.ToString("X2")));
                    Log.Debug($"CITP Laser: Received {bytesRead} bytes: {hex}", this);
                }

                if (ReadBigEndianUint(buffer, 0) != CITP_COOKIE) continue;

                var contentType = ReadBigEndianUint(buffer, 16); // ContentType at offset 16 in 20-byte CITP header

                if (contentType == CAEX_COOKIE)
                {
                    if (_printToLog)
                        Log.Debug($"CITP Laser: Identified CAEX message (TCP)", this);

                    var caexType = ReadLittleEndianUint(buffer, 20); // CAEX ContentCode is at offset 20 in 20-byte CITP header

                    switch ((CaexContentType)caexType)
                    {
                        case CaexContentType.GetLaserFeedList:
                            if (_printToLog) Log.Debug("CITP Laser: Received GetLaserFeedList request.", this);
                            SendLaserFeedList();
                            break;
                        case CaexContentType.LaserFeedControl:
                            if (_printToLog) Log.Debug("CITP Laser: Received LaserFeedControl message.", this);
                            HandleLaserFeedControl(buffer, bytesRead);
                            break;
                        case CaexContentType.EnterShow:
                            if (_printToLog) Log.Debug("CITP Laser: Received EnterShow request.", this);
                            SendEnterShow();
                            break;
                        default:
                            if (_printToLog) Log.Debug($"CITP Laser: Received unknown CAEX type: 0x{caexType:X8}", this);
                            break;
                    }
                }
                else
                {
                    if (_printToLog) Log.Debug($"CITP Laser: Received unknown CITP content type: 0x{contentType:X8}", this);
                }
            }
            catch (IOException ex) 
            { 
                if (_printToLog) Log.Debug($"CITP Laser: TCP IOException - {ex.Message}", this);
                StopTcpConnection(); 
                break; 
            }
            catch (ObjectDisposedException) 
            { 
                if (_printToLog) Log.Debug("CITP Laser: TCP ObjectDisposedException.", this);
                StopTcpConnection(); 
                break; 
            }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested)
                {
                    if (_printToLog) Log.Warning($"CITP Laser: TCP error - {ex.Message}", this);
                    StopTcpConnection();
                }
                break;
            }
        }
    }
    #endregion

    #region CAEX Message Handlers

    // Sends an EnterShow response to acknowledge Capture's request
    private void SendEnterShow()
    {
        NetworkStream? stream;
        lock (_connectionLock) stream = _tcpStream;
        if (stream == null) return;

        try
        {
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);

            WriteCitpHeader(writer, CAEX_COOKIE);
            writer.Write((uint)CaexContentType.EnterShow); 

            // Send "T3" as the show name (ucs2/UTF-16 LE)
            var nameBytes = Encoding.Unicode.GetBytes("T3\0");
            writer.Write(nameBytes);

            var data = ms.ToArray();
            var length = (uint)data.Length;
            data[8] = (byte)(length & 0xFF);
            data[9] = (byte)((length >> 8) & 0xFF);
            data[10] = (byte)((length >> 16) & 0xFF);
            data[11] = (byte)((length >> 24) & 0xFF);

            stream.Write(data, 0, data.Length);

            if (_printToLog)
                Log.Debug($"CITP Laser: Sent EnterShow response ({data.Length} bytes)", this);
        }
        catch (Exception ex)
        {
            if (_printToLog) Log.Warning($"CITP Laser: SendEnterShow failed - {ex.Message}", this);
        }
    }

    // Builds and sends a PINF PNam message
    private void SendPeerName()
    {
        NetworkStream? stream;
        lock (_connectionLock) stream = _tcpStream;
        if (stream == null) return;

        try
        {
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);

            // CITP Header (20 bytes) – integers little-endian per spec
            // ContentType in CITP header is PINF_COOKIE
            WriteCitpHeader(writer, PINF_COOKIE);

            // PINF ContentType (4 bytes, ASCII cookie)
            WriteCookieBytes(writer, PNAM_COOKIE);

            // PINF PNam body: ucs1 (ASCII) null-terminated string
            var nameBytes = Encoding.ASCII.GetBytes("T3 CITP Laser\0");
            writer.Write(nameBytes);

            // Update MessageSize in the CITP header (bytes 8-11, little-endian)
            var data = ms.ToArray();
            var length = (uint)data.Length;
            data[8] = (byte)(length & 0xFF);
            data[9] = (byte)((length >> 8) & 0xFF);
            data[10] = (byte)((length >> 16) & 0xFF);
            data[11] = (byte)((length >> 24) & 0xFF);

            stream.Write(data, 0, data.Length);

            if (_printToLog)
                Log.Debug($"CITP Laser: Sent PINF PNam message ({data.Length} bytes)", this);
        }
        catch (Exception ex)
        {
            if (_printToLog) Log.Warning($"CITP Laser: SendPeerName failed - {ex.Message}", this);
        }
    }

    private void SendPeerType()
    {
        NetworkStream? stream;
        lock (_connectionLock) stream = _tcpStream;
        if (stream == null) return;

        try
        {
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);

            WriteCitpHeader(writer, PINF_COOKIE);
            WriteCookieBytes(writer, PTYP_COOKIE);

            var typeBytes = Encoding.ASCII.GetBytes("Laser\0");
            writer.Write(typeBytes);

            var data = ms.ToArray();
            var length = (uint)data.Length;
            data[8] = (byte)(length & 0xFF);
            data[9] = (byte)((length >> 8) & 0xFF);
            data[10] = (byte)((length >> 16) & 0xFF);
            data[11] = (byte)((length >> 24) & 0xFF);

            stream.Write(data, 0, data.Length);

            if (_printToLog)
                Log.Debug($"CITP Laser: Sent PINF PTyp message 'Laser' ({data.Length} bytes)", this);
        }
        catch (Exception ex)
        {
            if (_printToLog) Log.Warning($"CITP Laser: SendPeerType failed - {ex.Message}", this);
        }
    }

    // Builds and sends a LaserFeedList message (EnterSession)
    private void SendLaserFeedList()
    {
        NetworkStream? stream;
        lock (_connectionLock) stream = _tcpStream;
        if (stream == null) return;

        try
        {
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);

            // CITP Header (20 bytes) – integers little-endian per spec
            // ContentType in CITP header is CAEX_COOKIE
            WriteCitpHeader(writer, CAEX_COOKIE);

            // CAEX ContentCode (uint32, little-endian numeric per CAEX spec)
            // Total CAEX header size: 20 (CITP) + 4 (ContentCode) = 24 bytes.
            writer.Write((uint)CaexContentType.LaserFeedList); // BinaryWriter writes LE by default

            // LaserFeedList body: source key + feed count + feed names
            writer.Write(_sourceKey); // uint32 LE

            var feeds = LaserPointsFeeds.CollectedInputs;
            var feedCount = (ushort)feeds.Count;
            writer.Write(feedCount); // uint16 LE

            var baseName = string.IsNullOrWhiteSpace(FeedName.Value) ? "Laser Feed" : FeedName.Value;
            for (int i = 0; i < feedCount; i++) // ucs2 encoding for name as per spec
            {
                var name = $"{baseName} {i}\0";
                var nameBytes = Encoding.Unicode.GetBytes(name); // LE Unicode (UCS-2 LE)
                writer.Write(nameBytes);
            }

            // Update MessageSize in the CITP header (bytes 8-11, little-endian)
            var data = ms.ToArray();
            var length = (uint)data.Length;
            data[8] = (byte)(length & 0xFF);
            data[9] = (byte)((length >> 8) & 0xFF);
            data[10] = (byte)((length >> 16) & 0xFF);
            data[11] = (byte)((length >> 24) & 0xFF);

            stream.Write(data, 0, data.Length);

            if (_printToLog)
                Log.Debug($"CITP Laser: Sent LaserFeedList with {feedCount} feeds ({data.Length} bytes)", this);
        }
        catch (Exception ex)
        {
            if (_printToLog) Log.Warning($"CITP Laser: SendLaserFeedList failed - {ex.Message}", this);
        }
    }

    // Handle LaserFeedControl – capture tells us to start/stop sending frames
    private void HandleLaserFeedControl(byte[] buffer, int length)
    {
        // Structure: FeedIndex (1) + FrameRate (1)
        // Headers: CITP_CAEX_Header (20 bytes CITP Header + 4 bytes ContentCode) = 24 bytes
        if (length < 26) return; // 24 bytes header + 1 byte FeedIndex + 1 byte FrameRate

        var feedIndex = buffer[24]; // FeedIndex is at offset 24 after the 24-byte CAEX header
        var frameRate = buffer[25]; // FrameRate is at offset 25

        _requiredFps = frameRate;
        _feedActive = frameRate != 0;

        if (_printToLog)
        {
            Log.Debug($"CITP Laser: Feed {feedIndex} frameRate = {frameRate} fps (FeedActive: {_feedActive})", this);
        }

        SetStatus($"Feed {feedIndex} {(frameRate != 0 ? $"active at {frameRate} fps" : "stopped")}", IStatusProvider.StatusLevel.Success);
    }
    #endregion

    #region Frame Sending Loop
    private void SendLoop()
    {
        CancellationToken token;
        lock (_connectionLock)
        {
            if (_cts == null) return;
            token = _cts.Token;
        }

        var udpSender = new UdpClient();
        var targetEp = new IPEndPoint(MulticastAddress, MulticastPort);

        while (!token.IsCancellationRequested)
        {
            // Only dequeue if the feed is active – prevents frame loss
            if (_feedActive && _frameQueue.TryDequeue(out var frameData))
            {
                try
                {
                    SendLaserFeedFrame(udpSender, frameData.feedIndex, frameData.points,
                        frameData.scaleX, frameData.offsetX, frameData.scaleY, frameData.offsetY, targetEp);
                }
                catch (Exception ex)
                {
                    if (_printToLog) Log.Warning($"CITP Laser: Send frame failed - {ex.Message}", this);
                }
            }
            else
            {
                Thread.Sleep(1);
            }
        }

        udpSender.Close();
    }

    private void SendLaserFeedFrame(UdpClient udpSender, int feedIndex, LaserPoint[] points,
                                     float scaleX, float offsetX, float scaleY, float offsetY, IPEndPoint target)
    {
        if (points.Length == 0) return;

        try
        {
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);

            // CITP Header (20 bytes) – integers little-endian per spec
            // ContentType in CITP header is CAEX_COOKIE
            WriteCitpHeader(writer, CAEX_COOKIE);

            // CAEX ContentCode (uint32, little-endian numeric per CAEX spec)
            // Total CAEX header size: 20 (CITP) + 4 (ContentCode) = 24 bytes.
            writer.Write((uint)CaexContentType.LaserFeedFrame); // BinaryWriter writes LE

            // LaserFeedFrame body: sourceKey + feedIndex + frameSequence + pointCount
            writer.Write(_sourceKey);                    // uint32 LE
            writer.Write((byte)feedIndex);
            writer.Write(_frameSequenceNo++);             // uint32 LE
            writer.Write((ushort)points.Length);           // uint16 LE

            // Points (5 bytes each)
            foreach (var p in points)
            {
                var cp = ConvertLaserPoint(p, scaleX, offsetX, scaleY, offsetY);
                writer.Write(cp.XLowByte);
                writer.Write(cp.YLowByte);
                writer.Write(cp.XYHighNibbles);
                writer.Write(cp.Color);                   // uint16 LE
            }

            // Update MessageSize in CITP header (little-endian)
            var data = ms.ToArray();
            var length = (uint)data.Length;
            data[8] = (byte)(length & 0xFF);
            data[9] = (byte)((length >> 8) & 0xFF);
            data[10] = (byte)((length >> 16) & 0xFF);
            data[11] = (byte)((length >> 24) & 0xFF);

            udpSender.Send(data, data.Length, target);

            if (_printToLog)
                Log.Debug($"CITP Laser: Sent LaserFeedFrame for feed {feedIndex} with {points.Length} points ({data.Length} bytes)", this);
        }
        catch (Exception ex)
        {
            if (_printToLog) Log.Warning($"CITP Laser: Frame send failed - {ex.Message}", this);
        }
    }
    #endregion

    #region Laser Point Conversion
    private struct CitpLaserPoint
    {
        public byte XLowByte;
        public byte YLowByte;
        public byte XYHighNibbles;
        public ushort Color;
    }

    private CitpLaserPoint ConvertLaserPoint(LaserPoint p, float scaleX, float offsetX, float scaleY, float offsetY)
    {
        var x = (int)((p.X + offsetX) * scaleX) & 0xFFF;
        var y = (int)((p.Y + offsetY) * scaleY) & 0xFFF;

        var r = (p.R >> 3) & 0x1F;
        var g = (p.G >> 2) & 0x3F;
        var b = (p.B >> 3) & 0x1F;
        var color = (ushort)(r | (g << 5) | (b << 11));

        return new CitpLaserPoint
        {
            XLowByte = (byte)(x & 0xFF),
            YLowByte = (byte)(y & 0xFF),
            XYHighNibbles = (byte)(((x >> 8) & 0x0F) | (((y >> 8) & 0x0F) << 4)), // Corrected Y high nibble packing
            Color = color
        };
    }
    #endregion

    #region Helper Methods – Little-Endian CITP Protocol (per spec: "All fields use little endian byte order")
    private static void WriteCitpHeader(BinaryWriter writer, uint contentType)
    {
        // CITP Header: 20 bytes total
        // Integer fields are little-endian; cookies are ASCII byte sequences
        WriteCookieBytes(writer, CITP_COOKIE);     // bytes 0-3: "CITP" (ASCII)
        writer.Write((byte)1);                      // byte 4: VersionMajor
        writer.Write((byte)0);                      // byte 5: VersionMinor
        writer.Write((ushort)0);                    // bytes 6-7: RequestIndex (uint16 LE)
        writer.Write((uint)0);                      // bytes 8-11: MessageSize placeholder (uint32 LE)
        writer.Write((ushort)1);                    // bytes 12-13: MessagePartCount (uint16 LE)
        writer.Write((ushort)0);                    // bytes 14-15: MessagePart (uint16 LE)
        WriteCookieBytes(writer, contentType);      // bytes 16-19: ContentType (ASCII cookie)
    }

    /// <summary>Writes a 4-byte ASCII cookie (e.g. "CITP", "PINF") from its big-endian uint constant.</summary>
    private static void WriteCookieBytes(BinaryWriter writer, uint cookie)
    {
        writer.Write((byte)((cookie >> 24) & 0xFF));
        writer.Write((byte)((cookie >> 16) & 0xFF));
        writer.Write((byte)((cookie >> 8) & 0xFF));
        writer.Write((byte)(cookie & 0xFF));
    }

    /// <summary>Reads a uint32 from buffer in little-endian byte order.</summary>
    private static uint ReadLittleEndianUint(byte[] buffer, int offset)
    {
        return buffer[offset] |
               ((uint)buffer[offset + 1] << 8) |
               ((uint)buffer[offset + 2] << 16) |
               ((uint)buffer[offset + 3] << 24);
    }

    private static uint ReadBigEndianUint(byte[] buffer, int offset)
    {
        return ((uint)buffer[offset] << 24) |
               ((uint)buffer[offset + 1] << 16) |
               ((uint)buffer[offset + 2] << 8) |
               buffer[offset + 3];
    }

    private string ReadUcs1String(byte[] buffer, ref int offset)
    {
        int start = offset;
        while (offset < buffer.Length && buffer[offset] != 0) offset++;
        var length = offset - start;
        var str = length > 0 ? Encoding.ASCII.GetString(buffer, start, length) : string.Empty;
        if (offset < buffer.Length) offset++; // skip null
        return str;
    }

    private void SetStatus(string message, IStatusProvider.StatusLevel level)
    {
        lock (_statusLock)
        {
            _statusMessage = message;
            _statusLevel = level;
        }
    }
    #endregion
}