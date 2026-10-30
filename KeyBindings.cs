using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Input;

namespace DynamicIsland;

enum BindingInput { Key, LeftClick, RightClick, MiddleClick, Mouse4, Mouse5, WheelUp, WheelDown }

sealed record BindingGesture(BindingInput Input, ModifierKeys Modifiers = ModifierKeys.None, Key Key = Key.None)
{
    [JsonIgnore]
    public bool Valid => Enum.IsDefined(Input) && (Modifiers & ~(ModifierKeys.Alt | ModifierKeys.Control | ModifierKeys.Shift | ModifierKeys.Windows)) == 0
        && (Input == BindingInput.Key ? Enum.IsDefined(Key) && Key is not (Key.None or Key.System or Key.ImeProcessed or Key.DeadCharProcessed
            or Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin
            or Key.Escape or Key.F12 or Key.VolumeUp or Key.VolumeDown or Key.VolumeMute or Key.MediaNextTrack or Key.MediaPreviousTrack
            or Key.MediaPlayPause or Key.MediaStop) : Key == Key.None);

    [JsonIgnore]
    public string Label
    {
        get
        {
            string prefix = (Modifiers.HasFlag(ModifierKeys.Control) ? "Ctrl + " : "")
                + (Modifiers.HasFlag(ModifierKeys.Alt) ? "Alt + " : "")
                + (Modifiers.HasFlag(ModifierKeys.Shift) ? "Shift + " : "")
                + (Modifiers.HasFlag(ModifierKeys.Windows) ? "Win + " : "");
            return prefix + (Input switch
            {
                BindingInput.LeftClick => "ЛКМ", BindingInput.RightClick => "ПКМ", BindingInput.MiddleClick => "СКМ",
                BindingInput.Mouse4 => "Mouse 4", BindingInput.Mouse5 => "Mouse 5",
                BindingInput.WheelUp => "Колесо вверх", BindingInput.WheelDown => "Колесо вниз",
                _ => new KeyConverter().ConvertToString(Key) ?? Key.ToString(),
            });
        }
    }

    public static BindingInput Mouse(MouseButton button) => button switch
    {
        MouseButton.Left => BindingInput.LeftClick, MouseButton.Right => BindingInput.RightClick,
        MouseButton.Middle => BindingInput.MiddleClick, MouseButton.XButton1 => BindingInput.Mouse4, _ => BindingInput.Mouse5,
    };
}

sealed record BindingDefinition(string Id, string Title, string Group, BindingGesture? Default = null);
sealed record BindingEntry(string Id, BindingGesture? Gesture);

sealed class KeyBindings
{
    public static readonly IReadOnlyList<BindingDefinition> Catalog = Array.AsReadOnly<BindingDefinition>([
        new("open", "Развернуть / свернуть", "Остров", new(BindingInput.LeftClick)),
        new("menu", "Меню", "Остров", new(BindingInput.RightClick)),
        new("hide", "Скрыть на 5 секунд", "Остров", new(BindingInput.MiddleClick)),
        new("volume-up", "Громкость выше", "Звук", new(BindingInput.WheelUp)),
        new("volume-down", "Громкость ниже", "Звук", new(BindingInput.WheelDown)),
        new("app-volume-up", "Приложение — громче", "Звук", new(BindingInput.WheelUp, ModifierKeys.Shift)),
        new("app-volume-down", "Приложение — тише", "Звук", new(BindingInput.WheelDown, ModifierKeys.Shift)),
        new("play", "Воспроизведение / пауза", "Звук"),
        new("previous", "Предыдущий трек", "Звук"), new("next", "Следующий трек", "Звук"),
        new("page-previous", "Предыдущий источник", "Звук", new(BindingInput.WheelUp, ModifierKeys.Control)),
        new("page-next", "Следующий источник", "Звук", new(BindingInput.WheelDown, ModifierKeys.Control)),
        new("translation", "Перевод с экрана", "Функции", new(BindingInput.Key, ModifierKeys.Control, Key.T)),
        new("mixer", "Микшер", "Функции"), new("shelf", "Полка", "Функции"),
        new("weather", "Погода", "Функции"), new("timer", "Таймер", "Функции"),
        new("record", "Запись OBS", "Функции"), new("qr", "QR-код с экрана", "Функции"),
        new("qr-area", "QR-код из области", "Функции"), new("codex", "Codex", "Функции"),
        new("zapret", "Zapret", "Функции"), new("throne", "Throne", "Функции"),
        new("settings", "Настройки", "Навигация"), new("look", "Оформление", "Навигация"),
        new("lyrics", "Настройки текста песен", "Навигация"), new("update", "Обновление", "Навигация"),
        new("bindings", "Горячие клавиши", "Навигация"),
    ]);

    readonly Dictionary<string, BindingGesture?> _values;
    public KeyBindings() => _values = Catalog.ToDictionary(d => d.Id, d => d.Default, StringComparer.Ordinal);
    KeyBindings(Dictionary<string, BindingGesture?> values) => _values = values;
    public BindingGesture? this[string id] => _values.GetValueOrDefault(id);
    public bool HasSettingsRoute => new[] { "menu", "settings", "bindings", "look", "lyrics", "update" }.Any(id => this[id] != null);
    public string? Find(BindingGesture gesture) => gesture.Valid ? Catalog.FirstOrDefault(d => this[d.Id] == gesture)?.Id : null;
    public string? Conflict(string id, BindingGesture? gesture) => gesture == null ? null : Find(gesture) is { } other && other != id ? other : null;

    public KeyBindings Assign(string id, BindingGesture? gesture, bool replace = false)
    {
        if (!_values.ContainsKey(id) || gesture is { Valid: false }) throw new ArgumentException("Invalid binding");
        string? conflict = Conflict(id, gesture);
        if (conflict != null && !replace) throw new InvalidOperationException("Binding is already assigned");
        var values = new Dictionary<string, BindingGesture?>(_values, StringComparer.Ordinal);
        if (conflict != null) values[conflict] = null;
        values[id] = gesture;
        return new(values);
    }

    sealed record Document(int Version, BindingEntry[] Entries);
    public string Serialize() => JsonSerializer.Serialize(new Document(2, Catalog.Select(d => new BindingEntry(d.Id, this[d.Id])).ToArray()));
    public static KeyBindings Parse(string json)
    {
        var result = new KeyBindings();
        if (string.IsNullOrWhiteSpace(json) || json.Length > 65536) return result;
        try
        {
            var document = JsonSerializer.Deserialize<Document>(json);
            if (document is not { Version: 1 or 2, Entries: not null } || document.Entries.Length > 256) return result;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var assigned = new HashSet<BindingGesture>();
            foreach (var entry in document.Entries)
            {
                if (entry == null || string.IsNullOrEmpty(entry.Id) || !seen.Add(entry.Id)) return new();
                if (!result._values.ContainsKey(entry.Id)) continue; // Retired/future actions do not affect known bindings.
                if (entry.Gesture is { Valid: false } || entry.Gesture != null && !assigned.Add(entry.Gesture)) return new();
                result._values[entry.Id] = entry.Gesture;
            }
            // Upgrade the previous preview's default without overwriting a custom
            // binding, a disabled action, or Ctrl+T assigned to another action.
            if (document.Version == 1 && result["translation"] == new BindingGesture(BindingInput.Key, ModifierKeys.Control | ModifierKeys.Shift, Key.T)
                && result.Find(Catalog.First(d => d.Id == "translation").Default!) == null)
                result._values["translation"] = Catalog.First(d => d.Id == "translation").Default;
            // A new default must not steal an existing custom shortcut after an upgrade.
            foreach (var d in Catalog.Where(d => !seen.Contains(d.Id)))
                if (result[d.Id] is { } gesture && result._values.Any(p => p.Key != d.Id && p.Value == gesture)) result._values[d.Id] = null;
            return result.HasSettingsRoute ? result : new();
        }
        catch (Exception e) when (e is JsonException or NotSupportedException or ArgumentException) { return new(); }
    }
}
