#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace T3.Core.Utils;

/// <summary>
/// One selectable IPv4 adapter. Collaboration and boygrouping hosting, LAN discovery and failover
/// heartbeats bind to the adapter picked in their settings, so a multi-homed show machine cannot
/// answer on the wrong network.
/// </summary>
public sealed class LanInterface
{
    public LanInterface(string name, string description, IPAddress address, IPAddress? broadcastAddress, bool supportsMulticast)
    {
        Name = name;
        Description = description;
        Address = address;
        BroadcastAddress = broadcastAddress;
        SupportsMulticast = supportsMulticast;
    }

    /// <summary>Stable adapter name (persisted in the user settings).</summary>
    public string Name { get; }

    public string Description { get; }

    public IPAddress Address { get; }

    /// <summary>Subnet broadcast address, or null when the adapter does not report a mask.</summary>
    public IPAddress? BroadcastAddress { get; }

    public bool SupportsMulticast { get; }

    public string DisplayName => $"{Name} ({Address})";
}

public static class LanInterfaces
{
    /// <summary>
    /// Up, non-tunnel IPv4 adapters, wired first. Loopback is excluded: it cannot carry a session
    /// between machines, and the transports fall back to all interfaces when none is selected.
    /// </summary>
    public static List<LanInterface> GetUsable()
    {
        var result = new List<(int Priority, LanInterface Interface)>();
        try
        {
            foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (networkInterface.OperationalStatus != OperationalStatus.Up
                    || networkInterface.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                    continue;

                IPAddress? address = null;
                IPAddress? mask = null;
                try
                {
                    foreach (var unicast in networkInterface.GetIPProperties().UnicastAddresses)
                    {
                        if (unicast.Address.AddressFamily != AddressFamily.InterNetwork
                            || IPAddress.IsLoopback(unicast.Address))
                            continue;

                        address = unicast.Address;
                        mask = unicast.IPv4Mask;
                        break;
                    }
                }
                catch (NetworkInformationException)
                {
                    // The adapter disappeared between enumeration and inspection.
                    continue;
                }

                if (address == null)
                    continue;

                result.Add((InterfacePriority(networkInterface.NetworkInterfaceType),
                            new LanInterface(networkInterface.Name,
                                             networkInterface.Description,
                                             address,
                                             ComputeBroadcastAddress(address, mask),
                                             networkInterface.SupportsMulticast)));
            }
        }
        catch (NetworkInformationException)
        {
            // An empty list means "automatic"; the caller binds all interfaces.
        }

        return result.OrderBy(entry => entry.Priority)
                     .ThenBy(entry => entry.Interface.Name, StringComparer.OrdinalIgnoreCase)
                     .Select(entry => entry.Interface)
                     .ToList();
    }

    /// <summary>Resolves a persisted adapter name; null when it is empty or the adapter is gone.</summary>
    public static LanInterface? Find(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        return GetUsable().FirstOrDefault(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Address to bind to, or null to bind all interfaces.</summary>
    public static IPAddress? ResolveAddress(string? name) => Find(name)?.Address;

    /// <summary>Directed broadcast for the selected adapter, or null to use the global broadcast.</summary>
    public static IPAddress? ResolveBroadcast(string? name) => Find(name)?.BroadcastAddress;

    /// <summary>Wired adapters sort first; that is what a show machine wants at the top of the list.</summary>
    private static int InterfacePriority(NetworkInterfaceType type)
    {
        return type switch
                   {
                       NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet => 0,
                       NetworkInterfaceType.FastEthernetT or NetworkInterfaceType.FastEthernetFx => 1,
                       NetworkInterfaceType.Wireless80211 => 2,
                       _ => 3,
                   };
    }

    private static IPAddress? ComputeBroadcastAddress(IPAddress address, IPAddress? mask)
    {
        var addressBytes = address.GetAddressBytes();
        var maskBytes = mask?.GetAddressBytes();
        if (addressBytes.Length != 4 || maskBytes == null || maskBytes.Length != 4)
            return null;

        var broadcast = new byte[4];
        for (var i = 0; i < 4; i++)
            broadcast[i] = (byte)(addressBytes[i] | (maskBytes[i] ^ 0xFF));

        return new IPAddress(broadcast);
    }
}
