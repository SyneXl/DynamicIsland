using System.Diagnostics;
using System.Windows.Threading;

namespace DynamicIsland;

sealed class FullscreenVideoService(Dispatcher ui, Func<uint, CancellationToken, Task<bool>> nativeVideo) : IDisposable
{
    const double ProbeEvery = 1.2;
    readonly CinemaState _state = new();
    readonly Stopwatch _clock = Stopwatch.StartNew();
    readonly CancellationTokenSource _stop = new();
    Native.FullscreenWindow? _target;
    double _nextProbe, _targetAt;
    bool _busy, _hasResult;
    int _generation;
    volatile bool _disposed;

    public event Action? Changed;
    public bool Active => _target != null && _state.IsActive(_clock.Elapsed.TotalSeconds);
    public bool Pending => _target != null && !Active
        && (_state.HasEvidence(_clock.Elapsed.TotalSeconds)
            || !_hasResult && _clock.Elapsed.TotalSeconds - _targetAt < 1.5);

    // Called only after the cheap Win32 fullscreen/monitor check. No accessibility
    // polling at all for ordinary desktop windows. A single MTA worker at a time
    // keeps slow/unresponsive providers away from the WPF Dispatcher.
    public void Observe(Native.FullscreenWindow? target)
    {
        if (_disposed) return;
        if (target != _target)
        {
            _target = target;
            _generation++;
            _state.Reset();
            _hasResult = false;
            _nextProbe = 0;
            _targetAt = _clock.Elapsed.TotalSeconds;
        }
        double now = _clock.Elapsed.TotalSeconds;
        if (target is not { } window || _busy || now < _nextProbe) return;
        _nextProbe = now + ProbeEvery;
        _busy = true;
        int generation = _generation;
        CancellationToken stop = _stop.Token;
        _ = Task.Run(async () =>
        {
            bool video = false;
            try
            {
                video = window.Browser
                    ? VideoAccessibility.ContainsFullscreenVideo(window.Handle, window.Bounds)
                    : await nativeVideo(window.Process, stop).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { App.Log(ex); }
            if (_disposed || ui.HasShutdownStarted) return;
            _ = ui.BeginInvoke(() =>
            {
                _busy = false;
                if (_disposed || generation != _generation) return;
                _hasResult = true;
                _state.Report(video, _clock.Elapsed.TotalSeconds);
                Changed?.Invoke();
            });
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stop.Cancel();
        _stop.Dispose();
        // No replacement workers are created for a blocked accessibility provider.
        _target = null;
        _state.Reset();
    }
}
