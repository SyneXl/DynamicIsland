using System.Windows.Media;
using System.Windows.Media.Animation;
using static DynamicIsland.Motion;

namespace DynamicIsland;

public partial class MainWindow
{
    const double NoticeBackgroundAlpha = 0.2;
    const double NoticeSeconds = 3.2;
    static readonly TimeSpan NoticeIconDelay = TimeSpan.FromMilliseconds(160);

    Action? _noticeOpen;

    void Notify(Glyph icon, SolidColorBrush tint, string title, string text, double seconds = NoticeSeconds, bool force = false, Glyph? from = null, bool warn = false,
        Action? open = null)
    {
        if (_ringing && !force) return;
        _noticeIsUpdate = false;

        bool shown = _view == View.Notice;
        Color color = tint.Color;
        NoticeIcon.Kind = icon;
        NoticeIcon.Fill = tint;
        NoticeBack.Background = new SolidColorBrush(Color.FromArgb((byte)(color.A * NoticeBackgroundAlpha), color.R, color.G, color.B));
        NoticeTitle.Text = title;
        NoticeText.Text = text;
        _noticeOpen = open;
        ShowTransient(View.Notice, seconds, force);
        if (shown) FadeIn(NoticeView);
        AnimateNoticeIcon(icon, from, warn);
    }

    void AnimateNoticeIcon(Glyph icon, Glyph? from, bool warn)
    {
        NoticeSize.AnimateScale(null);
        NoticeTurn.BeginAnimation(RotateTransform.AngleProperty, null);
        NoticeMove.BeginAnimation(TranslateTransform.YProperty, null);
        NoticeTurn.CenterY = 0;

        NoticeIcon.Play(from, NoticeIconDelay.TotalSeconds);
        var back = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.8 };
        TimeSpan run = TimeSpan.FromMilliseconds(460);
        if (warn)
            NoticeTurn.BeginAnimation(RotateTransform.AngleProperty, Sway(NoticeIconDelay, TimeSpan.FromMilliseconds(620), -15, 13, -10, 7, -3, 0));
        else if (icon == Glyph.Headphones)
            NoticeMove.BeginAnimation(TranslateTransform.YProperty, Delayed(-9, 0, NoticeIconDelay, run, back));
        else if (icon == Glyph.Wired)
            NoticeMove.BeginAnimation(TranslateTransform.YProperty, Delayed(8, 0, NoticeIconDelay, run, back));
        else if (icon == Glyph.Speaker)
            NoticeSize.AnimateScale(Delayed(0.6, 1, NoticeIconDelay, run, new ElasticEase { EasingMode = EasingMode.EaseOut, Oscillations = 1, Springiness = 5 }));
    }

    void OnNetworkChanged(NetworkService.State was, NetworkService.State now)
    {
        if (!Settings.Network) return;

        if (now.Vpn != was.Vpn)
        {
            string[] before = was.Tunnels, after = now.Tunnels;
            if (after.Except(before).FirstOrDefault() is { } up)
                Notify(Glyph.Vpn, _green, "VPN включён", up, from: Glyph.VpnOff);
            else if (before.Except(after).FirstOrDefault() is { } down)
                Notify(Glyph.VpnOff, _dim, "VPN отключён", down, from: Glyph.Vpn);
            return;
        }

        if (now.Link == NetworkService.Link.None)
        {
            Notify(Glyph.Offline, _red, "Нет сети", "Подключение потеряно", from: Glyph.Wifi);
            return;
        }

        bool wifi = now.Link == NetworkService.Link.Wifi;
        Glyph icon = wifi ? Glyph.Wifi : Glyph.Wired;
        string title = now.Link switch
        {
            NetworkService.Link.Wired => "Ethernet",
            _ when now.Name.Length > 0 => now.Name,
            NetworkService.Link.Wifi => "Wi-Fi",
            _ => "Мобильная сеть",
        };
        if (now.Internet) Notify(icon, _green, title, wifi ? "Wi-Fi подключён" : "Сеть подключена");
        else Notify(icon, _orange, title, "Без доступа к интернету");
    }
}
