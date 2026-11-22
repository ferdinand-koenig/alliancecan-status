using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using AllianceStatus.Feeds;
using AllianceStatus.Services;
using AllianceStatus.Views;
using Microsoft.Toolkit.Uwp.Notifications;

namespace AllianceStatus;

public partial class App : Application
{
    StateStore? _store;
    StatusMonitor? _monitor;
    UpdateChecker? _updates;
    TrayIcon? _tray;
    FlyoutWindow? _flyout;
    long _flyoutClosedAt;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Any(a => a.Equals("--cleanup", StringComparison.OrdinalIgnoreCase)))
        {
            Cleanup();
            Shutdown();
            return;
        }

        if (!SingleInstance.TryAcquire())
        {
            SingleInstance.SignalExisting();
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error("Unhandled UI exception", args.Exception);
            args.Handled = true; // keep the tray app alive
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Error("Fatal exception", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error("Unobserved task exception", args.Exception);
            args.SetObserved();
        };

        System.Windows.Forms.Application.EnableVisualStyles();
        ThemeManager.Initialize();

        _store = new StateStore();
        var state = _store.Load();
        Autostart.Repair();

        _monitor = new StatusMonitor(state, _store, Dispatcher);
        _monitor.Changed += UpdateTray;
        _updates = new UpdateChecker(state, _store, Dispatcher);

        _tray = new TrayIcon();
        _tray.OpenRequested += ToggleFlyout;
        _tray.RefreshRequested += () => _ = _monitor.RefreshAsync();
        _tray.WebsiteRequested += () => Shell.OpenUrl(Alliance.Site);
        _tray.ExitRequested += Quit;

        ThemeManager.ThemeChanged += () =>
        {
            _tray?.ApplyTheme();
            UpdateTray();
        };

        ToastNotificationManagerCompat.OnActivated += OnToastActivated;
        SingleInstance.Listen(() => Dispatcher.InvokeAsync(ShowFlyout));

        UpdateTray();
        _monitor.Start(); // fetch immediately, then every 30 minutes
        _updates.Start(); // GitHub release check, once a day

        bool quiet = e.Args.Any(a => a.Equals(Autostart.Flag, StringComparison.OrdinalIgnoreCase))
                     || ToastNotificationManagerCompat.WasCurrentProcessToastActivated();
        if (!quiet) ShowFlyout();
    }

    void OnToastActivated(ToastNotificationActivatedEventArgsCompat e)
    {
        var args = ToastArguments.Parse(e.Argument);
        Dispatcher.InvokeAsync(() =>
        {
            if (args.TryGetValue("url", out string url)) Shell.OpenUrl(url);
            else ShowFlyout();
        });
    }

    void UpdateTray()
    {
        if (_monitor is null || _tray is null) return;
        var view = _monitor.BuildView(DateTimeOffset.Now);
        string line = view.Overall switch
        {
            TrayStatus.Active => Fmt.Plural(view.Active.Count, "active incident") +
                                 (view.Upcoming.Count > 0 ? $", {view.Upcoming.Count} upcoming" : ""),
            TrayStatus.Upcoming => Fmt.Plural(view.Upcoming.Count, "upcoming event"),
            TrayStatus.Offline when view.LastSuccess is null => view.Refreshing ? "Checking…" : "Status unavailable",
            TrayStatus.Offline => "Some feeds unreachable",
            _ => "All systems operational",
        };
        _tray.SetStatus(view.Overall, $"Alliance Status\n{line}");
    }

    void ToggleFlyout()
    {
        if (_flyout is not null)
        {
            _flyout.Close();
            return;
        }
        // A click on the tray icon first deactivates (and closes) the open flyout;
        // don't immediately reopen it on the same click.
        if (Environment.TickCount64 - _flyoutClosedAt < 400) return;
        ShowFlyout();
    }

    void ShowFlyout()
    {
        if (_monitor is null || _updates is null) return;
        if (_flyout is not null)
        {
            _flyout.Activate();
            return;
        }
        _flyout = new FlyoutWindow(_monitor, _updates, Quit);
        _flyout.Closed += (_, _) =>
        {
            _flyout = null;
            _flyoutClosedAt = Environment.TickCount64;
        };
        _flyout.Show();
        _flyout.Activate();
    }

    void Quit()
    {
        _flyout?.Close();
        _monitor?.Dispose();
        _updates?.Dispose();
        _tray?.Dispose();
        _tray = null;
        SingleInstance.Release();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose(); // never leave a ghost icon behind
        base.OnExit(e);
    }

    static void Cleanup()
    {
        try { Autostart.Set(false); } catch { }
        try { ToastNotificationManagerCompat.Uninstall(); } catch { }
    }
}
