using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using AllianceStatus.Models;
using Microsoft.Toolkit.Uwp.Notifications;

namespace AllianceStatus.Services;

public enum ToastKind { New, Upcoming, Updated, Resolved }

public sealed record PendingToast(Incident Incident, ToastKind Kind);

/// <summary>Native Windows toasts. Clicking one opens the incident (argument "url").</summary>
public static class Notifier
{
    /// <summary>More changes than this in one check are collapsed into one summary toast.</summary>
    const int MaxIndividual = 3;

    public static void Show(IReadOnlyList<PendingToast> toasts)
    {
        if (toasts.Count == 0) return;
        try
        {
            if (toasts.Count > MaxIndividual)
            {
                ShowSummary(toasts);
                return;
            }
            foreach (var t in toasts) ShowOne(t);
        }
        catch (Exception ex)
        {
            Log.Error("Could not show notification", ex);
        }
    }

    static void ShowOne(PendingToast t)
    {
        var i = t.Incident;
        var heading = t.Kind switch
        {
            ToastKind.New => IncidentRules.IsMaintenance(i) ? "Maintenance" : "New incident",
            ToastKind.Upcoming => "Scheduled",
            ToastKind.Updated => "Updated",
            ToastKind.Resolved => "Resolved",
            _ => "Status",
        };
        var detail = i.Start is not null || i.End is not null
            ? Fmt.Range(i.Start, i.End)
            : Fmt.Truncate(i.Summary.Replace('\n', ' '), 140);

        var builder = new ToastContentBuilder()
            .AddArgument("url", IncidentRules.LinkFor(i))
            .AddText($"{heading}: {i.Cluster}")
            .AddText(i.Title);
        if (!string.IsNullOrWhiteSpace(detail)) builder.AddText(detail);
        builder.Show(toast =>
        {
            toast.Tag = Tag(i.Key); // replaces an older toast for the same incident
            toast.Group = "incidents";
        });
    }

    static void ShowSummary(IReadOnlyList<PendingToast> toasts)
    {
        var clusters = string.Join(", ", toasts.Select(t => t.Incident.Cluster).Distinct());
        new ToastContentBuilder()
            .AddArgument("action", "open")
            .AddText($"{toasts.Count} status changes")
            .AddText(clusters)
            .AddText("Select to see all incidents.")
            .Show(toast =>
            {
                toast.Tag = "summary";
                toast.Group = "summary";
            });
    }

    static string Tag(string key) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16];
}
