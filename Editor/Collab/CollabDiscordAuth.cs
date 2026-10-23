#nullable enable
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using T3.Editor.Gui.UiHelpers;
using T3.Core.SystemUi;

namespace T3.Editor.Collab;

/// <summary>
/// Discord login for collaboration identity. Implements the OAuth2 authorization-code flow locally:
/// a temporary HttpListener catches the redirect on 127.0.0.1, the code is exchanged at Discord's
/// token endpoint and the user profile (id, username, avatar) is fetched.
///
/// The client secret is read from user settings and used on this machine. That's the right tradeoff
/// for self-hosted sessions; a hosted relay service would perform the exchange on the server instead
/// so the secret never ships with the client.
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

    public static LoginState State { get; private set; } = LoginState.Idle;
    public static string StatusText { get; private set; } = "";

    private static CancellationTokenSource? _cancellation;

    public static void StartLogin()
    {
        if (State is LoginState.WaitingForBrowser or LoginState.ExchangingCode)
            return;

        var clientId = UserSettings.Config.CollabDiscordClientId;
        var clientSecret = UserSettings.Config.CollabDiscordClientSecret;
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
        {
            State = LoginState.Failed;
            StatusText = "Enter a Discord client id and client secret first";
            return;
        }

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
        HttpListener listener = null;
        var redirectUri = "";
        try
        {
            State = LoginState.WaitingForBrowser;
            var port = FindFreePort();
            redirectUri = $"http://127.0.0.1:{port}/callback/";

            listener = new HttpListener();
            listener.Prefixes.Add(redirectUri);
            listener.Start();

            var state = Guid.NewGuid().ToString("N");
            var authorizeUrl = "https://discord.com/oauth2/authorize" +
                               $"?client_id={Uri.EscapeDataString(clientId)}" +
                               "&response_type=code" +
                               $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
                               "&scope=identify" +
                               $"&state={state}";

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
            StatusText = "Exchanging token...";

            var profile = await ExchangeCodeAndFetchUserAsync(clientId, clientSecret, code, redirectUri, cancellation);

            var config = UserSettings.Config;
            config.CollabDiscordUserId = profile.UserId;
            config.CollabDiscordUserName = profile.UserName;
            config.CollabDiscordUserAvatar = profile.AvatarHash;
            UserSettings.Save();

            CollabManager.Instance.RefreshLocalIdentity();

            State = LoginState.Done;
            StatusText = $"Logged in as {profile.UserName}";
            Log.Info($"Collab: Discord login successful ({profile.UserName})");
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

    private static async Task<(string UserId, string UserName, string AvatarHash)> ExchangeCodeAndFetchUserAsync(
        string clientId, string clientSecret, string code, string redirectUri, CancellationToken cancellation)
    {
        using var client = new HttpClient();
        client.DefaultRequestHeaders.Add("User-Agent", "TiXLCollab");

        var form = new Dictionary<string, string>
                       {
                           ["client_id"] = clientId,
                           ["client_secret"] = clientSecret,
                           ["grant_type"] = "authorization_code",
                           ["code"] = code,
                           ["redirect_uri"] = redirectUri,
                       };

        using var tokenResponse = await client.PostAsync("https://discord.com/api/oauth2/token",
                                                         new FormUrlEncodedContent(form),
                                                         cancellation);
        var tokenBody = await tokenResponse.Content.ReadAsStringAsync(cancellation);
        if (!tokenResponse.IsSuccessStatusCode)
            throw new Exception($"token exchange failed ({(int)tokenResponse.StatusCode})");

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
                user["avatar"]?.Value<string>() ?? "");
    }

    private static int FindFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
