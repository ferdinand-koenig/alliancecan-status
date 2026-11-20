using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace AllianceStatus.Services;

/// <summary>Notification-area icon with a status badge and a themed context menu.</summary>
public sealed class TrayIcon : IDisposable
{
    readonly NotifyIcon _icon;
    readonly ContextMenuStrip _menu;
    readonly ToolStripMenuItem _autostart;
    Icon? _current;
    IntPtr _hIcon;
    TrayStatus _status = TrayStatus.Ok;

    public event Action? OpenRequested;
    public event Action? RefreshRequested;
    public event Action? WebsiteRequested;
    public event Action? ExitRequested;

    public TrayIcon()
    {
        _icon = new NotifyIcon { Text = "Alliance Status", Visible = false };

        var font = new Font("Segoe UI", 9f);
        _menu = new ContextMenuStrip { ShowImageMargin = false, ShowCheckMargin = true, Font = font, Padding = new Padding(2, 4, 2, 4) };
        var open = new ToolStripMenuItem("Open Alliance Status", null, (_, _) => OpenRequested?.Invoke())
        {
            Font = new Font(font, FontStyle.Bold),
        };
        var refresh = new ToolStripMenuItem("Refresh now", null, (_, _) => RefreshRequested?.Invoke());
        _autostart = new ToolStripMenuItem("Start with Windows", null, (_, _) => ToggleAutostart());
        var site = new ToolStripMenuItem("Open status website", null, (_, _) => WebsiteRequested?.Invoke());
        var quit = new ToolStripMenuItem("Quit", null, (_, _) => ExitRequested?.Invoke());
        _menu.Items.AddRange(new ToolStripItem[]
        {
            open, refresh, new ToolStripSeparator(), _autostart, site, new ToolStripSeparator(), quit,
        });
        foreach (ToolStripItem item in _menu.Items)
            if (item is ToolStripMenuItem mi) mi.Padding = new Padding(2, 5, 12, 5);
        _menu.Opening += (_, _) => _autostart.Checked = Autostart.IsEnabled;

        _icon.ContextMenuStrip = _menu;
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) OpenRequested?.Invoke();
        };

        ApplyTheme();
        _icon.Visible = true;
    }

    public void SetStatus(TrayStatus status, string tooltip)
    {
        _status = status;
        _icon.Text = tooltip.Length > 127 ? tooltip[..127] : tooltip;
        RenderIcon();
    }

    public void ApplyTheme()
    {
        bool dark = ThemeManager.IsDark;
        _menu.Renderer = new MenuRenderer(dark);
        _menu.BackColor = dark ? Color.FromArgb(0x2B, 0x2B, 0x2B) : Color.FromArgb(0xF9, 0xF9, 0xF9);
        RenderIcon();
    }

    void ToggleAutostart()
    {
        try { Autostart.Set(!Autostart.IsEnabled); }
        catch (Exception ex) { Log.Error("Could not change autostart", ex); }
    }

    void RenderIcon()
    {
        int size = SystemInformation.SmallIconSize.Width;
        var icon = IconPainter.Create(size, _status, ThemeManager.TaskbarIsDark, out var handle);
        _icon.Icon = icon;
        _current?.Dispose();
        if (_hIcon != IntPtr.Zero) Native.DestroyIcon(_hIcon);
        _current = icon;
        _hIcon = handle;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
        _current?.Dispose();
        if (_hIcon != IntPtr.Zero) Native.DestroyIcon(_hIcon);
        _hIcon = IntPtr.Zero;
    }
}

/// <summary>Draws a pulse glyph that matches the taskbar theme, plus a colored status badge.</summary>
internal static class IconPainter
{
    public static Icon Create(int size, TrayStatus status, bool darkTaskbar, out IntPtr handle)
    {
        using var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);

            float s = size;
            var fg = darkTaskbar ? Color.White : Color.FromArgb(0x1C, 0x1C, 0x1C);
            float stroke = Math.Max(1.4f, s / 10f);
            PointF P(float x, float y) => new(x * s, y * s);

            using (var pen = new Pen(fg, stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            {
                g.DrawLines(pen, new[]
                {
                    P(0.07f, 0.47f), P(0.28f, 0.47f), P(0.40f, 0.16f), P(0.56f, 0.80f), P(0.68f, 0.47f), P(0.93f, 0.47f),
                });
            }

            Color? badge = status switch
            {
                TrayStatus.Active => Color.FromArgb(0xE8, 0x48, 0x55),
                TrayStatus.Upcoming => Color.FromArgb(0xF7, 0xB5, 0x00),
                TrayStatus.Offline => Color.FromArgb(0x8A, 0x8A, 0x8A),
                _ => null,
            };
            if (badge is { } color)
            {
                float r = s * 0.24f, gap = Math.Max(1f, s / 14f);
                float cx = s - r - 0.5f, cy = s - r - 0.5f;
                // Cut a transparent ring so the badge reads clearly over the glyph.
                g.CompositingMode = CompositingMode.SourceCopy;
                using (var clear = new SolidBrush(Color.Transparent))
                    g.FillEllipse(clear, cx - r - gap, cy - r - gap, 2 * (r + gap), 2 * (r + gap));
                g.CompositingMode = CompositingMode.SourceOver;
                using var fill = new SolidBrush(color);
                g.FillEllipse(fill, cx - r, cy - r, 2 * r, 2 * r);
            }
        }
        handle = bmp.GetHicon();
        return Icon.FromHandle(handle);
    }
}

internal sealed class MenuRenderer : ToolStripProfessionalRenderer
{
    readonly bool _dark;

    public MenuRenderer(bool dark) : base(new MenuColors(dark))
    {
        _dark = dark;
        RoundedEdges = false;
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = !e.Item.Enabled ? Color.Gray : _dark ? Color.FromArgb(0xF2, 0xF2, 0xF2) : Color.FromArgb(0x1B, 0x1B, 0x1B);
        base.OnRenderItemText(e);
    }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = e.ImageRectangle;
        using var pen = new Pen(_dark ? Color.White : Color.Black, 1.6f)
        {
            StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round,
        };
        g.DrawLines(pen, new[]
        {
            new PointF(r.Left + r.Width * 0.22f, r.Top + r.Height * 0.52f),
            new PointF(r.Left + r.Width * 0.42f, r.Top + r.Height * 0.72f),
            new PointF(r.Left + r.Width * 0.80f, r.Top + r.Height * 0.30f),
        });
    }
}

internal sealed class MenuColors : ProfessionalColorTable
{
    readonly Color _bg, _hover, _border, _separator;

    public MenuColors(bool dark)
    {
        UseSystemColors = false;
        _bg = dark ? Color.FromArgb(0x2B, 0x2B, 0x2B) : Color.FromArgb(0xF9, 0xF9, 0xF9);
        _hover = dark ? Color.FromArgb(0x3A, 0x3A, 0x3A) : Color.FromArgb(0xEA, 0xEA, 0xEA);
        _border = dark ? Color.FromArgb(0x45, 0x45, 0x45) : Color.FromArgb(0xD5, 0xD5, 0xD5);
        _separator = dark ? Color.FromArgb(0x40, 0x40, 0x40) : Color.FromArgb(0xE0, 0xE0, 0xE0);
    }

    public override Color ToolStripDropDownBackground => _bg;
    public override Color ImageMarginGradientBegin => _bg;
    public override Color ImageMarginGradientMiddle => _bg;
    public override Color ImageMarginGradientEnd => _bg;
    public override Color MenuBorder => _border;
    public override Color MenuItemBorder => _hover;
    public override Color MenuItemSelected => _hover;
    public override Color MenuItemSelectedGradientBegin => _hover;
    public override Color MenuItemSelectedGradientEnd => _hover;
    public override Color MenuItemPressedGradientBegin => _hover;
    public override Color MenuItemPressedGradientEnd => _hover;
    public override Color SeparatorDark => _separator;
    public override Color SeparatorLight => _bg;
    public override Color CheckBackground => _bg;
    public override Color CheckSelectedBackground => _hover;
    public override Color CheckPressedBackground => _hover;
}
