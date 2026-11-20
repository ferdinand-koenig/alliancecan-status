using System;
using Microsoft.Win32;

namespace AllianceStatus.Services;

/// <summary>Per-user "start with Windows" via the HKCU Run key (no admin rights needed).</summary>
public static class Autostart
{
    public const string Flag = "--minimized";
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "AllianceStatus";

    static string Command => $"\"{Environment.ProcessPath}\" {Flag}";

    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(ValueName) is string;
            }
            catch { return false; }
        }
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (enabled) key.SetValue(ValueName, Command);
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    /// <summary>If autostart is on but the exe moved, point the entry at the current exe.</summary>
    public static void Repair()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key?.GetValue(ValueName) is string value && !string.Equals(value, Command, StringComparison.OrdinalIgnoreCase))
                key.SetValue(ValueName, Command);
        }
        catch (Exception ex)
        {
            Log.Error("Could not repair autostart entry", ex);
        }
    }
}
