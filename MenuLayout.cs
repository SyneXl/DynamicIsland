using System.Text.Json;

namespace DynamicIsland;

sealed record MenuDefinition(string Id, string Title, Glyph Icon, bool Required = false, bool DefaultVisible = true);
sealed record MenuEntry(string Id, bool Visible = true, bool Automatic = false);

// IDs are persistence keys, independent of translations, labels and enum ordinals.
sealed class MenuLayout
{
    public static IReadOnlyList<MenuDefinition> Catalog { get; } = Array.AsReadOnly<MenuDefinition>([
        new("timer", "Таймер", Glyph.Clock),
        new("obs", "Запись OBS", Glyph.Record),
        new("zapret", "Zapret", Glyph.Shield),
        new("throne", "Throne", Glyph.Vpn, DefaultVisible: false),
        new("shelf", "Полка", Glyph.Tray),
        new("weather", "Погода", Glyph.CloudSun),
        new("qr", "QR-код с экрана", Glyph.Qr),
        new("codex", "Codex", Glyph.Sparkle),
        new("settings", "Настройки", Glyph.Gear, true),
        new("appearance", "Оформление", Glyph.Look, true),
        new("exit", "Закрыть остров", Glyph.Power, true),
    ]);

    sealed record Stored(int Version, MenuEntry[] Entries);
    const int FormatVersion = 2, MaxStoredLength = 65_536;
    readonly IReadOnlyList<MenuDefinition> _catalog;
    public IReadOnlyList<MenuEntry> Entries { get; }
    public int VisibleCount => Entries.Count(e => e.Visible);
    public static bool ThroneDefaultVisible { get; set; }
    static bool DefaultVisible(MenuDefinition d) => d.Required || d.DefaultVisible || d.Id == "throne" && ThroneDefaultVisible;
    public bool IsDefault => Entries.SequenceEqual(_catalog.Select(d => new MenuEntry(d.Id, DefaultVisible(d), d.Id == "throne")));

    MenuLayout(IEnumerable<MenuEntry?> entries, IReadOnlyList<MenuDefinition> catalog)
    {
        _catalog = catalog;
        var known = catalog.ToDictionary(d => d.Id, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<MenuEntry>();
        foreach (MenuEntry? entry in entries)
        {
            if (entry?.Id == null || !known.TryGetValue(entry.Id, out var definition) || !seen.Add(entry.Id)) continue;
            result.Add(entry with { Visible = definition.Required || entry.Visible, Automatic = entry.Id == "throne" && entry.Automatic });
        }
        // Add Zapret beside integrations, not below the protected footer. Existing
        // entries keep their relative order and visibility.
        foreach (var definition in catalog)
            if (seen.Add(definition.Id))
            {
                int obs = result.FindIndex(e => e.Id == "obs");
                int zapret = result.FindIndex(e => e.Id == "zapret");
                var added = new MenuEntry(definition.Id, DefaultVisible(definition), definition.Id == "throne");
                if (definition.Id == "zapret" && obs >= 0) result.Insert(obs + 1, added);
                else if (definition.Id == "throne" && zapret >= 0) result.Insert(zapret + 1, added);
                else result.Add(added);
            }
        Entries = result.AsReadOnly();
    }

    public static MenuLayout Default(IReadOnlyList<MenuDefinition>? catalog = null) => new([], catalog ?? Catalog);


    public static MenuLayout Parse(string? json, IReadOnlyList<MenuDefinition>? catalog = null)
    {
        catalog ??= Catalog;
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaxStoredLength) return Default(catalog);
        try
        {
            var stored = JsonSerializer.Deserialize<Stored>(json);
            if (stored is not { Version: 1 or FormatVersion, Entries: not null }) return Default(catalog);
            var entries = stored.Entries;
            if (stored.Version == 1)
            {
                // v1 appended newly introduced integrations after Exit. Migrate
                // only that legacy/default arrangement, not arbitrary user sorts.
                string[] legacy = ["timer", "obs", "shelf", "weather", "qr", "codex", "settings", "appearance", "exit"];
                var ids = entries.Where(e => e?.Id != null).Select(e => e.Id).ToArray();
                int zapret = Array.IndexOf(ids, "zapret");
                if (entries.All(e => e != null) && ids.Where(id => id != "zapret").SequenceEqual(legacy) && zapret is 6 or 9)
                {
                    var ordered = entries.ToList();
                    var item = ordered.Single(e => e.Id == "zapret");
                    ordered.Remove(item); ordered.Insert(ordered.FindIndex(e => e.Id == "obs") + 1, item);
                    entries = ordered.ToArray();
                }
            }
            return new(entries, catalog);
        }
        catch (JsonException) { return Default(catalog); }
    }

    public string Serialize() => JsonSerializer.Serialize(new Stored(FormatVersion, Entries.ToArray()));

    public MenuLayout SetVisible(string id, bool visible) =>
        new(Entries.Select(e => e.Id == id ? e with { Visible = visible, Automatic = false } : e), _catalog);

    public MenuLayout DetectThrone(bool available) =>
        new(Entries.Select(e => e.Id == "throne" && e.Automatic ? e with { Visible = available } : e), _catalog);

    public MenuLayout Move(string id, int offset)
    {
        var entries = Entries.ToList();
        int from = entries.FindIndex(e => e.Id == id);
        if (from < 0) return this;
        int to = (int)Math.Clamp((long)from + offset, 0, entries.Count - 1);
        if (from == to) return this;
        MenuEntry entry = entries[from];
        entries.RemoveAt(from);
        entries.Insert(to, entry);
        return new(entries, _catalog);
    }
}
