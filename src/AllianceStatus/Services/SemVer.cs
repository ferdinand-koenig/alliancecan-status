using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace AllianceStatus.Services;

/// <summary>
/// Release version MAJOR.MINOR.PATCH, e.g. 0.1.5. Versions with a suffix such as
/// 0.1.5-beta.1 are deliberately rejected, so test builds are never offered as updates.
/// Build metadata after "+" (added by .NET, e.g. 0.1.5+3fc5616) is ignored.
/// </summary>
public readonly record struct SemVer(int Major, int Minor, int Patch) : IComparable<SemVer>
{
    static readonly Regex Rx = new(@"^v?(\d+)\.(\d+)\.(\d+)(?:\+.*)?$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static readonly SemVer Zero = new(0, 0, 0);

    public static bool TryParse(string? text, out SemVer version)
    {
        version = Zero;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var m = Rx.Match(text.Trim());
        if (!m.Success) return false;
        version = new SemVer(
            int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
            int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture),
            int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture));
        return true;
    }

    public int CompareTo(SemVer other)
    {
        int c = Major.CompareTo(other.Major);
        if (c == 0) c = Minor.CompareTo(other.Minor);
        if (c == 0) c = Patch.CompareTo(other.Patch);
        return c;
    }

    public static bool operator >(SemVer a, SemVer b) => a.CompareTo(b) > 0;
    public static bool operator <(SemVer a, SemVer b) => a.CompareTo(b) < 0;

    public override string ToString() => $"{Major}.{Minor}.{Patch}";
}
