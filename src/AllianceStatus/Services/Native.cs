using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace AllianceStatus.Services;

internal static class Native
{
    const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    const int DWMWCP_ROUND = 2;

    public static bool IsWindows11 => Environment.OSVersion.Version.Build >= 22000;

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    public static extern bool AllowSetForegroundWindow(int processId);

    [DllImport("user32.dll")]
    public static extern bool DestroyIcon(IntPtr handle);

    /// <summary>Rounded corners and a theme-matched frame on Windows 11.</summary>
    public static void StyleWindow(Window window, bool dark)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        try
        {
            int darkValue = dark ? 1 : 0;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkValue, sizeof(int));
            if (IsWindows11)
            {
                int corner = DWMWCP_ROUND;
                DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
            }
        }
        catch
        {
            // Purely cosmetic.
        }
    }
}
