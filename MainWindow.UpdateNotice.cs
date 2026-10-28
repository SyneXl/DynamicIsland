using System.Windows;

namespace DynamicIsland;

public partial class MainWindow
{
    UpdateAnnouncement _updateAnnouncement = new();
    bool _noticeIsUpdate;
    bool UpdateBubblePending => _updateAnnouncement is { Pending: true, Presented: true }
        && !(_transientView == View.Notice && _noticeIsUpdate);

    void ObserveUpdateAnnouncement()
    {
        if (App.SmokeTest || !IsLoaded) return;
        _updateAnnouncement.Observe(_updater.State == Updater.Stage.Available ? _updater.LatestVersion : null, Updater.CurrentVersion);
        if (_panel == Panel.Update && _updater.State == Updater.Stage.Available) _updateAnnouncement.Consume();
        TryAnnounceUpdate();
        UpdateTargets();
    }

    void TryAnnounceUpdate()
    {
        if (!_updateAnnouncement.Pending || _updateAnnouncement.Presented || _panel != Panel.None
            || _ringing || _hiddenByFullscreen || _sentAway || _cinemaMode || _transientView != null) return;
        if (!_updateAnnouncement.Present()) return;
        ShowUpdateNotice();
    }

    void ShowUpdateNotice()
    {
        Notify(Glyph.Update, _indigo, "Доступно обновление", "Версия " + _updateAnnouncement.Version, seconds: 4.5,
            open: () => { _updateAnnouncement.Consume(); ShowPanelAndCheckUpdate(Panel.Update); });
        _noticeIsUpdate = true;
        UpdateTargets();
    }

    void ReplayUpdateNotice()
    {
        _updateAnnouncement.Consume();
        // Clicking is explicit: bypass automatic-notice suppression, without making a second bubble.
        Notify(Glyph.Update, _indigo, "Доступно обновление", "Версия " + _updateAnnouncement.Version, seconds: 4.5, force: true,
            open: () => ShowPanelAndCheckUpdate(Panel.Update));
        _noticeIsUpdate = true;
        UpdateTargets();
    }
}
