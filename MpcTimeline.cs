using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace DynamicIsland;

// MPC-BE 1.9 exposes playback buttons through SMTC, but no SMTC timeline/seek.
// Read its native status control and use its existing seek-bar message handler.
// No hooks, player configuration changes, web server or /slave registration.
sealed class MpcTimeline
{
    public readonly record struct Reading(TimeSpan Position, TimeSpan Duration);
    sealed record Binding(IntPtr Window, IntPtr Time, IntPtr Seek, uint Process, string Caption, string Executable);
    const int StatusId = 10016, TimeId = 10627, SeekId = 10017;
    const uint GetText = 0x000D, ButtonDown = 0x0201, ButtonUp = 0x0202;
    readonly object _gate = new();
    Binding? _bound;
    string _source = "", _title = "";
    TimeSpan _lastDuration;

    public static bool Supports(string source) =>
        Path.GetFileName(source).Equals("mpc-be64.exe", StringComparison.OrdinalIgnoreCase)
        || Path.GetFileName(source).Equals("mpc-be.exe", StringComparison.OrdinalIgnoreCase);

    public static bool TrackBelongsToProcess(string source, string title, uint process) =>
        Locate(source, title) is { } bound && bound.Process == process;

    public Reading? Read(string source, string title)
    {
        lock (_gate)
        {
            if (source != _source || title != _title || _bound != null && !Valid(_bound))
            {
                _bound = null;
                _lastDuration = TimeSpan.Zero;
                _source = source;
                _title = title;
            }
            _bound ??= Locate(source, title);
            return _bound is { } bound ? ReadBound(bound) : null;
        }
    }

    public Reading? Seek(string source, string title, double fraction)
    {
        if (!double.IsFinite(fraction)) return null;
        lock (_gate)
        {
            if (_bound is not { } bound || _source != source || _title != title || !Valid(bound)) return null;
            if (ReadBound(bound) is not { } before || !GetClientRect(bound.Seek, out var client)) return null;
            uint dpi = GetDpiForWindow(bound.Seek);
            if (!TryPoint(client.Right, client.Bottom, fraction, DarkTheme(bound.Executable), dpi, out int x, out int y)) return null;
            IntPtr point = new((y << 16) | (x & 0xffff));
            bool sent;
            try { sent = SendMessageTimeout(bound.Seek, ButtonDown, new IntPtr(1), point, 3, 100, out _) != IntPtr.Zero; }
            finally { SendMessageTimeout(bound.Seek, ButtonUp, IntPtr.Zero, point, 3, 100, out _); }
            if (!sent) return null;
            double target = before.Duration.TotalSeconds * Math.Clamp(fraction, 0, 1);
            double tolerance = Math.Max(3, before.Duration.TotalSeconds * 2 / Math.Max(1, client.Right) + 1);
            // A successful message alone is not a successful seek. Read the
            // player's actual resulting time, including its key-frame snapping.
            var deadline = Stopwatch.StartNew();
            while (deadline.ElapsedMilliseconds < 900)
            {
                Thread.Sleep(70);
                if (!Valid(bound)) return null;
                if (ReadBound(bound) is { } actual && Math.Abs(actual.Position.TotalSeconds - target) <= tolerance) return actual;
            }
            return null;
        }
    }

    Reading? ReadBound(Binding bound)
    {
        string? label = Text(bound.Time);
        if (label == null) return null;
        if (TryParse(label, out var reading))
        {
            _lastDuration = reading.Duration;
            return reading;
        }
        // At EOF MPC removes "/ duration". Reuse only this file's previously
        // verified duration, never a previous track's duration.
        if (_lastDuration > TimeSpan.Zero && TryClock(label, out var at) && at <= _lastDuration)
            return new Reading(at, _lastDuration);
        return null;
    }

    static Binding? Locate(string source, string title)
    {
        if (!Supports(source) || title.Length == 0) return null;
        var matches = new List<Binding>();
        foreach (IntPtr window in SourceApp.Windows(source))
        {
            string? caption = Text(window);
            if (caption == null || !CaptionMatches(caption, title)) continue;
            GetWindowThreadProcessId(window, out uint pid);
            IntPtr status = FindControl(window, StatusId), seek = FindControl(window, SeekId);
            IntPtr time = status != IntPtr.Zero ? FindControl(status, TimeId) : IntPtr.Zero;
            if (time == IntPtr.Zero || seek == IntPtr.Zero) continue;
            try
            {
                using var process = Process.GetProcessById((int)pid);
                string? exe = process.MainModule?.FileName;
                if (exe != null && Supports(exe)) matches.Add(new(window, time, seek, pid, caption, exe));
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
        // Identical titles in multiple instances are ambiguous. Never seek an
        // arbitrary instance just because its executable has the same name.
        return matches.Count == 1 ? matches[0] : null;
    }

    static bool Valid(Binding bound)
    {
        GetWindowThreadProcessId(bound.Window, out uint pid);
        return pid == bound.Process && IsWindow(bound.Window) && IsChild(bound.Window, bound.Time)
            && IsChild(bound.Window, bound.Seek) && Text(bound.Window) == bound.Caption;
    }

    internal static bool CaptionMatches(string caption, string title)
    {
        int suffix = caption.LastIndexOf(" - MPC-BE", StringComparison.OrdinalIgnoreCase);
        if (suffix <= 0) return false;
        string file = caption[..suffix].Trim();
        return file.Equals(title.Trim(), StringComparison.OrdinalIgnoreCase)
            || Path.GetFileName(file).Equals(Path.GetFileName(title.Trim()), StringComparison.OrdinalIgnoreCase);
    }

    internal static bool TryParse(string label, out Reading reading)
    {
        reading = default;
        string[] parts = label.Split('/');
        if (parts.Length != 2 || !TryClock(parts[1], out var duration) || duration <= TimeSpan.Zero) return false;
        string left = parts[0].Trim();
        bool remaining = left.StartsWith('-') || left.StartsWith('\u2212');
        if (remaining) left = left[1..].Trim();
        if (!TryClock(left, out var value) || value > duration) return false;
        reading = new(remaining ? duration - value : value, duration);
        return true;
    }

    static bool TryClock(string text, out TimeSpan value)
    {
        value = default;
        string[] parts = text.Trim().Split(':');
        if (parts.Length is not (2 or 3)) return false; // Reject frame counters.
        double seconds = 0;
        for (int i = 0; i < parts.Length; i++)
        {
            string part = parts[i].Trim().Replace(',', '.');
            if (!double.TryParse(part, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double n)
                || !double.IsFinite(n) || n < 0 || i > 0 && n >= 60
                || i < parts.Length - 1 && n != Math.Truncate(n)) return false;
            seconds = seconds * 60 + n;
        }
        if (seconds > TimeSpan.MaxValue.TotalSeconds - 1) return false;
        value = TimeSpan.FromSeconds(seconds);
        return true;
    }

    internal static bool TryPoint(int width, int height, double fraction, bool dark, uint dpi, out int x, out int y)
    {
        x = y = 0;
        if (!double.IsFinite(fraction) || width < 2 || width > short.MaxValue || height < 2 || height > short.MaxValue) return false;
        double scale = (dpi == 0 ? 96 : dpi) / 96.0;
        int inset = dark ? 0 : (int)Math.Round(8 * scale);
        int top = dark ? 0 : (int)Math.Round(7 * scale) + 2;
        int bottom = height - (dark ? 0 : (int)Math.Round(5 * scale) + 1);
        if (width <= 2 * inset || bottom <= top) return false;
        x = Math.Clamp((int)Math.Round(inset + (width - 2 * inset) * Math.Clamp(fraction, 0, 1)), inset, width - inset - 1);
        y = (top + bottom) / 2;
        return true;
    }

    static bool DarkTheme(string executable)
    {
        string ini = Path.ChangeExtension(executable, ".ini");
        if (File.Exists(ini)) return GetPrivateProfileInt("Theme", "UseDarkTheme", 1, ini) != 0;
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\MPC-BE\Theme");
        return key?.GetValue("UseDarkTheme") is not int enabled || enabled != 0;
    }

    static IntPtr FindControl(IntPtr parent, int id)
    {
        IntPtr found = GetDlgItem(parent, id);
        if (found != IntPtr.Zero) return found;
        EnumChildWindows(parent, (child, _) => { if (GetDlgCtrlID(child) != id) return true; found = child; return false; }, IntPtr.Zero);
        return found;
    }

    static string? Text(IntPtr window)
    {
        var text = new StringBuilder(2048);
        return SendText(window, GetText, new IntPtr(text.Capacity), text, 3, 60, out _) != IntPtr.Zero ? text.ToString() : null;
    }

    [StructLayout(LayoutKind.Sequential)] struct Rect { public int Left, Top, Right, Bottom; }
    delegate bool EnumProc(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, EnumProc callback, IntPtr parameter);
    [DllImport("user32.dll")] static extern IntPtr GetDlgItem(IntPtr parent, int id);
    [DllImport("user32.dll")] static extern int GetDlgCtrlID(IntPtr window);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] static extern bool IsChild(IntPtr parent, IntPtr child);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageTimeoutW")]
    static extern IntPtr SendText(IntPtr window, uint message, IntPtr wParam, StringBuilder text, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern uint GetPrivateProfileInt(string section, string key, int fallback, string path);
}
