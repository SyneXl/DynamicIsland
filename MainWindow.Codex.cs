using System.Globalization;
using System.Windows;

namespace DynamicIsland;

public partial class MainWindow
{
    readonly CodexQuotaService _codex = new();

    void CodexRow_Click(object sender, RoutedEventArgs e)
    {
        ShowPanel(Panel.Codex);
        SyncCodex();
        _ = Task.Run(() => _codex.RefreshAsync());
    }

    void CodexRefresh_Click(object sender, RoutedEventArgs e) => _ = Task.Run(() => _codex.RefreshAsync());

    void CodexAutoPing_Click(object sender, RoutedEventArgs e)
    {
        Settings.CodexAutoPing = !Settings.CodexAutoPing;
        _codex.SetEnabled(Settings.CodexAutoPing);
        CodexAutoSwitch.Set(Settings.CodexAutoPing, true);
        SyncCodex();
    }

    static string QuotaPercent(CodexWindow? window) => window == null ? "—" : window.Remaining.ToString("0.#", CultureInfo.InvariantCulture) + "%";

    static string QuotaReset(DateTimeOffset reset, DateTimeOffset now)
    {
        TimeSpan left = reset - now;
        if (left <= TimeSpan.Zero) return "Ожидаю сброса";
        long minutes = (long)Math.Ceiling(left.TotalMinutes);
        string distance = minutes >= 1440 ? $"{minutes / 1440} д" + (minutes / 60 % 24 > 0 ? $" {minutes / 60 % 24} ч" : "")
            : minutes >= 60 ? $"{minutes / 60} ч" + (minutes % 60 > 0 ? $" {minutes % 60} мин" : "") : $"{minutes} мин";
        return "Сброс через " + distance;
    }

    static string QuotaResetDetail(CodexWindow? window) => window == null ? "Время сброса недоступно"
        : "Сброс " + window.Reset.ToLocalTime().ToString("d MMMM, HH:mm", CultureInfo.GetCultureInfo("ru-RU"));

    // The switch already communicates on/off; a countdown already communicates waiting.
    // Leave unfamiliar and actionable messages visible rather than suppressing them.
    static string CodexMessage(string status) => status switch
    {
        "Открой, чтобы проверить лимиты" or "Автопинг выключен" or "Автопинг включён"
            or "Ожидаю следующего сброса" or "Новое окно запущено" => "",
        _ => status,
    };

    void SyncCodex()
    {
        var usage = _codex.Latest;
        MenuCodex.Text = usage?.Five == null ? "" : QuotaPercent(usage.Five);
        CodexFiveValue.Text = QuotaPercent(usage?.Five);
        CodexWeekValue.Text = QuotaPercent(usage?.Week);
        CodexFiveFill.Width = 264 * (usage?.Five?.Remaining ?? 0) / 100;
        CodexWeekFill.Width = 264 * (usage?.Week?.Remaining ?? 0) / 100;
        var now = DateTimeOffset.UtcNow;
        CodexFiveReset.Text = usage?.Five == null ? "Нет данных"
            : _codex.WindowState == CodexWindowState.Unanchored ? "Окно ещё не началось"
            : _codex.WindowState is CodexWindowState.Anchored or CodexWindowState.Exhausted ? QuotaReset(usage.Five.Reset, now) : "Уточняю сброс…";
        CodexWeekReset.Text = usage?.Week == null ? "Нет данных" : QuotaReset(usage.Week.Reset, now);
        CodexFiveReset.ToolTip = _codex.WindowState is CodexWindowState.Anchored or CodexWindowState.Exhausted ? QuotaResetDetail(usage?.Five) : null;
        CodexWeekReset.ToolTip = QuotaResetDetail(usage?.Week);
        CodexFiveValue.ToolTip = "Осталось " + QuotaPercent(usage?.Five);
        CodexWeekValue.ToolTip = "Осталось " + QuotaPercent(usage?.Week);
        CodexStatus.Text = CodexMessage(_codex.Status);
        CodexStatus.Visibility = CodexStatus.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        CodexStatus.ToolTip = usage == null ? null : "Проверено " + usage.At.ToLocalTime().ToString("dd.MM HH:mm");
        CodexRefresh.IsEnabled = !_codex.Busy;
        CodexAutoSwitch.Set(Settings.CodexAutoPing, false);
        CodexBody.Measure(new Size(CodexView.Width, double.PositiveInfinity));
        double height = Math.Ceiling(CodexBody.DesiredSize.Height) + 24;
        if (height == CodexView.Height) return;
        CodexView.Height = height;
        if (_view == View.Codex) UpdateTargets();
    }
}
