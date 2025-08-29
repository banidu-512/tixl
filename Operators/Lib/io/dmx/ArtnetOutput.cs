#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using T3.Core.Logging;
using T3.Core.Operator;
using T3.Core.Operator.Attributes;
using T3.Core.Operator.Slots;
using T3.Core.Utils;

namespace Lib.io.dmx;

[Guid("98efc7c8-cafd-45ee-8746-14f37e9f59f8")]
internal sealed class ArtnetOutput : Instance<ArtnetOutput>, IStatusProvider, ICustomDropdownHolder, IDisposable
{
    private const int ArtNetPort = 6454;

    [Output(Guid = "499329d0-15e9-410e-9f61-63724dbec937")]
    public readonly Slot<Command> Result = new();

    public ArtnetOutput() { Result.UpdateAction = Update; }

    private string? _lastLocalIp;
    private string? _lastTargetIp;
    private bool _lastSendUnicast;
    private readonly Stopwatch _stopwatch = new();
    private long _nextFrameTimeTicks;

    private void Update(EvaluationContext context)
    {
        var localIp = LocalIpAddress.GetValue(context);
        var targetIp = TargetIpAddress.GetValue(context);
        var sendUnicast = SendUnicast.GetValue(context);

        var settingsChanged = localIp != _lastLocalIp || targetIp != _lastTargetIp || sendUnicast != _lastSendUnicast;
        if (settingsChanged)
        {
            CloseSocket();
            OpenSocket(localIp);
            _lastLocalIp = localIp;
            _lastTargetIp = targetIp;
            _lastSendUnicast = sendUnicast;
        }

        if (_socket == null) return;

        var maxFps = MaxFps.GetValue(context);
        if (maxFps > 0)
        {
            if (!_stopwatch.IsRunning) _stopwatch.Start();
            while (_stopwatch.ElapsedTicks < _nextFrameTimeTicks)
            {
                if (_nextFrameTimeTicks - _stopwatch.ElapsedTicks > Stopwatch.Frequency / 1000) Thread.Sleep(1);
                else Thread.SpinWait(100);
            }
            _nextFrameTimeTicks += Stopwatch.Frequency / maxFps;
        }

        var startUniverse = StartUniverse.GetValue(context);
        var inputValueLists = InputsValues.GetCollectedTypedInputs();
        SendData(context, startUniverse, inputValueLists);
    }

    private void OpenSocket(string? localIpAddress)
    {
        if (string.IsNullOrEmpty(localIpAddress)) { SetStatus("Local IP not selected.", IStatusProvider.StatusLevel.Warning); return; }
        if (!IPAddress.TryParse(localIpAddress, out var ip)) { SetStatus($"Invalid Local IP.", IStatusProvider.StatusLevel.Warning); return; }

        try
        {
            _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            _socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, true);
            _socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _socket.Bind(new IPEndPoint(ip, 0)); // Bind to a dynamic port for sending
            SetStatus($"Socket ready on {ip}", IStatusProvider.StatusLevel.Success);
        }
        catch (Exception e)
        {
            SetStatus($"Failed to open socket: {e.Message}", IStatusProvider.StatusLevel.Error);
            CloseSocket();
        }
    }

    private void CloseSocket()
    {
        _socket?.Close();
        _socket = null;
    }

    private void SendData(EvaluationContext context, int startUniverse, List<Slot<List<int>>> connections)
    {
        var targetEndPoint = GetTargetEndPoint();
        if (_socket == null || targetEndPoint == null) return;

        var universeIndex = startUniverse;
        foreach (var input in connections)
        {
            var buffer = input.GetValue(context);
            if (buffer == null) continue;
            for (var i = 0; i < buffer.Count; i += 512)
            {
                var chunkCount = Math.Min(buffer.Count - i, 512);
                var sendLength = Math.Max(2, chunkCount);
                if (sendLength % 2 != 0) sendLength++;
                var dmxData = new byte[sendLength];
                for (var j = 0; j < chunkCount; j++) dmxData[j] = (byte)buffer[i + j].Clamp(0, 255);

                using var ms = new MemoryStream(18 + dmxData.Length);
                using var writer = new BinaryWriter(ms);
                writer.Write("Art-Net\0"u8.ToArray());
                writer.Write((short)0x5000); // OpDmx
                writer.Write((short)14);     // Protocol Version
                writer.Write(_sequenceNumber);
                writer.Write((byte)0);      // Physical
                writer.Write((ushort)universeIndex);
                writer.Write((short)IPAddress.HostToNetworkOrder((short)dmxData.Length));
                writer.Write(dmxData);

                try { _socket.SendTo(ms.ToArray(), targetEndPoint); }
                catch (Exception e) { SetStatus($"Send error: {e.Message}", IStatusProvider.StatusLevel.Warning); }
                universeIndex++;
            }
        }
        _sequenceNumber++;
        if (_sequenceNumber == 0) _sequenceNumber = 1;
    }

    private IPEndPoint? GetTargetEndPoint()
    {
        if (SendUnicast.Value && IPAddress.TryParse(TargetIpAddress.Value, out var targetIp))
        {
            return new IPEndPoint(targetIp, ArtNetPort);
        }
        if (IPAddress.TryParse(LocalIpAddress.Value, out var localIp))
        {
            var subnet = GetSubnetForIpAddress(localIp);
            if (subnet != null)
            {
                var broadcast = CalculateBroadcastAddress(localIp, subnet);
                if (broadcast != null) return new IPEndPoint(broadcast, ArtNetPort);
            }
        }
        return new IPEndPoint(IPAddress.Broadcast, ArtNetPort); // Fallback
    }

    public void Dispose() { CloseSocket(); }

    private Socket? _socket;
    private byte _sequenceNumber = 1;

    #region IStatusProvider & ICustomDropdownHolder
    private string? _lastErrorMessage = "Not connected.";
    private IStatusProvider.StatusLevel _lastStatusLevel = IStatusProvider.StatusLevel.Notice;
    public void SetStatus(string m, IStatusProvider.StatusLevel l) { _lastErrorMessage = m; _lastStatusLevel = l; }
    public IStatusProvider.StatusLevel GetStatusLevel() => _lastStatusLevel;
    public string? GetStatusMessage() => _lastErrorMessage;

    string ICustomDropdownHolder.GetValueForInput(Guid id) => id == LocalIpAddress.Id ? LocalIpAddress.Value : string.Empty;
    IEnumerable<string> ICustomDropdownHolder.GetOptionsForInput(Guid id) => id == LocalIpAddress.Id ? GetLocalIPv4Addresses() : Enumerable.Empty<string>();
    void ICustomDropdownHolder.HandleResultForInput(Guid id, string? s, bool i)
    {
        if (string.IsNullOrEmpty(s) || !i || id != LocalIpAddress.Id) return;
        LocalIpAddress.SetTypedInputValue(s.Split(' ')[0]);
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

    private static IPAddress? GetSubnetForIpAddress(IPAddress localIp)
    {
        if (IPAddress.IsLoopback(localIp)) return IPAddress.Parse("255.0.0.0");
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            foreach (var ipInfo in ni.GetIPProperties().UnicastAddresses)
                if (ipInfo.Address.Equals(localIp)) return ipInfo.IPv4Mask;
        }
        return null;
    }

    private static IPAddress? CalculateBroadcastAddress(IPAddress address, IPAddress subnetMask)
    {
        var ipBytes = address.GetAddressBytes();
        var maskBytes = subnetMask.GetAddressBytes();
        for (var i = 0; i < ipBytes.Length; i++) ipBytes[i] |= (byte)~maskBytes[i];
        return new IPAddress(ipBytes);
    }
    #endregion

    [Input(Guid = "9E23335A-D63A-4286-930E-C63E86D0E6F0")] public readonly InputSlot<string> LocalIpAddress = new();
    [Input(Guid = "34aeeda5-72b0-4f13-bfd3-4ad5cf42b24f")] public readonly InputSlot<int> StartUniverse = new(1);
    [Input(Guid = "F7520A37-C2D4-41FA-A6BA-A6ED0423A4EC")] public readonly MultiInputSlot<List<int>> InputsValues = new();
    [Input(Guid = "7c15da5f-cfa1-4339-aceb-4ed0099ea041")] public readonly InputSlot<bool> SendUnicast = new();
    [Input(Guid = "0fc76369-788a-4ffe-9dde-8eea5f10cf32")] public readonly InputSlot<string> TargetIpAddress = new("127.0.0.1");
    [Input(Guid = "C1F747F5-3634-4142-A16D-346743A13728")] public readonly InputSlot<int> MaxFps = new(40);
}