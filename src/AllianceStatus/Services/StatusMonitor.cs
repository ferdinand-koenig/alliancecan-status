using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using AllianceStatus.Feeds;
using AllianceStatus.Models;
using Microsoft.Win32;

namespace AllianceStatus.Services;

public enum TrayStatus { Ok, Upcoming, Active, Offline }

public sealed record StatusView(
    IReadOnlyList<Incident> Active,
    IReadOnlyList<Incident> Upcoming,
    IReadOnlyList<Incident> Past,
    IReadOnlyList<(string Cluster, string Error)> Errors,
    DateTimeOffset? LastSuccess,
    bool Refreshing)
{
    public TrayStatus Overall =>
        Active.Count > 0 ? TrayStatus.Active :
        Upcoming.Count > 0 ? TrayStatus.Upcoming :
        Errors.Count > 0 || LastSuccess is null ? TrayStatus.Offline :
        TrayStatus.Ok;
}

/// <summary>
/// Polls subscribed feeds, merges results into the persisted state and raises toasts.
/// All state mutation happens on the UI dispatcher; only network I/O and parsing run elsewhere.
/// </summary>
public sealed class StatusMonitor : IDisposable
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(30);
    static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(5);
    static readonly TimeSpan DiscoveryMaxAge = TimeSpan.FromDays(1);
    static readonly TimeSpan NotificationMemory = TimeSpan.FromDays(60);

    /// <summary>
    /// Bump when classification changes. Records made under older rules are refreshed
    /// silently once, so an upgrade doesn't re-announce incidents users already know about.
    /// </summary>
    const int CurrentRulesVersion = 2;

    readonly AppState _state;
    readonly StateStore _store;
    readonly Dispatcher _ui;
    readonly DispatcherTimer _timer;
    bool _busy, _again;
    Task _current = Task.CompletedTask;

    public event Action? Changed;

    public StatusMonitor(AppState state, StateStore store, Dispatcher ui)
    {
        _state = state;
        _store = store;
        _ui = ui;
        _timer = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = PollInterval };
        _timer.Tick += (_, _) => _ = RefreshAsync();
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.TimeChanged += OnClockChanged;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
    }

    public AppState State => _state;
    public bool IsRefreshing => _busy;

    public void Start()
    {
        _timer.Start();
        _ = RefreshAsync();
    }

    /// <summary>Fetches all feeds now. Concurrent calls coalesce into one follow-up run.</summary>
    public Task RefreshAsync()
    {
        if (!_ui.CheckAccess()) return _ui.InvokeAsync<Task>(RefreshAsync).Task.Unwrap();
        if (_busy)
        {
            _again = true;
            return _current;
        }
        _busy = true;
        _timer.Stop();
        Changed?.Invoke();
        _current = RunAsync();
        return _current;
    }

    async Task RunAsync()
    {
        try
        {
            var subs = _state.Subscriptions.ToList();
            var results = await Task.WhenAll(subs.Select(FetchAsync));
            var now = DateTimeOffset.Now;
            var toasts = new List<PendingToast>();
            bool silentUpgrade = _state.RulesVersion < CurrentRulesVersion;
            foreach (var r in results) Apply(r, now, toasts, silentUpgrade);
            if (silentUpgrade && results.All(r => r.Error is null)) _state.RulesVersion = CurrentRulesVersion;
            ResolveClusters(results, now, toasts);
            Prune(now);
            _state.LastCheck = now;
            _store.Save(_state);
            Notifier.Show(toasts);
            _timer.Interval = NextCheckIn(now, results.Any(r => r.Error is not null) ? RetryInterval : PollInterval);
        }
        catch (Exception ex)
        {
            Log.Error("Refresh failed", ex);
            _timer.Interval = RetryInterval;
        }
        finally
        {
            _busy = false;
            _timer.Start();
            Changed?.Invoke();
            if (_again)
            {
                _again = false;
                _ = _ui.InvokeAsync(() => { _ = RefreshAsync(); });
            }
        }
    }

    sealed record FetchResult(Subscription Sub, FeedDocument? Doc, string? Error);

    static Task<FetchResult> FetchAsync(Subscription sub) => Task.Run(async () =>
    {
        try
        {
            var text = await FeedClient.GetStringAsync(sub.Url, CancellationToken.None).ConfigureAwait(false);
            var doc = FeedParser.Parse(text);
            if (doc.Repaired) Log.Info($"{sub.Name}: feed was malformed and has been repaired");
            return new FetchResult(sub, doc, null);
        }
        catch (Exception ex)
        {
            var message = FeedErrors.Describe(ex);
            Log.Info($"{sub.Name}: {message} ({ex.GetType().Name})");
            return new FetchResult(sub, null, message);
        }
    });

    void Apply(FetchResult r, DateTimeOffset now, List<PendingToast> toasts, bool silentUpgrade)
    {
        var sub = r.Sub;
        if (!_state.Subscriptions.Contains(sub)) return; // unsubscribed while fetching

        if (!_state.Feeds.TryGetValue(sub.Url, out var health))
            _state.Feeds[sub.Url] = health = new FeedHealth();
        health.LastAttempt = now;

        if (r.Doc is null)
        {
            // Keep what we know; never mark incidents as gone because of a failed fetch.
            health.Error = r.Error;
            return;
        }

        health.Error = null;
        health.LastSuccess = now;
        if (sub.IsCustom && sub.Name == sub.Url && r.Doc.Title is { } feedTitle) sub.Name = feedTitle;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in r.Doc.Items)
        {
            var key = $"{sub.Url}#{item.Id}";
            if (!seen.Add(key)) continue;

            if (!_state.Incidents.TryGetValue(key, out var inc))
                _state.Incidents[key] = inc = new Incident { Key = key, FeedUrl = sub.Url, FirstSeen = now };

            inc.Cluster = sub.Name;
            inc.Title = item.Title;
            inc.Summary = item.Summary;
            inc.Link = ResolveLink(sub.Url, item.Link);
            inc.Status = item.Status;
            inc.Categories = item.Categories;
            inc.Published = item.Published;
            inc.Updated = item.Updated;
            (inc.Start, inc.End) = IncidentRules.Schedule(sub.Url, item, now);
            inc.LastSeen = now;
            inc.GoneSince = null;

            QueueNotification(inc, now, toasts, silentUpgrade);
        }

        foreach (var inc in _state.Incidents.Values)
            if (inc.FeedUrl == sub.Url && inc.GoneSince is null && !seen.Contains(inc.Key))
                inc.GoneSince = now;
    }

    /// <summary>Toast only for genuinely new or changed incidents; remember what was sent.</summary>
    void QueueNotification(Incident inc, DateTimeOffset now, List<PendingToast> toasts, bool silentUpgrade)
    {
        var state = IncidentRules.Classify(inc, now);
        var fingerprint = IncidentRules.Fingerprint(inc);
        var schedule = ScheduleKey(inc);
        var summaryHash = HashText(inc.Summary);
        _state.Notified.TryGetValue(inc.Key, out var record);

        if (record is null || record.Fingerprint != fingerprint)
            Log.Info($"{inc.Cluster}: \"{Fmt.Truncate(inc.Title, 60)}\" is {state} " +
                     $"(start {inc.Start?.ToString("u") ?? "none"}, end {inc.End?.ToString("u") ?? "none"})");

        if (silentUpgrade && record is not null)
        {
            // Already known to the user; only the rules changed.
        }
        else if (record is null)
        {
            // Old, already-finished entries seen for the first time are recorded silently.
            if (state != IncidentState.Past)
                toasts.Add(new PendingToast(inc, state == IncidentState.Upcoming ? ToastKind.Upcoming : ToastKind.New));
        }
        else if (record.Fingerprint != fingerprint && state != IncidentState.Past)
        {
            // An incident that ended is announced per cluster in ResolveClusters, not here.
            toasts.Add(new PendingToast(inc, ToastKind.Updated, DescribeChange(record, inc, schedule, summaryHash)));
        }

        _state.Notified[inc.Key] = new NotificationRecord
        {
            Fingerprint = fingerprint, State = state, LastSeen = now, Schedule = schedule, SummaryHash = summaryHash,
        };
    }

    static string ScheduleKey(Incident i) =>
        $"{i.Start?.ToUniversalTime().ToString("o") ?? ""}|{i.End?.ToUniversalTime().ToString("o") ?? ""}";

    static string HashText(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];

    /// <summary>Third line of an "Updated" toast; null means "use the usual line" (e.g. records from before 0.2.1).</summary>
    static string? DescribeChange(NotificationRecord old, Incident inc, string schedule, string summaryHash)
    {
        if (old.Schedule is null || old.SummaryHash is null) return null;
        if (old.Schedule != schedule)
            return inc.Start is null && inc.End is null ? "Schedule removed" : $"New time: {Fmt.Range(inc.Start, inc.End)}";
        if (old.SummaryHash != summaryHash)
        {
            var firstLine = inc.Summary.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
            return $"Description changed: {Fmt.Truncate(firstLine, 110)}";
        }
        return null; // title or status changed: the toast's title line shows it
    }

    /// <summary>
    /// Announces a cluster (feed) as resolved when its last active incident ended, whether the feed
    /// gave an end time, the planned end passed, or the entry was removed. Only feeds fetched
    /// successfully in this check are considered, so being offline never looks like "resolved".
    /// The first time a feed is seen (new subscription, update from an older version) it is
    /// recorded silently.
    /// </summary>
    void ResolveClusters(IEnumerable<FetchResult> results, DateTimeOffset now, List<PendingToast> toasts)
    {
        foreach (var r in results)
        {
            if (r.Doc is null || !_state.Subscriptions.Contains(r.Sub)) continue;
            var url = r.Sub.Url;
            var active = _state.Incidents.Values
                .Where(i => i.FeedUrl == url && IncidentRules.Classify(i, now) == IncidentState.Active)
                .Select(i => i.Key)
                .OrderBy(k => k, StringComparer.Ordinal)
                .ToList();

            if (_state.ActiveByFeed.TryGetValue(url, out var before) && before.Count > 0 && active.Count == 0)
            {
                var ended = before.Select(k => _state.Incidents.GetValueOrDefault(k)).OfType<Incident>().ToList();
                var subject = ended.FirstOrDefault() ?? new Incident { Key = url, FeedUrl = url, Cluster = r.Sub.Name };
                subject.Cluster = r.Sub.Name;
                string? detail = ended.Count switch
                {
                    0 => null,
                    1 => EndedLine(ended[0], now),
                    _ => $"{ended.Count} incidents ended",
                };
                toasts.Add(new PendingToast(subject, ToastKind.Resolved, detail));
                Log.Info($"{r.Sub.Name}: resolved ({detail ?? "no details"})");
            }

            _state.ActiveByFeed[url] = active;
        }
    }

    static string EndedLine(Incident i, DateTimeOffset now)
    {
        // Removed from the feed without an end time: we only know when we noticed.
        bool approximate = !(i.End is { } e && e <= now) && i.GoneSince is not null;
        var at = IncidentRules.PastSince(i, now);
        return $"{Fmt.Truncate(i.Title, 60)} ended {(approximate ? "≈" : "")}{Fmt.When(at, now)}";
    }

    /// <summary>
    /// The normal interval, or sooner when an active incident has a planned end before then:
    /// check one minute after it, so "Resolved" arrives on time (and a last-minute extension is seen).
    /// </summary>
    TimeSpan NextCheckIn(DateTimeOffset now, TimeSpan normal)
    {
        var nextEnd = _state.Incidents.Values
            .Where(i => i.End is { } e && e > now && IncidentRules.Classify(i, now) == IncidentState.Active)
            .Select(i => i.End!.Value)
            .DefaultIfEmpty(DateTimeOffset.MaxValue)
            .Min();
        if (nextEnd == DateTimeOffset.MaxValue) return normal;
        var wait = nextEnd - now + TimeSpan.FromMinutes(1);
        if (wait < TimeSpan.FromMinutes(1)) wait = TimeSpan.FromMinutes(1);
        return wait < normal ? wait : normal;
    }

    void Prune(DateTimeOffset now)
    {
        var subscribed = new HashSet<string>(_state.Subscriptions.Select(s => s.Url));
        foreach (var (key, inc) in _state.Incidents.ToList())
        {
            bool expired = IncidentRules.Classify(inc, now) == IncidentState.Past &&
                           now - IncidentRules.PastSince(inc, now) > IncidentRules.PastRetention;
            if (expired || !subscribed.Contains(inc.FeedUrl)) _state.Incidents.Remove(key);
        }
        foreach (var (key, record) in _state.Notified.ToList())
            if (now - record.LastSeen > NotificationMemory) _state.Notified.Remove(key);
        foreach (var url in _state.Feeds.Keys.ToList())
            if (!subscribed.Contains(url)) _state.Feeds.Remove(url);
        foreach (var url in _state.ActiveByFeed.Keys.ToList())
            if (!subscribed.Contains(url)) _state.ActiveByFeed.Remove(url);
    }

    static string? ResolveLink(string feedUrl, string? link)
    {
        if (string.IsNullOrWhiteSpace(link)) return null;
        if (Uri.TryCreate(link, UriKind.Absolute, out var abs) && (abs.Scheme == Uri.UriSchemeHttp || abs.Scheme == Uri.UriSchemeHttps))
            return abs.AbsoluteUri;
        if (Uri.TryCreate(feedUrl, UriKind.Absolute, out var baseUri) && Uri.TryCreate(baseUri, link, out var rel))
            return rel.AbsoluteUri;
        return null;
    }

    public StatusView BuildView(DateTimeOffset now)
    {
        var subs = _state.Subscriptions;
        var urls = new HashSet<string>(subs.Select(s => s.Url));
        var active = new List<Incident>();
        var upcoming = new List<Incident>();
        var past = new List<Incident>();

        foreach (var i in _state.Incidents.Values)
        {
            if (!urls.Contains(i.FeedUrl)) continue;
            switch (IncidentRules.Classify(i, now))
            {
                case IncidentState.Active: active.Add(i); break;
                case IncidentState.Upcoming: upcoming.Add(i); break;
                default:
                    if (now - IncidentRules.PastSince(i, now) <= IncidentRules.PastRetention) past.Add(i);
                    break;
            }
        }

        active.Sort((a, b) => IncidentRules.SortDate(b).CompareTo(IncidentRules.SortDate(a)));
        upcoming.Sort((a, b) => (a.Start ?? DateTimeOffset.MaxValue).CompareTo(b.Start ?? DateTimeOffset.MaxValue));
        past.Sort((a, b) => IncidentRules.PastSince(b, now).CompareTo(IncidentRules.PastSince(a, now)));

        var errors = new List<(string, string)>();
        DateTimeOffset? lastSuccess = null;
        foreach (var s in subs)
        {
            if (!_state.Feeds.TryGetValue(s.Url, out var h)) continue;
            if (h.Error is not null) errors.Add((s.Name, h.Error));
            if (h.LastSuccess is { } ok && (lastSuccess is null || ok > lastSuccess)) lastSuccess = ok;
        }

        return new StatusView(active, upcoming, past, errors, lastSuccess, _busy);
    }

    // ---- Subscriptions & discovery -------------------------------------------------------

    public bool IsSubscribed(string url) => Find(url) is not null;

    Subscription? Find(string url) =>
        _state.Subscriptions.FirstOrDefault(s => string.Equals(s.Url, url, StringComparison.OrdinalIgnoreCase));

    public void SetSubscription(string name, string url, bool subscribed, bool custom)
    {
        var existing = Find(url);
        if (subscribed == (existing is not null)) return;

        if (subscribed)
        {
            _state.Subscriptions.Add(new Subscription { Name = name, Url = url, IsCustom = custom });
        }
        else
        {
            _state.Subscriptions.Remove(existing!);
            foreach (var key in _state.Incidents.Where(kv => kv.Value.FeedUrl == existing!.Url).Select(kv => kv.Key).ToList())
                _state.Incidents.Remove(key);
            _state.Feeds.Remove(existing!.Url);
            _state.ActiveByFeed.Remove(existing!.Url);
        }

        _store.Save(_state);
        Changed?.Invoke();
        if (subscribed) _ = RefreshAsync();
    }

    /// <summary>Fetches and parses a feed to validate it; returns its title if it has one.</summary>
    public static async Task<string?> ProbeAsync(string url)
    {
        var text = await FeedClient.GetStringAsync(url, CancellationToken.None);
        var doc = await Task.Run(() => FeedParser.Parse(text));
        return doc.Title;
    }

    public bool DiscoveryStale =>
        !_state.DiscoveryChecksFeeds || _state.DiscoveredAt is not { } at || DateTimeOffset.Now - at > DiscoveryMaxAge;

    public async Task<bool> DiscoverAsync()
    {
        try
        {
            var names = await Alliance.DiscoverAsync(CancellationToken.None);
            _state.DiscoveredClusters = names;
            _state.DiscoveredAt = DateTimeOffset.Now;
            _state.DiscoveryChecksFeeds = true;
            _store.Save(_state);
            return true;
        }
        catch (Exception ex)
        {
            Log.Info($"Cluster discovery failed: {FeedErrors.Describe(ex)}");
            return false;
        }
    }

    /// <summary>Discovered clusters (or the built-in list) plus anything already subscribed.</summary>
    public IReadOnlyList<string> ClusterCandidates()
    {
        var set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        set.UnionWith(_state.DiscoveredClusters.Count > 0 ? _state.DiscoveredClusters : Alliance.KnownClusters);
        foreach (var s in _state.Subscriptions)
            if (!s.IsCustom) set.Add(s.Name);
        return set.ToList();
    }

    // ---- Wake-up handling ----------------------------------------------------------------

    void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Resume) return;
        _ui.InvokeAsync(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(15)); // give the network a moment after sleep
            await RefreshAsync();
        });
    }

    /// <summary>Time zone or clock changed: .NET caches the time zone, so drop the cache and redraw.</summary>
    void OnClockChanged(object? sender, EventArgs e)
    {
        TimeZoneInfo.ClearCachedData();
        CultureInfo.CurrentCulture.ClearCachedData();
        _ui.InvokeAsync(() => Changed?.Invoke());
    }

    void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category == UserPreferenceCategory.Locale) OnClockChanged(sender, e);
    }

    void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e)
    {
        if (!e.IsAvailable) return;
        _ui.InvokeAsync(async () =>
        {
            if (!_state.Feeds.Values.Any(f => f.Error is not null)) return;
            await Task.Delay(TimeSpan.FromSeconds(5));
            await RefreshAsync();
        });
    }

    public void Dispose()
    {
        _timer.Stop();
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.TimeChanged -= OnClockChanged;
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
    }
}
