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

    /// <param name="approximateEnd">Prefix the end with "≈", e.g. when it is only known to within a check interval.</param>
    public static string Range(DateTimeOffset? start, DateTimeOffset? end, bool approximateEnd = false)
    {
        string mark = approximateEnd ? "≈" : "";
        if (start is { } s && end is { } e)
            return s.ToLocalTime().Date == e.ToLocalTime().Date
                ? $"{Stamp(s)} – {mark}{Time(e)}"
                : $"{Stamp(s)} – {mark}{Stamp(e)}";
        if (start is { } onlyStart) return $"From {Stamp(onlyStart)}";
        if (end is { } onlyEnd) return $"Until {mark}{Stamp(onlyEnd)}";
        return "";
    }

    /// <summary>
    /// Relative time that counts calendar days: "in 3 h" (today), "tomorrow, 08:00",
    /// "in 2 days" (Sunday → Tuesday), "yesterday, 22:10", "3 days ago".
    /// </summary>
    public static string Relative(DateTimeOffset when, DateTimeOffset now)
    {
        var diff = when - now;
        var a = diff.Duration();
        if (a.TotalMinutes < 1) return "just now";

        int days = (when.ToLocalTime().Date - now.ToLocalTime().Date).Days;
        if (days == 0)
        {
            string span = a.TotalHours < 1 ? $"{(int)a.TotalMinutes} min" : $"{(int)a.TotalHours} h";
            return diff > TimeSpan.Zero ? $"in {span}" : $"{span} ago";
        }
        if (days == 1) return $"tomorrow, {Time(when)}";
        if (days == -1) return $"yesterday, {Time(when)}";
        if (days is > 1 and < 7) return $"in {days} days";
        if (days is < -1 and > -7) return $"{-days} days ago";
        return Day(when);
    }

    /// <summary>"16:00" if today, otherwise "mer. 25 nov., 16:00".</summary>
    public static string When(DateTimeOffset d, DateTimeOffset now) =>
        d.ToLocalTime().Date == now.ToLocalTime().Date ? Time(d) : Stamp(d);

    public static string Plural(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";

    public static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..(max - 1)].TrimEnd() + "…";
}
