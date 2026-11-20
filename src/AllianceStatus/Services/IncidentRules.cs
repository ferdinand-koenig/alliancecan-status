using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AllianceStatus.Feeds;
using AllianceStatus.Models;

namespace AllianceStatus.Services;

/// <summary>
/// Decides whether an incident is active, upcoming or past. Status feeds rarely carry
/// structured state, so this combines explicit fields, schedule times and wording.
/// </summary>
public static class IncidentRules
{
    /// <summary>How long past incidents stay visible.</summary>
    public static readonly TimeSpan PastRetention = TimeSpan.FromDays(7);

    /// <summary>An open-ended incident with no activity for this long is treated as over.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromDays(14);

    // Skips phrases like "not resolved", "will be completed", "until restored".
    const string NegationGuard = @"(?<!\b(?:not|be|been|being|until|once|when|after|if|unless)(?:\s+\w+)?\s+)";

    static readonly Regex ResolvedRx = new(
        NegationGuard + @"\b(?:resolved|completed|finished|ended|fixed|restored|cancell?ed|" +
        @"back\s+(?:online|up|in\s+(?:service|production))|operational\s+again|is\s+over)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    static readonly Regex MaintenanceRx = new(
        @"\b(?:maintenance|scheduled|planned|upgrade|downtime|outage\s+window)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static bool IsResolved(Incident i) =>
        ResolvedRx.IsMatch(i.Status ?? "") ||
        ResolvedRx.IsMatch(i.Title) ||
        ResolvedRx.IsMatch(i.Categories ?? "") ||
        ResolvedRx.IsMatch(Head(i.Summary));

    public static bool IsMaintenance(Incident i) =>
        MaintenanceRx.IsMatch(i.Title) || MaintenanceRx.IsMatch(i.Categories ?? "") || MaintenanceRx.IsMatch(i.Status ?? "");

    public static IncidentState Classify(Incident i, DateTimeOffset now)
    {
        if (i.GoneSince is not null || IsResolved(i)) return IncidentState.Past;
        if (i.End is { } end && end <= now) return IncidentState.Past;
        if (i.Start is { } start && start > now) return IncidentState.Upcoming;
        if (i.End is null && now - LastActivity(i) > StaleAfter) return IncidentState.Past;
        return IncidentState.Active;
    }

    /// <summary>When the incident became past; used for the 7-day retention.</summary>
    public static DateTimeOffset PastSince(Incident i, DateTimeOffset now)
    {
        if (i.End is { } end && end <= now) return end;
        if (IsResolved(i)) return i.Updated ?? i.Published ?? i.GoneSince ?? i.FirstSeen;
        if (i.GoneSince is { } gone) return gone;
        if (i.End is null) return LastActivity(i) + StaleAfter;
        return now;
    }

    /// <summary>Date used to order active incidents (newest first).</summary>
    public static DateTimeOffset SortDate(Incident i) => i.Start ?? i.Published ?? i.Updated ?? i.FirstSeen;

    static DateTimeOffset LastActivity(Incident i)
    {
        var last = i.Published ?? i.FirstSeen;
        if (i.Updated is { } u && u > last) last = u;
        if (i.Start is { } s && s > last) last = s;
        return last;
    }

    /// <summary>Content hash used to detect real changes. Excludes timestamps feeds regenerate.</summary>
    public static string Fingerprint(Incident i)
    {
        var text = string.Join("\n", i.Title, i.Summary, i.Status ?? "",
            i.Start?.ToUniversalTime().ToString("o") ?? "", i.End?.ToUniversalTime().ToString("o") ?? "");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..32];
    }

    public static string LinkFor(Incident i) => i.Link ?? Alliance.PageForFeed(i.FeedUrl);

    static string Head(string s) => s.Length <= 160 ? s : s[..160];
}
