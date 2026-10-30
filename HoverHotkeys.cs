using System.Runtime.InteropServices;
using System.Windows.Input;

namespace DynamicIsland;

// Registrations exist only while the pointer is over the island. No keyboard hook,
// polling of typed keys or permanently global shortcut is needed.
sealed class HoverHotkeys(IntPtr hwnd) : IDisposable
{
    [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    readonly Dictionary<int, string> _registered = [];
    public const int Message = 0x0312;
    public string? Action(int id) => _registered.GetValueOrDefault(id);
    public IReadOnlyList<string> Enable(KeyBindings bindings)
    {
        Disable();
        var failed = new List<string>();
        for (int i = 0; i < KeyBindings.Catalog.Count; i++)
        {
            string action = KeyBindings.Catalog[i].Id;
            if (bindings[action] is not { Input: BindingInput.Key } gesture) continue;
            int id = 0x2400 + i;
            if (RegisterHotKey(hwnd, id, (uint)gesture.Modifiers | 0x4000, (uint)KeyInterop.VirtualKeyFromKey(gesture.Key))) _registered[id] = action;
            else failed.Add(action);
        }
        return failed;
    }
    public bool CanRegister(BindingGesture gesture)
    {
        if (gesture.Input != BindingInput.Key) return true;
        if (!RegisterHotKey(hwnd, 0x6fff, (uint)gesture.Modifiers | 0x4000, (uint)KeyInterop.VirtualKeyFromKey(gesture.Key))) return false;
        UnregisterHotKey(hwnd, 0x6fff);
        return true;
    }
    public void Disable()
    {
        foreach (int id in _registered.Keys) UnregisterHotKey(hwnd, id);
        _registered.Clear();
    }
    public void Dispose() => Disable();
}
