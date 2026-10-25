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

    private static CancellationTokenSource? _cancellation;

    public static void StartLogin()
    {
        if (State is LoginState.WaitingForBrowser or LoginState.ExchangingCode)
            return;

        var configuredId = UserSettings.Config.CollabDiscordClientId;
        var clientId = string.IsNullOrWhiteSpace(configuredId) ? DefaultClientId : configuredId.Trim();
        var clientSecret = UserSettings.Config.CollabDiscordClientSecret;
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
        {
            State = LoginState.Failed;
            StatusText = "Set the Discord Client ID first (Settings → Collaboration)";
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
                               "&scope=identify" +
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
            StatusText = "Exchanging token...";

            var profile = await ExchangeCodeAndFetchUserAsync(clientId, clientSecret, code, redirectUri, codeVerifier, cancellation);

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
                user["avatar"]?.Value<string>() ?? "");
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
