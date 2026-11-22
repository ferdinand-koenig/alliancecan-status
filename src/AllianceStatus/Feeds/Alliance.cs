using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace AllianceStatus.Feeds;

/// <summary>Knowledge about status.alliancecan.ca: URL scheme and cluster discovery.</summary>
public static class Alliance
{
    public const string Site = "https://status.alliancecan.ca/";

    /// <summary>Fallback list used when the status page can't be reached or parsed.</summary>
    public static readonly string[] KnownClusters =
    {
        "Nibi", "Fir", "Rorqual", "Narval", "Trillium", "Arbutus", "Killarney", "TamIA", "Vulcan",
    };

    static readonly Regex FeedUrlRx = new(@"^https?://status\.alliancecan\.ca/system/(?<n>[^/?#]+)/feed\.rss",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    static readonly Regex SystemLinkRx = new(@"/system/(?<n>[^/""'?#<>\s]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string FeedUrl(string cluster) => $"{Site}system/{Uri.EscapeDataString(cluster)}/feed.rss";

    public static string PageUrl(string cluster) => $"{Site}system/{Uri.EscapeDataString(cluster)}";

    public static string? ClusterFromFeedUrl(string url)
    {
        var m = FeedUrlRx.Match(url ?? "");
        return m.Success ? Uri.UnescapeDataString(m.Groups["n"].Value) : null;
    }

    /// <summary>Best page to open for a feed: the cluster page for Alliance feeds, else the feed's site.</summary>
    public static string PageForFeed(string feedUrl)
    {
        if (ClusterFromFeedUrl(feedUrl) is { } name) return PageUrl(name);
        return Uri.TryCreate(feedUrl, UriKind.Absolute, out var u) ? $"{u.Scheme}://{u.Authority}/" : feedUrl;
    }

    /// <summary>Scrapes the status home page for /system/&lt;name&gt; links.</summary>
    public static async Task<List<string>> DiscoverAsync(CancellationToken ct)
    {
        var html = await FeedClient.GetStringAsync(Site, ct).ConfigureAwait(false);
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in SystemLinkRx.Matches(html))
        {
            string name;
            try { name = WebUtility.HtmlDecode(Uri.UnescapeDataString(m.Groups["n"].Value)).Trim(); }
            catch (UriFormatException) { continue; }
            if (name.Length is > 0 and <= 40 && !name.Contains('.')) names.Add(name);
        }
        if (names.Count == 0) throw new FeedFormatException("No clusters listed on the status page");

        // Some entries (e.g. Globus, Nuage) have a status page but no feed. Keep only names whose feed parses.
        var checks = await Task.WhenAll(names.Select(async n => (Name: n, Ok: await HasFeedAsync(n, ct).ConfigureAwait(false))))
            .ConfigureAwait(false);
        return checks.Where(c => c.Ok).Select(c => c.Name).ToList();
    }

    static async Task<bool> HasFeedAsync(string cluster, CancellationToken ct)
    {
        try
        {
            FeedParser.Parse(await FeedClient.GetStringAsync(FeedUrl(cluster), ct).ConfigureAwait(false));
            return true;
        }
        catch (Exception ex) when (ex is FeedFormatException or FeedNotFoundException)
        {
            return false; // a web page or a 404: no feed for this entry
        }
        catch (Exception)
        {
            return true; // network trouble: don't hide a cluster we couldn't check
        }
    }
}
