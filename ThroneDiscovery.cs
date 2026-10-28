using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace DynamicIsland;

sealed record ThronePreferences(string? DataDirectory = null, string? Executable = null, int Group = -1, int Profile = -1, bool SortByLatency = false)
{
    public static ThronePreferences Parse(string value)
    {
        try { return JsonSerializer.Deserialize<ThronePreferences>(value) ?? new(); }
        catch { return new(); }
    }
    public string Serialize() => JsonSerializer.Serialize(this);
}

sealed record ThroneDiscoveryResult(string? Executable, string? DataDirectory, bool Capable, bool Enabled, ThroneEndpoint? Endpoint, string? Error);

static class ThroneDiscovery
{
    static readonly Dictionary<string, (long Length, long Modified, bool Api)> Cache = new(StringComparer.OrdinalIgnoreCase);
    public static ThroneDiscoveryResult Find(ThronePreferences preferences)
    {
        string[] installed = Executables(preferences.Executable).Distinct(StringComparer.OrdinalIgnoreCase).Where(File.Exists).ToArray();
        string? executable = installed.FirstOrDefault(HasApi) ?? installed.FirstOrDefault();
        string local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Throne");
        string? install = executable == null ? null : Path.GetDirectoryName(executable);
        string? pinned = ReadMarker(local);
        string? preferred = pinned != null && string.Equals(pinned, install, StringComparison.OrdinalIgnoreCase) ? local : install;
        var candidates = (preferences.DataDirectory != null ? new[] { preferences.DataDirectory } : new[] { preferred, local, install, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Throne") })
            .Where(p => !string.IsNullOrWhiteSpace(p)).Cast<string>().Distinct(StringComparer.OrdinalIgnoreCase);
        bool capable = executable != null && HasApi(executable);
        if (!capable) return new(executable, preferences.DataDirectory, false, false, null, null);
        foreach (string root in candidates)
        {
            string db = Path.Combine(root, "config", "throne.db");
            if (!File.Exists(db)) continue;
            try
            {
                var settings = ReadApiSettings(db);
                // The presence of the actual API schema, not just 1.4.0 file
                // metadata, distinguishes the beta from non-API builds.
                bool schema = settings.ContainsKey("remote_api_enable");
                if (!schema) continue;
                bool enabled = settings.GetValueOrDefault("remote_api_enable") == "true";
                if (!enabled) return new(executable, root, capable, false, null, null);
                int port = int.TryParse(settings.GetValueOrDefault("remote_api_port"), out int p) && p is > 0 and <= 65535 ? p : 9095;
                string key = settings.GetValueOrDefault("remote_api_key", "");
                if (key.Length == 0 && File.Exists(Path.Combine(root, "config", "api.key"))) key = File.ReadAllText(Path.Combine(root, "config", "api.key"));
                try { return new(executable, root, true, true, new ThroneEndpoint("127.0.0.1", port, key), null); }
                catch (InvalidDataException) { return new(executable, root, true, true, null, "Обнови ключ API в настройках Throne"); }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { }
        }
        // Headless installs keep a key file rather than a GUI setting.
        if (preferences.DataDirectory is { } data && File.Exists(Path.Combine(data, "config", "api.key")))
        {
            try { return new(executable, data, true, true, new ThroneEndpoint("127.0.0.1", 9095, File.ReadAllText(Path.Combine(data, "config", "api.key"))), null); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { }
        }
        return new(executable, preferences.DataDirectory, capable, false, null, preferences.DataDirectory == null ? null : "Проверь папку данных: настройки API не найдены");
    }

    static string? ReadMarker(string root)
    {
        try { return File.ReadAllText(Path.Combine(root, "config", ".install-dir-unwritable")).Trim().Replace('/', Path.DirectorySeparatorChar); }
        catch { return null; }
    }

    static IEnumerable<string> Executables(string? selected)
    {
        if (selected != null) yield return selected;
        foreach (var process in Process.GetProcessesByName("Throne"))
        {
            string? path = null;
            using (process)
            {
                IntPtr handle = OpenProcess(0x1000, false, process.Id);
                if (handle != IntPtr.Zero)
                {
                    try { var text = new StringBuilder(32768); int length = text.Capacity; if (QueryFullProcessImageName(handle, 0, text, ref length)) path = text.ToString(); }
                    finally { CloseHandle(handle); }
                }
            }
            if (path != null) yield return path;
        }
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
            foreach (string branch in new[] { @"Software\Microsoft\Windows\CurrentVersion\Uninstall", @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" })
            {
                var paths = new List<string>();
                try
                {
                    using var parent = hive.OpenSubKey(branch);
                    if (parent != null) foreach (string sub in parent.GetSubKeyNames())
                    {
                        using var key = parent.OpenSubKey(sub);
                        if ((key?.GetValue("DisplayName") as string)?.StartsWith("Throne", StringComparison.OrdinalIgnoreCase) != true) continue;
                        if (key?.GetValue("InstallLocation") is string dir && dir.Length > 0) paths.Add(Path.Combine(dir, "Throne.exe"));
                    }
                }
                catch (System.Security.SecurityException) { }
                foreach (string path in paths) yield return path;
            }
        foreach (string root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) })
        {
            yield return Path.Combine(root, "Throne", "Throne.exe");
            yield return Path.Combine(root, "Programs", "Throne", "Throne.exe");
        }
    }

    internal static bool HasApi(string path)
    {
        try
        {
            var file = new FileInfo(path);
            lock (Cache) if (Cache.TryGetValue(path, out var cached) && cached.Length == file.Length && cached.Modified == file.LastWriteTimeUtc.Ticks) return cached.Api;
            byte[] marker = Encoding.ASCII.GetBytes("throne-api/1 c2s"), buffer = new byte[65536 + marker.Length];
            bool found = false;
            using var stream = File.OpenRead(path);
            int kept = 0, read;
            while ((read = stream.Read(buffer, kept, 65536)) > 0)
            {
                int total = kept + read;
                if (buffer.AsSpan(0, total).IndexOf(marker) >= 0) { found = true; break; }
                kept = Math.Min(marker.Length - 1, total); buffer.AsSpan(total - kept, kept).CopyTo(buffer);
            }
            lock (Cache) Cache[path] = (file.Length, file.LastWriteTimeUtc.Ticks, found);
            return found;
        }
        catch { return false; }
    }

    // Windows ships SQLite. Read-only against the live WAL; no copies of stale
    // database pages, no writes to Throne's preferences and no secret in logs.
    internal static Dictionary<string, string> ReadApiSettings(string path)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        IntPtr db = IntPtr.Zero, statement = IntPtr.Zero;
        try
        {
            if (sqlite3_open_v2(path, out db, 1, IntPtr.Zero) != 0) throw new IOException("База Throne недоступна");
            sqlite3_busy_timeout(db, 500);
            if (sqlite3_prepare_v2(db, "SELECT key,value FROM settings WHERE key IN ('remote_api_enable','remote_api_key','remote_api_port')", -1, out statement, IntPtr.Zero) != 0)
                throw new InvalidDataException("Неизвестная база Throne");
            int status;
            while ((status = sqlite3_step(statement)) == 100)
                result[Marshal.PtrToStringUTF8(sqlite3_column_text(statement, 0)) ?? ""] = Marshal.PtrToStringUTF8(sqlite3_column_text(statement, 1)) ?? "";
            if (status != 101) throw new IOException("База Throne занята");
            return result;
        }
        finally { if (statement != IntPtr.Zero) sqlite3_finalize(statement); if (db != IntPtr.Zero) sqlite3_close(db); }
    }
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out IntPtr db, int flags, IntPtr vfs);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_prepare_v2(IntPtr db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, int length, out IntPtr statement, IntPtr tail);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_step(IntPtr statement);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] static extern IntPtr sqlite3_column_text(IntPtr statement, int column);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_busy_timeout(IntPtr db, int ms);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_finalize(IntPtr statement);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_close(IntPtr db);
    [DllImport("kernel32", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, int id);
    [DllImport("kernel32", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder path, ref int length);
    [DllImport("kernel32")] static extern bool CloseHandle(IntPtr handle);
}
