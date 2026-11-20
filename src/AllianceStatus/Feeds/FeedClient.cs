using System;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AllianceStatus.Feeds;

/// <summary>One shared HttpClient with timeouts and a couple of gentle retries.</summary>
public static class FeedClient
{
    static readonly HttpClient Http = Create();

    static HttpClient Create()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            ConnectTimeout = TimeSpan.FromSeconds(15),
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("AllianceStatus/1.0 (Windows tray monitor)");
        client.DefaultRequestHeaders.Accept.ParseAdd(
            "application/rss+xml, application/atom+xml, application/xml;q=0.9, text/xml;q=0.9, */*;q=0.5");
        return client;
    }

    public static async Task<string> GetStringAsync(string url, CancellationToken ct)
    {
        if (!NetworkInterface.GetIsNetworkAvailable()) throw new OfflineException();

        Exception? last = null;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
                if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
                    throw new FeedNotFoundException();
                response.EnsureSuccessStatusCode();
                try
                {
                    return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                }
                catch (InvalidOperationException)
                {
                    // Bogus charset header: fall back to UTF-8.
                    var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                    return Encoding.UTF8.GetString(bytes);
                }
            }
            catch (Exception ex) when ((ex is HttpRequestException || ex is TaskCanceledException) && !ct.IsCancellationRequested)
            {
                last = ex;
                // Don't retry client errors other than 408/429.
                if (ex is HttpRequestException { StatusCode: { } code } && (int)code is >= 400 and < 500 and not 408 and not 429)
                    break;
            }
            if (attempt < 2) await Task.Delay(TimeSpan.FromSeconds(3 * (attempt + 1)), ct).ConfigureAwait(false);
        }
        throw last ?? new HttpRequestException("Request failed");
    }
}
