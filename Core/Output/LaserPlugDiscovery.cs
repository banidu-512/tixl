#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using LaserCore.EtherDream.Net.Discovery;
using T3.Core.Logging;

namespace T3.Core.Output;

/// <summary>
/// Device discovery for laser plugs: Ether Dream DACs announce themselves via UDP broadcast, and
/// <see cref="DeviceDiscovery"/> collects them in a shared static table (one listener owns the port;
/// further instances fail softly). The Output Setup's plug card starts the listener when an
/// EtherDream plug is being edited and lists what it found, so binding at a venue is a dropdown
/// instead of a typed IP. Also enumerates this machine's IPv4 interfaces for the plug's interface
/// field. Both lists are cached — the UI reads them every frame, and their queries (LINQ over the
/// shared table, GetAllNetworkInterfaces) range from allocating to outright blocking.
/// </summary>
public static class LaserPlugDiscovery
{
    /// <summary>
    /// Starts the shared broadcast listener if it isn't already running (idempotent, never throws).
    /// DeviceDiscovery only receives inside its own query calls, so a background loop drains the
    /// socket — without it, broadcasts pile up past the kernel buffer and drop, and the shared
    /// table stays empty forever.
    /// </summary>
    public static void EnsureEtherDreamDiscovery()
    {
        if (_started)
            return;

        _started = true;
        try
        {
            _discovery = new DeviceDiscovery();
        }
        catch (Exception e)
        {
            // Address-in-use when the laser driver's stream already owns the port - its shared
            // DiscoveredDevices table still fills, so this is fine.
            Log.Debug($"Laser plug discovery not started ({e.Message}) - reusing any running discovery.");
            return;
        }

        var discovery = _discovery;
        _ = Task.Run(() =>
        {
            while (true)
            {
                try
                {
                    discovery.GetAvailableDevices();
                }
                catch
                {
                    // Disposed (editor shutdown) — nothing left to drain
                    return;
                }

                Thread.Sleep(200);
            }
        });
    }

    private static DeviceDiscovery? _discovery;
    private static bool _started;

    /// <summary>Currently announced Ether Dream DACs as "(name, ip)" pairs, sorted by ip. Cached for a second.</summary>
    public static IReadOnlyList<(string Name, string Ip)> EtherDreamDacs
    {
        get
        {
            var now = Environment.TickCount64;
            if (_cachedDacs != null && now - _dacsTimestamp < 1000)
                return _cachedDacs;

            _dacsTimestamp = now;
            try
            {
                var devices = DeviceDiscovery.DiscoveredDevices;
                if (devices == null || devices.Count == 0)
                    return _cachedDacs = Array.Empty<(string, string)>();

                _cachedDacs = devices.Values
                                     .Select(d => (DeviceDiscovery.GetDeviceName(d), d.Ip))
                                     .OrderBy(pair => pair.Item2, StringComparer.OrdinalIgnoreCase)
                                     .ToArray();
            }
            catch
            {
                _cachedDacs = Array.Empty<(string, string)>();
            }

            return _cachedDacs;
        }
    }

    private static IReadOnlyList<(string Name, string Ip)>? _cachedDacs;
    private static long _dacsTimestamp = long.MinValue;

    /// <summary>
    /// The device an output op is currently streaming to, reported by the op: a connected DAC
    /// stops its discovery broadcasts, so without this the plug's picker would offer nothing
    /// exactly while a show is running. Set/cleared from the ops; read on the UI thread.
    /// </summary>
    public static (string Name, string Ip)? ConnectedDevice { get; private set; }

    public static void SetConnectedDevice(string name, string ip)
    {
        ConnectedDevice = (name, ip);
        _dacsTimestamp = long.MinValue; // the picker re-lists at once
    }

    public static void ClearConnectedDevice(string ip)
    {
        if (ConnectedDevice is { } device && device.Ip == ip)
        {
            ConnectedDevice = null;
            _dacsTimestamp = long.MinValue;
        }
    }

    /// <summary>
    /// This machine's IPv4 addresses with their interface names ("ethernet: 10.0.0.69"), cached for
    /// ten seconds. GetAllNetworkInterfaces queries the network stack and can block for tens to
    /// hundreds of milliseconds — it must never run per frame, or the UI hangs in steps whenever
    /// Windows refreshes its adapter state.
    /// </summary>
    public static IReadOnlyList<(string Name, string Ip)> InterfaceAddresses
    {
        get
        {
            var now = Environment.TickCount64;
            if (_cachedInterfaces != null && now - _interfacesTimestamp < 10000)
                return _cachedInterfaces;

            _interfacesTimestamp = now;
            var result = new List<(string, string)>
                             {
                                 // Always offered: bind-all and loopback (local simulators/receivers).
                                 ("any interface", "0.0.0.0"),
                                 ("localhost", "127.0.0.1"),
                             };
            try
            {
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback
                        || nic.OperationalStatus != OperationalStatus.Up)
                        continue;

                    foreach (var address in nic.GetIPProperties().UnicastAddresses)
                    {
                        if (address.Address.AddressFamily != AddressFamily.InterNetwork)
                            continue;

                        result.Add((nic.Name, address.Address.ToString()));
                    }
                }
            }
            catch
            {
                // A NIC vanishing mid-enumeration is not worth a dialog
            }

            _cachedInterfaces = result;
            return _cachedInterfaces;
        }
    }

    private static IReadOnlyList<(string Name, string Ip)>? _cachedInterfaces;
    private static long _interfacesTimestamp = long.MinValue;
}
