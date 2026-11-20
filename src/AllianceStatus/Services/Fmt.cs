using System;
using System.Globalization;

namespace AllianceStatus.Services;

/// <summary>Compact, culture-aware date formatting for the UI and toasts.</summary>
public static class Fmt
{
    static CultureInfo C => CultureInfo.CurrentCulture;
    static string MonthDay => C.DateTimeFormat.MonthDayPattern.Replace("MMMM", "MMM");

    public static string Day(DateTimeOffset d)
    {
        var l = d.ToLocalTime();
        return l.ToString("ddd", C) + " " + l.ToString(MonthDay, C);
    }

    public static string Time(DateTimeOffset d) => d.ToLocalTime().ToString("t", C);

    public static string Stamp(DateTimeOffset d) => $"{Day(d)}, {Time(d)}";

    public static string Range(DateTimeOffset? start, DateTimeOffset? end)
    {
        if (start is { } s && end is { } e)
            return s.ToLocalTime().Date == e.ToLocalTime().Date
                ? $"{Stamp(s)} – {Time(e)}"
                : $"{Stamp(s)} – {Stamp(e)}";
        if (start is { } onlyStart) return $"From {Stamp(onlyStart)}";
        if (end is { } onlyEnd) return $"Until {Stamp(onlyEnd)}";
        return "";
    }

    public static string Relative(DateTimeOffset when, DateTimeOffset now)
    {
        var diff = now - when;
        bool future = diff < TimeSpan.Zero;
        var a = diff.Duration();
        if (a.TotalMinutes < 1) return "just now";
        string? span =
            a.TotalHours < 1 ? $"{(int)a.TotalMinutes} min" :
            a.TotalDays < 1 ? $"{(int)a.TotalHours} h" :
            a.TotalDays < 7 ? (int)a.TotalDays == 1 ? "1 day" : $"{(int)a.TotalDays} days" :
            null;
        if (span is null) return Day(when);
        return future ? $"in {span}" : $"{span} ago";
    }

    public static string Plural(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";

    public static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..(max - 1)].TrimEnd() + "…";
}
