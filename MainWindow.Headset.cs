using System.Windows.Media;
using System.Windows.Media.Animation;
using static DynamicIsland.Motion;

namespace DynamicIsland;

public partial class MainWindow
{
    const double HeadsetSeconds = 3.6;
    const string ConnectedLabel = "Подключено";
    const string LowChargeLabel = "Низкий заряд";
    static readonly string[] EarbudNames = ["pods", "buds"];
    static readonly TimeSpan HeadsetGadgetDelay = TimeSpan.FromMilliseconds(80);
    static readonly TimeSpan HeadsetFillDelay = TimeSpan.FromMilliseconds(250);
    static readonly TimeSpan HeadsetFillRun = TimeSpan.FromMilliseconds(700);

    static Gadget.Model HeadsetModel(AudioService.Output device) =>
        !device.Headphones ? Gadget.Model.Speaker
        : EarbudNames.Any(part => device.Name.Contains(part, StringComparison.OrdinalIgnoreCase)) ? Gadget.Model.Earbuds
        : Gadget.Model.Headphones;

    void ShowHeadset(string name, Gadget.Model model, int level, bool low)
    {
        if (_ringing) return;

        Brush tint = low ? _red : Brushes.White;
        HeadsetLabel.Text = name;
        HeadsetState.Text = low ? LowChargeLabel : ConnectedLabel;
        HeadsetState.Foreground = low ? _red : _dim;
        HeadsetPercent.Text = level + "%";
        HeadsetPercent.Foreground = HeadsetFill.Background = tint;

        ShowTransient(View.Headset, HeadsetSeconds);
        HeadsetFill.BeginAnimation(WidthProperty, Delayed(0, ChargeFillWidth * level / 100.0, HeadsetFillDelay, HeadsetFillRun,
            new CubicEase { EasingMode = EasingMode.EaseOut }));
        HeadsetGadget.Play(model, low, HeadsetGadgetDelay, TimeSpan.FromSeconds(HeadsetSeconds));
    }
}
