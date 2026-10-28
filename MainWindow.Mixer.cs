using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace DynamicIsland;

public partial class MainWindow
{
    sealed record MixerRow(Grid Root, Button Mute, Icon Icon, TextBlock Name, TextBlock Percent, Scrubber Slider)
    {
        public MixerChannel? Channel;
    }
    readonly Dictionary<string, MixerRow> _mixerRows = new(StringComparer.OrdinalIgnoreCase);
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

    void RefreshMixer()
    {
        if (Mouse.Captured != null && MixerView.IsMouseCaptureWithin) return;
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
            row.Percent.Text = ((int)Math.Round(channel.Volume * 100)) + "%";
            row.Slider.Set(channel.Volume * 100, false);
            row.Icon.Kind = channel.Muted || channel.Volume == 0 ? Glyph.Mute : Glyph.Loud;
            row.Slider.Opacity = channel.Muted ? 0.4 : 1;
            row.Mute.ToolTip = channel.Muted ? "Включить звук" : "Выключить звук";
            AutomationProperties.SetName(row.Mute, channel.Name + (channel.Muted ? ": включить звук" : ": выключить звук"));
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
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
        grid.Children.Add(mute);
        var name = new TextBlock { FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(43, 8, 44, 0) };
        var percent = new TextBlock { FontSize = 11, Foreground = _dim, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 9, 8, 0) };
        // The button supplies pointer capture to the existing island Scrubber.
        var track = new Button { Style = (Style)FindResource("RowButton"), Height = 32,
            Margin = new Thickness(33, 26, 0, 0), Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Top };
        var slider = new Scrubber { Minimum = 0, Maximum = 100, Step = 1, Height = 26, Margin = new Thickness(0, 0, 0, 0) };
        track.Content = slider;
        grid.Children.Add(name); grid.Children.Add(percent); grid.Children.Add(track);
        var row = new MixerRow(grid, mute, icon, name, percent, slider);
        mute.Click += (_, e) => { e.Handled = true; ChangeMixer(row, null, !(row.Channel?.Muted ?? false)); };
        slider.Changed += (_, _) => ChangeMixer(row, (float)slider.Value / 100, null);
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
        if (success)
        {
            row.Channel = channel with { Volume = level ?? channel.Volume, Muted = muted ?? channel.Muted };
            row.Percent.Text = ((int)Math.Round(row.Channel.Volume * 100)) + "%";
            row.Icon.Kind = row.Channel.Muted || row.Channel.Volume == 0 ? Glyph.Mute : Glyph.Loud;
            row.Slider.Opacity = row.Channel.Muted ? 0.4 : 1;
            row.Slider.Set(row.Channel.Volume * 100, false);
            row.Mute.ToolTip = row.Channel.Muted ? "Включить звук" : "Выключить звук";
        }
        else { row.Slider.Set(channel.Volume * 100, false); Dispatcher.BeginInvoke(() => RefreshMixer()); }
    }
}
