using System;
using System.Diagnostics;

namespace AllianceStatus.Services;

public static class Shell
{
    /// <summary>Opens an http(s) URL in the default browser; anything else is ignored.</summary>
    public static void OpenUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return;
        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error("Could not open link", ex);
        }
    }
}
