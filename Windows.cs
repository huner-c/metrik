using System.Drawing.Text;

namespace Metrik;

internal static class Palette
{
    public static readonly Color Ink = Color.FromArgb(13, 21, 24);
    public static readonly Color Panel = Color.FromArgb(22, 33, 37);
    public static readonly Color Line = Color.FromArgb(45, 62, 65);
    public static readonly Color Text = Color.FromArgb(236, 247, 244);
    public static readonly Color Muted = Color.FromArgb(172, 196, 194);
    public static readonly Color Mint = Color.FromArgb(130, 245, 201);
    public static readonly Color Amber = Color.FromArgb(253, 195, 113);
}

internal enum TrackingStatus { WaitingForGame, SettingsFocused, ReceivingFrames, WaitingForFrames }

internal sealed class ControlWindow : Form
{
    private readonly Label _capture = new() { AutoSize = true };
    private readonly Label _gpu = new() { AutoSize = true };
    private readonly Label _hint = new() { AutoSize = true, ForeColor = Palette.Muted };
    private readonly Label _tabletStatus = new() { AutoSize = true, ForeColor = Palette.Muted };
    private readonly Label _usbStatus = new() { AutoSize = true, ForeColor = Palette.Muted };
    private readonly Button _toggle;
    private readonly Button _preview;
    private readonly Button _elevate;
    private readonly Button _tablet;
    private readonly Button _usb;
    private readonly Button _firewall;
    private OverlaySettings _settings;
    private string? _lastError;

    public event Action? OverlayToggled;
    public event Action? PreviewToggled;
    public event Action? HotkeyPressed;
    public event Action? RunElevated;
    public event Action? TabletToggled;
    public event Action? UsbToggled;
    public event Action? TabletFirewallRequested;
    public event Action<OverlaySettings>? SettingsChanged;

    public ControlWindow(OverlaySettings settings, Icon icon)
    {
        _settings = settings;
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "Metrik";
        Icon = icon;
        BackColor = Palette.Ink;
        ForeColor = Palette.Text;
        Font = new Font("Segoe UI", 10);
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(590, 600);
        MinimumSize = new Size(295, 220);

        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        var content = Stack(DockStyle.Top);
        content.Padding = new Padding(18, 14, 18, 14);
        Controls.Add(scroll);
        scroll.Controls.Add(content);

        Row(content, new Label { Text = "Metrik", Font = new Font(Font.FontFamily, 18, FontStyle.Bold), AutoSize = true });
        var status = Stack(DockStyle.Fill);
        status.Padding = new Padding(8);
        status.BackColor = Palette.Panel;
        _capture.Dock = _gpu.Dock = DockStyle.Fill;
        Row(status, _capture);
        Row(status, _gpu);
        Row(content, status);

        Row(content, Heading("Overlay metrics"));
        var metrics = Flow();
        foreach (var (flag, title) in new (Metric, string)[]
        {
            (Metric.Fps, "FPS"), (Metric.FrameTime, "Frame time"), (Metric.Load, "GPU usage"),
            (Metric.Temperature, "GPU temperature"), (Metric.Hotspot, "Hotspot"), (Metric.Clock, "GPU clock"),
            (Metric.VramUsed, "VRAM usage"), (Metric.VramTotal, "Total VRAM"),
            (Metric.Runtime, "Graphics API"), (Metric.Process, "Game name")
        })
        {
            var box = new CheckBox
            {
                Text = title, AutoSize = true, Checked = settings.VisibleMetrics.HasFlag(flag),
                Margin = new Padding(3, 4, 12, 4), Cursor = Cursors.Hand
            };
            box.CheckedChanged += (_, _) =>
            {
                var mask = box.Checked ? _settings.VisibleMetrics | flag : _settings.VisibleMetrics & ~flag;
                ChangeSettings(_settings with { VisibleMetrics = mask });
            };
            metrics.Controls.Add(box);
        }
        Row(content, metrics);

        Row(content, Heading("Overlay size"));
        var sizes = Flow();
        var percent = new Label { AutoSize = true, Text = $"{settings.ScalePercent}%", Anchor = AnchorStyles.Left };
        var scale = new TrackBar
        {
            Minimum = OverlaySettings.MinimumScale, Maximum = OverlaySettings.MaximumScale,
            Value = settings.ScalePercent, TickFrequency = 25, SmallChange = 5, LargeChange = 25,
            AccessibleName = "Overlay size, percent", Width = 180
        };
        scale.ValueChanged += (_, _) =>
        {
            percent.Text = $"{scale.Value}%";
            ChangeSettings(_settings with { ScalePercent = scale.Value });
        };
        sizes.Controls.Add(scale);
        sizes.Controls.Add(percent);
        Row(content, sizes);

        var buttons = Flow();
        _toggle = MakeButton("Hide game overlay", true);
        _preview = MakeButton("Show preview");
        _elevate = MakeButton("Restart as administrator");
        _toggle.Click += (_, _) => OverlayToggled?.Invoke();
        _preview.Click += (_, _) => PreviewToggled?.Invoke();
        _elevate.Click += (_, _) => RunElevated?.Invoke();
        _elevate.Visible = false;
        buttons.Controls.AddRange([_toggle, _preview, _elevate]);
        Row(content, buttons);
        _hint.Dock = DockStyle.Fill;
        Row(content, _hint);

        Row(content, Heading("Tablet connection"));
        var tabletButtons = Flow();
        _tablet = MakeButton("Start LAN stream");
        _tablet.Click += (_, _) => TabletToggled?.Invoke();
        tabletButtons.Controls.Add(_tablet);
        _usb = MakeButton("Start USB stream");
        _usb.Click += (_, _) => UsbToggled?.Invoke();
        tabletButtons.Controls.Add(_usb);
        _firewall = MakeButton("Allow LAN access");
        _firewall.Click += (_, _) => TabletFirewallRequested?.Invoke();
        _firewall.Visible = false;
        tabletButtons.Controls.Add(_firewall);
        Row(content, tabletButtons);
        _tabletStatus.Dock = DockStyle.Fill;
        Row(content, _tabletStatus);
        _usbStatus.Dock = DockStyle.Fill;
        Row(content, _usbStatus);
        Resize += (_, _) => { if (WindowState == FormWindowState.Minimized) Hide(); };
        ResumeLayout(true);
    }

    protected override void OnLoad(EventArgs e)
    {
        var work = Screen.FromControl(this).WorkingArea;
        if (Width > work.Width || Height > work.Height)
        {
            Size = new Size(Math.Min(Width, work.Width), Math.Min(Height, work.Height));
            Location = new Point(Math.Clamp(Left, work.Left, Math.Max(work.Left, work.Right - Width)),
                Math.Clamp(Top, work.Top, Math.Max(work.Top, work.Bottom - Height)));
        }
        base.OnLoad(e);
    }

    private void ChangeSettings(OverlaySettings next)
    {
        _settings = next;
        SettingsChanged?.Invoke(next);
    }

    public void SetSettings(OverlaySettings settings) => _settings = settings;

    private static TableLayoutPanel Stack(DockStyle dock)
    {
        var stack = new TableLayoutPanel
        {
            ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = dock, Margin = new Padding(0, 2, 0, 4)
        };
        stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return stack;
    }

    private static void Row(TableLayoutPanel panel, Control child)
    {
        int index = panel.RowCount++;
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.Controls.Add(child, 0, index);
    }

    private static FlowLayoutPanel Flow() => new()
    {
        AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true,
        Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 4)
    };

    private Label Heading(string text) => new()
    {
        AutoSize = true, Text = text, ForeColor = Palette.Mint, Font = new Font(Font, FontStyle.Bold),
        Margin = new Padding(3, 10, 3, 3)
    };

    private static Button MakeButton(string label, bool primary = false)
    {
        var button = new Button
        {
            Text = label, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(8, 4, 8, 4), Margin = new Padding(3, 4, 5, 4),
            FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand, UseVisualStyleBackColor = false,
            BackColor = primary ? Palette.Mint : Palette.Panel, ForeColor = primary ? Palette.Ink : Palette.Text
        };
        button.FlatAppearance.BorderColor = primary ? Palette.Mint : Palette.Line;
        return button;
    }

    public void RefreshStatus(bool enabled, bool preview, string? error, bool accessDenied, bool hotkey,
        TrackingStatus status, GpuReading sensors, bool sharing)
    {
        _toggle.Text = enabled ? "Hide game overlay" : "Show game overlay";
        _preview.Text = preview ? "Hide preview" : "Show preview";
        _elevate.Visible = accessDenied;
        _capture.ForeColor = error is null ? Palette.Mint : Palette.Amber;
        _capture.Text = (error, accessDenied, enabled, preview, status) switch
        {
            (_, _, false, _, _) when !sharing => "Game overlay hidden",
            (_, _, _, true, _) => "Preview active · FPS and frame time are sample values",
            (not null, true, _, _, _) => "FPS capture requires administrator access",
            (not null, _, _, _, _) => $"Could not start frame capture · {error.Split(':')[0]}",
            _ when enabled && _settings.VisibleMetrics == Metric.None && !sharing => "Choose which metrics to display",
            (_, _, _, _, TrackingStatus.SettingsFocused) => "Return to the game to capture frames",
            (_, _, _, _, TrackingStatus.ReceivingFrames) => "Capturing DirectX frames",
            (_, _, _, _, TrackingStatus.WaitingForFrames) => "Game detected · Waiting for frames",
            _ => "Waiting for an active DirectX game"
        };
        _gpu.Text = sensors.Available ? $"{sensors.Name}  ·  Sensors active" : sensors.Name;
        _hint.Text = _lastError ?? (hotkey ? "Ctrl + Shift + F10  ·  Toggle overlay"
            : "Shortcut already in use. Toggle the overlay from the notification area.");
    }

    public void RefreshTablet(bool started, string? addresses, string? code, string? error, string? firewallStatus)
    {
        _tablet.Text = started ? "Stop LAN stream" : "Start LAN stream";
        _firewall.Visible = started && error is null;
        _tabletStatus.ForeColor = error is null ? Palette.Muted : Palette.Amber;
        _tabletStatus.Text = error ?? (started
            ? string.IsNullOrEmpty(addresses) ? "Waiting for a local network address…"
                : $"PC address: {addresses}  ·  Code: {code}\n" + (firewallStatus ?? "Connect the tablet to the same network and enter these details in Metrik.")
            : "LAN streaming is off.");
    }

    public void RefreshUsb(bool started, string? code, string? status)
    {
        _usb.Text = started ? "Stop USB stream" : "Start USB stream";
        _usbStatus.Text = started ? $"USB · Code {code}\n{status}" : status ?? "USB streaming is off.";
    }

    public string? ChooseAdb()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Select adb.exe from Android SDK Platform-Tools",
            Filter = "Android Debug Bridge (adb.exe)|adb.exe",
            CheckFileExists = true
        };
        return dialog.ShowDialog(this) == DialogResult.OK &&
            string.Equals(Path.GetFileName(dialog.FileName), "adb.exe", StringComparison.OrdinalIgnoreCase) ? dialog.FileName : null;
    }

    public void ShowError(string message) => _lastError = message;

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x0312 && (int)m.WParam == 1) HotkeyPressed?.Invoke();
        base.WndProc(ref m);
    }
}

internal sealed class HudWindow : Form
{
    private readonly Font _font = new("Segoe UI", 14, FontStyle.Bold, GraphicsUnit.Pixel);
    private readonly StringFormat _format = new(StringFormat.GenericTypographic) { FormatFlags = StringFormatFlags.NoWrap };
    private string _line = string.Empty;
    private SizeF _naturalSize;
    private float _scale = 1;

    public HudWindow()
    {
        BackColor = Palette.Ink;
        DoubleBuffered = true;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        TopMost = true;
        Opacity = 0.88;
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams parameters = base.CreateParams;
            parameters.ExStyle |= 0x08000000 | 0x00000020 | 0x00000080 | 0x00080000;
            return parameters;
        }
    }

    public void Display(Rectangle area, string line, int percent)
    {
        if (line.Length == 0)
        {
            if (Visible) Hide();
            return;
        }

        bool changed = _line != line;
        if (changed)
        {
            _line = line;
            using var g = CreateGraphics();
            g.PageUnit = GraphicsUnit.Pixel;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            SizeF text = g.MeasureString(line, _font, PointF.Empty, _format);
            _naturalSize = new SizeF(text.Width + 16, text.Height + 6);
        }
        int inset = Math.Max(2, (int)Math.Round(6 * DeviceDpi / 96f));
        float requested = DeviceDpi / 96f * percent / 100f;
        float scale = Math.Min(requested, Math.Max(1, area.Width - 2 * inset) / _naturalSize.Width);
        var bounds = new Rectangle(area.Left + inset, area.Top + inset,
            (int)Math.Ceiling(_naturalSize.Width * scale), (int)Math.Ceiling(_naturalSize.Height * scale));
        bool scaled = _scale != scale;
        _scale = scale;
        if (Bounds != bounds) Bounds = bounds;
        if (changed || scaled) Invalidate();
        if (!Visible) Show();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.PageUnit = GraphicsUnit.Pixel;
        g.ScaleTransform(_scale, _scale);
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        using var text = new SolidBrush(Palette.Text);
        g.DrawString(_line, _font, text, 8, 3, _format);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _font.Dispose();
            _format.Dispose();
        }
        base.Dispose(disposing);
    }
}
