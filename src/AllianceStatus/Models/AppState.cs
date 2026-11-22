using System;
using System.Collections.Generic;
using AllianceStatus.Feeds;

namespace AllianceStatus.Models;

public sealed class Subscription
{
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    /// <summary>True for feeds added by URL rather than picked from the Alliance cluster list.</summary>
    public bool IsCustom { get; set; }
}

public sealed class FeedHealth
{
    public DateTimeOffset? LastAttempt { get; set; }
    public DateTimeOffset? LastSuccess { get; set; }
    public string? Error { get; set; }
}

/// <summary>What we last told the user about an incident; prevents duplicate toasts.</summary>
public sealed class NotificationRecord
{
    public string Fingerprint { get; set; } = "";
    public IncidentState State { get; set; }
    public DateTimeOffset LastSeen { get; set; }
}

/// <summary>Everything persisted to %LOCALAPPDATA%\AllianceStatus\state.json.</summary>
public sealed class AppState
{
    public int Version { get; set; } = 1;
    public List<Subscription> Subscriptions { get; set; } = new();
    public Dictionary<string, Incident> Incidents { get; set; } = new();
    public Dictionary<string, NotificationRecord> Notified { get; set; } = new();
    public Dictionary<string, FeedHealth> Feeds { get; set; } = new();
    public List<string> DiscoveredClusters { get; set; } = new();
    public DateTimeOffset? DiscoveredAt { get; set; }
    public DateTimeOffset? LastCheck { get; set; }

    /// <summary>Version of the classification rules the stored notification records were made with.</summary>
    public int RulesVersion { get; set; }

    // Update checker
    public DateTimeOffset? LastUpdateCheck { get; set; }
    public string? LatestVersion { get; set; }
    public string? LatestUrl { get; set; }
    public string? LatestSetupUrl { get; set; }
    public string? LatestSetupSha256 { get; set; }
    /// <summary>Last version we showed an update toast for (one toast per version).</summary>
    public string? NotifiedUpdate { get; set; }

    public static AppState CreateDefault() => new()
    {
        Subscriptions = { new Subscription { Name = "Nibi", Url = Alliance.FeedUrl("Nibi") } },
    };

    /// <summary>Repairs nulls that a hand-edited or older state file may contain.</summary>
    public void Normalize()
    {
        Subscriptions ??= new();
        Incidents ??= new();
        Notified ??= new();
        Feeds ??= new();
        DiscoveredClusters ??= new();
        Subscriptions.RemoveAll(s => s is null || string.IsNullOrWhiteSpace(s.Url));
        foreach (var s in Subscriptions)
            if (string.IsNullOrWhiteSpace(s.Name)) s.Name = s.Url;
        foreach (var (key, inc) in new Dictionary<string, Incident>(Incidents))
        {
            if (inc is null) { Incidents.Remove(key); continue; }
            inc.Key = key;
            inc.Title ??= "";
            inc.Summary ??= "";
            inc.Cluster ??= "";
            inc.FeedUrl ??= "";
        }
    }
}
