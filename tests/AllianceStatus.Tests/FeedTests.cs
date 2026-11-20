using System;
using System.Globalization;
using AllianceStatus.Feeds;
using AllianceStatus.Models;
using AllianceStatus.Services;
using Xunit;

namespace AllianceStatus.Tests;

public class LooseDateTests
{
    [Theory]
    [InlineData("2026-11-20 21:42:26+00:00", "2026-11-20T21:42:26Z")]   // Alliance style
    [InlineData("2026-11-24 13:00", "2026-11-24T13:00:00Z")]            // Alliance style, no zone => UTC
    [InlineData("2026-11-20T21:42:26Z", "2026-11-20T21:42:26Z")]
    [InlineData("2026-11-20T16:42:26-05:00", "2026-11-20T21:42:26Z")]
    [InlineData("2026-11-20T21:42:26.123456789+00:00", "2026-11-20T21:42:26.1234567Z")]
    [InlineData("Fri, 20 Nov 2026 21:42:26 +0000", "2026-11-20T21:42:26Z")]
    [InlineData("Fri, 20 Nov 2026 16:42:26 EST", "2026-11-20T21:42:26Z")]
    [InlineData("Mon, 20 Nov 2026 21:42:26 GMT", "2026-11-20T21:42:26Z")]  // wrong weekday
    [InlineData("20 Nov 26 21:42 UT", "2026-11-20T21:42:00Z")]
    [InlineData("  2026-11-20   ", "2026-11-20T00:00:00Z")]
    [InlineData("Updated 2026-11-20 21:42 UTC by staff", "2026-11-20T21:42:00Z")]
    public void Parses(string input, string expected)
    {
        var want = DateTimeOffset.Parse(expected, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
        Assert.Equal<DateTimeOffset?>(want, LooseDate.Parse(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("soon")]
    [InlineData("2026-13-45 99:99")]
    public void RejectsGarbage(string? input) => Assert.Null(LooseDate.Parse(input));
}

public class FeedParserTests
{
    const string MalformedRss = """
        <?xml version="1.0"?>
        <rss version="2.0"><channel><title>Nibi</title>
        <item>
          <title>Login nodes degraded & slow</title>
          <link>https://status.alliancecan.ca/system/Nibi</link>
          <description>Investigating.&nbsp;Start: 2026-11-20 21:42:26+00:00</description>
          <pubDate>2026-11-20 21:42:26+00:00</pubDate>
          <guid>inc-1</guid>
        </item>
        <item>
          <title>Scheduled maintenance</title>
          <description><![CDATA[<p>From 2026-11-24 13:00 to 2026-11-24 17:00</p>]]></description>
          <pubDate>2026-11-24 13:00</pubDate>
          <guid>inc-2</guid>
        </item>
        </channel></rss>
        """;

    [Fact]
    public void RepairsMalformedRssAndReadsAllianceDates()
    {
        var doc = FeedParser.Parse(MalformedRss);
        Assert.True(doc.Repaired);
        Assert.Equal(2, doc.Items.Count);

        var first = doc.Items[0];
        Assert.Equal("inc-1", first.Id);
        Assert.Equal("Login nodes degraded & slow", first.Title);
        Assert.Equal(new DateTimeOffset(2026, 11, 20, 21, 42, 26, TimeSpan.Zero), first.Published);
        Assert.Equal(new DateTimeOffset(2026, 11, 20, 21, 42, 26, TimeSpan.Zero), first.Start);

        var second = doc.Items[1];
        Assert.Equal(new DateTimeOffset(2026, 11, 24, 13, 0, 0, TimeSpan.Zero), second.Start);
        Assert.Equal(new DateTimeOffset(2026, 11, 24, 17, 0, 0, TimeSpan.Zero), second.End);
        Assert.Equal("From 2026-11-24 13:00 to 2026-11-24 17:00", second.Summary);
    }

    [Fact]
    public void ParsesAtom()
    {
        const string atom = """
            <feed xmlns="http://www.w3.org/2005/Atom"><title>Example</title>
              <entry>
                <id>urn:1</id><title>Storage outage</title>
                <link rel="self" href="https://example.org/self"/>
                <link rel="alternate" href="https://example.org/incident/1"/>
                <updated>2026-11-20 21:42:26+00:00</updated>
                <summary type="html">&lt;b&gt;Fixed&lt;/b&gt; at 22:00</summary>
              </entry>
            </feed>
            """;
        var doc = FeedParser.Parse(atom);
        Assert.False(doc.Repaired);
        Assert.Equal("Example", doc.Title);
        var item = Assert.Single(doc.Items);
        Assert.Equal("https://example.org/incident/1", item.Link);
        Assert.Equal("Fixed at 22:00", item.Summary);
        Assert.NotNull(item.Updated);
    }

    [Fact]
    public void SalvagesTruncatedFeed()
    {
        var doc = FeedParser.Parse("<rss><channel><item><title>A</title><guid>1</guid></item><item><title>B");
        Assert.Equal("A", Assert.Single(doc.Items).Title);
    }

    [Fact]
    public void RejectsHtml() =>
        Assert.Throws<FeedFormatException>(() => FeedParser.Parse("<!DOCTYPE html><html><body>Oops</body></html>"));
}

public class IncidentRulesTests
{
    static readonly DateTimeOffset Now = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);

    static Incident Make(string title, DateTimeOffset? start = null, DateTimeOffset? end = null) => new()
    {
        Key = "k", FeedUrl = Alliance.FeedUrl("Nibi"), Cluster = "Nibi", Title = title,
        Published = Now.AddHours(-1), FirstSeen = Now.AddHours(-1), Start = start, End = end,
    };

    [Theory]
    [InlineData("Resolved: scheduler unavailable", true)]
    [InlineData("Maintenance completed", true)]
    [InlineData("Scheduler not resolved yet", false)]
    [InlineData("Upgrade will be completed on Friday", false)]
    [InlineData("Login nodes unavailable", false)]
    public void DetectsResolution(string title, bool resolved) =>
        Assert.Equal(resolved, IncidentRules.IsResolved(Make(title)));

    [Fact]
    public void Classifies()
    {
        Assert.Equal(IncidentState.Active, IncidentRules.Classify(Make("Outage"), Now));
        Assert.Equal(IncidentState.Upcoming, IncidentRules.Classify(Make("Maintenance", Now.AddDays(2)), Now));
        Assert.Equal(IncidentState.Past, IncidentRules.Classify(Make("Maintenance", Now.AddDays(-2), Now.AddDays(-1)), Now));
        Assert.Equal(IncidentState.Active, IncidentRules.Classify(Make("Maintenance", Now.AddHours(-1), Now.AddHours(3)), Now));
    }
}
