using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace AllianceStatus.Feeds;

/// <summary>
/// Forgiving timestamp parser for RSS/Atom feeds. Handles RFC 822 (RSS), ISO 8601 (Atom)
/// and the Alliance status page's non-standard forms such as
/// "2026-11-20 21:42:26+00:00" and "2026-11-24 13:00".
/// </summary>
public static class LooseDate
{
    /// <summary>
    /// Offset assumed for timestamps that carry no zone (e.g. "2026-11-24 13:00").
    /// The Alliance feed writes explicit times in UTC (+00:00), so UTC is the consistent choice.
    /// </summary>
    public static readonly TimeSpan AssumedOffset = TimeSpan.Zero;

    /// <summary>Regex fragment matching an ISO-like timestamp embedded in free text.</summary>
    public const string Pattern =
        @"\d{4}-\d{1,2}-\d{1,2}(?:[T ]\d{1,2}:\d{2}(?::\d{2}(?:[.,]\d+)?)?)?" +
        @"(?:\s?(?:(?:Z|UTC|GMT)\b|[+-](?:0\d|1[0-4])(?::?[0-5]\d)?(?![\d-])))?";

    static readonly Regex Iso = new(
        @"^(?<y>\d{4})-(?<mo>\d{1,2})-(?<d>\d{1,2})" +
        @"(?:(?:T|\s+)(?<h>\d{1,2}):(?<mi>\d{2})(?::(?<s>\d{2})(?:[.,](?<f>\d{1,7})\d*)?)?)?" +
        @"\s*(?<z>Z|UTC|GMT|[+-]\d{2}(?::?\d{2})?)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    static readonly Regex Rfc822 = new(
        @"^(?:[A-Za-z]{2,}\.?,?\s*)?(?<d>\d{1,2})[\s-]+(?<mon>[A-Za-z]{3,})\.?[\s-]+(?<y>\d{2,4})" +
        @"(?:[\sT]+(?<h>\d{1,2}):(?<mi>\d{2})(?::(?<s>\d{2}))?)?\s*(?<z>[A-Za-z]{1,5}|[+-]\d{2}:?\d{2})?\s*$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    static readonly Regex Embedded = new(Pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    static readonly Dictionary<string, int> Months = new(StringComparer.OrdinalIgnoreCase)
    {
        ["jan"] = 1, ["feb"] = 2, ["mar"] = 3, ["apr"] = 4, ["may"] = 5, ["jun"] = 6,
        ["jul"] = 7, ["aug"] = 8, ["sep"] = 9, ["oct"] = 10, ["nov"] = 11, ["dec"] = 12,
    };

    static readonly Dictionary<string, TimeSpan> Zones = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Z"] = TimeSpan.Zero, ["UT"] = TimeSpan.Zero, ["UTC"] = TimeSpan.Zero, ["GMT"] = TimeSpan.Zero,
        ["EST"] = TimeSpan.FromHours(-5), ["EDT"] = TimeSpan.FromHours(-4),
        ["CST"] = TimeSpan.FromHours(-6), ["CDT"] = TimeSpan.FromHours(-5),
        ["MST"] = TimeSpan.FromHours(-7), ["MDT"] = TimeSpan.FromHours(-6),
        ["PST"] = TimeSpan.FromHours(-8), ["PDT"] = TimeSpan.FromHours(-7),
        ["AST"] = TimeSpan.FromHours(-4), ["ADT"] = TimeSpan.FromHours(-3),
        ["NST"] = TimeSpan.FromMinutes(-210), ["NDT"] = TimeSpan.FromMinutes(-150),
        ["CET"] = TimeSpan.FromHours(1), ["CEST"] = TimeSpan.FromHours(2), ["BST"] = TimeSpan.FromHours(1),
    };

    public static DateTimeOffset? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var s = text.Trim();
        return TryIso(s) ?? TryRfc822(s) ?? TryFramework(s) ?? TryEmbedded(s);
    }

    static DateTimeOffset? TryIso(string s)
    {
        var m = Iso.Match(s);
        if (!m.Success) return null;
        var offset = m.Groups["z"].Success ? Zone(m.Groups["z"].Value) : AssumedOffset;
        if (offset is null) return null;
        try
        {
            var result = new DateTimeOffset(
                Int(m, "y"), Int(m, "mo"), Int(m, "d"),
                Int(m, "h"), Int(m, "mi"), Int(m, "s"), offset.Value);
            if (m.Groups["f"].Success)
            {
                var f = m.Groups["f"].Value.PadRight(7, '0');
                result = result.AddTicks(long.Parse(f, CultureInfo.InvariantCulture));
            }
            return Sane(result);
        }
        catch (ArgumentException) { return null; }
    }

    static DateTimeOffset? TryRfc822(string s)
    {
        var m = Rfc822.Match(s);
        if (!m.Success) return null;
        var mon = m.Groups["mon"].Value;
        if (mon.Length < 3 || !Months.TryGetValue(mon[..3], out var month)) return null;
        int year = Int(m, "y");
        if (year < 100) year += 2000;
        var offset = m.Groups["z"].Success ? Zone(m.Groups["z"].Value) ?? AssumedOffset : AssumedOffset;
        try
        {
            return Sane(new DateTimeOffset(year, month, Int(m, "d"), Int(m, "h"), Int(m, "mi"), Int(m, "s"), offset));
        }
        catch (ArgumentException) { return null; }
    }

    static DateTimeOffset? TryFramework(string s)
    {
        // AssumeUniversal matches AssumedOffset == UTC.
        return DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal, out var r)
            ? Sane(r)
            : null;
    }

    static DateTimeOffset? TryEmbedded(string s)
    {
        var m = Embedded.Match(s);
        return m.Success && m.Value.Length < s.Length ? TryIso(m.Value.Trim()) : null;
    }

    static TimeSpan? Zone(string raw)
    {
        var z = raw.Trim();
        if (z.Length == 0) return AssumedOffset;
        if (Zones.TryGetValue(z, out var known)) return known;
        if (z[0] is not ('+' or '-')) return null;
        int sign = z[0] == '-' ? -1 : 1;
        var digits = z[1..].Replace(":", "");
        if (digits.Length == 2) digits += "00";
        if (digits.Length != 4 || !int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var v))
            return null;
        int hh = v / 100, mm = v % 100;
        if (hh > 14 || mm > 59) return null;
        return TimeSpan.FromMinutes(sign * (hh * 60 + mm));
    }

    static int Int(Match m, string group) =>
        m.Groups[group].Success ? int.Parse(m.Groups[group].Value, CultureInfo.InvariantCulture) : 0;

    static DateTimeOffset? Sane(DateTimeOffset d) => d.Year is >= 1990 and <= 2200 ? d : null;
}
