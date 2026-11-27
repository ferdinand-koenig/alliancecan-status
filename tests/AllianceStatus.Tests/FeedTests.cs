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

public class SemVerTests
{
    [Theory]
    [InlineData("v0.1.5", "0.1.5")]
    [InlineData("0.1.5", "0.1.5")]
    [InlineData("0.1.5+3fc5616", "0.1.5")]   // InformationalVersion with commit
    public void Parses(string text, string expected)
    {
        Assert.True(SemVer.TryParse(text, out var v));
        Assert.Equal(expected, v.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("latest")]
    [InlineData("v1.2")]            // needs all three parts
    [InlineData("v0.1.5-beta.1")]   // test versions are filtered out
    [InlineData("0.1.5-rc.1")]
    public void Rejects(string? text) => Assert.False(SemVer.TryParse(text, out _));

    [Theory]
    [InlineData("0.1.10", "0.1.9")]
    [InlineData("0.2.0", "0.1.99")]
    [InlineData("1.0.0", "0.9.9")]
    public void Orders(string newer, string older)
    {
        SemVer.TryParse(newer, out var a);
        SemVer.TryParse(older, out var b);
        Assert.True(a > b);
        Assert.True(b < a);
    }
}

public class ScheduleTests
{
    static readonly DateTimeOffset Now = new(2026, 11, 22, 17, 18, 0, TimeSpan.Zero);

    const string NibiFeed = """
        <?xml version="1.0" encoding="UTF-8"?>
        <feed xmlns="http://www.w3.org/2005/Atom">
          <title>Incident Feed - Nibi</title>
          <updated>2026-11-22 17:18:29+00:00</updated>
          <entry>
            <id>1782</id>
            <title>Planned Outage - Arrêt planifié</title>
            <content type="html">&lt;h4&gt;Due to physical adjustment of equipment, the Nibi cluster will be unavailable from 8:00 AM Tuesday, November 24 to 4:00 PM EST Wednesday, November 25, 2026.&lt;/h4&gt;</content>
            <link href="https://status.alliancecan.ca/view_incident?incident=1782"/>
            <updated>2026-11-25 21:00</updated>
            <published>2026-11-24 13:00</published>
          </entry>
        </feed>
        """;

    static Incident ToIncident(string feedUrl, FeedItem item)
    {
        var inc = new Incident { Key = "k", FeedUrl = feedUrl, Cluster = "Nibi", Title = item.Title, Summary = item.Summary,
                                 Published = item.Published, Updated = item.Updated, FirstSeen = Now };
        (inc.Start, inc.End) = IncidentRules.Schedule(feedUrl, item, Now);
        return inc;
    }

    [Fact]
    public void AllianceDatesAreTheOutageWindow()
    {
        var url = Alliance.FeedUrl("Nibi");
        var item = Assert.Single(FeedParser.Parse(NibiFeed).Items);
        var inc = ToIncident(url, item);

        Assert.Equal(new DateTimeOffset(2026, 11, 24, 13, 0, 0, TimeSpan.Zero), inc.Start);
        Assert.Equal(new DateTimeOffset(2026, 11, 25, 21, 0, 0, TimeSpan.Zero), inc.End);
        Assert.Equal(IncidentState.Upcoming, IncidentRules.Classify(inc, Now));
        Assert.Equal(IncidentState.Active, IncidentRules.Classify(inc, new DateTimeOffset(2026, 11, 25, 12, 0, 0, TimeSpan.Zero)));
        Assert.Equal(IncidentState.Past, IncidentRules.Classify(inc, new DateTimeOffset(2026, 11, 26, 0, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void AllianceEntryWithoutEndStaysActive()
    {
        var url = Alliance.FeedUrl("Nibi");
        var item = new FeedItem("1", "Login nodes unreachable", null, "", Now.AddHours(-2), Now.AddHours(-2), null, null, null, null);
        var inc = ToIncident(url, item);
        Assert.Null(inc.End);
        Assert.Equal(IncidentState.Active, IncidentRules.Classify(inc, Now));
    }

    [Fact]
    public void AllianceOpenEndedIncidentStaysActiveForWeeks()
    {
        // Arbutus, Nov 2026: <published>2026-10-05 21:05</published> <updated>None</updated>
        const string feed = """
            <feed xmlns="http://www.w3.org/2005/Atom"><title>Incident Feed - Arbutus</title>
              <entry>
                <id>1700</id>
                <title>Ongoing Volume Storage Slowness</title>
                <content type="html">Volume storage is slower than usual.</content>
                <updated>None</updated>
                <published>2026-10-05 21:05</published>
              </entry>
            </feed>
            """;
        var url = Alliance.FeedUrl("Arbutus");
        var inc = ToIncident(url, Assert.Single(FeedParser.Parse(feed).Items));
        Assert.Null(inc.End);
        Assert.Equal(IncidentState.Active, IncidentRules.Classify(inc, Now));
    }

    [Fact]
    public void OtherFeedsStillGoStaleAfter14Days()
    {
        var item = new FeedItem("1", "Outage", null, "", Now.AddDays(-20), null, null, null, null, null);
        var inc = ToIncident("https://example.org/feed.xml", item);
        Assert.Equal(IncidentState.Past, IncidentRules.Classify(inc, Now));
    }

    [Fact]
    public void OtherFeedsUseFuturePublicationAsStart()
    {
        var item = new FeedItem("1", "Upgrade", null, "", Now.AddDays(2), null, null, null, null, null);
        var inc = ToIncident("https://example.org/feed.xml", item);
        Assert.Equal(IncidentState.Upcoming, IncidentRules.Classify(inc, Now));
    }
}

public class RelativeTimeTests
{
    // Built from local times, so the tests don't depend on the machine's time zone.
    static DateTimeOffset Local(int day, int hour, int minute = 0) =>
        new(new DateTime(2026, 11, day, hour, minute, 0, DateTimeKind.Local));

    static readonly DateTimeOffset SundayAfternoon = Local(22, 17);

    [Fact]
    public void CountsCalendarDays() =>
        Assert.Equal("in 2 days", Fmt.Relative(Local(24, 8), SundayAfternoon)); // Sunday 17:00 → Tuesday 08:00

    [Fact]
    public void SaysTomorrowAndYesterday()
    {
        Assert.StartsWith("tomorrow, ", Fmt.Relative(Local(23, 8), SundayAfternoon));
        Assert.StartsWith("yesterday, ", Fmt.Relative(Local(21, 23), SundayAfternoon));
    }

    [Fact]
    public void UsesHoursAndMinutesToday()
    {
        Assert.Equal("in 3 h", Fmt.Relative(Local(22, 20), SundayAfternoon));
        Assert.Equal("20 min ago", Fmt.Relative(Local(22, 16, 40), SundayAfternoon));
        Assert.Equal("just now", Fmt.Relative(SundayAfternoon, SundayAfternoon));
    }

    [Fact]
    public void CountsPastDays() =>
        Assert.Equal("2 days ago", Fmt.Relative(Local(20, 10), SundayAfternoon));

    [Fact]
    public void MarksApproximateEnd() =>
        Assert.Contains("– ≈", Fmt.Range(Local(24, 7, 30), Local(24, 13, 39), approximateEnd: true));
}
