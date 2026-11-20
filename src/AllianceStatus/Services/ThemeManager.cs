using System;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace AllianceStatus.Services;

/// <summary>
/// Follows the Windows light/dark setting and accent color, publishing them as
/// DynamicResource brushes (Bg, Card, Text, Accent, Critical, …).
/// </summary>
public static class ThemeManager
{
    const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    public static bool IsDark { get; private set; }
    public static bool TaskbarIsDark { get; private set; }
    public static event Action? ThemeChanged;

    static bool _applied, _hooked;
    static Color _accent;

    static readonly (string Key, string Hex)[] Light =
    {
        ("Bg", "#F3F3F3"), ("Card", "#FFFFFF"), ("CardHover", "#FAFAFA"), ("CardPressed", "#F5F5F5"),
        ("CardBorder", "#E3E3E3"), ("Divider", "#E5E5E5"), ("Footer", "#EDEDED"), ("Chip", "#EFEFEF"),
        ("Text", "#1B1B1B"), ("TextSecondary", "#5C5C5C"), ("TextTertiary", "#8A8A8A"),
        ("SubtleHover", "#0F000000"), ("SubtlePressed", "#08000000"),
        ("ControlFill", "#FFFFFF"), ("ControlStroke", "#D4D4D4"), ("ControlStrokeStrong", "#868686"),
        ("ScrollThumb", "#9A9A9A"),
        ("Critical", "#C42B1C"), ("Caution", "#9D5D00"), ("Success", "#0F7B0F"), ("Neutral", "#8A8A8A"),
    };

    static readonly (string Key, string Hex)[] Dark =
    {
        ("Bg", "#202020"), ("Card", "#2B2B2B"), ("CardHover", "#323232"), ("CardPressed", "#292929"),
        ("CardBorder", "#353535"), ("Divider", "#2E2E2E"), ("Footer", "#1C1C1C"), ("Chip", "#3B3B3B"),
        ("Text", "#FFFFFF"), ("TextSecondary", "#C8C8C8"), ("TextTertiary", "#8F8F8F"),
        ("SubtleHover", "#14FFFFFF"), ("SubtlePressed", "#0BFFFFFF"),
        ("ControlFill", "#2D2D2D"), ("ControlStroke", "#404040"), ("ControlStrokeStrong", "#9E9E9E"),
        ("ScrollThumb", "#7A7A7A"),
        ("Critical", "#FF99A4"), ("Caution", "#FCE100"), ("Success", "#6CCB5F"), ("Neutral", "#8F8F8F"),
    };

    public static void Initialize()
    {
        Apply();
        if (_hooked) return;
        _hooked = true;
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle)
                Application.Current?.Dispatcher.InvokeAsync(() =>
                {
                    if (Apply()) ThemeChanged?.Invoke();
                });
        };
    }

    public static Brush Brush(string key) =>
        Application.Current.Resources[key] as Brush ?? Brushes.Gray;

    /// <summary>Returns true if anything changed.</summary>
    static bool Apply()
    {
        bool dark = ReadDword("AppsUseLightTheme", 1) == 0;
        bool taskbarDark = ReadDword("SystemUsesLightTheme", 0) == 0;
        var accent = ReadAccent(dark);
        if (_applied && dark == IsDark && taskbarDark == TaskbarIsDark && accent == _accent) return false;

        IsDark = dark;
        TaskbarIsDark = taskbarDark;
        _accent = accent;
        _applied = true;

        var resources = Application.Current.Resources;
        foreach (var (key, hex) in dark ? Dark : Light)
            resources[key] = Frozen((Color)ColorConverter.ConvertFromString(hex));
        resources["Accent"] = Frozen(accent);
        resources["AccentText"] = Frozen(dark ? Colors.Black : Colors.White);
        return true;
    }

    static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    static int ReadDword(string name, int fallback)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue(name) is int v ? v : fallback;
        }
        catch { return fallback; }
    }

    static Color ReadAccent(bool dark)
    {
        try
        {
            var ui = new global::Windows.UI.ViewManagement.UISettings();
            var c = ui.GetColorValue(dark
                ? global::Windows.UI.ViewManagement.UIColorType.AccentLight2
                : global::Windows.UI.ViewManagement.UIColorType.AccentDark1);
            return Color.FromRgb(c.R, c.G, c.B);
        }
        catch
        {
            return dark ? Color.FromRgb(0x60, 0xCD, 0xFF) : Color.FromRgb(0x00, 0x5F, 0xB8);
        }
    }
}
