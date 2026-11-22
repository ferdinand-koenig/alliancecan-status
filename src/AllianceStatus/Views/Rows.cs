using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using AllianceStatus.Models;
using AllianceStatus.Services;

namespace AllianceStatus.Views;

public sealed class SectionRow
{
    public SectionRow(string title) => Title = title;
    public string Title { get; }
}

/// <summary>Immutable display model for one incident card.</summary>
public sealed class IncidentRow
{
    public string Cluster { get; init; } = "";
    public string Title { get; init; } = "";
    public string Summary { get; init; } = "";
    public string Schedule { get; init; } = "";
    public string When { get; init; } = "";
    public string Badge { get; init; } = "";
    public string Url { get; init; } = "";
    public Brush Stripe { get; init; } = Brushes.Gray;
    /// <summary>Badge text ("Scheduled"); a darker shade than the stripe where needed for readability.</summary>
    public Brush BadgeBrush { get; init; } = Brushes.Gray;
    public double Opacity { get; init; } = 1;

    public Visibility SummaryVisibility => Summary.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ScheduleVisibility => Schedule.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    public static IncidentRow From(Incident i, IncidentState state, DateTimeOffset now)
    {
        var summary = i.Summary == i.Title ? "" : Fmt.Truncate(i.Summary, 280);
        return new IncidentRow
        {
            Cluster = i.Cluster,
            Title = i.Title,
            Summary = summary,
            Schedule = Fmt.Range(i.Start, i.End),
            Url = IncidentRules.LinkFor(i),
            When = state switch
            {
                IncidentState.Upcoming => i.Start is { } s ? Fmt.Relative(s, now) : "",
                IncidentState.Past => Fmt.Relative(IncidentRules.PastSince(i, now), now),
                _ => Fmt.Relative(IncidentRules.SortDate(i), now),
            },
            Badge = state switch
            {
                IncidentState.Active => IncidentRules.IsMaintenance(i) ? "Maintenance" : "Ongoing",
                IncidentState.Upcoming => "Scheduled",
                _ => IncidentRules.IsResolved(i) ? "Resolved" : "Ended",
            },
            Stripe = ThemeManager.Brush(state switch
            {
                IncidentState.Active => "Critical",
                IncidentState.Upcoming => "CautionFill",
                _ => "Neutral",
            }),
            BadgeBrush = ThemeManager.Brush(state switch
            {
                IncidentState.Active => "Critical",
                IncidentState.Upcoming => "Caution",
                _ => "Neutral",
            }),
            Opacity = state == IncidentState.Past ? 0.55 : 1,
        };
    }
}

/// <summary>A cluster checkbox in Settings.</summary>
public sealed class ClusterOption : INotifyPropertyChanged
{
    readonly Action<ClusterOption, bool> _onChanged;
    bool _isChecked;

    public ClusterOption(string name, string url, bool isChecked, Action<ClusterOption, bool> onChanged)
    {
        Name = name;
        Url = url;
        _isChecked = isChecked;
        _onChanged = onChanged;
    }

    public string Name { get; }
    public string Url { get; }

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked == value) return;
            _isChecked = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
            _onChanged(this, value);
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
