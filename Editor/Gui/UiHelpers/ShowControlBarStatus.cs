#nullable enable
using T3.Core.Show;
using T3.Core.Utils;
using T3.Editor.Collab;
using T3.Editor.Show;
using T3.Editor.Gui.UiHelpers;

namespace T3.Editor.Gui;

/// <summary>
/// The compact show-control line drawn centered in the menu bar: what is running on this machine,
/// in which role, and the address that matters — the local interface on the serving side, the
/// peer's address on the following side. Empty when nothing is active.
/// </summary>
internal static class ShowControlBarStatus
{
    private static readonly List<string> _peerAddresses = [];

    public static string Compose()
    {
        var parts = new List<string>(3);

        // Failover first: when enabled it owns the streaming role.
        if (FailoverArbiter.IsEnabled)
        {
            var role = FailoverArbiter.RoleText.Replace("ACTIVE (streaming)", "Active", StringComparison.Ordinal);
            _peerAddresses.Clear();
            FailoverArbiter.GetLivePeerAddresses(_peerAddresses);
            var peer = _peerAddresses.Count > 0 ? _peerAddresses[0] : "no peer";
            parts.Add($"Failover {role} · peer {peer}");
        }

        var boygroup = BoygroupManager.Instance;
        if (boygroup.State == BoygroupManager.BoygroupState.Server)
        {
            var ip = LocalAddress() ?? "any";
            parts.Add($"Boygroup Master · {ip}{GroupSuffix(boygroup.GroupName)}");
        }
        else if (boygroup.State == BoygroupManager.BoygroupState.Client)
        {
            parts.Add($"Boygroup Slave → {boygroup.ConnectedMasterAddress ?? "?"}{GroupSuffix(boygroup.GroupName)}");
        }

        var collab = CollabManager.Instance;
        if (collab.IsEnabled && collab.IsSessionActive)
        {
            if (collab.State == CollabManager.SessionState.Hosting)
            {
                var ip = LocalAddress() ?? "any";
                var room = collab.RoomCode;
                parts.Add(string.IsNullOrEmpty(room)
                              ? $"Collab Host · {ip}"
                              : $"Collab Host · {ip} · room {room}");
            }
            else
            {
                var host = collab.ConnectedHostAddress;
                if (!string.IsNullOrEmpty(host))
                {
                    parts.Add($"Collab Client → {host}");
                }
                else
                {
                    var room = UserSettings.Config.CollabLastRelayRoom;
                    var relay = collab.RelayEndpoint;
                    parts.Add(string.IsNullOrEmpty(room)
                                  ? $"Collab Client via {relay}"
                                  : $"Collab Client · room {room} via {relay}");
                }
            }
        }

        return string.Join("   |   ", parts);
    }

    private static string GroupSuffix(string group) =>
        string.IsNullOrEmpty(group) ? "" : $" · {group}";

    /// <summary>The interface TiXL serves show-control traffic on (Settings → Network pin honored).</summary>
    private static string? LocalAddress() =>
        LanInterfaces.ResolveAddress(UserSettings.Config.NetworkInterface)?.ToString();
}
