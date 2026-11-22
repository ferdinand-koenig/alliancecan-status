using System;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Threading;
using AllianceStatus.Models;

namespace AllianceStatus.Services;

/// <summary>
/// Checks GitHub once a day for a newer release and announces it once per version
/// (toast + banner in the overview). Development builds (version 0.0.0) never check.
/// </summary>
public sealed class UpdateChecker : IDisposable
{
    public const string Repository = "ferdinand-koenig/alliancecan-status";
    static readonly TimeSpan CheckEvery = TimeSpan.FromHours(24);

    /// <summary>Version of the running app, e.g. 0.1.3. Declared before Http, which uses it.</summary>
    public static Version Current { get; } = Normalize(Assembly.GetExecutingAssembly().GetName().Version);

    static readonly HttpClient Http = CreateClient();

    readonly AppState _state;
    readonly StateStore _store;
    readonly DispatcherTimer _timer;
    bool _checking;

    public event Action? Changed;

    public UpdateChecker(AppState state, StateStore store, Dispatcher ui)
    {
        _state = state;
        _store = store;
        // Ticks hourly and checks when 24 h have passed, so sleep or offline periods don't skip a day.
        _timer = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = TimeSpan.FromHours(1) };
        _timer.Tick += async (_, _) => await CheckIfDueAsync();
    }

    public static bool IsDevelopmentBuild => Current == new Version(0, 0, 0);

    public static string CurrentText => IsDevelopmentBuild ? "development build" : Current.ToString(3);

    public bool UpdateAvailable => TryParse(_state.LatestVersion, out var latest) && latest > Current;

    public string? LatestVersion => _state.LatestVersion;

    public string DownloadPage => _state.LatestUrl ?? $"https://github.com/{Repository}/releases/latest";

    public async void Start()
    {
        _timer.Start();
        await Task.Delay(TimeSpan.FromSeconds(20)); // let the status fetch go first
        await CheckIfDueAsync();
    }

    async Task CheckIfDueAsync()
    {
        if (IsDevelopmentBuild || _checking) return;
        if (_state.LastUpdateCheck is { } last && DateTimeOffset.Now - last < CheckEvery) return;
        _checking = true;
        try { await CheckAsync(); }
        finally { _checking = false; }
    }

    async Task CheckAsync()
    {
        try
        {
            using var response = await Http.GetAsync($"https://api.github.com/repos/{Repository}/releases/latest");
            if (!response.IsSuccessStatusCode)
            {
                Log.Info($"Update check: GitHub returned {(int)response.StatusCode}");
                return; // retried at the next hourly tick
            }

            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = json.RootElement;
            var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
            var page = root.TryGetProperty("html_url", out var h) ? h.GetString() : null;
            if (!TryParse(tag, out var latest))
            {
                Log.Info($"Update check: can't read release tag '{tag}'");
                return;
            }

            _state.LastUpdateCheck = DateTimeOffset.Now;
            _state.LatestVersion = latest.ToString(3);
            _state.LatestUrl = page;

            if (latest > Current && _state.NotifiedUpdate != _state.LatestVersion)
            {
                Notifier.ShowUpdate(_state.LatestVersion, DownloadPage);
                _state.NotifiedUpdate = _state.LatestVersion;
                Log.Info($"Update available: {_state.LatestVersion} (running {CurrentText})");
            }

            _store.Save(_state);
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Info($"Update check failed: {ex.GetType().Name}");
        }
    }

    /// <summary>Reads "v0.1.4", "0.1.4" or "0.2" as a three-part version.</summary>
    public static bool TryParse(string? text, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(text)) return false;
        var m = Regex.Match(text.Trim(), @"^v?(\d+)\.(\d+)(?:\.(\d+))?");
        if (!m.Success) return false;
        version = new Version(
            int.Parse(m.Groups[1].Value),
            int.Parse(m.Groups[2].Value),
            m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0);
        return true;
    }

    static Version Normalize(Version? v) =>
        v is null ? new Version(0, 0, 0) : new Version(v.Major, v.Minor, Math.Max(v.Build, 0));

    static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"AllianceStatus/{CurrentText.Replace(' ', '-')}"); // GitHub requires a User-Agent
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    public void Dispose() => _timer.Stop();
}
