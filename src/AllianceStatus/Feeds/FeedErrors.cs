using System;
using System.Net.Http;
using System.Threading.Tasks;

namespace AllianceStatus.Feeds;

public sealed class FeedFormatException(string message) : Exception(message);
public sealed class FeedNotFoundException() : Exception("Feed not found");
public sealed class OfflineException() : Exception("Offline");

public static class FeedErrors
{
    /// <summary>Short, user-facing description of a fetch or parse failure.</summary>
    public static string Describe(Exception ex) => ex switch
    {
        OfflineException => "Offline",
        FeedNotFoundException => "Feed not found (404)",
        FeedFormatException f => f.Message,
        TaskCanceledException or TimeoutException => "Timed out",
        HttpRequestException { StatusCode: { } code } => $"Server returned {(int)code}",
        HttpRequestException => "Couldn't connect",
        _ => "Unexpected error",
    };
}
