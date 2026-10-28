using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;

namespace DynamicIsland;

sealed class PlayerBar
{
    public readonly record struct Reading(string Text, TimeSpan? Time, int Percent);

    const string ClientMark = "gram";
    static readonly TimeSpan FirstRetry = TimeSpan.FromSeconds(2), LongestRetry = TimeSpan.FromSeconds(30);

    const uint WM_MOUSEMOVE = 0x0200, WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202, WM_MOUSELEAVE = 0x02A3;
    const int MK_LBUTTON = 1;

    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int X, Y; }

    [DllImport("user32.dll")] static extern bool ScreenToClient(IntPtr hwnd, ref POINT point);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    AutomationElement? _text, _time, _slider;
    IntPtr _window;
    string _app = "";
    DateTime _retryAt;
    TimeSpan _retryDelay = FirstRetry;

    public static bool Supports(string appId) =>
        appId.Contains(ClientMark, StringComparison.OrdinalIgnoreCase) || SourceApp.Name(appId).Contains(ClientMark, StringComparison.OrdinalIgnoreCase);

    public void ResetRetry()
    {
        _retryAt = default;
        _retryDelay = FirstRetry;
    }

    public Reading? Read(string appId)
    {
        if (appId != _app)
        {
            _app = appId;
            _text = null;
            ResetRetry();
        }

        try
        {
            if (_text == null && !Locate(appId)) return null;
            string value = ((ValuePattern)_slider!.GetCurrentPattern(ValuePattern.Pattern)).Current.Value;
            return new Reading(_text!.Current.Name, ParseTime(_time!.Current.Name), int.TryParse(value.TrimEnd('%'), out int percent) ? percent : -1);
        }
        catch
        {
            _text = null;
            return null;
        }
    }

    public bool Seek(double fraction)
    {
        try
        {
            if (_text == null || _slider == null) return false;
            Rect slider = _slider.Current.BoundingRectangle;
            if (slider.IsEmpty || slider.Width < 1 || _slider.Current.IsOffscreen) return false;

            var at = new POINT
            {
                X = (int)Math.Round(slider.X + slider.Width * Math.Clamp(fraction, 0, 1)),
                Y = (int)(slider.Y + slider.Height / 2),
            };
            if (!ScreenToClient(_window, ref at)) return false;

            IntPtr where = (at.Y << 16) | (at.X & 0xFFFF);
            PostMessage(_window, WM_MOUSEMOVE, IntPtr.Zero, where);
            PostMessage(_window, WM_LBUTTONDOWN, MK_LBUTTON, where);
            PostMessage(_window, WM_LBUTTONUP, IntPtr.Zero, where);
            PostMessage(_window, WM_MOUSELEAVE, IntPtr.Zero, IntPtr.Zero);
            return true;
        }
        catch
        {
            return false;
        }
    }

    bool Locate(string appId)
    {
        if (DateTime.UtcNow < _retryAt) return false;

        foreach (IntPtr window in SourceApp.Windows(appId))
        {
            AutomationElement? bar = AutomationElement.FromHandle(window).FindFirst(TreeScope.Descendants, ByClass("class Media::Player::Widget"));
            if (bar == null) continue;

            _text = bar.FindFirst(TreeScope.Children, ByClass("class Ui::FlatLabel"));
            _time = bar.FindFirst(TreeScope.Descendants, ByClass("class Ui::LabelSimple"));
            _slider = bar.FindFirst(TreeScope.Children, ByClass("class Ui::FilledSlider"));
            if (_text != null && _time != null && _slider != null)
            {
                _window = window;
                _retryDelay = FirstRetry;
                return true;
            }
        }

        _text = null;
        _retryAt = DateTime.UtcNow + _retryDelay;
        TimeSpan doubled = _retryDelay + _retryDelay;
        _retryDelay = doubled < LongestRetry ? doubled : LongestRetry;
        return false;
    }

    static PropertyCondition ByClass(string name) => new(AutomationElement.ClassNameProperty, name);

    static TimeSpan? ParseTime(string label)
    {
        int seconds = 0;
        foreach (string part in label.Split(':'))
        {
            if (!int.TryParse(part, out int n) || n < 0) return null;
            seconds = seconds * 60 + n;
        }
        return TimeSpan.FromSeconds(seconds);
    }
}
