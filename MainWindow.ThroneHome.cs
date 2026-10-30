using System.Windows;
using System.Windows.Automation;

namespace DynamicIsland;

public partial class MainWindow
{
    bool ShowHomeThrone => _thronePreferences.ShowOnHome && (_throneInstallation?.Capable == true || _throneApi != null);
    bool ShouldPollThrone => !_hiddenByFullscreen && !_sentAway && (_view is View.Throne or View.ThroneGroups or View.ThroneServers
        || _view == View.IdleBig && ShowHomeThrone);

    void ThroneHome_Click(object sender, RoutedEventArgs e)
    {
        SaveThrone(_thronePreferences with { ShowOnHome = !_thronePreferences.ShowOnHome });
        SyncThrone();
    }

    void SyncHomeThrone()
    {
        HomeThroneButton.Visibility = ShowHomeThrone ? Visibility.Visible : Visibility.Collapsed;
        bool fresh = _throneStatus != null && Environment.TickCount64 - _thronePolledAt <= 7500;
        string state = !fresh ? "Нет связи" : _throneStatus!.State switch
        {
            "running" => "Включён", "idle" => "Выключен", "connecting" => "Подключаюсь", "stopping" => "Отключаюсь", _ => "Нет связи"
        };
        HomeThroneText.Text = state switch { "Включён" => "VPN вкл.", "Выключен" => "VPN выкл.", "Подключаюсь" or "Отключаюсь" => "VPN …", _ => "VPN —" };
        HomeThroneIcon.Kind = fresh && _throneStatus!.Running ? Glyph.Vpn : Glyph.VpnOff;
        HomeThroneIcon.Fill = fresh && _throneStatus!.Running ? _green : _dim;
        HomeThroneText.Foreground = _dim;
        HomeThroneButton.ToolTip = "Throne: " + state.ToLowerInvariant();
        AutomationProperties.SetName(HomeThroneButton, "Throne: " + state + ". Открыть управление VPN");
        SyncHomeHeight();
    }

    void SyncHomeHeight()
    {
        HomeIndicators.Measure(new Size(110, double.PositiveInfinity));
        double height = Math.Max(124, HomeIndicators.DesiredSize.Height + 28);
        if (Math.Abs(IdleBigView.Height - height) < .01) return;
        IdleBigView.Height = height;
        if (_view == View.IdleBig) UpdateTargets();
    }
}
