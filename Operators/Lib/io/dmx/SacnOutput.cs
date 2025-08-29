#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using T3.Core.Logging;
using T3.Core.Operator;
using T3.Core.Operator.Attributes;
using T3.Core.Operator.Slots;
using T3.Core.Utils;

// ReSharper disable MemberCanBePrivate.Global

namespace Lib.io.dmx;

[Guid("e5a8d9e6-3c5a-4bbb-9da3-737b6330b9c3")]
internal sealed class SacnOutput : Instance<SacnOutput>, IStatusProvider, ICustomDropdownHolder
{
    private const int SacnPort = 5568;
    private const string SacnDiscoveryIp = "239.255.250.214";

    [Output(Guid = "a3c4a2e8-bc1b-453a-9773-1952a6ea10a3")]
    public readonly Slot<Command> Result = new();

    private Thread? _discoveryListenerThread;
    private volatile bool _isDiscovering;
    private UdpClient? _discoveryUdpClient;
    private readonly ConcurrentDictionary<string, string> _discoveredSources = new();
    private readonly byte[] _cid = Guid.NewGuid().ToByteArray();
    private readonly Stopwatch _stopwatch = new();
    private long _nextFrameTimeTicks;

    public SacnOutput()
    {
        Result.UpdateAction = Update;
    }

    private void Update(EvaluationContext context)
    {
        var maxFps = MaxFps.GetValue(context);
        if (maxFps > 0)
        {
            if (!_stopwatch.IsRunning) _stopwatch.Start();
            while (_stopwatch.ElapsedTicks < _nextFrameTimeTicks)
            {
                if (_nextFrameTimeTicks - _stopwatch.ElapsedTicks > Stopwatch.Frequency / 1000) Thread.Sleep(1);
                else Thread.SpinWait(100);
            }
            _nextFrameTimeTicks += (long)(Stopwatch.Frequency / (double)maxFps);
        }

        _enableSending = SendTrigger.GetValue(context);
        var settingsChanged = _connectionSettings.Update(LocalIpAddress.GetValue(context), TargetIpAddress.GetValue(context), SendUnicast.GetValue(context));

        if (Reconnect.GetValue(context) || settingsChanged)
        {
            Reconnect.SetTypedInputValue(false);
            Log.Debug("Reconnecting sACN socket...", this);
            CloseSocket();
            _connected = TryConnectSacn(_connectionSettings.LocalIp);
        }

        var discoverSources = DiscoverSources.GetValue(context);
        if (discoverSources && !_isDiscovering) StartSacnDiscovery();
        else if (!discoverSources && _isDiscovering) StopSacnDiscovery();

        if (!_enableSending || !_connected || _socket == null) return;

        var startUniverse = Math.Max(1, StartUniverse.GetValue(context));
        var priority = Priority.GetValue(context).Clamp(0, 200);
        var sourceName = SourceName.GetValue(context);
        var enableSync = EnableSync.GetValue(context);
        var syncUniverse = SyncUniverse.GetValue(context);

        SendData(context, startUniverse, priority, sourceName, enableSync, syncUniverse, _sequenceNumber, InputsValues.GetCollectedTypedInputs());
        _sequenceNumber++;
    }

    private void StartSacnDiscovery()
    {
        Log.Debug("Starting sACN Discovery Listener...", this);
        _isDiscovering = true;
        _discoveredSources.Clear();
        _discoveryListenerThread = new Thread(ListenForSacnDiscovery) { IsBackground = true, Name = "sACNDiscoveryListener" };
        _discoveryListenerThread.Start();
    }

    private void StopSacnDiscovery()
    {
        if (!_isDiscovering) return;
        Log.Debug("Stopping sACN Discovery.", this);
        _isDiscovering = false;
        _discoveryUdpClient?.Close();
    }

    private void ListenForSacnDiscovery()
    {
        try
        {
            _discoveryUdpClient = new UdpClient();
            var localEp = new IPEndPoint(IPAddress.Any, SacnPort);
            _discoveryUdpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _discoveryUdpClient.Client.Bind(localEp);
            _discoveryUdpClient.JoinMulticastGroup(IPAddress.Parse(SacnDiscoveryIp));

            while (_isDiscovering)
            {
                try
                {
                    var remoteEp = new IPEndPoint(IPAddress.Any, 0);
                    var data = _discoveryUdpClient.Receive(ref remoteEp);
                    if (data.Length <= 125) continue;

                    var sourceName = Encoding.UTF8.GetString(data, 44, 64).TrimEnd('\0');
                    var ipString = remoteEp.Address.ToString();
                    var displayName = string.IsNullOrWhiteSpace(sourceName) ? ipString : sourceName;

                    _discoveredSources[ipString] = $"{displayName} ({ipString})";
                }
                catch (SocketException) { if (_isDiscovering) break; }
            }
        }
        catch (Exception e) { if (_isDiscovering) Log.Error($"sACN discovery listener failed: {e.Message}", this); }
    }

    private void SendData(EvaluationContext context, int startUniverse, int priority, string sourceName, bool enableSync, int syncUniverse, byte sequenceNumber, List<Slot<List<int>>> connections)
    {
        if (_socket == null) return;
        var syncAddress = enableSync ? (ushort)syncUniverse.Clamp(1, 63999) : (ushort)0;
        var universeIndex = startUniverse;
        foreach (var input in connections)
        {
            var buffer = input.GetValue(context);
            if (buffer == null || buffer.Count == 0) continue;
            for (var i = 0; i < buffer.Count; i += 512)
            {
                var chunkCount = Math.Min(buffer.Count - i, 512);
                var dmxData = new byte[chunkCount];
                for (var j = 0; j < chunkCount; j++) dmxData[j] = (byte)buffer[i + j].Clamp(0, 255);
                try
                {
                    var packet = BuildSacnDataPacket(universeIndex, priority, sourceName, dmxData, syncAddress, sequenceNumber);
                    var targetEndPoint = (_connectionSettings.SendUnicast && _connectionSettings.TargetIp != null)
                                             ? new IPEndPoint(_connectionSettings.TargetIp, SacnPort)
                                             : new IPEndPoint(GetSacnMulticastAddress(universeIndex), SacnPort);
                    _socket.SendTo(packet, targetEndPoint);
                }
                catch (SocketException e)
                {
                    Log.Warning($"sACN send failed for universe {universeIndex}: {e.Message}", this);
                    _connected = false; return;
                }
                universeIndex++;
            }
        }
        if (enableSync) SendSacnSync(syncAddress, sequenceNumber);
    }

    private void SendSacnSync(ushort syncAddress, byte sequenceNumber)
    {
        if (_socket == null) return;
        try
        {
            var syncPacket = BuildSacnSyncPacket(syncAddress, sequenceNumber);
            var syncEndPoint = new IPEndPoint(GetSacnMulticastAddress(syncAddress), SacnPort);
            _socket.SendTo(syncPacket, syncEndPoint);
        }
        catch (SocketException e)
        {
            Log.Warning($"Failed to send sACN sync packet to universe {syncAddress}: {e.Message}", this);
            _connected = false;
        }
    }

    private byte[] BuildSacnSyncPacket(ushort syncUniverse, byte sequenceNumber)
    {
        using var ms = new MemoryStream(49);
        using var writer = new BinaryWriter(ms);
        writer.Write(new byte[] { 0, 0x10, 0, 0, 65, 83, 67, 45, 69, 49, 46, 49, 55, 0, 0, 0 });
        writer.Write((ushort)IPAddress.HostToNetworkOrder((short)(0x7000 | 33)));
        writer.Write(IPAddress.HostToNetworkOrder(0x00000004));
        writer.Write(_cid);
        writer.Write((ushort)IPAddress.HostToNetworkOrder((short)(0x7000 | 9)));
        writer.Write(IPAddress.HostToNetworkOrder(0x00000001));
        writer.Write(sequenceNumber);
        writer.Write((ushort)IPAddress.HostToNetworkOrder(syncUniverse));
        writer.Write((ushort)0);
        return ms.ToArray();
    }

    private byte[] BuildSacnDataPacket(int universe, int priority, string sourceName, byte[] dmxData, ushort syncUniverse, byte sequenceNumber)
    {
        using var ms = new MemoryStream(126 + dmxData.Length);
        using var writer = new BinaryWriter(ms);
        writer.Write(new byte[] { 0, 0x10, 0, 0, 65, 83, 67, 45, 69, 49, 46, 49, 55, 0, 0, 0 });
        writer.Write((ushort)IPAddress.HostToNetworkOrder((short)(0x7000 | (110 + dmxData.Length))));
        writer.Write(IPAddress.HostToNetworkOrder(0x00000004));
        writer.Write(_cid);
        writer.Write((ushort)IPAddress.HostToNetworkOrder((short)(0x7000 | (88 + dmxData.Length))));
        writer.Write(IPAddress.HostToNetworkOrder(0x00000002));
        var sourceNameBytes = new byte[64];
        Encoding.UTF8.GetBytes(sourceName, 0, Math.Min(sourceName.Length, 63), sourceNameBytes, 0);
        writer.Write(sourceNameBytes);
        writer.Write((byte)priority);
        writer.Write((ushort)IPAddress.HostToNetworkOrder(syncUniverse));
        writer.Write(sequenceNumber);
        writer.Write((byte)0x00);
        writer.Write((ushort)IPAddress.HostToNetworkOrder((short)universe));
        writer.Write((ushort)IPAddress.HostToNetworkOrder((short)(0x7000 | (11 + dmxData.Length))));
        writer.Write((byte)0x02);
        writer.Write((byte)0xa1);
        writer.Write((ushort)0);
        writer.Write((ushort)IPAddress.HostToNetworkOrder((short)0x0001));
        writer.Write((ushort)IPAddress.HostToNetworkOrder((short)(dmxData.Length + 1)));
        writer.Write((byte)0x00);
        writer.Write(dmxData);
        return ms.ToArray();
    }

    protected override void Dispose(bool isDisposing)
    {
        if (isDisposing)
        {
            StopSacnDiscovery();
            CloseSocket();
        }
    }

    private void CloseSocket()
    {
        _socket?.Close();
        _socket = null;
        _connected = false;
    }

    private bool TryConnectSacn(IPAddress? localIp)
    {
        if (localIp == null)
        {
            _lastErrorMessage = "Local IP Address is not valid.";
            return false;
        }
        try
        {
            _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            _socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, true);
            _socket.Bind(new IPEndPoint(localIp, 0));
            _socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 10);
            _lastErrorMessage = null;
            return _connected = true;
        }
        catch (Exception e)
        {
            _lastErrorMessage = $"Failed to bind sACN socket to {localIp}: {e.Message}";
            CloseSocket();
            return false;
        }
    }

    private static IPAddress GetSacnMulticastAddress(int universe)
    {
        var u = (ushort)universe.Clamp(1, 63999);
        return new IPAddress(new byte[] { 239, 255, (byte)(u >> 8), (byte)(u & 0xFF) });
    }

    private static IEnumerable<string> GetLocalIPv4Addresses()
    {
        yield return "127.0.0.1";
        if (!NetworkInterface.GetIsNetworkAvailable()) yield break;
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            foreach (var ipInfo in ni.GetIPProperties().UnicastAddresses)
                if (ipInfo.Address.AddressFamily == AddressFamily.InterNetwork)
                    yield return ipInfo.Address.ToString();
        }
    }

    private Socket? _socket;
    private bool _connected;
    private bool _enableSending;
    private byte _sequenceNumber;
    private readonly ConnectionSettings _connectionSettings = new();
    private string? _lastErrorMessage;

    public IStatusProvider.StatusLevel GetStatusLevel() => _connected ? IStatusProvider.StatusLevel.Success : IStatusProvider.StatusLevel.Warning;
    public string? GetStatusMessage() => _lastErrorMessage;

    #region ICustomDropdownHolder implementation
    string ICustomDropdownHolder.GetValueForInput(Guid inputId)
    {
        if (inputId == LocalIpAddress.Id) return LocalIpAddress.Value;
        if (inputId == TargetIpAddress.Id) return TargetIpAddress.Value;
        return string.Empty;
    }

    // FIX: This method now correctly uses `yield return` for all paths.
    IEnumerable<string> ICustomDropdownHolder.GetOptionsForInput(Guid inputId)
    {
        if (inputId == LocalIpAddress.Id)
        {
            foreach (var address in GetLocalIPv4Addresses())
            {
                yield return address;
            }
        }
        else if (inputId == TargetIpAddress.Id)
        {
            if (!_isDiscovering && _discoveredSources.IsEmpty)
            {
                yield return "Enable 'Discover Sources' to search...";
            }
            else if (_isDiscovering && _discoveredSources.IsEmpty)
            {
                yield return "Searching for sources...";
            }
            else
            {
                foreach (var sourceName in _discoveredSources.Values.OrderBy(name => name))
                {
                    yield return sourceName;
                }
            }
        }
    }

    void ICustomDropdownHolder.HandleResultForInput(Guid inputId, string? selected, bool isAListItem)
    {
        if (string.IsNullOrEmpty(selected) || !isAListItem) return;
        var finalIp = selected;
        if (inputId == TargetIpAddress.Id)
        {
            var match = Regex.Match(selected, @"\(([^)]*)\)");
            if (match.Success) finalIp = match.Groups[1].Value;
        }
        if (inputId == LocalIpAddress.Id) LocalIpAddress.SetTypedInputValue(finalIp);
        else if (inputId == TargetIpAddress.Id) TargetIpAddress.SetTypedInputValue(finalIp);
    }
    #endregion

    [Input(Guid = "2a8d39a3-5a41-477d-815a-8b8b9d8b1e4a")] public readonly MultiInputSlot<List<int>> InputsValues = new();
    [Input(Guid = "1b26f5d5-8141-4b13-b88d-6859ed5a4af8")] public readonly InputSlot<int> StartUniverse = new(1);
    [Input(Guid = "f8a7e0c8-c6c7-4b53-9a3a-3e5f2a4f4e1c")] public readonly InputSlot<string> LocalIpAddress = new();
    [Input(Guid = "9c233633-959f-4447-b248-4d431c1b18e7")] public readonly InputSlot<bool> SendTrigger = new();
    [Input(Guid = "c2a9e3e3-a4e9-430b-9c6a-4e1a1e0b8e2e")] public readonly InputSlot<bool> Reconnect = new();
    [Input(Guid = "8c6c9a8d-29c5-489e-8c6b-9e4a3c1e2b6a")] public readonly InputSlot<bool> SendUnicast = new();
    [Input(Guid = "d9e8d7c6-b5a4-434a-9e3a-4e2b1d0c9a7b")] public readonly InputSlot<string> TargetIpAddress = new();
    [Input(Guid = "3f25c04c-0a88-42fb-93d3-05992b861e61")] public readonly InputSlot<bool> DiscoverSources = new();
    [Input(Guid = "4a9e2d3b-8c6f-4b1d-8d7e-9f3a5b2c1d0e")] public readonly InputSlot<int> Priority = new(100);
    [Input(Guid = "5b1d9c8a-7e3f-4a2b-9c8d-1e0f3a5b2c1d")] public readonly InputSlot<string> SourceName = new("T3 sACN Output");
    [Input(Guid = "6f5c4b3a-2e1d-4f9c-8a7b-3d2e1f0c9b8a")] public readonly InputSlot<int> MaxFps = new(60);
    [Input(Guid = "7a8b9c0d-1e2f-3a4b-5c6d-7e8f9a0b1c2d")] public readonly InputSlot<bool> EnableSync = new();
    [Input(Guid = "8b9c0d1e-2f3a-4b5c-6d7e-8f9a0b1c2d3e")] public readonly InputSlot<int> SyncUniverse = new(64001);

    private sealed class ConnectionSettings
    {
        public IPAddress? LocalIp { get; private set; }
        public IPAddress? TargetIp { get; private set; }
        public bool SendUnicast { get; private set; }
        private string? _lastLocalIpStr;
        private string? _lastTargetIpStr;
        private bool _lastSendUnicast;

        public bool Update(string? localIpStr, string? targetIpStr, bool sendUnicast)
        {
            var changed = _lastLocalIpStr != localIpStr ||
                          _lastTargetIpStr != targetIpStr ||
                          _lastSendUnicast != sendUnicast;

            if (!changed) return false;

            _lastLocalIpStr = localIpStr;
            _lastTargetIpStr = targetIpStr;
            _lastSendUnicast = sendUnicast;
            SendUnicast = sendUnicast;

            IPAddress.TryParse(localIpStr, out var parsedLocalIp);
            LocalIp = parsedLocalIp;

            if (sendUnicast)
            {
                // FIX: Parse to a local variable first before assigning to the property.
                IPAddress.TryParse(targetIpStr, out var parsedTargetIp);
                TargetIp = parsedTargetIp;
            }
            else
            {
                TargetIp = null;
            }
            return true;
        }
    }
}