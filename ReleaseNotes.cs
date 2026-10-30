using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DynamicIsland;

sealed record ReleaseNote(string Version, string[] Lines);
sealed record ReleaseNotesSnapshot(string? From, string To, ReleaseNote[] Releases, bool Complete, string Repository = Updater.Repository)
{
    public static bool TryVersion(string? value, out Version version)
    {
        version = new(0, 0, 0);
        return value != null && Regex.IsMatch(value, @"\Av?\d+\.\d+\.\d+\z", RegexOptions.CultureInvariant)
            && System.Version.TryParse(value.TrimStart('v'), out version!);
    }

    public bool Valid()
    {
        if (Repository != Updater.Repository || !TryVersion(To, out var to) || From != null && (!TryVersion(From, out var from) || from >= to)
            || Releases == null || Releases.Length is < 1 or > 500) return false;
        var versions = new HashSet<Version>(); int length = 0;
        foreach (var release in Releases)
        {
            if (release == null || !TryVersion(release.Version, out var version) || version > to
                || From != null && TryVersion(From, out var lower) && version <= lower || !versions.Add(version)
                || release.Lines == null || release.Lines.Length > 200) return false;
            foreach (string line in release.Lines)
                if (line == null || line.Length > 1000 || (length += line.Length) > 200_000) return false;
        }
        return versions.Contains(to);
    }

    public bool Matches(Version from, Version to) => From == from.ToString() && To == to.ToString() && Valid();

    public static ReleaseNotesSnapshot Create(Version? from, Version to, IEnumerable<ReleaseNote> releases, bool complete) =>
        new(from?.ToString(), to.ToString(), releases
            .Where(r => TryVersion(r.Version, out var version) && version <= to && (from == null ? version == to : version > from))
            .GroupBy(r => System.Version.Parse(r.Version.TrimStart('v'))).OrderByDescending(g => g.Key)
            .Select(g => new ReleaseNote(g.Key.ToString(), g.First().Lines)).ToArray(), complete);
}

// A small plain-text journal, independent of GUID staging folders and their cleanup.
// Installed snapshots are version-keyed: rollback can still show the old version's notes.
sealed class ReleaseNotesStore
{
    sealed record Stored(int Schema, string Repository, ReleaseNotesSnapshot? Available, ReleaseNotesSnapshot[] Installed);
    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DynamicIsland", "Updates", "history.json");
    readonly string? _path;
    ReleaseNotesSnapshot? _available;
    readonly Dictionary<string, ReleaseNotesSnapshot> _installed = [];
    public ReleaseNotesStore(string? path = null) { _path = path; Reload(); }
    public void Reload()
    {
        if (_path == null) return;
        try
        {
            if (!File.Exists(_path) || new FileInfo(_path).Length > 2_000_000) return;
            var stored = JsonSerializer.Deserialize<Stored>(File.ReadAllText(_path));
            if (stored is not { Schema: 1, Repository: Updater.Repository, Installed.Length: <= 8 }) return;
            _available = stored.Available?.Valid() == true ? stored.Available : null;
            _installed.Clear();
            foreach (var item in stored.Installed) if (item?.Valid() == true) _installed[item.To] = item;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { App.Log(ex); }
    }
    public ReleaseNotesSnapshot? Available(Version from, Version to) => _available?.Matches(from, to) == true ? _available : null;
    public ReleaseNotesSnapshot? Installed(Version version) => _installed.GetValueOrDefault(version.ToString());
    public bool SaveAvailable(ReleaseNotesSnapshot snapshot)
    {
        if (!snapshot.Valid()) return false;
        if (_available is { Complete: true } previous && !snapshot.Complete && previous.From == snapshot.From && previous.To == snapshot.To) return true;
        _available = snapshot; return Save();
    }
    public bool SaveInstalled(ReleaseNotesSnapshot snapshot)
    {
        if (!snapshot.Valid()) return false;
        // Re-read to preserve the available cache written by the newly started UI.
        Reload();
        _installed[snapshot.To] = snapshot;
        foreach (string old in _installed.Keys.OrderByDescending(Version.Parse).Skip(8).ToArray()) _installed.Remove(old);
        return Save();
    }
    bool Save()
    {
        if (_path == null) return true; // Explicitly in-memory for fixtures/smoke tests.
        string temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            string json = JsonSerializer.Serialize(new Stored(1, Updater.Repository, _available, _installed.Values.ToArray()));
            if (json.Length > 2_000_000) throw new IOException("Release notes journal is too large.");
            File.WriteAllText(temporary, json);
            File.Move(temporary, _path, true); return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { App.Log(ex); return false; }
        finally { try { File.Delete(temporary); } catch { } }
    }
}

static class BundledReleaseNotes
{
    sealed record PublishedIndex(int Schema, string Repository, string[] Versions);
    static readonly Lazy<HashSet<Version>> Published = new(() =>
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("DynamicIsland.ReleaseNotes.Published");
        if (stream == null) return [];
        PublishedIndex? index;
        try { index = JsonSerializer.Deserialize<PublishedIndex>(stream); }
        catch (JsonException ex) { App.Log(ex); return []; }
        return index is { Schema: 1, Repository: Updater.Repository, Versions: not null }
            ? index.Versions.Where(v => ReleaseNotesSnapshot.TryVersion(v, out _)).Select(Version.Parse).ToHashSet() : [];
    });
    public static bool IsPublished(Version version) => Published.Value.Contains(version);
    static readonly Lazy<ReleaseNote[]> All = new(() =>
    {
        const string prefix = "DynamicIsland.ReleaseNotes.";
        var assembly = Assembly.GetExecutingAssembly(); var result = new List<ReleaseNote>();
        foreach (string name in assembly.GetManifestResourceNames().Where(n => n.StartsWith(prefix, StringComparison.Ordinal)))
        {
            if (!ReleaseNotesSnapshot.TryVersion(name[prefix.Length..], out var version) || version > Updater.CurrentVersion) continue;
            if (version != Updater.CurrentVersion && !IsPublished(version)) continue;
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            result.Add(new(version.ToString(), Updater.ParseNotes(reader.ReadToEnd())));
        }
        return result.ToArray();
    });
    public static ReleaseNotesSnapshot? For(Version? from, Version to)
    {
        if (to > Updater.CurrentVersion) return null;
        bool complete = Published.Value.Count > 0 && Published.Value.Where(v => v <= to && (from == null ? v == to : v > from))
            .All(v => All.Value.Any(n => n.Version == v.ToString()));
        var result = ReleaseNotesSnapshot.Create(from, to, All.Value, complete);
        return result.Valid() ? result : null;
    }
}
