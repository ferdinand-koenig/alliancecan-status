using System;

namespace AllianceStatus.Models;

public enum IncidentState { Active, Upcoming, Past }

/// <summary>One incident or maintenance entry from a feed, as stored locally.</summary>
public sealed class Incident
{
    public string Key { get; set; } = "";
    public string FeedUrl { get; set; } = "";
    public string Cluster { get; set; } = "";
    public string Title { get; set; } = "";
    public string Summary { get; set; } = "";
    public string? Link { get; set; }
    public string? Status { get; set; }
    public string? Categories { get; set; }

    public DateTimeOffset? Published { get; set; }
    public DateTimeOffset? Updated { get; set; }
    public DateTimeOffset? Start { get; set; }
    public DateTimeOffset? End { get; set; }

    public DateTimeOffset FirstSeen { get; set; }
    public DateTimeOffset LastSeen { get; set; }

    /// <summary>Set when the entry disappeared from a successfully fetched feed.</summary>
    public DateTimeOffset? GoneSince { get; set; }
}
