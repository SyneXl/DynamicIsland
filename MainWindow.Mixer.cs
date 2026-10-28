using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace DynamicIsland;

public partial class MainWindow
{
    sealed record MixerRow(Grid Root, Button Mute, Icon Icon, Image AppIcon, Icon AppFallback, TextBlock Name, TextBlock Percent, Scrubber Slider)
    {
        public MixerChannel? Channel;
    }
    readonly Dictionary<string, MixerRow> _mixerRows = new(StringComparer.OrdinalIgnoreCase);
    readonly MixerIcons _mixerIcons = new();
    MixerSnapshot? _mixerSnapshot;
    Panel _mixerReturnPanel;
    Func<MixerSnapshot?>? _mixerReader = null;
    Func<string, string[]?, float?, bool?, bool>? _mixerWriter = null;

    void MixerOpen_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        OpenMixer();
    }

    void OpenMixer()
    {
        if (_panel != Panel.Mixer) _mixerReturnPanel = _panel;
        CancelGrab();
        RefreshMixer();
        ShowPanel(Panel.Mixer);
    }

    void MixerBack_Click(object sender, RoutedEventArgs e) => ShowPanel(_mixerReturnPanel);

    void RefreshMixer() => ReadAndRefreshMixer(false);

    void ReadAndRefreshMixer(bool duringInput, MixerRow? reset = null)
    {
        if (!duringInput && Mouse.Captured != null && MixerView.IsMouseCaptureWithin) return;
        MixerSnapshot? snapshot = _mixerReader != null ? _mixerReader() : _audio.ReadMixer();
        _mixerSnapshot = snapshot;
        MixerUnavailable.SetVisible(snapshot == null);
        MixerEmpty.SetVisible(snapshot is { Channels.Length: 0 });
        if (snapshot == null)
        {
            MixerRows.Children.Clear(); _mixerRows.Clear();
            MixerScroll.Height = 0; MixerView.Height = 112; UpdateTargets(); return;
        }
        var channels = new[] { new MixerChannel("master", "Общая громкость", [], snapshot.Volume, snapshot.Muted, true) }.Concat(snapshot.Channels).ToArray();
        var keep = channels.Select(c => c.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string key in _mixerRows.Keys.Where(k => !keep.Contains(k)).ToArray())
        {
            MixerRows.Children.Remove(_mixerRows[key].Root); _mixerRows.Remove(key);
        }
        foreach (MixerChannel channel in channels)
        {
            if (!_mixerRows.TryGetValue(channel.Key, out MixerRow? row))
            {
                row = CreateMixerRow(channel.Key); _mixerRows.Add(channel.Key, row); MixerRows.Children.Add(row.Root);
            }
            row.Channel = channel;
            row.Name.Text = channel.Name;
            row.Percent.Text = MixerVolume.Percent(channel.Volume) + "%";
            // Do not reset the grabbed knob while Core Audio rounds its actual value.
            // Every other row (including master) still follows this drag immediately.
            if (!row.Root.IsMouseCaptureWithin || ReferenceEquals(row, reset)) row.Slider.Set(channel.Volume * 100, false);
            bool muted = channel.Muted || snapshot.Muted;
            row.Icon.Kind = muted || channel.Volume == 0 ? Glyph.Mute : Glyph.Loud;
            row.Slider.Opacity = muted ? 0.4 : 1;
            row.Mute.ToolTip = muted ? "Включить звук" : "Выключить звук";
            AutomationProperties.SetName(row.Mute, channel.Name + (muted ? ": включить звук" : ": выключить звук"));
            row.AppIcon.Source = channel.Key == "master" ? null : _mixerIcons.Get(channel.IconPath, channel.Key);
            row.AppIcon.SetVisible(row.AppIcon.Source != null);
            row.AppFallback.SetVisible(row.AppIcon.Source == null);
        }
        // Do not reorder rows as sessions start/stop; that would move a control under the pointer.
        MixerScroll.Height = Math.Min(350, channels.Length * 66);
        MixerView.Height = 54 + MixerScroll.Height + (snapshot.Channels.Length == 0 ? 32 : 0);
        UpdateTargets();
    }

    MixerRow CreateMixerRow(string key)
    {
        var grid = new Grid { Height = 66, Margin = new Thickness(0, 0, 0, 0) };
        var icon = new Icon { Kind = Glyph.Loud, Width = 17, Height = 17, Fill = _dim };
        var mute = new Button { Content = icon, Style = (Style)FindResource("IconButton"), Width = 34, Height = 34,
            Margin = new Thickness(0, 25, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        grid.Children.Add(mute);
        var appIcon = new Image { Width = 20, Height = 20, Stretch = Stretch.Uniform, IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(7, 5, 0, 0) };
        var fallback = new Icon { Kind = key == "master" ? Glyph.Speaker : key == "system" ? Glyph.Bell : Glyph.Windows,
            Width = 20, Height = 20, Fill = _dim, HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top, Margin = appIcon.Margin, IsHitTestVisible = false };
        grid.Children.Add(appIcon); grid.Children.Add(fallback);
        var name = new TextBlock { FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(43, 8, 44, 0) };
        var percent = new TextBlock { FontSize = 11, Foreground = _dim, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 9, 8, 0) };
        // The button supplies pointer capture to the existing island Scrubber.
        var track = new Button { Style = (Style)FindResource("RowButton"), Height = 32,
            Margin = new Thickness(33, 26, 0, 0), Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Top };
        var slider = new Scrubber { Minimum = 0, Maximum = 100, Step = 1, Height = 26, Margin = new Thickness(0, 0, 0, 0) };
        track.Content = slider;
        grid.Children.Add(name); grid.Children.Add(percent); grid.Children.Add(track);
        var row = new MixerRow(grid, mute, icon, appIcon, fallback, name, percent, slider);
        mute.Click += (_, e) => { e.Handled = true; ChangeMixer(row, null, !((row.Channel?.Muted ?? false) || (_mixerSnapshot?.Muted ?? false))); };
        slider.Changed += (_, _) => ChangeMixer(row, (float)slider.Value / 100, null);
        track.LostMouseCapture += (_, _) => Dispatcher.BeginInvoke(() => { if (_panel == Panel.Mixer) RefreshMixer(); });
        track.Focusable = mute.Focusable = true;
        track.PreviewKeyDown += (_, e) =>
        {
            if (e.Key is not (Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End)) return;
            e.Handled = true;
            float value = e.Key == Key.Home ? 0 : e.Key == Key.End ? 1
                : Math.Clamp((row.Channel?.Volume ?? 0) + (e.Key is Key.Right or Key.Up ? WheelVolumeStep : -WheelVolumeStep), 0, 1);
            ChangeMixer(row, value, null);
        };
        return row;
    }

    void ChangeMixer(MixerRow row, float? level, bool? muted)
    {
        if (_mixerSnapshot == null || row.Channel is not { } channel) return;
        string[]? sessions = channel.Key == "master" ? null : channel.Sessions;
        bool success = _mixerWriter != null ? _mixerWriter(_mixerSnapshot.Device, sessions, level, muted)
            : _audio.SetMixer(_mixerSnapshot.Device, sessions, level, muted);
        // Read back ALL faders after every input, also after partial failures.
        // Optimistically updating only the touched row hid the master/app coupling.
        ReadAndRefreshMixer(true, success ? null : row);
    }
}
