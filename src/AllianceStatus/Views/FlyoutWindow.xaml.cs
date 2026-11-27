using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using AllianceStatus.Feeds;
using AllianceStatus.Models;
using AllianceStatus.Services;

namespace AllianceStatus.Views;

/// <summary>Compact flyout anchored above the notification area. Closes when it loses focus.</summary>
public partial class FlyoutWindow : Window
{
    const double ScreenGap = 12;

    readonly StatusMonitor _monitor;
    readonly UpdateChecker _updates;
    readonly Action _quit;
    readonly DispatcherTimer _clock;
    readonly ObservableCollection<ClusterOption> _clusters = new();
    readonly ObservableCollection<Subscription> _custom = new();

    System.Drawing.Rectangle _workArea, _screenBounds;
    bool _closing, _spinning, _loadingSettings, _installing;

    public FlyoutWindow(StatusMonitor monitor, UpdateChecker updates, Action quit)
    {
        _monitor = monitor;
        _updates = updates;
        _quit = quit;
        InitializeComponent();

        ClusterList.ItemsSource = _clusters;
        CustomList.ItemsSource = _custom;
        CreditsText.Text = $"Alliance Status {UpdateChecker.CurrentText}. By Ferdinand Koenig, with assistance from Claude (Anthropic).";

        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _clock.Tick += (_, _) => RenderFooter(_monitor.BuildView(DateTimeOffset.Now));

        _monitor.Changed += Render;
        _updates.Changed += RenderUpdate;
        ThemeManager.ThemeChanged += OnThemeChanged;

        SourceInitialized += (_, _) =>
        {
            Native.StyleWindow(this, ThemeManager.IsDark);
            if (!Native.IsWindows11) OuterBorder.BorderThickness = new Thickness(1);
        };
        Loaded += (_, _) =>
        {
            CaptureScreen();
            Reposition();
            AnimateIn();
            _clock.Start();
        };
        SizeChanged += (_, _) => Reposition();
        Deactivated += (_, _) => CloseSoon();
        PreviewKeyDown += OnPreviewKeyDown;
        Closed += (_, _) =>
        {
            _clock.Stop();
            _monitor.Changed -= Render;
            _updates.Changed -= RenderUpdate;
            ThemeManager.ThemeChanged -= OnThemeChanged;
        };

        Render();
        RenderUpdate();
    }

    // ---- Placement & motion --------------------------------------------------------------

    void CaptureScreen()
    {
        var screen = System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position);
        _workArea = screen.WorkingArea;
        _screenBounds = screen.Bounds;
    }

    bool TaskbarOnTop => _workArea.Top > _screenBounds.Top;
    bool TaskbarOnLeft => _workArea.Left > _screenBounds.Left;

    void Reposition()
    {
        if (_workArea.IsEmpty || ActualWidth == 0) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        double left = TaskbarOnLeft
            ? _workArea.Left / dpi.DpiScaleX + ScreenGap
            : _workArea.Right / dpi.DpiScaleX - ActualWidth - ScreenGap;
        double top = TaskbarOnTop
            ? _workArea.Top / dpi.DpiScaleY + ScreenGap
            : _workArea.Bottom / dpi.DpiScaleY - ActualHeight - ScreenGap;
        Left = Math.Round(left);
        Top = Math.Round(top);
    }

    void AnimateIn()
    {
        if (!SystemParameters.ClientAreaAnimation) return;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(170);
        Root.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = ease });
        Slide.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(TaskbarOnTop ? -14 : 14, 0, duration) { EasingFunction = ease });
    }

    void CloseSoon()
    {
        if (_closing) return;
        _closing = true;
        Dispatcher.InvokeAsync(Close);
    }

    void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        if (SettingsPage.Visibility == Visibility.Visible) ShowMain();
        else CloseSoon();
    }

    void OnThemeChanged()
    {
        Native.StyleWindow(this, ThemeManager.IsDark);
        Render();
    }

    // ---- Overview ------------------------------------------------------------------------

    void Render()
    {
        var now = DateTimeOffset.Now;
        var view = _monitor.BuildView(now);

        var rows = new List<object>();
        AddSection(rows, "Active", view.Active, IncidentState.Active, now);
        AddSection(rows, "Upcoming", view.Upcoming, IncidentState.Upcoming, now);
        AddSection(rows, "Past 7 days", view.Past, IncidentState.Past, now);
        IncidentList.ItemsSource = rows;

        bool empty = rows.Count == 0;
        IncidentScroll.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        if (empty) RenderEmptyState(view);

        RenderHeader(view);
        RenderFooter(view);
        SetSpinning(view.Refreshing);
    }

    static void AddSection(List<object> rows, string title, IReadOnlyList<Incident> items, IncidentState state, DateTimeOffset now)
    {
        if (items.Count == 0) return;
        rows.Add(new SectionRow(title));
        rows.AddRange(items.Select(i => IncidentRow.From(i, state, now)));
    }

    void RenderHeader(StatusView view)
    {
        int clusters = _monitor.State.Subscriptions.Count;
        Subtitle.Text =
            clusters == 0 ? "No clusters selected" :
            view.Active.Count > 0 ? Fmt.Plural(view.Active.Count, "active incident") +
                                    (view.Upcoming.Count > 0 ? $", {view.Upcoming.Count} upcoming" : "") :
            view.Upcoming.Count > 0 ? $"No active incidents, {view.Upcoming.Count} upcoming" :
            view.LastSuccess is null ? (view.Refreshing ? "Checking status…" : "Status unavailable") :
            "No active incidents";

        StatusDot.Fill = ThemeManager.Brush(view.Overall switch
        {
            TrayStatus.Active => "Critical",
            TrayStatus.Upcoming => "CautionFill",
            TrayStatus.Offline => "Neutral",
            _ => "Success",
        });
    }

    void RenderEmptyState(StatusView view)
    {
        var subs = _monitor.State.Subscriptions;
        EmptyAction.Visibility = Visibility.Collapsed;

        if (subs.Count == 0)
        {
            SetEmpty("\uE713", "TextSecondary", "No clusters selected", "Choose the Alliance clusters you want to hear about.");
            EmptyAction.Visibility = Visibility.Visible;
        }
        else if (view.LastSuccess is null && view.Refreshing)
        {
            SetEmpty("\uE895", "TextSecondary", "Checking status…", "");
        }
        else if (view.LastSuccess is null)
        {
            SetEmpty("\uE7BA", "Caution", "Can't reach the status site",
                "Check your connection. Alliance Status retries automatically.");
        }
        else
        {
            var names = string.Join(", ", subs.Select(s => s.Name));
            SetEmpty("\uE73E", "Success", "All clear", $"No incidents for {names} in the last 7 days.");
        }
    }

    void SetEmpty(string glyph, string brushKey, string title, string body)
    {
        var brush = ThemeManager.Brush(brushKey);
        EmptyGlyph.Text = glyph;
        EmptyGlyph.Foreground = brush;
        EmptyGlyphBg.Fill = brush;
        EmptyTitle.Text = title;
        EmptyBody.Text = body;
        EmptyBody.Visibility = body.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void RenderFooter(StatusView view)
    {
        var now = DateTimeOffset.Now;
        int feeds = _monitor.State.Subscriptions.Count;
        int failing = view.Errors.Count;

        FooterText.Text =
            view.Refreshing ? "Refreshing…" :
            (view.LastSuccess is { } t)
                ? failing == 0 ? $"Updated {Fmt.Relative(t, now)}"
                : failing >= feeds ? $"Offline, last updated {Fmt.Relative(t, now)}"
                : $"Updated {Fmt.Relative(t, now)}, {Fmt.Plural(failing, "feed")} unreachable"
            : failing > 0 ? "Offline" : "Not checked yet";

        FooterWarn.Visibility = failing > 0 && !view.Refreshing ? Visibility.Visible : Visibility.Collapsed;
        FooterPanel.ToolTip = failing > 0
            ? string.Join("\n", view.Errors.Select(e => $"{e.Cluster}: {e.Error}"))
            : "Checked at startup and every 30 minutes";
    }

    void SetSpinning(bool on)
    {
        if (on == _spinning) return;
        _spinning = on;
        if (on)
        {
            RefreshSpin.BeginAnimation(RotateTransform.AngleProperty,
                new DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.9)) { RepeatBehavior = RepeatBehavior.Forever });
        }
        else
        {
            RefreshSpin.BeginAnimation(RotateTransform.AngleProperty, null);
            RefreshSpin.Angle = 0;
        }
    }

    /// <summary>Blue dot on the gear, and a download card at the top of Settings.</summary>
    void RenderUpdate()
    {
        bool show = _updates.UpdateAvailable;
        UpdateDot.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        UpdatePanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        SettingsButton.ToolTip = show ? "Settings (update available)" : "Settings";
        if (!show || _installing) return;
        UpdateTitle.Text = $"Version {_updates.LatestVersion} is available";
        UpdateSubtitle.Text = $"You have {UpdateChecker.CurrentText}. Your settings are kept.";
        UpdateSubtitle.Foreground = ThemeManager.Brush("TextTertiary");
        InstallButton.IsEnabled = true;
    }

    void Install_Click(object sender, RoutedEventArgs e) => InstallUpdate();

    void ReleaseNotes_Click(object sender, RoutedEventArgs e) => Shell.OpenUrl(_updates.DownloadPage);

    /// <summary>Downloads, verifies and silently installs the update, then quits so the installer can replace the app.</summary>
    public async void InstallUpdate()
    {
        if (_installing || !_updates.UpdateAvailable) return;
        _installing = true;
        if (SettingsPage.Visibility != Visibility.Visible) ShowSettings();
        InstallButton.IsEnabled = false;
        UpdateSubtitle.Foreground = ThemeManager.Brush("TextTertiary");
        UpdateSubtitle.Text = "Downloading…";
        try
        {
            await _updates.InstallAsync(new Progress<int>(p => UpdateSubtitle.Text = $"Downloading… {p} %"));
            UpdateSubtitle.Text = "Installing. Alliance Status restarts in a moment.";
            await System.Threading.Tasks.Task.Delay(800);
            _quit();
        }
        catch (Exception ex)
        {
            Log.Error("Update install failed", ex);
            _installing = false;
            InstallButton.IsEnabled = true;
            UpdateSubtitle.Foreground = ThemeManager.Brush("Critical");
            UpdateSubtitle.Text = ex is InvalidOperationException
                ? ex.Message + " You can also use \"What's new\" to download it."
                : "The installer couldn't be started. Use \"What's new\" to download it.";
        }
    }

    async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        if (UpdateChecker.IsDevelopmentBuild)
        {
            UpdateStatus.Text = "Development builds don't check for updates.";
            return;
        }
        CheckUpdatesButton.IsEnabled = false;
        UpdateStatus.Text = "Checking…";
        bool ok = await _updates.CheckNowAsync();
        if (_closing) return;
        CheckUpdatesButton.IsEnabled = true;
        UpdateStatus.Text = !ok ? "Couldn't reach GitHub. Try again later."
            : _updates.UpdateAvailable ? $"Version {_updates.LatestVersion} is available (see above)."
            : $"You have the latest version ({UpdateChecker.CurrentText}).";
    }

    async void Refresh_Click(object sender, RoutedEventArgs e) => await _monitor.RefreshAsync();

    void Incident_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is IncidentRow row) Shell.OpenUrl(row.Url);
    }

    void Website_Click(object sender, RoutedEventArgs e) => Shell.OpenUrl(Alliance.Site);

    // ---- Settings ------------------------------------------------------------------------

    void Settings_Click(object sender, RoutedEventArgs e) => ShowSettings();

    void Back_Click(object sender, RoutedEventArgs e) => ShowMain();

    void Quit_Click(object sender, RoutedEventArgs e) => _quit();

    void ShowMain()
    {
        SettingsPage.Visibility = Visibility.Collapsed;
        MainPage.Visibility = Visibility.Visible;
        Render();
    }

    void ShowSettings()
    {
        MainPage.Visibility = Visibility.Collapsed;
        SettingsPage.Visibility = Visibility.Visible;

        _loadingSettings = true;
        AutostartToggle.IsChecked = Autostart.IsEnabled;
        _loadingSettings = false;

        CustomMessage.Visibility = Visibility.Collapsed;
        UpdateStatus.Text = $"Version {UpdateChecker.CurrentText}";
        RebuildClusterOptions();
        DiscoverIfNeeded();
    }

    async void DiscoverIfNeeded()
    {
        if (!_monitor.DiscoveryStale)
        {
            DiscoveryText.Text = DiscoveredText();
            return;
        }
        DiscoveryText.Text = "Looking for clusters on status.alliancecan.ca…";
        bool ok = await _monitor.DiscoverAsync();
        if (_closing || !IsLoaded) return;
        RebuildClusterOptions();
        DiscoveryText.Text = ok ? DiscoveredText() : "Couldn't reach the status site, so the built-in cluster list is shown.";
    }

    string DiscoveredText()
    {
        int n = _monitor.State.DiscoveredClusters.Count;
        return n > 0 ? $"{Fmt.Plural(n, "cluster")} listed on status.alliancecan.ca." : "Showing the built-in cluster list.";
    }

    void RebuildClusterOptions()
    {
        _clusters.Clear();
        foreach (var name in _monitor.ClusterCandidates())
        {
            var url = Alliance.FeedUrl(name);
            _clusters.Add(new ClusterOption(name, url, _monitor.IsSubscribed(url), OnClusterToggled));
        }

        _custom.Clear();
        foreach (var s in _monitor.State.Subscriptions.Where(s => s.IsCustom)) _custom.Add(s);
        CustomList.Visibility = _custom.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void OnClusterToggled(ClusterOption option, bool on) =>
        _monitor.SetSubscription(option.Name, option.Url, on, custom: false);

    void RemoveCustom_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not Subscription sub) return;
        _monitor.SetSubscription(sub.Name, sub.Url, false, custom: true);
        RebuildClusterOptions();
    }

    void CustomUrl_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        AddCustom_Click(sender, e);
    }

    async void AddCustom_Click(object sender, RoutedEventArgs e)
    {
        var text = CustomUrl.Text.Trim();
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            ShowCustomMessage("Enter a full feed address starting with https://", error: true);
            return;
        }

        var url = uri.AbsoluteUri;
        if (_monitor.IsSubscribed(url))
        {
            ShowCustomMessage("You're already following this feed.", error: false);
            return;
        }

        // An Alliance cluster feed pasted by URL is just a cluster subscription.
        if (Alliance.ClusterFromFeedUrl(url) is { } cluster)
        {
            _monitor.SetSubscription(cluster, Alliance.FeedUrl(cluster), true, custom: false);
            CustomUrl.Text = "";
            CustomMessage.Visibility = Visibility.Collapsed;
            RebuildClusterOptions();
            return;
        }

        AddButton.IsEnabled = false;
        CustomUrl.IsEnabled = false;
        ShowCustomMessage("Checking feed…", error: false);
        try
        {
            var title = await StatusMonitor.ProbeAsync(url);
            if (_closing) return;
            _monitor.SetSubscription(title ?? uri.Host, url, true, custom: true);
            CustomUrl.Text = "";
            CustomMessage.Visibility = Visibility.Collapsed;
            RebuildClusterOptions();
        }
        catch (Exception ex)
        {
            if (_closing) return;
            ShowCustomMessage($"Couldn't add this feed: {FeedErrors.Describe(ex)}.", error: true);
        }
        finally
        {
            AddButton.IsEnabled = true;
            CustomUrl.IsEnabled = true;
        }
    }

    void ShowCustomMessage(string text, bool error)
    {
        CustomMessage.Text = text;
        CustomMessage.Foreground = ThemeManager.Brush(error ? "Critical" : "TextTertiary");
        CustomMessage.Visibility = Visibility.Visible;
    }

    void Autostart_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        try
        {
            Autostart.Set(AutostartToggle.IsChecked == true);
        }
        catch (Exception ex)
        {
            Log.Error("Could not change autostart", ex);
            _loadingSettings = true;
            AutostartToggle.IsChecked = Autostart.IsEnabled;
            _loadingSettings = false;
        }
    }
}
