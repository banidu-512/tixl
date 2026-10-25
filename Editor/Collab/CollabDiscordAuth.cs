#nullable enable
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using T3.Editor.Gui.UiHelpers;
using T3.Core.SystemUi;

namespace T3.Editor.Collab;

/// <summary>
/// Discord login for collaboration identity. Opens Discord in the browser with an OAuth2
/// authorization-code request, catches the redirect on a loopback listener, exchanges the code with
/// PKCE (so a public client needs no secret) and fetches the user profile.
///
/// A Discord application is registered once; its only required entry is the fixed redirect URI
/// below — Discord matches it verbatim, which is why the port is a constant and not picked at run time.
/// </summary>
internal static class CollabDiscordAuth
{
    public enum LoginState
    {
        Idle,
        WaitingForBrowser,
        ExchangingCode,
        Done,
        Failed,
    }

    /// <summary>The browser returns here. Must be registered verbatim in the Discord application.</summary>
    public const int RedirectPort = 38270;

    public const string RedirectPath = "/callback/";

    public static string RedirectUri => $"http://127.0.0.1:{RedirectPort}{RedirectPath}";

    /// <summary>
    /// A TiXL-owned Discord application id can be baked in here: users then only press "Log in".
    /// Left empty, the client id is entered once in Settings → Collaboration.
    /// </summary>
    private const string DefaultClientId = "";

    public static bool HasDefaultClient => !string.IsNullOrWhiteSpace(DefaultClientId);

    public static LoginState State { get; private set; } = LoginState.Idle;
    public static string StatusText { get; private set; } = "";

    /// <summary>The invite of the community server; membership there is what "logged in" means.</summary>
    public const string InviteCode = "YmSyQdeH3S";

    public static string InviteUrl => $"https://discord.gg/{InviteCode}";

    /// <summary>Set when the account authenticated but is not a member of the community server.</summary>
    public static bool NeedsGuildJoin { get; private set; }

    /// <summary>Name of the community server, resolved from the invite for the status line.</summary>
    public static string GuildName { get; private set; } = "";

    private static string _cachedGuildId = "";
    private static CancellationTokenSource? _cancellation;

    /// <summary>Opens the invite in the browser so a signed-in account can join the server.</summary>
    public static void OpenInvite() => CoreUi.Instance.OpenWithDefaultApplication(InviteUrl);

    public static void StartLogin()
    {
        if (State is LoginState.WaitingForBrowser or LoginState.ExchangingCode)
            return;

        var configuredId = UserSettings.Config.CollabDiscordClientId;
        var clientId = string.IsNullOrWhiteSpace(configuredId) ? DefaultClientId : configuredId.Trim();
        var clientSecret = UserSettings.Config.CollabDiscordClientSecret;
        if (string.IsNullOrWhiteSpace(clientId))
        {
            State = LoginState.Failed;
            StatusText = "Set the Discord Client ID first (Settings → Collaboration)";
            return;
        }

        NeedsGuildJoin = false;
        _cancellation = new CancellationTokenSource();
        _ = Task.Run(() => LoginFlowAsync(clientId, clientSecret, _cancellation.Token));
    }

    public static void Cancel()
    {
        _cancellation?.Cancel();
        State = LoginState.Idle;
        StatusText = "";
    }

    private static async Task LoginFlowAsync(string clientId, string clientSecret, CancellationToken cancellation)
    {
        HttpListener? listener = null;
        var redirectUri = RedirectUri;
        try
        {
            State = LoginState.WaitingForBrowser;

            listener = new HttpListener();
            listener.Prefixes.Add(redirectUri);
            try
            {
                listener.Start();
            }
            catch (HttpListenerException e)
            {
                throw new Exception($"Port {RedirectPort} is already in use — close the other TiXL instance or program using it", e);
            }

            // PKCE: a public Discord client needs no secret. The verifier never leaves this machine;
            // only its SHA-256 hash travels in the authorize URL.
            var codeVerifier = CreateCodeVerifier();
            var codeChallenge = CreateCodeChallenge(codeVerifier);

            var state = Guid.NewGuid().ToString("N");
            var authorizeUrl = "https://discord.com/oauth2/authorize" +
                               $"?client_id={Uri.EscapeDataString(clientId)}" +
                               "&response_type=code" +
                               $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
                               // guilds is what lets us verify that the account is in the community server.
                               "&scope=identify%20guilds" +
                               $"&state={state}" +
                               $"&code_challenge={codeChallenge}" +
                               "&code_challenge_method=S256";

            CoreUi.Instance.OpenWithDefaultApplication(authorizeUrl);
            StatusText = "Waiting for the Discord login in your browser...";

            var context = await listener.GetContextAsync().WaitAsync(TimeSpan.FromMinutes(3), cancellation);
            var query = context.Request.QueryString;

            try
            {
                context.Response.ContentType = "text/html";
                var responseBytes = Encoding.UTF8.GetBytes(
                                                            "<html><body style=\"font-family:sans-serif;background:#1a1a1e;color:#eee;display:flex;align-items:center;justify-content:center;height:100vh\">"
                                                            + "<h2>Login complete &mdash; you can close this tab and return to TiXL.</h2></body></html>");
                context.Response.ContentLength64 = responseBytes.Length;
                context.Response.OutputStream.Write(responseBytes);
                context.Response.OutputStream.Close();
            }
            catch
            {
                // The browser may have already closed the connection.
            }

            if (query["state"] != state)
                throw new Exception("OAuth state mismatch (possible CSRF) — aborting");

            var error = query["error"];
            if (!string.IsNullOrEmpty(error))
                throw new Exception($"Discord login was cancelled: {error}");

            var code = query["code"];
            if (string.IsNullOrEmpty(code))
                throw new Exception("No authorization code received from Discord");

            State = LoginState.ExchangingCode;
            StatusText = "Checking your Discord account...";

            var profile = await ExchangeCodeAndFetchUserAsync(clientId, clientSecret, code, redirectUri, codeVerifier, cancellation);

            // Membership in the community server is the actual access check: a Discord account alone
            // proves an identity but not that it belongs to this show's people.
            var guild = await ResolveInviteGuildAsync(cancellation);
            var guilds = await FetchGuildIdsAsync(profile.AccessToken, cancellation);
            if (!guilds.Contains(guild.Id))
            {
                NeedsGuildJoin = true;
                State = LoginState.Failed;
                StatusText = $"{profile.UserName} is not a member of {guild.Name} yet — join it, then log in again.";
                Log.Info($"Collab: Discord account '{profile.UserName}' is not in guild {guild.Name} ({guild.Id})");
                return;
            }

            GuildName = guild.Name;
            var config = UserSettings.Config;
            config.CollabDiscordUserId = profile.UserId;
            config.CollabDiscordUserName = profile.UserName;
            config.CollabDiscordUserAvatar = profile.AvatarHash;
            UserSettings.Save();

            CollabManager.Instance.RefreshLocalIdentity();

            State = LoginState.Done;
            StatusText = $"Logged in as {profile.UserName} · {guild.Name}";
            Log.Info($"Collab: Discord login successful ({profile.UserName}, guild '{guild.Name}')");
        }
        catch (OperationCanceledException)
        {
            State = LoginState.Idle;
            StatusText = "";
        }
        catch (Exception e)
        {
            State = LoginState.Failed;
            StatusText = $"Discord login failed: {e.Message}";
            Log.Warning($"Collab: Discord login failed: {e}");
        }
        finally
        {
            try
            {
                listener?.Stop();
                listener?.Close();
            }
            catch
            {
                // Listener teardown.
            }
        }
    }

    private static async Task<(string UserId, string UserName, string AvatarHash, string AccessToken)> ExchangeCodeAndFetchUserAsync(
        string clientId, string clientSecret, string code, string redirectUri, string codeVerifier, CancellationToken cancellation)
    {
        using var client = new HttpClient();
        client.DefaultRequestHeaders.Add("User-Agent", "TiXLCollab");

        var form = new Dictionary<string, string>
                       {
                           ["client_id"] = clientId,
                           ["grant_type"] = "authorization_code",
                           ["code"] = code,
                           ["redirect_uri"] = redirectUri,
                           ["code_verifier"] = codeVerifier,
                       };

        // A confidential app may still have its secret configured; a PKCE-only public app leaves it empty.
        if (!string.IsNullOrWhiteSpace(clientSecret))
            form["client_secret"] = clientSecret;

        using var tokenResponse = await client.PostAsync("https://discord.com/api/oauth2/token",
                                                         new FormUrlEncodedContent(form),
                                                         cancellation);
        var tokenBody = await tokenResponse.Content.ReadAsStringAsync(cancellation);
        if (!tokenResponse.IsSuccessStatusCode)
            throw new Exception($"token exchange failed ({(int)tokenResponse.StatusCode}): {tokenBody.Trim()}");

        var accessToken = JObject.Parse(tokenBody)["access_token"]?.Value<string>();
        if (string.IsNullOrEmpty(accessToken))
            throw new Exception("no access token in response");

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://discord.com/api/users/@me");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        using var userResponse = await client.SendAsync(request, cancellation);
        var userBody = await userResponse.Content.ReadAsStringAsync(cancellation);
        if (!userResponse.IsSuccessStatusCode)
            throw new Exception($"profile fetch failed ({(int)userResponse.StatusCode})");

        var user = JObject.Parse(userBody);
        return (user["id"]?.Value<string>() ?? "",
                user["global_name"]?.Value<string>() ?? user["username"]?.Value<string>() ?? "discord user",
                user["avatar"]?.Value<string>() ?? "",
                accessToken);
    }

    /// <summary>
    /// The guild behind the community invite. Resolved once per run: the invite endpoint is public
    /// and returns the guild id and name, which is what membership is compared against.
    /// </summary>
    private static async Task<(string Id, string Name)> ResolveInviteGuildAsync(CancellationToken cancellation)
    {
        if (!string.IsNullOrEmpty(_cachedGuildId))
            return (_cachedGuildId, GuildName);

        using var client = CreateHttpClient();
        using var response = await client.GetAsync($"https://discord.com/api/v10/invites/{InviteCode}?with_counts=true", cancellation);
        var body = await response.Content.ReadAsStringAsync(cancellation);
        if (!response.IsSuccessStatusCode)
            throw new Exception($"could not resolve the Discord invite '{InviteCode}' ({(int)response.StatusCode}) — it may have expired");

        var guild = JObject.Parse(body)["guild"] as JObject;
        var id = guild?["id"]?.Value<string>() ?? "";
        if (string.IsNullOrEmpty(id))
            throw new Exception("the Discord invite does not point at a server");

        GuildName = guild?["name"]?.Value<string>() ?? "the TiXL Discord";
        _cachedGuildId = id;
        return (id, GuildName);
    }

    /// <summary>Guild ids of the signed-in account. Requires the 'guilds' scope on the token.</summary>
    private static async Task<HashSet<string>> FetchGuildIdsAsync(string accessToken, CancellationToken cancellation)
    {
        using var client = CreateHttpClient();
        var ids = new HashSet<string>();
        var after = "";

        // Discord returns at most 200 guilds per page; a user in more servers must be paged through
        // or the target server could sit on a later page and the membership check would be wrong.
        for (var page = 0; page < 20; page++)
        {
            var url = "https://discord.com/api/v10/users/@me/guilds?limit=200"
                      + (after.Length > 0 ? $"&after={Uri.EscapeDataString(after)}" : "");
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
            using var response = await client.SendAsync(request, cancellation);
            var body = await response.Content.ReadAsStringAsync(cancellation);
            if (!response.IsSuccessStatusCode)
                throw new Exception($"guild list failed ({(int)response.StatusCode}): {body.Trim()}");

            if (JToken.Parse(body) is not JArray guilds || guilds.Count == 0)
                break;

            foreach (var guild in guilds)
            {
                var id = guild["id"]?.Value<string>();
                if (!string.IsNullOrEmpty(id))
                {
                    ids.Add(id);
                    after = id;
                }
            }

            if (guilds.Count < 200)
                break;
        }

        return ids;
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.Add("User-Agent", "TiXLCollab");
        return client;
    }

    private static string CreateCodeVerifier()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Base64Url(bytes);
    }

    private static string CreateCodeChallenge(string verifier)
    {
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
        return Base64Url(hash);
    }

    private static string Base64Url(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
