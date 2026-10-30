using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;

namespace DynamicIsland;

public partial class App : Application
{
    const string InstanceName = "DynamicIsland.SingleInstance";
    const string LogFileName = "DynamicIsland.log";

    Mutex? _mutex;
    internal static bool SmokeTest { get; private set; }

    protected override async void OnStartup(StartupEventArgs e)
    {
        if (e.Args is ["--apply-update", var manifest])
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(await UpdateHandoff.RunAsync(manifest));
            return;
        }
        if (e.Args is ["--zapret-operation", var operationFolder])
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            int exitCode = await ZapretElevation.RunHelperAsync(operationFolder);
            Shutdown(exitCode);
            return;
        }
        int smoke = Array.IndexOf(e.Args, "--smoke-test");
        int updated = Array.IndexOf(e.Args, "--updated");
        string? readyPath = updated >= 0 && updated + 1 < e.Args.Length ? e.Args[updated + 1] : null;
        SmokeTest = smoke >= 0 && smoke + 1 < e.Args.Length;
        if (SmokeTest)
        {
            base.OnStartup(e);
            string result = Path.GetFullPath(e.Args[smoke + 1]);
            DispatcherUnhandledException += (_, args) =>
            {
                File.WriteAllText(result, args.Exception.ToString());
                args.Handled = true;
                Shutdown(1);
            };
            var window = new MainWindow();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                bool ready = window.IsLoaded && window.IsVisible
                    && window.FindName("Root") is FrameworkElement { ActualWidth: > 0 }
                    && window.FindName("CinemaSwitch") != null
                    && window.FindName("CodexView") != null
                    && window.FindName("CodexAutoSwitch") != null
                    && window.FindName("MenuLayoutView") is FrameworkElement { Height: > 0 }
                    && window.FindName("MenuLayoutRow") != null
                    && window.FindName("UpdatePage") != null
                    && window.FindName("ReleaseNotesPage") != null
                    && window.FindName("UpdateWhatsNewButton") != null
                    && window.FindName("LoadingView") != null
                    && window.FindName("LyricsPage") != null
                    && window.FindName("HeadsetGadget") != null
                    && window.FindName("ZapretView") is FrameworkElement { Height: > 0 }
                    && window.FindName("ZapretFolderRows") != null
                    && window.FindName("ZapretStrategyRows") != null
                    && window.FindName("ThroneView") is FrameworkElement { Height: > 0 }
                    && window.FindName("ThroneGroupRows") != null
                    && window.FindName("ThroneServerRows") != null
                    && window.FindName("MixerView") is FrameworkElement { Height: > 0 }
                    && window.FindName("HomeMixerButton") != null
                    && window.FindName("VolumeMixerButton") != null
                    && window.FindName("ShelfNoteEditor") != null
                    && window.FindName("ShelfPasteButton") != null
                    && window.FindName("BubbleUpdate") != null;
                File.WriteAllText(result, JsonSerializer.Serialize(new { ready, ui = "WPF", externalServices = false,
                    version = Updater.CurrentVersion.ToString(), updateRepository = Updater.Repository }));
                window.Close();
                Shutdown(ready ? 0 : 1);
            };
            window.Show();
            if (readyPath != null) _ = Dispatcher.InvokeAsync(() => UpdateHandoff.Acknowledge(readyPath), DispatcherPriority.ApplicationIdle);
            timer.Start();
            return;
        }
        _mutex = new Mutex(true, InstanceName, out bool isOnlyInstance);
        if (!isOnlyInstance)
        {
            Shutdown();
            return;
        }

        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            Log(args.Exception);
            args.Handled = true;
        };
        new MainWindow().Show();
        if (readyPath != null) _ = Dispatcher.InvokeAsync(() => UpdateHandoff.Acknowledge(readyPath), DispatcherPriority.ApplicationIdle);
        _ = UpdateHandoff.CleanCompletedAsync();
    }

    public static void Log(Exception ex)
    {
        try
        {
            File.AppendAllText(Path.Combine(Path.GetTempPath(), LogFileName),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\n\n");
        }
        catch { }
    }

}
