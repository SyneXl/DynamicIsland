using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace DynamicIsland;

// A verified downloaded EXE is also the helper. It waits outside the island's
// single-instance mutex, and never replaces a still-running island.
static class UpdateHandoff
{
    internal sealed record Plan(string Target, string Payload, string Sha256, string Version,
        string OriginalSha256, int ParentPid, long ParentStarted, string[]? TestArguments = null, ReleaseNotesSnapshot? Notes = null);
    static string UpdatesRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DynamicIsland", "Updates");
    public static string NewFolder()
    {
        string folder = Path.Combine(UpdatesRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    public static void Launch(VerifiedUpdate update)
    {
        string folder = Path.GetDirectoryName(update.Payload)!;
        ValidateFolder(folder);
        if ((File.GetAttributes(update.Target) & FileAttributes.ReadOnly) != 0)
            throw new IOException("The island executable is read-only.");
        // Fail while the current island is still alive if this portable folder
        // cannot accept the atomic replacement.
        string probe = Path.Combine(Path.GetDirectoryName(update.Target)!, ".island-update-" + Guid.NewGuid().ToString("N"));
        using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
        using var parent = Process.GetCurrentProcess();
        var plan = new Plan(Path.GetFullPath(update.Target), Path.GetFullPath(update.Payload), update.Sha256, update.Version,
            Hash(update.Target), parent.Id, parent.StartTime.ToUniversalTime().Ticks, Notes: update.Notes);
        string path = Path.Combine(folder, "plan.json");
        File.WriteAllText(path, JsonSerializer.Serialize(plan));
        var start = new ProcessStartInfo(update.Payload) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = folder };
        start.ArgumentList.Add("--apply-update");
        start.ArgumentList.Add(path);
        if (Process.Start(start) is not { } helper) throw new IOException("Could not start update helper.");
        helper.Dispose();
    }

    public static async Task<int> RunAsync(string manifest, TimeSpan? readyTimeout = null, Func<ReleaseNotesSnapshot, bool>? saveNotes = null)
    {
        string folder = Path.GetDirectoryName(Path.GetFullPath(manifest))!;
        string backup = "", incoming = "";
        Plan? plan = null;
        bool replaced = false, restored = false, canRestartOriginal = false;
        Process? started = null;
        try
        {
            ValidateFolder(folder);
            if (Path.GetFileName(manifest) != "plan.json") throw new InvalidDataException("Invalid update manifest.");
            plan = JsonSerializer.Deserialize<Plan>(await File.ReadAllTextAsync(manifest)) ?? throw new InvalidDataException("Empty update plan.");
            string payload = Path.Combine(folder, Updater.AssetName);
            if (!Path.GetFullPath(plan.Payload).Equals(payload, StringComparison.OrdinalIgnoreCase)
                || !Path.IsPathFullyQualified(plan.Target) || !Path.GetExtension(plan.Target).Equals(".exe", StringComparison.OrdinalIgnoreCase)
                || Path.GetDirectoryName(plan.Target)!.Equals(folder, StringComparison.OrdinalIgnoreCase)
                || !Version.TryParse(plan.Version, out _) || !Hash(payload).Equals(plan.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Invalid update payload or target.");
            // Include creation time so a reused PID cannot become our parent.
            using (var parent = FindParent(plan))
            {
                if (parent != null)
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                    await parent.WaitForExitAsync(timeout.Token);
                }
            }
            if (!Hash(plan.Target).Equals(plan.OriginalSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The installed file changed since the update was requested.");
            // New helpers also understand plans emitted by older islands. Their
            // payload's bundled release archive fills the gap without a network.
            var originalInfo = FileVersionInfo.GetVersionInfo(plan.Target);
            var from = new Version(Math.Max(0, originalInfo.FileMajorPart), Math.Max(0, originalInfo.FileMinorPart), Math.Max(0, originalInfo.FileBuildPart));
            Version destination = Version.Parse(plan.Version);
            ReleaseNotesSnapshot? notes = plan.Notes?.Matches(from, destination) == true ? plan.Notes : null;
            if (notes?.Complete != true) notes = BundledReleaseNotes.For(from < destination ? from : null, destination) ?? notes;
            canRestartOriginal = true;
            string suffix = Path.GetFileName(folder);
            incoming = plan.Target + ".update-" + suffix + ".new";
            backup = plan.Target + ".update-" + suffix + ".old";
            File.Copy(payload, incoming, false);
            File.Replace(incoming, plan.Target, backup, true);
            replaced = true;
            string ready = Path.Combine(folder, "ready.json");
            // Only the isolated regression fixture sets TestArguments.
            started = Start(plan.Target, plan.TestArguments ?? ["--updated", ready]);
            var watch = Stopwatch.StartNew();
            bool acknowledged = false;
            while (watch.Elapsed < (readyTimeout ?? TimeSpan.FromSeconds(25)))
            {
                if (File.Exists(ready))
                {
                    try
                    {
                        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(ready));
                        acknowledged = json.RootElement.GetProperty("pid").GetInt32() == started.Id
                            && json.RootElement.GetProperty("version").GetString() == plan.Version;
                    }
                    catch (IOException) { }
                    catch (JsonException) { }
                }
                if (acknowledged || started.HasExited) break;
                await Task.Delay(100);
            }
            if (!acknowledged) throw new IOException("Updated application did not confirm startup.");
            // Never commit history for an unacknowledged launch/rollback, and do
            // not turn a cache write error into a failed executable installation.
            if (notes?.Valid() == true)
            {
                try
                {
                    bool saved = saveNotes?.Invoke(notes) ?? (plan.TestArguments != null || new ReleaseNotesStore(ReleaseNotesStore.DefaultPath).SaveInstalled(notes));
                    if (!saved) App.Log(new IOException("Could not save installed release notes."));
                }
                catch (Exception ex) { App.Log(ex); }
            }
            try { File.Delete(backup); }
            catch (Exception ex) { App.Log(ex); }
            try { await WriteResult(folder, true, false, ""); }
            catch (Exception ex) { App.Log(ex); }
            return 0;
        }
        catch (Exception ex)
        {
            App.Log(ex);
            if (replaced && plan != null)
            {
                try
                {
                    // Only the exact child started here may be stopped.
                    if (started is { HasExited: false })
                    {
                        started.Kill();
                        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                        await started.WaitForExitAsync(timeout.Token);
                    }
                    // Process exit can precede release of the mapped image (or
                    // a short antivirus scan). A single Move made rollback flaky.
                    await RestoreBackupAsync(backup, plan.Target);
                    restored = Hash(plan.Target).Equals(plan.OriginalSha256, StringComparison.OrdinalIgnoreCase);
                    if (restored) using (Start(plan.Target, plan.TestArguments ?? [])) { }
                }
                catch (Exception rollbackError) { App.Log(rollbackError); }
            }
            else if (canRestartOriginal && plan != null)
            {
                try { using (Start(plan.Target, plan.TestArguments ?? [])) { } }
                catch (Exception restartError) { App.Log(restartError); }
            }
            try { ValidateFolder(folder); await WriteResult(folder, false, restored, ex.Message); }
            catch { }
            return 1;
        }
        finally
        {
            started?.Dispose();
            try { if (incoming.Length > 0) File.Delete(incoming); }
            catch { }
        }
    }

    static Process? FindParent(Plan plan)
    {
        try
        {
            var process = Process.GetProcessById(plan.ParentPid);
            if (process.StartTime.ToUniversalTime().Ticks == plan.ParentStarted) return process;
            process.Dispose();
        }
        catch (ArgumentException) { }
        catch (InvalidOperationException) { }
        return null;
    }

    internal static async Task RestoreBackupAsync(string backup, string target)
    {
        long deadline = Environment.TickCount64 + 5000;
        while (true)
        {
            try { File.Move(backup, target, true); return; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && Environment.TickCount64 < deadline)
            { await Task.Delay(100); }
        }
    }

    public static void Acknowledge(string manifestReadyPath)
    {
        try
        {
            if (System.Windows.Application.Current?.MainWindow is not MainWindow { IsLoaded: true, IsVisible: true } window
                || window.FindName("Root") is not System.Windows.FrameworkElement { ActualWidth: > 0 }
                || window.FindName("UpdatePage") == null) return;
            string folder = Path.GetDirectoryName(Path.GetFullPath(manifestReadyPath))!;
            ValidateFolder(folder);
            if (Path.GetFileName(manifestReadyPath) != "ready.json") return;
            var plan = JsonSerializer.Deserialize<Plan>(File.ReadAllText(Path.Combine(folder, "plan.json")));
            if (plan == null || !Path.GetFullPath(Environment.ProcessPath!).Equals(plan.Target, StringComparison.OrdinalIgnoreCase)
                || Updater.CurrentVersion.ToString() != plan.Version) return;
            string temporary = manifestReadyPath + ".new";
            File.WriteAllText(temporary, JsonSerializer.Serialize(new { pid = Environment.ProcessId, version = Updater.CurrentVersion.ToString() }));
            File.Move(temporary, manifestReadyPath, true);
        }
        catch (Exception ex) { App.Log(ex); }
    }

    static Process Start(string file, string[] args)
    {
        var info = new ProcessStartInfo(file) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(file)! };
        foreach (string argument in args) info.ArgumentList.Add(argument);
        return Process.Start(info) ?? throw new IOException("Could not restart the island.");
    }

    static void ValidateFolder(string folder)
    {
        if (!Path.GetDirectoryName(folder)!.Equals(UpdatesRoot, StringComparison.OrdinalIgnoreCase)
            || !Guid.TryParseExact(Path.GetFileName(folder), "N", out _)
            || (File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0
            || (File.GetAttributes(UpdatesRoot) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Update folder is not a private staging directory.");
    }

    internal static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    public static async Task CleanCompletedAsync()
    {
        await Task.Delay(3000);
        try
        {
            if (!Directory.Exists(UpdatesRoot)) return;
            string[] allowed = [Updater.AssetName, "plan.json", "ready.json", "ready.json.new", "result.json"];
            foreach (string folder in Directory.EnumerateDirectories(UpdatesRoot))
            {
                try
                {
                    ValidateFolder(folder);
                    string result = Path.Combine(folder, "result.json");
                    bool complete = false;
                    if (File.Exists(result))
                    {
                        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(result));
                        complete = json.RootElement.GetProperty("installed").GetBoolean() || json.RootElement.GetProperty("restored").GetBoolean();
                    }
                    if (!complete && Directory.GetCreationTimeUtc(folder) > DateTime.UtcNow.AddDays(-7)) continue;
                    if (Directory.EnumerateDirectories(folder).Any() || Directory.EnumerateFiles(folder).Any(f => !allowed.Contains(Path.GetFileName(f)))) continue;
                    // No recursive deletion; a helper that is still running keeps
                    // its payload locked, and this operation is retried next time.
                    File.Delete(Path.Combine(folder, Updater.AssetName));
                    foreach (string name in allowed.Skip(1)) File.Delete(Path.Combine(folder, name));
                    Directory.Delete(folder);
                }
                catch { }
            }
        }
        catch { }
    }

    static Task WriteResult(string folder, bool installed, bool restored, string error) =>
        File.WriteAllTextAsync(Path.Combine(folder, "result.json"), JsonSerializer.Serialize(new { installed, restored, error, at = DateTime.UtcNow }));
}
