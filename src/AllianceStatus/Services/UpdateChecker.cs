using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Threading;
using AllianceStatus.Models;

namespace AllianceStatus.Services;

/// <summary>
/// Checks GitHub once a day for a newer release, announces it once per version (toast + blue
/// dot on the gear), and on request downloads the installer, verifies its checksum and runs it
/// silently. Only plain MAJOR.MINOR.PATCH releases count: GitHub pre-releases and tags with a
/// suffix (e.g. v0.1.5-beta.1) are ignored. Development builds (0.0.0) never check.
/// </summary>
public sealed class UpdateChecker : IDisposable
{
    public const string Repository = "ferdinand-koenig/alliancecan-status";
    const string SetupAsset = "AllianceStatus-Setup.exe";
    static readonly string DownloadPrefix = $"https://github.com/{Repository}/releases/download/";
    static readonly TimeSpan CheckEvery = TimeSpan.FromHours(24);

    /// <summary>Version of the running app, e.g. 0.1.5. Declared before the HTTP clients, which use it.</summary>
    public static SemVer Current { get; } = ReadCurrentVersion();

    static readonly HttpClient Api = CreateClient(TimeSpan.FromSeconds(20), api: true);
    static readonly HttpClient Download = CreateClient(TimeSpan.FromMinutes(10), api: false);

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

    public static bool IsDevelopmentBuild => Current == SemVer.Zero;

    public static string CurrentText => IsDevelopmentBuild ? "development build" : Current.ToString();

    public bool UpdateAvailable => SemVer.TryParse(_state.LatestVersion, out var latest) && latest > Current;

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
        if (_state.LastUpdateCheck is { } last && DateTimeOffset.Now - last < CheckEvery) return;
        await CheckAsync(notify: true);
    }

    /// <summary>"Check for updates" in Settings. Returns false if GitHub couldn't be reached.</summary>
    public Task<bool> CheckNowAsync() => CheckAsync(notify: false);

    async Task<bool> CheckAsync(bool notify)
    {
        if (IsDevelopmentBuild || _checking) return false;
        _checking = true;
        try
        {
            if (await LatestStableAsync() is not { } r) return false; // GitHub unreachable
            var tag = r.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
            if (!SemVer.TryParse(tag, out var latest))
            {
                // e.g. a v0.1.5-beta.1 release: not an update, but the check itself worked.
                Log.Info($"Update check: ignoring release '{tag}' (not a plain version number)");
                _state.LastUpdateCheck = DateTimeOffset.Now;
                _store.Save(_state);
                return true;
            }

            _state.LastUpdateCheck = DateTimeOffset.Now;
            _state.LatestVersion = latest.ToString();
            _state.LatestUrl = r.TryGetProperty("html_url", out var page) ? page.GetString() : null;
            _state.LatestSetupUrl = null;
            _state.LatestSetupSha256 = null;
            if (r.TryGetProperty("assets", out var assets))
            {
                foreach (var a in assets.EnumerateArray())
                {
                    if (a.GetProperty("name").GetString() != SetupAsset) continue;
                    _state.LatestSetupUrl = a.GetProperty("browser_download_url").GetString();
                    // GitHub publishes "sha256:<hex>" for release assets.
                    if (a.TryGetProperty("digest", out var d) && d.GetString() is { } digest && digest.StartsWith("sha256:"))
                        _state.LatestSetupSha256 = digest["sha256:".Length..].ToLowerInvariant();
                }
            }

            if (latest > Current && _state.NotifiedUpdate != _state.LatestVersion)
            {
                if (notify) Notifier.ShowUpdate(_state.LatestVersion);
                _state.NotifiedUpdate = _state.LatestVersion;
                Log.Info($"Update available: {_state.LatestVersion} (running {CurrentText})");
            }

            _store.Save(_state);
            Changed?.Invoke();
            return true;
        }
        catch (Exception ex)
        {
            Log.Info($"Update check failed: {ex.GetType().Name}");
            return false;
        }
        finally
        {
            _checking = false;
        }
    }

    static async Task<JsonElement?> LatestStableAsync()
    {
        // /releases/latest never returns drafts or pre-releases.
        using var response = await Api.GetAsync($"https://api.github.com/repos/{Repository}/releases/latest");
        if (!response.IsSuccessStatusCode)
        {
            Log.Info($"Update check: GitHub returned {(int)response.StatusCode}");
            return null;
        }
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.Clone();
    }

    /// <summary>
    /// Downloads the new installer, checks its SHA-256 against GitHub's, and starts it silently.
    /// The caller must quit the app right after, so the installer can replace the files.
    /// Throws with a user-readable message on failure.
    /// </summary>
    public async Task InstallAsync(IProgress<int> progress)
    {
        var url = _state.LatestSetupUrl;
        var version = _state.LatestVersion;
        if (url is null || version is null || !url.StartsWith(DownloadPrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("No installer found for this release.");

        var folder = Path.Combine(Path.GetTempPath(), "AllianceStatus-Update");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, $"AllianceStatus-Setup-{version}.exe");

        try
        {
            using (var response = await Download.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                long? total = response.Content.Headers.ContentLength;
                await using var input = await response.Content.ReadAsStreamAsync();
                await using var output = File.Create(file);
                var buffer = new byte[81920];
                long done = 0;
                int read, lastPercent = -1;
                while ((read = await input.ReadAsync(buffer)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read));
                    done += read;
                    if (total is > 0)
                    {
                        int percent = (int)(done * 100 / total.Value);
                        if (percent != lastPercent) progress.Report(lastPercent = percent);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            Log.Info($"Update download failed: {ex.GetType().Name}");
            throw new InvalidOperationException("The download failed. Check your connection and try again.");
        }

        if (_state.LatestSetupSha256 is { } expected)
        {
            string actual;
            await using (var stream = File.OpenRead(file))
                actual = Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();
            if (actual != expected)
            {
                Log.Error($"Update checksum mismatch: expected {expected}, got {actual}");
                File.Delete(file);
                throw new InvalidOperationException("The download is damaged or was altered, so it wasn't installed.");
            }
        }
        else
        {
            Log.Info("Update: GitHub gave no checksum for the installer; relying on HTTPS from github.com");
        }

        var logFile = Path.Combine(folder, "install.log");
        Log.Info($"Starting installer for {version}");
        Process.Start(new ProcessStartInfo(file, $"/SILENT /SUPPRESSMSGBOXES /NORESTART /LOG=\"{logFile}\"")
        {
            UseShellExecute = true,
        });
    }

    static SemVer ReadCurrentVersion()
    {
        // InformationalVersion is the version from the tag, plus "+commit" added by .NET.
        var info = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return SemVer.TryParse(info, out var v) ? v : SemVer.Zero;
    }

    static HttpClient CreateClient(TimeSpan timeout, bool api)
    {
        var client = new HttpClient { Timeout = timeout };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"AllianceStatus/{(IsDevelopmentBuild ? "dev" : Current.ToString())}"); // GitHub requires a User-Agent
        if (api) client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    public void Dispose() => _timer.Stop();
}
