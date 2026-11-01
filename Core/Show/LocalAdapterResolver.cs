#nullable enable
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace T3.Core.Show;

/// <summary>
/// Chooses the network adapter a DMX output binds to. A project shared by a failover pair or a boygroup stores one
/// local address, but every machine has its own, so a machine that does not hold the stored address binds the
/// adapter in the same subnet instead (then one in the target's subnet, then the first usable one).
/// </summary>
public sealed class LocalAdapterResolver
{
    /// <summary>The address to bind: the stored one when this machine has it, otherwise the substitute.</summary>
    public string? EffectiveIp { get; private set; }

    /// <summary>Subnet mask of the chosen adapter, or null when none was found.</summary>
    public IPAddress? SubnetMask { get; private set; }

    /// <summary>
    /// Re-evaluates when the stored or target address changed, and every two seconds while the stored address is
    /// missing here (an adapter may come up later). Cheap otherwise: no allocations per call.
    /// </summary>
    public void Update(string? configured, string? target, double time)
    {
        if (configured == _configured && target == _target && (_isExact || time - _lastRefreshTime <= RetryIntervalSeconds))
            return;

        _configured = configured;
        _target = target;
        _lastRefreshTime = time;

        EffectiveIp = configured;
        SubnetMask = null;
        _isExact = false;
        if (string.IsNullOrEmpty(configured))
            return;

        var adapters = ListAdapters();
        var adapter = adapters.FirstOrDefault(a => a.Ip.ToString() == configured);
        _isExact = adapter != null;
        adapter ??= FindInSubnetOf(adapters, configured)
                    ?? FindInSubnetOf(adapters, target)
                    ?? adapters.FirstOrDefault(a => !IPAddress.IsLoopback(a.Ip));
        if (adapter == null)
            return;

        EffectiveIp = adapter.Ip.ToString();
        SubnetMask = adapter.Mask;
    }

    private static Adapter? FindInSubnetOf(Adapter[] adapters, string? address)
    {
        if (!IPAddress.TryParse(address, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork)
            return null;

        var bytes = ip.GetAddressBytes();
        foreach (var adapter in adapters)
        {
            if (IPAddress.IsLoopback(adapter.Ip))
                continue;

            var adapterBytes = adapter.Ip.GetAddressBytes();
            var mask = adapter.Mask.GetAddressBytes();
            var same = true;
            for (var i = 0; i < 4 && same; i++)
                same = (bytes[i] & mask[i]) == (adapterBytes[i] & mask[i]);

            if (same)
                return adapter;
        }

        return null;
    }

    private static Adapter[] ListAdapters()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                                   .Where(ni => ni.OperationalStatus == OperationalStatus.Up
                                                && ni.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                                   .SelectMany(ni => ni.GetIPProperties().UnicastAddresses)
                                   .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                                   .Select(a => new Adapter(a.Address, a.IPv4Mask))
                                   .Prepend(new Adapter(IPAddress.Loopback, IPAddress.Parse("255.0.0.0")))
                                   .ToArray();
        }
        catch
        {
            return [new Adapter(IPAddress.Loopback, IPAddress.Parse("255.0.0.0"))];
        }
    }

    private sealed record Adapter(IPAddress Ip, IPAddress Mask);

    private const double RetryIntervalSeconds = 2.0;
    private string? _configured;
    private string? _target;
    private double _lastRefreshTime;
    private bool _isExact;
}
