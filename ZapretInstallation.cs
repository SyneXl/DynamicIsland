using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Reflection.PortableExecutable;

namespace DynamicIsland;

sealed record ZapretStrategy(string FileName)
{
    public string Name => Path.GetFileNameWithoutExtension(FileName);
}

sealed record ZapretInstallation(string Root, string Version, ZapretStrategy[] Strategies)
{
    public string Name => new DirectoryInfo(Root).Name;
    public string Executable => Path.Combine(Root, "bin", "winws.exe");
    public static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    public static bool HasRuntimeContract(string root)
    {
        try
        {
            if (!File.Exists(Path.Combine(root, "bin", "winws.exe"))) return false;
            string source = ReadText(Path.Combine(root, "service.bat"));
            return Regex.IsMatch(source, @"(?im)^:service_install\s*$") && Regex.IsMatch(source, @"(?im)^:service_remove\s*$")
                && source.Contains("zapret", StringComparison.OrdinalIgnoreCase) && source.Contains("winws.exe", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }
    public static string ReadText(string file)
    {
        if (new FileInfo(file).Length > 1_048_576) throw new InvalidDataException("Слишком большой BAT-файл.");
        byte[] bytes = File.ReadAllBytes(file);
        try { return new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF'); }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(1251).GetString(bytes);
        }
    }

    public static ZapretInstallation? Inspect(string path)
    {
        try
        {
            string root = Normalize(path), file = Path.Combine(root, "service.bat");
            if (!File.Exists(file) || !File.Exists(Path.Combine(root, "bin", "winws.exe"))) return null;
            string source = ReadText(file);
            if (!Regex.IsMatch(source, @"(?im)^:service_install\s*$") || !Regex.IsMatch(source, @"(?im)^:service_remove\s*$")
                || !source.Contains("zapret", StringComparison.OrdinalIgnoreCase) || !source.Contains("winws.exe", StringComparison.OrdinalIgnoreCase)) return null;
            using (var stream = File.OpenRead(Path.Combine(root, "bin", "winws.exe")))
            using (var pe = new PEReader(stream))
                if (pe.PEHeaders.PEHeader == null) return null;
            var strategies = Directory.EnumerateFiles(root, "*.bat", SearchOption.TopDirectoryOnly)
                .Where(p => !Path.GetFileName(p).StartsWith("service", StringComparison.OrdinalIgnoreCase))
                .Where(p => Regex.IsMatch(ReadText(p), @"(?im)^(?!\s*(?:::|rem\b)).*winws\.exe"""))
                .OrderBy(p => Natural(Path.GetFileName(p)), StringComparer.OrdinalIgnoreCase)
                .Select(p => new ZapretStrategy(Path.GetFileName(p))).ToArray();
            if (strategies.Length == 0) return null;
            string version = Regex.Match(source, "LOCAL_VERSION=([^\"\\r\\n]+)", RegexOptions.IgnoreCase).Groups[1].Value;
            return new(root, version.Length == 0 ? "?" : version, strategies);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or BadImageFormatException) { return null; }
    }

    static string Natural(string name) => Regex.Replace(name, @"\d+", m => m.Value.PadLeft(8, '0'));
}

sealed record ZapretPreferences(string? SelectedRoot, Dictionary<string, string> Strategies)
{
    public static ZapretPreferences Empty => new(null, new(StringComparer.OrdinalIgnoreCase));
    public static ZapretPreferences Parse(string? json)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(json) || json.Length > 65536) return Empty;
            var value = JsonSerializer.Deserialize<ZapretPreferences>(json);
            var entries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (value?.Strategies != null)
                foreach (var pair in value.Strategies)
                    if (Path.IsPathFullyQualified(pair.Key) && pair.Value is { Length: > 0 }
                        && Path.GetFileName(pair.Value) == pair.Value && pair.Value.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
                        entries[ZapretInstallation.Normalize(pair.Key)] = pair.Value;
            string? root = value?.SelectedRoot is { } path && Path.IsPathFullyQualified(path) ? ZapretInstallation.Normalize(path) : null;
            return new(root, entries);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or NotSupportedException) { return Empty; }
    }
    public string Serialize() => JsonSerializer.Serialize(this);
    public ZapretPreferences Select(string root, string? strategy = null)
    {
        root = ZapretInstallation.Normalize(root);
        var entries = new Dictionary<string, string>(Strategies, StringComparer.OrdinalIgnoreCase);
        if (strategy != null) entries[root] = strategy;
        return new(root, entries);
    }
}

sealed record ZapretScanProgress(ZapretInstallation? Found, int Directories, int Inaccessible, string Root, bool Complete = false);

static class ZapretDiscovery
{
    public static IEnumerable<string> LocalRoots()
    {
        foreach (var drive in DriveInfo.GetDrives())
        {
            string? root = null;
            try { if (drive.DriveType is DriveType.Fixed or DriveType.Removable && drive.IsReady) root = drive.RootDirectory.FullName; }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            if (root != null) yield return root;
        }
    }

    public static Task ScanAsync(IEnumerable<string> roots, IEnumerable<string> hints, Action<ZapretScanProgress> report, CancellationToken cancellation) => Task.Run(() =>
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int directories = 0, inaccessible = 0;
        void Inspect(string path)
        {
            if (ZapretInstallation.Inspect(path) is { } item && found.Add(item.Root)) report(new(item, directories, inaccessible, item.Root));
        }
        foreach (string path in hints.Distinct(StringComparer.OrdinalIgnoreCase)) { cancellation.ThrowIfCancellationRequested(); Inspect(path); }
        var options = new EnumerationOptions { RecurseSubdirectories = false, IgnoreInaccessible = false, AttributesToSkip = FileAttributes.ReparsePoint };
        long last = Environment.TickCount64;
        foreach (string root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var pending = new Stack<string>(); pending.Push(root);
            while (pending.TryPop(out string? path))
            {
                cancellation.ThrowIfCancellationRequested();
                try
                {
                    if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) continue;
                    // Test corpora created by this app are not user installations. An explicitly
                    // supplied scan root is still inspectable by the regression suite.
                    if (!path.Equals(root, StringComparison.OrdinalIgnoreCase) && File.Exists(Path.Combine(path, ".dynamic-island-test-fixtures"))) continue;
                    directories++;
                    if (File.Exists(Path.Combine(path, "service.bat"))) Inspect(path);
                    foreach (string child in Directory.EnumerateDirectories(path, "*", options)) pending.Push(child);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { inaccessible++; }
                if (Environment.TickCount64 - last >= 300)
                {
                    report(new(null, directories, inaccessible, root)); last = Environment.TickCount64;
                    Thread.Sleep(2); // One low-noise worker, not one task per directory.
                }
            }
        }
        report(new(null, directories, inaccessible, "", true));
    }, cancellation);
}
