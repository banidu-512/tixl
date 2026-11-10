#nullable enable
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace T3.Editor.Collab;

/// <summary>One session a relay lists: enough to pick it from a table and join it.</summary>
internal sealed record LobbyRoom(string Code,
                                 string Title,
                                 string Purpose,
                                 string Project,
                                 string HostName,
                                 string Lang,
                                 bool HostVerified,
                                 int Members,
                                 int MaxMembers,
                                 bool PasswordProtected);

/// <summary>
/// The session browser's data: asks a relay which sessions its hosts chose to list. Only hosts
/// that turned on "List in session browser" appear; everything else is reachable by session ID only.
/// The request runs on a pool thread; the UI reads the latest result.
/// </summary>
internal static class CollabLobby
{
    /// <summary>Result of the last successful refresh. Replaced as a whole, never changed in place.</summary>
    public static IReadOnlyList<LobbyRoom> Rooms => _rooms;

    public static bool IsRefreshing => _refreshing != 0;

    /// <summary>Why the last refresh failed; empty after a successful one.</summary>
    public static string ErrorText { get; private set; } = "";

    /// <summary>Relay the listed rooms belong to ("host:port"); joining goes there.</summary>
    public static string RelayEndpoint { get; private set; } = "";

    public static DateTime LastRefreshUtc { get; private set; } = DateTime.MinValue;

    private static volatile LobbyRoom[] _rooms = [];
    private static int _refreshing;

    public static void Refresh(string relayHost, int relayPort)
    {
        if (Interlocked.Exchange(ref _refreshing, 1) != 0)
            return;

        _ = Task.Run(async () =>
                     {
                         try
                         {
                             var list = await CollabRelayWire.FetchRoomListAsync(relayHost, relayPort, CancellationToken.None);
                             var rooms = new List<LobbyRoom>(list.Count);
                             foreach (var token in list)
                             {
                                 if (token is not JObject entry)
                                     continue;

                                 var code = CollabProtocol.GetString(entry, "code");
                                 if (string.IsNullOrEmpty(code))
                                     continue;

                                 CollabProtocol.TryGetBool(entry, "verified", out var verified);
                                 CollabProtocol.TryGetBool(entry, "pwd", out var passwordProtected);
                                 CollabProtocol.TryGetInt(entry, "members", out var members);
                                 CollabProtocol.TryGetInt(entry, "max", out var maxMembers);
                                 rooms.Add(new LobbyRoom(code,
                                                         CollabProtocol.GetString(entry, "title"),
                                                         CollabProtocol.GetString(entry, "purpose"),
                                                         CollabProtocol.GetString(entry, "project"),
                                                         CollabProtocol.GetString(entry, "host"),
                                                         CollabProtocol.GetString(entry, "lang"),
                                                         verified,
                                                         members,
                                                         maxMembers,
                                                         passwordProtected));
                             }

                             _rooms = rooms.ToArray();
                             RelayEndpoint = $"{relayHost}:{relayPort}";
                             ErrorText = "";
                             LastRefreshUtc = DateTime.UtcNow;
                         }
                         catch (Exception e)
                         {
                             ErrorText = $"Could not load the session list: {e.Message}";
                         }
                         finally
                         {
                             Interlocked.Exchange(ref _refreshing, 0);
                         }
                     });
    }
}
