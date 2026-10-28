using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace DynamicIsland;

sealed class MixerIcons
{
    readonly Dictionary<string, BitmapSource?> _cache = new(StringComparer.OrdinalIgnoreCase);

    public BitmapSource? Get(string iconPath, string executable)
    {
        string key = iconPath + "\n" + executable;
        if (_cache.TryGetValue(key, out var image)) return image;
        if (_cache.Count >= 256) _cache.Clear();
        // Cache failures too: a protected or icon-less app must not trigger disk I/O every tick.
        return _cache[key] = Load(iconPath) ?? (iconPath != executable ? Load(executable) : null);
    }

    internal static (string Path, int Index)? Parse(string value)
    {
        value = Environment.ExpandEnvironmentVariables(value.Trim().TrimStart('@'));
        int index = 0, comma = value.LastIndexOf(',');
        if (comma >= 0 && int.TryParse(value[(comma + 1)..].Trim(), out int resource))
        {
            index = resource;
            value = value[..comma];
        }
        value = value.Trim().Trim('"');
        // Never turn an app-supplied icon path into a network request or shell command.
        if (!Path.IsPathFullyQualified(value) || value.StartsWith(@"\\", StringComparison.Ordinal)
            || !new[] { ".exe", ".dll", ".ico", ".cpl" }.Contains(Path.GetExtension(value), StringComparer.OrdinalIgnoreCase)) return null;
        return (value, index);
    }

    static BitmapSource? Load(string value)
    {
        IntPtr large = IntPtr.Zero, small = IntPtr.Zero;
        try
        {
            if (Parse(value) is not { } path || !File.Exists(path.Path)) return null;
            uint count = ExtractIconEx(path.Path, path.Index, out large, out small, 1);
            if (count is 0 or uint.MaxValue) return null;
            IntPtr icon = large != IntPtr.Zero ? large : small;
            if (icon == IntPtr.Zero) return null;
            var image = Imaging.CreateBitmapSourceFromHIcon(icon, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(24, 24));
            image.Freeze(); return image;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or COMException or NotSupportedException) { return null; }
        finally
        {
            if (large != IntPtr.Zero) DestroyIcon(large);
            if (small != IntPtr.Zero && small != large) DestroyIcon(small);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern uint ExtractIconEx(string file, int index, out IntPtr large, out IntPtr small, uint count);
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr icon);
}
