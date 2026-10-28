using System.IO;
using System.Text.Json;

namespace DynamicIsland;

// Persist the notification, not installation metadata. Downloads still go through Updater verification.
sealed class UpdateAnnouncement
{
    sealed record Stored(string Version, bool Presented, bool Pending);
    readonly string _path;
    public Version? Version { get; private set; }
    public bool Presented { get; private set; }
    public bool Pending { get; private set; }

    public UpdateAnnouncement(string? path = null)
    {
        _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DynamicIsland", "Updates", "notification.json");
        try
        {
            if (!File.Exists(_path) || new FileInfo(_path).Length > 4096) return;
            var value = JsonSerializer.Deserialize<Stored>(File.ReadAllText(_path));
            if (value != null && System.Version.TryParse(value.Version, out var version))
                (Version, Presented, Pending) = (version, value.Presented, value.Pending);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { App.Log(ex); }
    }

    public void Observe(Version? available, Version current)
    {
        if (Version != null && Version <= current)
        {
            Version = null; Presented = Pending = false; Save();
        }
        if (available == null || available <= current || Version != null && available <= Version) return;
        Version = available; Presented = false; Pending = true; Save();
    }

    public bool Present()
    {
        if (!Pending || Presented) return false;
        Presented = true; Save(); return true;
    }

    public void Consume()
    {
        if (!Pending) return;
        Pending = false; Presented = true; Save();
    }

    void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            string temporary = _path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(new Stored(Version?.ToString() ?? "", Presented, Pending)));
            File.Move(temporary, _path, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { App.Log(ex); }
    }
}
