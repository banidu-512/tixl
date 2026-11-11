#nullable enable
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using T3.Core.Logging;
using T3.Core.Settings;

namespace T3.Editor.Gui.AiModels;

/// <summary>
/// Downloads catalog models into the model store, reporting progress the settings window can show,
/// and checks each file's size on arrival. One download per catalog entry runs at a time; the
/// browser rows and the one-click setup share this service. Finished downloads stay listed until
/// the model is removed from the store.
/// </summary>
internal static class AiModelDownloadService
{
    internal enum States { NotStarted, Downloading, Completed, Failed }

    internal sealed class DownloadStatus
    {
        public States State = States.NotStarted;
        public float Progress;          // 0..1 across all files of the entry
        public string CurrentFile = ""; // name and size of the file being downloaded
        public string? Error;
    }

    private static readonly object StartLock = new();
    private static readonly ConcurrentDictionary<string, DownloadStatus> StatusByEntryId = new();
    private static readonly ConcurrentDictionary<string, Task> RunningTasks = new();

    private static readonly DownloadStatus NotStarted = new();

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(60) };

    public static DownloadStatus GetStatus(ModelCatalogEntry entry) =>
        StatusByEntryId.TryGetValue(entry.Id, out var status) ? status : NotStarted;

    public static bool IsRunning(ModelCatalogEntry entry) => RunningTasks.ContainsKey(entry.Id);

    /// <summary>Starts downloading all missing files of the entry in the background; false when it was already running.</summary>
    public static bool Start(ModelCatalogEntry entry)
    {
        lock (StartLock)
        {
            if (RunningTasks.ContainsKey(entry.Id))
                return false;

            StartLocked(entry);
            return true;
        }
    }

    /** Returns the entry's running download task, starting one when none is running. */
    public static Task GetOrStartTask(ModelCatalogEntry entry)
    {
        lock (StartLock)
        {
            if (RunningTasks.TryGetValue(entry.Id, out var running))
                return running;

            return StartLocked(entry);
        }
    }

    private static Task StartLocked(ModelCatalogEntry entry)
    {
        var status = StatusByEntryId.GetOrAdd(entry.Id, _ => new DownloadStatus());
        status.State = States.Downloading;
        status.Error = null;
        status.Progress = 0;
        status.CurrentFile = "";

        Task task = null!;
        task = Task.Run(() =>
        {
            try
            {
                DownloadEntry(entry, status);
            }
            finally
            {
                // Only remove our own registration; a newer download of the same entry may have started
                if (RunningTasks.TryGetValue(entry.Id, out var current) && current == task)
                    RunningTasks.TryRemove(entry.Id, out _);
            }
        });

        RunningTasks[entry.Id] = task;
        return task;
    }

    /** Removes the entry's files from the store and forgets its last download result. */
    public static void Remove(ModelCatalogEntry entry)
    {
        if (IsRunning(entry))
            return;

        if (entry.RequiresLocalExport)
            TryDeleteDirectory(entry.ExportFolder);
        else
        {
            foreach (var file in entry.Files)
                TryDeleteFile(entry.TargetPath(file.TargetFileName));
        }

        StatusByEntryId.TryRemove(entry.Id, out _);
        CleanupEmptyFolders(entry);
    }

    private static void CleanupEmptyFolders(ModelCatalogEntry entry)
    {
        try
        {
            var familyDirectory = AiModelSettings.FamilyDirectory(entry.Family.StoreFolder());

            if (!string.IsNullOrEmpty(entry.InstallSubFolder))
            {
                var subFolder = Path.Combine(familyDirectory, entry.InstallSubFolder);
                if (Directory.Exists(subFolder) && Directory.GetFileSystemEntries(subFolder).Length == 0)
                    Directory.Delete(subFolder);
            }

            if (Directory.Exists(familyDirectory) && Directory.GetFileSystemEntries(familyDirectory).Length == 0)
                Directory.Delete(familyDirectory);
        }
        catch
        {
            // Not being able to tidy up an empty folder is not worth an error
        }
    }

    private static void DownloadEntry(ModelCatalogEntry entry, DownloadStatus status)
    {
        try
        {
            if (!AiModelSettings.EnsureFamilyDirectory(entry.Family.StoreFolder(), out var familyDirectory, out var createError))
                throw new IOException(createError);

            if (!string.IsNullOrEmpty(entry.InstallSubFolder))
                Directory.CreateDirectory(Path.Combine(familyDirectory, entry.InstallSubFolder));

            var totalBytes = 0L;
            var completedBytes = 0L;
            foreach (var file in entry.Files)
            {
                var targetPath = entry.TargetPath(file.TargetFileName);
                var existingSize = File.Exists(targetPath) ? new FileInfo(targetPath).Length : -1;
                if (existingSize >= 0 && IsPlausibleSize(existingSize, file))
                {
                    totalBytes += file.ExpectedSizeBytes;
                    completedBytes += file.ExpectedSizeBytes;
                    continue;
                }

                totalBytes += file.ExpectedSizeBytes;
                var downloaded = DownloadFile(file, targetPath, status, completedBytes, totalBytes);
                completedBytes += downloaded;
                status.Progress = totalBytes > 0 ? Math.Clamp(completedBytes / (float)totalBytes, 0f, 1f) : 0;
                status.CurrentFile = file.TargetFileName;
            }

            status.State = States.Completed;
            status.CurrentFile = "";
            Log.Info($"[AI Models] Installed '{entry.DisplayName}' into the model store");
        }
        catch (Exception e)
        {
            status.State = States.Failed;
            status.Error = e.Message;
            Log.Error($"[AI Models] Download of '{entry.DisplayName}' failed: {e.Message}");
        }
    }

    /** Streams one file into a .part staging file, then moves it into place with its final name. */
    private static long DownloadFile(CatalogFile file, string targetPath,
                                     DownloadStatus status, long completedBytes, long totalBytes)
    {
        var partPath = targetPath + ".part";
        TryDeleteFile(partPath);

        using var response = Http.GetAsync(file.Url, HttpCompletionOption.ResponseHeadersRead).Result;
        response.EnsureSuccessStatusCode();

        using var stream = response.Content.ReadAsStream();
        var downloaded = 0L;

        using (var output = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true))
        {
            var buffer = new byte[65536];
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                output.Write(buffer, 0, read);
                downloaded += read;

                var done = completedBytes + downloaded;
                var reportedTotal = totalBytes > 0 ? totalBytes : done;
                status.Progress = Math.Clamp(done / (float)reportedTotal, 0f, 1f);
                status.CurrentFile = $"{file.TargetFileName}  ({downloaded / 1024 / 1024} MB)";
            }
        }

        if (!IsPlausibleSize(downloaded, file))
        {
            TryDeleteFile(partPath);
            throw new IOException($"{file.TargetFileName} arrived with {downloaded / 1024 / 1024} MB, expected about " +
                                  $"{file.ExpectedSizeBytes / 1024 / 1024} MB - the source may have changed. Try again later.");
        }

        TryDeleteFile(targetPath);
        File.Move(partPath, targetPath);
        return downloaded;
    }

    /** The expected sizes are catalog estimates; CDN content may differ a little but not wildly. */
    private static bool IsPlausibleSize(long size, CatalogFile file)
    {
        var expected = file.ExpectedSizeBytes;
        return expected <= 0 || Math.Abs(size - expected) / (double)expected < 0.25;
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception e)
        {
            Log.Warning($"[AI Models] Could not delete {path}: {e.Message}");
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (Exception e)
        {
            Log.Warning($"[AI Models] Could not delete {path}: {e.Message}");
        }
    }
}
