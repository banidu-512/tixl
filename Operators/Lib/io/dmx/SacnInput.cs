#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
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

[Guid("e3207424-deaf-4462-acd5-21f2c6f16d1b")]
internal sealed class SacnInput : Instance<SacnInput>, IStatusProvider, ICustomDropdownHolder, IDisposable
{
    private const int SacnPort = 5568;
    private static readonly byte[] SacnId = "ASC-E1.17\0\0\0"u8.ToArray();
    private const int MinPacketLength = 126;

    [Output(Guid = "b0bcc3de-de79-42ac-a9cc-ec5a699f252b", DirtyFlagTrigger = DirtyFlagTrigger.Animated)]
    public readonly Slot<List<int>> Result = new();

    public SacnInput()
    {
        Result.UpdateAction = Update;
    }

    private void Update(EvaluationContext context)
    {
        var active = Active.GetValue(context);
        var localIp = LocalIpAddress.GetValue(context);
        var startUniverse = StartUniverse.GetValue(context);
        var numUniverses = NumUniverses.GetValue(context).Clamp(1, 4096);

        var settingsChanged = active != _isListening || localIp != _lastLocalIp;
        if (settingsChanged)
        {
            _isListening = active;
            StopListening();
            if (active) StartListening();
            _lastLocalIp = localIp;
        }

        if (_isListening)
        {
            UpdateUniverseSubscriptions(startUniverse, numUniverses);
        }

        CleanupStaleUniverses(Timeout.GetValue(context));

        var combinedDmxData = new List<int>(numUniverses * 512);
        for (var i = 0; i < numUniverses; i++)
        {
            var currentUniverseId = startUniverse + i;
            if (_receivedUniverses.TryGetValue(currentUniverseId, out var universeData))
            {
                var dmxSnapshot = new int[512];
                lock (universeData.DmxData)
                {
                    for (var j = 0; j < 512; j++) dmxSnapshot[j] = universeData.DmxData[j];
                }
                combinedDmxData.AddRange(dmxSnapshot);
            }
            else
            {
                combinedDmxData.AddRange(Enumerable.Repeat(0, 512));
            }
        }
        Result.Value = combinedDmxData;
        UpdateStatusMessage(numUniverses, startUniverse);
    }

    private void StartListening()
    {
        if (_listenerThread is { IsAlive: true }) return;
        _runListener = true;
        _listenerThread = new Thread(ListenLoop) { IsBackground = true, Name = "SacnInputListener" };
        _listenerThread.Start();
    }

    private void StopListening()
    {
        if (!_runListener) return;
        _runListener = false;
        _udpClient?.Close();
        _listenerThread?.Join(500);
        _listenerThread = null;
        _subscribedUniverses.Clear();
    }

    private void ListenLoop()
    {
        try
        {
            var localIpStr = LocalIpAddress.Value;
            var listenIp = IPAddress.Any;
            if (!string.IsNullOrEmpty(localIpStr) && localIpStr != "0.0.0.0 (Any)" && IPAddress.TryParse(localIpStr, out var parsedIp))
                listenIp = parsedIp;
            _boundIpAddress = listenIp;

            _udpClient = new UdpClient { ExclusiveAddressUse = false };
            _udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _udpClient.Client.Bind(new IPEndPoint(_boundIpAddress, SacnPort));

            var remoteEndPoint = new IPEndPoint(IPAddress.Any, 0);
            while (_runListener)
            {
                try
                {
                    var data = _udpClient.Receive(ref remoteEndPoint);
                    if (data.Length < MinPacketLength || !data.AsSpan(4, 12).SequenceEqual(SacnId)) continue;
                    var universe = (data[113] << 8) | data[114];
                    var propertyValueCount = (data[123] << 8) | data[124];
                    var dmxLength = propertyValueCount - 1;
                    if (dmxLength <= 0 || dmxLength > 512 || data.Length < 126 + dmxLength) continue;
                    var universeData = _receivedUniverses.GetOrAdd(universe, _ => new UniverseData());
                    lock (universeData.DmxData)
                    {
                        System.Buffer.BlockCopy(data, 126, universeData.DmxData, 0, dmxLength);
                        if (dmxLength < 512) Array.Clear(universeData.DmxData, dmxLength, 512 - dmxLength);
                    }
                    universeData.LastReceivedTicks = Stopwatch.GetTimestamp();
                    Result.DirtyFlag.Invalidate();
                }
                catch (SocketException) { break; }
                catch (Exception e) { if (_runListener) Log.Error($"sACN receive error: {e.Message}", this); }
            }
        }
        catch (Exception e) { SetStatus($"Failed to bind sACN socket: {e.Message}", IStatusProvider.StatusLevel.Error); }
        finally { _udpClient?.Close(); }
    }

    private void UpdateUniverseSubscriptions(int startUniverse, int numUniverses)
    {
        if (!_runListener || _udpClient == null || _boundIpAddress == null) return;
        var requiredUniverses = new HashSet<int>(Enumerable.Range(startUniverse, numUniverses));
        var universesToUnsubscribe = _subscribedUniverses.Except(requiredUniverses).ToList();
        var universesToSubscribe = requiredUniverses.Except(_subscribedUniverses).ToList();

        foreach (var uni in universesToUnsubscribe)
        {
            try
            {
                _udpClient.DropMulticastGroup(GetSacnMulticastAddress(uni));
                _subscribedUniverses.Remove(uni);
            }
            catch (Exception e) { Log.Warning($"Failed to unsubscribe from sACN universe {uni}: {e.Message}", this); }
        }
        foreach (var uni in universesToSubscribe)
        {
            try
            {
                _udpClient.JoinMulticastGroup(GetSacnMulticastAddress(uni), _boundIpAddress);
                _subscribedUniverses.Add(uni);
            }
            catch (Exception e) { Log.Warning($"Failed to subscribe to sACN universe {uni}: {e.Message}", this); }
        }
    }

    private void CleanupStaleUniverses(float timeoutInSeconds)
    {
        if (timeoutInSeconds <= 0) return;
        var timeoutTicks = (long)(timeoutInSeconds * Stopwatch.Frequency);
        var nowTicks = Stopwatch.GetTimestamp();
        var staleUniverses = _receivedUniverses.Where(pair => (nowTicks - pair.Value.LastReceivedTicks) > timeoutTicks).Select(pair => pair.Key).ToList();
        foreach (var universeId in staleUniverses)
        {
            if (_receivedUniverses.TryRemove(universeId, out _))
                Result.DirtyFlag.Invalidate();
        }
    }

    private void UpdateStatusMessage(int numUniverses, int startUniverse)
    {
        if (!_isListening) SetStatus("Inactive. Enable 'Active'.", IStatusProvider.StatusLevel.Notice);
        else if (_lastStatusLevel != IStatusProvider.StatusLevel.Error)
        {
            var receivedCount = _receivedUniverses.Count;
            if (receivedCount == 0) SetStatus($"Listening on port {SacnPort} for {numUniverses} universes (from {startUniverse})... No packets received.", IStatusProvider.StatusLevel.Warning);
            else SetStatus($"Listening for {numUniverses} universes. Receiving {receivedCount} universes.", IStatusProvider.StatusLevel.Success);
        }
    }

    // FIX: Full implementation provided.
    private static IPAddress GetSacnMulticastAddress(int universe)
    {
        var highByte = (byte)(universe >> 8);
        var lowByte = (byte)(universe & 0xFF);
        return new IPAddress(new byte[] { 239, 255, highByte, lowByte });
    }

    public void Dispose() { StopListening(); }

    private sealed class UniverseData { public readonly byte[] DmxData = new byte[512]; public long LastReceivedTicks; }
    private Thread? _listenerThread;
    private volatile bool _runListener;
    private bool _isListening;
    private string? _lastLocalIp;
    private IPAddress? _boundIpAddress;
    private UdpClient? _udpClient;
    private readonly ConcurrentDictionary<int, UniverseData> _receivedUniverses = new();
    private readonly HashSet<int> _subscribedUniverses = new();

    #region IStatusProvider & ICustomDropdownHolder
    private string _lastStatusMessage = string.Empty;
    private IStatusProvider.StatusLevel _lastStatusLevel;
    public void SetStatus(string message, IStatusProvider.StatusLevel level) { _lastStatusMessage = message; _lastStatusLevel = level; }
    public IStatusProvider.StatusLevel GetStatusLevel() => _lastStatusLevel;
    public string GetStatusMessage() => _lastStatusMessage;

    string ICustomDropdownHolder.GetValueForInput(Guid inputId) => inputId == LocalIpAddress.Id ? LocalIpAddress.Value : string.Empty;
    IEnumerable<string> ICustomDropdownHolder.GetOptionsForInput(Guid inputId) => inputId == LocalIpAddress.Id ? GetLocalIPv4Addresses() : Enumerable.Empty<string>();

    void ICustomDropdownHolder.HandleResultForInput(Guid inputId, string? selected, bool isAListItem)
    {
        if (string.IsNullOrEmpty(selected) || !isAListItem || inputId != LocalIpAddress.Id) return;
        var ip = selected.Split(' ')[0];
        LocalIpAddress.SetTypedInputValue(ip);
    }

    // FIX: Full implementation provided.
    private static IEnumerable<string> GetLocalIPv4Addresses()
    {
        yield return "0.0.0.0 (Any)";
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
    #endregion

    [Input(Guid = "24B5D450-4E83-49DB-88B1-7D688E64585D")] public readonly InputSlot<string> LocalIpAddress = new("0.0.0.0");
    [Input(Guid = "ca55c1b3-0669-46f1-bcc4-ee2e7f5a6028")] public readonly InputSlot<bool> Active = new();
    [Input(Guid = "0c348760-474e-4e30-a8c1-55e59cb1a908")] public readonly InputSlot<int> StartUniverse = new(1);
    [Input(Guid = "2cffbf1c-ce09-4283-a685-5234e4e49fee")] public readonly InputSlot<int> NumUniverses = new(1);
    [Input(Guid = "bed01653-6cd0-4578-81a9-3eda144ab279")] public readonly InputSlot<float> Timeout = new();
}