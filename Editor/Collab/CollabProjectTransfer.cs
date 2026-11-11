#nullable enable
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using T3.Editor.Gui.UiHelpers;

namespace T3.Editor.Collab;

/// <summary>
/// Project handoff for backend parties: the host zips its project when the party is formed and
/// uploads it in chunks; a joiner downloads that exact snapshot and opens it, so both sides run
/// byte-identical symbol GUIDs. The snapshot lives on the backend only while the party lasts.
/// </summary>
internal static class CollabProjectTransfer
{
    private const int ChunkSize = 1024 * 1024;
    private static readonly string[] ExcludedFolders = ["bin", "obj", ".git", ".vs", ".temp", "node_modules"];

    private static readonly HttpClient _http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("TiXLCollab/1.0");
        return client;
    }

    /// <summary>Zips the project folder (build output and VCS folders excluded). Returns the zip path.</summary>
    public static string ZipProject(string projectFolder, Action<int>? progress, CancellationToken cancellation)
    {
        var zipPath = Path.Combine(Path.GetTempPath(), $"tixl-party-{Guid.NewGuid():N}.zip");
        var files = EnumerateProjectFiles(projectFolder).ToArray();
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        for (var i = 0; i < files.Length; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(projectFolder, files[i]);
            zip.CreateEntryFromFile(files[i], relative, CompressionLevel.Fastest);
            if (i % 16 == 0)
                progress?.Invoke((int)(100f * (i + 1) / files.Length));
        }

        return zipPath;
    }

    private static IEnumerable<string> EnumerateProjectFiles(string folder)
    {
        foreach (var path in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(folder, path);
            var firstSegment = relative.Split(Path.DirectorySeparatorChar, '/')[0];
            if (ExcludedFolders.Contains(firstSegment, StringComparer.OrdinalIgnoreCase))
                continue;

            yield return path;
        }
    }

    /// <summary>
    /// Newest write time of the project's own files. A party that runs for hours can gain new
    /// operators or assets; comparing this with the snapshot's upload time tells the host whether
    /// joiners would get a stale project.
    /// </summary>
    public static DateTime NewestWriteUtc(string folder)
    {
        var newest = DateTime.MinValue;
        try
        {
            foreach (var path in EnumerateProjectFiles(folder))
            {
                var written = File.GetLastWriteTimeUtc(path);
                if (written > newest)
                    newest = written;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A file vanished mid-scan; the last known time is good enough for a refresh decision.
        }

        return newest;
    }

    /// <summary>
    /// Starts the upload slot for a party. Called BEFORE zipping: the backend drops the previous
    /// archive and reports "uploading", so a joiner arriving now waits for the fresh snapshot
    /// instead of downloading the stale one.
    /// </summary>
    public static async Task<string?> BeginUploadAsync(string room, string key, string projectName, CancellationToken cancellation)
    {
        var init = await PostJsonAsync("project-init", new JObject
                                                          {
                                                              ["room"] = room,
                                                              ["key"] = key,
                                                              ["name"] = projectName,
                                                          }, cancellation);
        return init.Error;
    }

    /// <summary>Uploads a zip into the slot opened by <see cref="BeginUploadAsync"/>. Returns null on success.</summary>
    public static async Task<string?> UploadZipAsync(string room, string key, string zipPath,
                                                     Action<int>? progress, CancellationToken cancellation)
    {
        await using var stream = File.OpenRead(zipPath);
        var buffer = new byte[ChunkSize];
        long sent = 0;
        var length = stream.Length;
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            var read = await stream.ReadAsync(buffer, cancellation);
            if (read <= 0)
                break;

            using var content = new ByteArrayContent(buffer, 0, read);
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                $"{CollabWebLobby.BackendUrl}?op=project-chunk&room={Uri.EscapeDataString(room)}&key={Uri.EscapeDataString(key)}")
                                  {
                                      Content = content,
                                  };

            var (ok, error, _) = await SendAsync(request, cancellation);
            if (!ok)
                return error ?? "the project upload was refused";

            sent += read;
            progress?.Invoke((int)(100 * sent / Math.Max(1, length)));
        }

        var (finishOk, finishError) = await PostJsonAsync("project-finish", new JObject
                                                                             {
                                                                                 ["room"] = room,
                                                                                 ["key"] = key,
                                                                             }, cancellation);
        return finishOk == null && finishError == null ? null : finishError;
    }

    /// <summary>Asks the party's project state: none / uploading / ready, plus its name and size.</summary>
    public static async Task<(string State, string Name, long Size, string Error)> FetchInfoAsync(string room, CancellationToken cancellation)
    {
        try
        {
            using var response = await _http.GetAsync($"{CollabWebLobby.BackendUrl}?op=project-info&room={Uri.EscapeDataString(room)}", cancellation);
            var text = await response.Content.ReadAsStringAsync(cancellation);
            var json = JObject.Parse(text);
            if (!(json["ok"]?.Value<bool>() ?? false))
                return ("none", "", 0, json["error"]?.Value<string>() ?? "the backend refused the request");

            return (CollabProtocol.GetString(json, "state", "none"),
                    CollabProtocol.GetString(json, "name"),
                    CollabProtocol.TryGetInt(json, "size", out var size) ? size : 0,
                    "");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or Newtonsoft.Json.JsonException)
        {
            return ("none", "", 0, e.Message);
        }
    }

    /// <summary>Downloads the party's project snapshot to a temp zip. Returns the path.</summary>
    public static async Task<(string? ZipPath, string? Error)> DownloadAsync(string room, string ticket,
                                                                             Action<int>? progress, CancellationToken cancellation)
    {
        var url = $"{CollabWebLobby.BackendUrl}?op=project-get&room={Uri.EscapeDataString(room)}&ticket={Uri.EscapeDataString(ticket)}";
        var zipPath = Path.Combine(Path.GetTempPath(), $"tixl-party-recv-{Guid.NewGuid():N}.zip");
        try
        {
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellation);
            if (!response.IsSuccessStatusCode)
            {
                var text = await response.Content.ReadAsStringAsync(cancellation);
                var message = text;
                try
                {
                    message = JObject.Parse(text)["error"]?.Value<string>() ?? text;
                }
                catch (Newtonsoft.Json.JsonException)
                {
                    // Not JSON (a proxy error page); the raw text is the best hint we have.
                }

                return (null, message);
            }

            var total = response.Content.Headers.ContentLength ?? 0;
            await using var source = await response.Content.ReadAsStreamAsync(cancellation);
            await using var target = File.Create(zipPath);
            var buffer = new byte[ChunkSize];
            long received = 0;
            while (true)
            {
                var read = await source.ReadAsync(buffer, cancellation);
                if (read <= 0)
                    break;

                await target.WriteAsync(buffer.AsMemory(0, read), cancellation);
                received += read;
                if (total > 0)
                    progress?.Invoke((int)(100 * received / total));
            }

            return (zipPath, null);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException)
        {
            return (null, e.Message);
        }
    }

    /// <summary>
    /// Extracts a received snapshot into the projects root. Always lands in a party folder, never on
    /// top of one of the user's own projects.
    /// </summary>
    public static string ExtractToProjects(string zipPath, string projectName)
    {
        var root = UserSettings.Config.ProjectDirectories.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(root))
            root = Path.Combine(Path.GetTempPath(), "TiXL", "Projects");

        var safeName = new string((projectName ?? "party").Where(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_').ToArray()).Trim();
        if (safeName.Length == 0)
            safeName = "party";

        var target = Path.Combine(root, "_party", safeName);
        if (Directory.Exists(target))
            Directory.Delete(target, true);

        Directory.CreateDirectory(target);
        ZipFile.ExtractToDirectory(zipPath, target, overwriteFiles: true);
        return target;
    }

    private static async Task<(JObject? Body, string? Error)> PostJsonAsync(string op, JObject payload, CancellationToken cancellation)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{CollabWebLobby.BackendUrl}?op={op}");
            request.Content = new StringContent(payload.ToString(Newtonsoft.Json.Formatting.None),
                                                System.Text.Encoding.UTF8, "application/json");
            var (ok, error, body) = await SendAsync(request, cancellation);
            return ok ? (body, null) : (null, error);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            return (null, e.Message);
        }
    }

    private static async Task<(bool Ok, string? Error, JObject? Body)> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
    {
        try
        {
            using var response = await _http.SendAsync(request, cancellation);
            var text = await response.Content.ReadAsStringAsync(cancellation);
            JObject json;
            try
            {
                json = JObject.Parse(text);
            }
            catch (Newtonsoft.Json.JsonException)
            {
                return (false, $"the backend answered with something that is not JSON ({response.StatusCode})", null);
            }

            var ok = json["ok"]?.Value<bool>() ?? false;
            return ok ? (true, null, json) : (false, json["error"]?.Value<string>() ?? "the backend refused the request", null);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            return (false, e.Message, null);
        }
    }
}
