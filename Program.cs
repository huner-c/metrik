using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

namespace Metrik;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args is ["--allow-tablet"])
        {
            Environment.ExitCode = AllowTabletThroughFirewall();
            return;
        }

        if (args.Length >= 2 && args[0] == "--probe")
        {
            Probe(args);
            return;
        }

        if (args is ["--wait-for", var previous] && int.TryParse(previous, out int pid))
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                process.WaitForExit(8000);
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception) { }
        }

        using var instance = new Mutex(true, @"Local\Metrik.Overlay", out bool first);
        if (!first) return;
        ApplicationConfiguration.Initialize();
        using var app = new MetrikApp();
        Application.Run(app);
        // Keep the instance mutex rooted until the tray, HUD and trace have been torn down.
        GC.KeepAlive(instance);
    }

    private static int AllowTabletThroughFirewall()
    {
        try
        {
            string program = Environment.ProcessPath!;
            string id = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(program.ToUpperInvariant())))[..12];
            dynamic policy = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2", throwOnError: true)!)!;
            dynamic rule = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FWRule", throwOnError: true)!)!;
            rule.Name = $"Metrik Tablet ({id})";
            rule.Description = "Allows local Metrik tablet telemetry.";
            rule.ApplicationName = program;
            rule.Direction = 1; // Inbound
            rule.Action = 1; // Allow
            rule.Protocol = 17; // UDP
            rule.LocalPorts = TabletServer.Port.ToString(CultureInfo.InvariantCulture);
            rule.RemoteAddresses = "LocalSubnet";
            rule.InterfaceTypes = "Lan,Wireless";
            rule.Profiles = int.MaxValue; // Includes home networks configured as Public.
            rule.EdgeTraversal = false;
            rule.Enabled = true;
            policy.Rules.Add(rule);
            return 0;
        }
        catch (Exception e) when (e is COMException or UnauthorizedAccessException)
        {
            return e.HResult;
        }
    }

    private static void Probe(string[] args)
    {
        using var capture = new FrameCapture();
        using var gpu = new GpuTelemetry();
        GpuReading sensors = gpu.Read();
        using var output = new StreamWriter(args[1]) { AutoFlush = true };
        output.WriteLine(sensors);
        output.WriteLine(capture.Error ?? "ETW OK");
        if (args.Length < 3 || !uint.TryParse(args[2], out uint pid)) return;
        capture.SetTarget(pid);
        for (int i = 0; i < 12 && capture.Error is null; i++)
        {
            Thread.Sleep(500);
            if (capture.Read() is { } frame) output.WriteLine(frame);
        }
    }
}

internal sealed class MetrikApp : ApplicationContext
{
    private const int HotkeyId = 1;
    private readonly Icon _icon = LoadIcon();
    private readonly ControlWindow _control;
    private OverlaySettings _settings = OverlaySettings.Load();
    private readonly HudWindow _hud = new();
    private readonly NotifyIcon _tray;
    private readonly ContextMenuStrip _menu = new();
    private readonly ToolStripMenuItem _toggle;
    private readonly ToolStripMenuItem _previewItem;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 180 };
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _sensorWorker;
    private readonly FrameCapture _capture = new();
    private readonly bool _hotkey;
    private volatile GpuReading _gpu = new("Reading GPU sensors…");
    private bool _enabled;
    private bool _preview;
    private TabletFeed? _tabletFeed;
    private TabletServer? _tablet;
    private UsbTabletLink? _usb;
    private string? _tabletError;
    private string? _usbError;
    private string? _firewallStatus;
    private long _nextTabletUpdate;
    private NativeWindows.Candidate? _target;
    private long _lastForeground;
    private string? _runtime;
    private string _process = string.Empty;

    public MetrikApp()
    {
        _enabled = _settings.OverlayVisible;
        _control = new ControlWindow(_settings, _icon);
        _control.SettingsChanged += settings =>
        {
            SaveSettings(settings);
            Refresh();
        };
        _sensorWorker = Task.Run(PollGpu);
        _control.OverlayToggled += ToggleOverlay;
        _control.PreviewToggled += TogglePreview;
        _control.TabletToggled += ToggleTablet;
        _control.UsbToggled += ToggleUsb;
        _control.TabletFirewallRequested += AllowTablet;
        _control.RunElevated += RunElevated;
        _control.FormClosed += (_, _) => ExitThread();
        _control.HotkeyPressed += ToggleOverlay;
        _hotkey = NativeWindows.RegisterHotKey(_control.Handle, HotkeyId, 0x4006, (uint)Keys.F10);

        _menu.Items.Add("Open Metrik", null, (_, _) => ShowControls());
        _toggle = new ToolStripMenuItem(null, null, (_, _) => ToggleOverlay());
        _previewItem = new ToolStripMenuItem("Show preview", null, (_, _) => TogglePreview());
        _menu.Items.Add(_toggle);
        _menu.Items.Add(_previewItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Exit", null, (_, _) => ExitThread());
        _tray = new NotifyIcon { Icon = _icon, Text = "Metrik", ContextMenuStrip = _menu, Visible = true };
        _tray.DoubleClick += (_, _) => ShowControls();

        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        if (_settings.TabletSharing) StartTablet();
        if (_settings.UsbSharing) StartUsb(chooseAdb: false);
        _control.Show();
        UpdateTabletControls();
        SyncMenu();
    }

    private static Icon LoadIcon()
    {
        using var resource = typeof(MetrikApp).Assembly.GetManifestResourceStream("Metrik.AppIcon")!;
        return new Icon(resource);
    }

    private async Task PollGpu()
    {
        try
        {
            using var reader = new GpuTelemetry();
            while (!_shutdown.IsCancellationRequested)
            {
                _gpu = reader.Read();
                await Task.Delay(1000, _shutdown.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (Exception e) when (e is ExternalException or DllNotFoundException or EntryPointNotFoundException)
        {
            _gpu = new("GPU sensors unavailable");
        }
    }

    private void Refresh()
    {
        nint foreground = NativeWindows.GetForegroundWindow();
        bool controlsFocused = foreground == _control.Handle;
        bool sharing = _tablet is { Error: null } || _usb is not null;
        var window = (_enabled && !_preview || sharing) ? FindTarget(foreground, Stopwatch.GetTimestamp()) : null;
        SelectTarget(window);
        Render(_capture.Read(), _capture.Error, controlsFocused);
    }

    private NativeWindows.Candidate? FindTarget(nint foreground, long now)
    {
        // Windows can briefly report no foreground window during a focus transition.
        bool transient = foreground == 0 || (_hud.IsHandleCreated && foreground == _hud.Handle);
        if (!transient)
        {
            var current = NativeWindows.GameCandidate(foreground);
            if (current.HasValue) _lastForeground = now;
            return current;
        }

        if (_target is not { } last || Stopwatch.GetElapsedTime(_lastForeground, now).TotalMilliseconds >= 550)
            return null;
        var previous = NativeWindows.GameCandidate(last.Handle);
        return previous?.ProcessId == last.ProcessId ? previous : null;
    }

    private void SelectTarget(NativeWindows.Candidate? window)
    {
        if (window?.Handle != _target?.Handle || window?.ProcessId != _target?.ProcessId)
        {
            uint pid = window?.ProcessId ?? 0;
            _capture.SetTarget(pid);
            _process = ReadProcessName(pid);
            _runtime = null;
        }
        _target = window;
    }

    private void Render(FrameReading? frame, string? error, bool controlsFocused)
    {
        GpuReading gpu = _gpu;
        if (frame.HasValue) _runtime = frame.Value.Runtime;
        var status = (controlsFocused, frame is { IsLastSample: false }, _runtime) switch
        {
            (true, _, _) => TrackingStatus.SettingsFocused,
            (_, true, _) => TrackingStatus.ReceivingFrames,
            (_, _, not null) => TrackingStatus.WaitingForFrames,
            _ => TrackingStatus.WaitingForGame
        };
        if (_tabletFeed is { } feed && Environment.TickCount64 >= _nextTabletUpdate)
        {
            _nextTabletUpdate = Environment.TickCount64 + 1000;
            string frameStatus = (error, _capture.AccessDenied, frame, status) switch
            {
                (not null, true, _, _) => "FPS capture requires administrator access",
                (not null, _, _, _) => "Frame capture unavailable",
                (_, _, { IsLastSample: true }, _) => "Last frame reading",
                (_, _, not null, _) => "Live DirectX capture",
                (_, _, _, TrackingStatus.SettingsFocused) => "Return to the game for FPS",
                _ => "Waiting for an active DirectX game"
            };
            feed.Publish(new(frame, _runtime is null ? null : _process, _runtime, frameStatus, gpu));
            if (_control.Visible) UpdateTabletControls();
        }
        if (_control.Visible)
            _control.RefreshStatus(_enabled, _preview, error, _capture.AccessDenied, _hotkey, status, gpu,
                _tablet is { Error: null } || _usb is not null);
        if (_preview && _enabled)
        {
            Rectangle screen = Screen.FromHandle(_control.Handle).WorkingArea;
            _hud.Display(screen, _settings.Format(new(119.8, 8.35, "DXGI"), gpu, "Sample game", true), _settings.ScalePercent);
        }
        else if (_enabled && _target is { } target && _runtime is not null && error is null)
        {
            _hud.Display(target.ClientBounds, _settings.Format(frame, gpu, _process, false, _runtime), _settings.ScalePercent);
        }
        else if (_hud.Visible) _hud.Hide();
    }

    private static string ReadProcessName(uint pid)
    {
        if (pid == 0) return string.Empty;
        try
        {
            using var process = Process.GetProcessById((int)pid);
            return process.ProcessName;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return pid.ToString(CultureInfo.InvariantCulture);
        }
    }

    private void ToggleOverlay()
    {
        _enabled = !_enabled;
        if (!_enabled) _preview = false;
        SaveSettings(_settings with { OverlayVisible = _enabled });
        SyncMenu();
    }

    private void TogglePreview()
    {
        _preview = !_preview;
        if (_preview && !_enabled)
        {
            _enabled = true;
            SaveSettings(_settings with { OverlayVisible = true });
        }
        SyncMenu();
    }

    private void ToggleTablet()
    {
        if (_tablet is { } tablet)
        {
            _tablet = null;
            tablet.Dispose();
            _tabletError = null;
            _firewallStatus = null;
            SaveSettings(_settings with { TabletSharing = false });
        }
        else StartTablet();
        RefreshTabletAfterToggle();
    }

    private void ToggleUsb()
    {
        if (_usb is { } usb)
        {
            _usb = null;
            usb.Dispose();
            _usbError = null;
            SaveSettings(_settings with { UsbSharing = false });
        }
        else StartUsb(chooseAdb: true);
        RefreshTabletAfterToggle();
    }

    private void RefreshTabletAfterToggle()
    {
        StopFeedIfIdle();
        _nextTabletUpdate = 0;
        UpdateTabletControls();
        Refresh();
    }

    private void StartTablet()
    {
        string code = GetTabletPin();
        try
        {
            _tablet = new TabletServer(code, _tabletFeed ??= new TabletFeed());
            _tabletError = null;
            if (!_settings.TabletSharing || code != _settings.TabletPin)
                SaveSettings(_settings with { TabletSharing = true, TabletPin = code });
        }
        catch (SocketException e)
        {
            _tabletError = e.SocketErrorCode == SocketError.AddressAlreadyInUse
                ? $"UDP port {TabletServer.Port} is already in use."
                : $"Could not start tablet streaming (socket error {e.ErrorCode}).";
            StopFeedIfIdle();
        }
    }

    private void StartUsb(bool chooseAdb)
    {
        string? adb = UsbTabletLink.FindAdb(_settings.AdbPath);
        if (adb is null && chooseAdb) adb = _control.ChooseAdb();
        if (adb is null)
        {
            _usbError = "Select adb.exe from Android SDK Platform-Tools to use USB.";
            return;
        }

        string code = GetTabletPin();
        _usb = new UsbTabletLink(adb, code, _tabletFeed ??= new TabletFeed());
        _usbError = null;
        if (!_settings.UsbSharing || code != _settings.TabletPin || adb != _settings.AdbPath)
            SaveSettings(_settings with { UsbSharing = true, TabletPin = code, AdbPath = adb });
    }

    private string GetTabletPin() => _settings.TabletPin is { Length: 6 } code && code.All(char.IsAsciiDigit)
        ? code : RandomNumberGenerator.GetInt32(1_000_000).ToString("D6", CultureInfo.InvariantCulture);

    private void StopFeedIfIdle()
    {
        if (_tablet is not null || _usb is not null) return;
        _tabletFeed?.Dispose();
        _tabletFeed = null;
    }

    private void UpdateTabletControls()
    {
        _control.RefreshTablet(_tablet is not null, _tablet?.Addresses, _settings.TabletPin,
            _tablet?.Error ?? _tabletError, _firewallStatus);
        _control.RefreshUsb(_usb is not null, _settings.TabletPin, _usb?.Status ?? _usbError);
    }

    private async void AllowTablet()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(Environment.ProcessPath!)
            {
                Arguments = "--allow-tablet", UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden,
            });
            if (process is null) return;
            await process.WaitForExitAsync();

            _firewallStatus = process.ExitCode == 0 ? "Local network access granted."
                : $"Could not grant local network access (0x{process.ExitCode:X8}).";
        }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223) { }
        catch (Win32Exception e) { _firewallStatus = $"Could not open the firewall prompt (Windows error {e.NativeErrorCode})."; }
        if (!_control.IsDisposed) UpdateTabletControls();
    }

    private void SaveSettings(OverlaySettings settings)
    {
        _settings = settings;
        _control.SetSettings(settings);
        if (!settings.Save()) _control.ShowError("Settings could only be applied for this session.");
    }

    private void SyncMenu()
    {
        _toggle.Text = _enabled ? "Hide game overlay" : "Show game overlay";
        _previewItem.Text = _preview ? "Hide preview" : "Show preview";
        Refresh();
    }

    private void ShowControls()
    {
        _control.Show();
        if (_control.WindowState == FormWindowState.Minimized) _control.WindowState = FormWindowState.Normal;
        _control.Activate();
        UpdateTabletControls();
        Refresh();
    }

    private void RunElevated()
    {
        // Send a new instance only after the old one has released its single-instance mutex.
        // The helper process is this same executable, launched with a one-shot handoff argument.
        try
        {
            using Process? elevated = Process.Start(new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = $"--wait-for {Environment.ProcessId}"
            });
            if (elevated is not null) ExitThread();
        }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223) { }
        catch (Win32Exception e) { _control.ShowError($"Could not restart as administrator (Windows error {e.NativeErrorCode})."); }
    }

    protected override void ExitThreadCore()
    {
        _timer.Stop();
        _tray.Visible = false;
        _shutdown.Cancel();
        base.ExitThreadCore();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (_hotkey && _control.IsHandleCreated) NativeWindows.UnregisterHotKey(_control.Handle, HotkeyId);
            _timer.Dispose();
            _tray.Dispose();
            _menu.Dispose();
            _usb?.Dispose();
            _tablet?.Dispose();
            _tabletFeed?.Dispose();
            _capture.Dispose();
            _shutdown.Cancel();
            _sensorWorker.GetAwaiter().GetResult();
            _shutdown.Dispose();
            _hud.Dispose();
            _control.Dispose();
            _icon.Dispose();
        }
        base.Dispose(disposing);
    }
}

internal static class NativeWindows
{
    internal readonly record struct Candidate(nint Handle, uint ProcessId, Rectangle ClientBounds);

    public static Candidate? GameCandidate(nint window)
    {
        if (window == 0 || window == GetShellWindow() || !IsWindowVisible(window) || IsIconic(window)) return null;
        GetWindowThreadProcessId(window, out uint pid);
        if (pid == 0 || pid == Environment.ProcessId || !GetClientRect(window, out Rect rect) ||
            rect.Right < 280 || rect.Bottom < 180) return null;
        var topLeft = new Point();
        if (!ClientToScreen(window, ref topLeft)) return null;
        var bounds = new Rectangle(topLeft.X, topLeft.Y, rect.Right, rect.Bottom);
        var visibleArea = Rectangle.Intersect(bounds, Screen.FromHandle(window).Bounds);
        return visibleArea.Width >= 280 && visibleArea.Height >= 180 ? new(window, pid, visibleArea) : null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] public static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern nint GetShellWindow();
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint pid);
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint window, out Rect rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(nint window, ref Point point);
    [DllImport("user32.dll")] public static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(nint window, int id);
}
