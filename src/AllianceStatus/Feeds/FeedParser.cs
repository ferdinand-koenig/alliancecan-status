using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace AllianceStatus.Feeds;

public sealed record FeedItem(
    string Id,
    string Title,
    string? Link,
    string Summary,
    DateTimeOffset? Published,
    DateTimeOffset? Updated,
    DateTimeOffset? Start,
    DateTimeOffset? End,
    string? Status,
    string? Categories);

public sealed record FeedDocument(string? Title, IReadOnlyList<FeedItem> Items, bool Repaired);

/// <summary>
/// Namespace-agnostic RSS 0.9x/2.0, RSS 1.0 (RDF) and Atom parser that degrades gracefully:
/// strict XML first, then a repaired copy (bare ampersands, HTML entities, control characters),
/// and finally per-item salvage with regular expressions.
/// </summary>
public static class FeedParser
{
    static readonly string[] TitleNames = { "title" };
    static readonly string[] IdNames = { "guid", "id" };
    static readonly string[] SummaryNames = { "description", "summary", "encoded", "content" };
    static readonly string[] PublishedNames = { "pubDate", "published", "issued", "created", "date" };
    static readonly string[] UpdatedNames = { "updated", "modified", "lastUpdated" };
    static readonly string[] StartNames =
    {
        "start", "startDate", "start_date", "startTime", "start_time", "begin", "beginTime",
        "scheduledStart", "scheduled_start", "scheduledFor", "dtstart",
    };
    static readonly string[] EndNames =
    {
        "end", "endDate", "end_date", "endTime", "end_time", "finish", "scheduledEnd", "scheduled_end",
        "resolvedAt", "resolved_at", "dtend",
    };
    static readonly string[] StatusNames = { "status", "state", "phase" };

    const RegexOptions Opts = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;

    static readonly Regex LooksLikeFeed = new(@"<(?:[\w.-]+:)?(?:rss|feed|RDF)\b", Opts);
    static readonly Regex ItemBlock = new(@"<((?:[\w.-]+:)?(?:item|entry))\b[^>]*>(.*?)</\1\s*>", Opts | RegexOptions.Singleline);
    static readonly Regex BareAmp = new(@"&(?!(?:#\d{1,7}|#x[0-9a-f]{1,6}|[a-z][a-z0-9]{1,31});)", Opts);
    static readonly Regex NamedEntity = new(@"&([a-z][a-z0-9]{1,31});", Opts);
    static readonly Regex Doctype = new(@"<!DOCTYPE[^>\[]*(?:\[[^\]]*\])?\s*>", Opts);
    static readonly Regex CData = new(@"<!\[CDATA\[(.*?)\]\]>", RegexOptions.Singleline | RegexOptions.Compiled);
    static readonly Regex ScriptStyle = new(@"<(script|style)\b.*?</\1\s*>", Opts | RegexOptions.Singleline);
    static readonly Regex BlockTags = new(@"<\s*(?:br|hr|/p|/div|/h\d|/tr|/ul|/ol)\b[^>]*>", Opts);
    static readonly Regex ListItem = new(@"<\s*li\b[^>]*>", Opts);
    static readonly Regex Tags = new(@"<[^>]+>", RegexOptions.Compiled);
    static readonly Regex InlineSpace = new(@"[ \t\f\v\u00A0]+", RegexOptions.Compiled);
    static readonly Regex LineBreaks = new(@"[ \t]*\n\s*", RegexOptions.Compiled);
    static readonly Regex AnyWhitespace = new(@"\s+", RegexOptions.Compiled);
    static readonly Regex XmlnsAttr = new(@"\s+xmlns(?::[\w.-]+)?\s*=\s*(?:""[^""]*""|'[^']*')", Opts);
    static readonly Regex PrefixedTag = new(@"<(/?)[a-z_][\w.-]*:", Opts);
    static readonly Regex PrefixedAttr = new(@"(\s)[a-z_][\w.-]*:([\w.-]+\s*=)", Opts);

    static readonly Regex StartRx = new(
        $@"\b(?:start(?:s|ing|ed)?|begin(?:s|ning)?|from|scheduled(?:\s+for)?|commenc\w*)\b(?:\s+(?:time|date|at|on))?\s*[:=–-]?\s*(?<d>{LooseDate.Pattern})", Opts);
    static readonly Regex EndRx = new(
        $@"\b(?:end(?:s|ing|ed)?|until|till|to|finish(?:es|ed)?|complet\w*|resolved|expected\s+end)\b(?:\s+(?:time|date|at|on))?\s*[:=–-]?\s*(?<d>{LooseDate.Pattern})", Opts);
    static readonly Regex RangeRx = new(
        $@"(?<a>{LooseDate.Pattern})\s*(?:-|–|—|to|until)\s*(?<b>{LooseDate.Pattern}|\d{{1,2}}:\d{{2}})", Opts);

    public static FeedDocument Parse(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) throw new FeedFormatException("Empty response");

        int lt = raw.IndexOf('<');
        if (lt < 0) throw new FeedFormatException("Response is not XML");
        var text = raw[lt..];

        bool repaired = false;
        var doc = TryLoad(text);
        if (doc is null)
        {
            doc = TryLoad(Sanitize(text));
            repaired = doc is not null;
        }

        if (doc?.Root is { } root)
        {
            var kind = root.Name.LocalName.ToLowerInvariant();
            if (kind is "html") throw new FeedFormatException("Got a web page instead of a feed");
            if (kind is not ("rss" or "feed" or "rdf"))
                throw new FeedFormatException($"Unrecognized feed format <{root.Name.LocalName}>");

            var items = root.Descendants()
                .Where(e => e.Name.LocalName is "item" or "entry")
                .Select(FromElement)
                .OfType<FeedItem>()
                .ToList();
            return new FeedDocument(FeedTitle(root), items, repaired);
        }

        // Last resort: salvage individual items from badly broken XML.
        if (!LooksLikeFeed.IsMatch(text)) throw new FeedFormatException("Malformed feed");
        var salvaged = new List<FeedItem>();
        foreach (Match m in ItemBlock.Matches(text))
        {
            var block = StripPrefixes(Sanitize(m.Value));
            var el = TryLoad("<x>" + block + "</x>")?.Root?.Elements().FirstOrDefault();
            var item = el is not null ? FromElement(el) : FromBlock(m.Groups[2].Value);
            if (item is not null) salvaged.Add(item);
        }
        return new FeedDocument(null, salvaged, true);
    }

    static XDocument? TryLoad(string xml)
    {
        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Ignore,
                CheckCharacters = false,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true,
            };
            using var sr = new StringReader(xml);
            using var xr = XmlReader.Create(sr, settings);
            return XDocument.Load(xr);
        }
        catch (XmlException) { return null; }
    }

    internal static string Sanitize(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
            if (XmlConvert.IsXmlChar(c) || char.IsSurrogate(c)) sb.Append(c);
        s = Doctype.Replace(sb.ToString(), "");
        s = BareAmp.Replace(s, "&amp;");
        return NamedEntity.Replace(s, m =>
        {
            var name = m.Groups[1].Value;
            if (name is "amp" or "lt" or "gt" or "quot" or "apos") return m.Value;
            var decoded = WebUtility.HtmlDecode(m.Value);
            return decoded == m.Value ? "&amp;" + name + ";" : SecurityElement.Escape(decoded) ?? "";
        });
    }

    static string StripPrefixes(string s)
    {
        s = XmlnsAttr.Replace(s, "");
        s = PrefixedTag.Replace(s, "<$1");
        return PrefixedAttr.Replace(s, "$1$2");
    }

    static string? FeedTitle(XElement root)
    {
        var t = root.Descendants().FirstOrDefault(e =>
            e.Name.LocalName == "title" && e.Parent?.Name.LocalName is not ("item" or "entry" or "image"));
        var clean = Clean(t?.Value, singleLine: true);
        return clean.Length > 0 ? clean : null;
    }

    static FeedItem? FromElement(XElement e)
    {
        string? Get(string[] names)
        {
            foreach (var n in names)
            {
                var c = e.Elements().FirstOrDefault(x => string.Equals(x.Name.LocalName, n, StringComparison.OrdinalIgnoreCase));
                if (c is not null && !string.IsNullOrWhiteSpace(c.Value)) return c.Value.Trim();
            }
            return null;
        }

        var categories = string.Join(", ", e.Elements()
            .Where(x => x.Name.LocalName.Equals("category", StringComparison.OrdinalIgnoreCase))
            .Select(x => ((string?)x.Attribute("term") ?? x.Value).Trim())
            .Where(v => v.Length > 0));

        return Build(Get, PickLink(e), categories.Length > 0 ? categories : null);
    }

    static string? PickLink(XElement e)
    {
        string? fallback = null;
        foreach (var l in e.Elements().Where(x => x.Name.LocalName.Equals("link", StringComparison.OrdinalIgnoreCase)))
        {
            var href = ((string?)l.Attribute("href"))?.Trim();
            if (!string.IsNullOrEmpty(href))
            {
                var rel = (string?)l.Attribute("rel");
                if (rel is null or "alternate") return href;
                fallback ??= href;
            }
            else if (!string.IsNullOrWhiteSpace(l.Value)) return l.Value.Trim();
        }
        if (fallback is not null) return fallback;

        var guid = e.Elements().FirstOrDefault(x => x.Name.LocalName == "guid");
        var g = guid?.Value.Trim();
        if (g is not null && (string?)guid!.Attribute("isPermaLink") != "false" && Uri.IsWellFormedUriString(g, UriKind.Absolute))
            return g;
        return null;
    }

    static FeedItem? FromBlock(string inner)
    {
        string? Get(string[] names)
        {
            foreach (var n in names)
            {
                var name = Regex.Escape(n);
                var m = Regex.Match(inner, $@"<(?:[\w.-]+:)?{name}\b[^>]*>(.*?)</(?:[\w.-]+:)?{name}\s*>",
                    RegexOptions.Singleline | RegexOptions.IgnoreCase);
                if (m.Success && !string.IsNullOrWhiteSpace(m.Groups[1].Value))
                    return CData.Replace(m.Groups[1].Value, "$1").Trim();
            }
            return null;
        }

        var href = Regex.Match(inner, @"<link\b[^>]*\bhref\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase);
        var link = href.Success ? WebUtility.HtmlDecode(href.Groups[1].Value) : Get(new[] { "link" });
        return Build(Get, link, null);
    }

    static FeedItem? Build(Func<string[], string?> get, string? link, string? categories)
    {
        var title = Clean(get(TitleNames), singleLine: true);
        var summary = Clean(get(SummaryNames), singleLine: false);
        if (title.Length == 0 && summary.Length == 0) return null;
        if (title.Length == 0) title = summary.Length > 90 ? summary[..89].TrimEnd() + "…" : summary;

        var published = LooseDate.Parse(get(PublishedNames));
        var updated = LooseDate.Parse(get(UpdatedNames));
        var start = LooseDate.Parse(get(StartNames));
        var end = LooseDate.Parse(get(EndNames));
        var status = Clean(get(StatusNames), singleLine: true);

        if (start is null || end is null)
        {
            var (scannedStart, scannedEnd) = ScanText(title + "\n" + summary);
            start ??= scannedStart;
            end ??= scannedEnd;
        }
        if (start is { } st && end is { } en && en < st) end = null;

        var id = get(IdNames) ?? link ?? $"{title}|{published?.ToUniversalTime():o}";
        return new FeedItem(id.Trim(), title, link?.Trim(), summary, published, updated, start, end,
            status.Length > 0 ? status : null, categories);
    }

    /// <summary>Finds "Start: …", "until …" or "A – B" schedules in free text.</summary>
    internal static (DateTimeOffset? Start, DateTimeOffset? End) ScanText(string text)
    {
        DateTimeOffset? start = null, end = null;
        var sm = StartRx.Match(text);
        if (sm.Success) start = LooseDate.Parse(sm.Groups["d"].Value);
        var em = EndRx.Match(text);
        if (em.Success) end = LooseDate.Parse(em.Groups["d"].Value);
        if (start is not null || end is not null) return (start, end);

        var rm = RangeRx.Match(text);
        if (rm.Success && LooseDate.Parse(rm.Groups["a"].Value) is { } a)
        {
            start = a;
            var b = rm.Groups["b"].Value.Trim();
            if (b.Length <= 5)
            {
                var parts = b.Split(':');
                if (int.TryParse(parts[0], out var h) && int.TryParse(parts[1], out var mi) && h < 24 && mi < 60)
                {
                    var t = new DateTimeOffset(a.Year, a.Month, a.Day, h, mi, 0, a.Offset);
                    end = t > a ? t : t.AddDays(1);
                }
            }
            else end = LooseDate.Parse(b);
        }
        return (start, end);
    }

    /// <summary>Turns feed HTML/text into readable plain text.</summary>
    public static string Clean(string? raw, bool singleLine)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var s = CData.Replace(raw, "$1");
        s = ScriptStyle.Replace(s, " ");
        s = BlockTags.Replace(s, "\n");
        s = ListItem.Replace(s, "\n• ");
        s = Tags.Replace(s, "");
        s = WebUtility.HtmlDecode(s).Replace("\r", "");
        s = InlineSpace.Replace(s, " ");
        s = singleLine ? AnyWhitespace.Replace(s, " ") : LineBreaks.Replace(s, "\n");
        return s.Trim();
    }
}
