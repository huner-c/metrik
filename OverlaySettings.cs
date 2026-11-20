using System.Text.Json;

namespace Metrik;

[Flags]
internal enum Metric
{
    None = 0, Fps = 1, FrameTime = 2, Load = 4, Temperature = 8, Hotspot = 16,
    Clock = 32, VramUsed = 64, VramTotal = 128, Runtime = 256, Process = 512
}

internal sealed record OverlaySettings
{
    public const int MinimumScale = 50, MaximumScale = 200;
    public Metric VisibleMetrics { get; init; } = Metric.Fps | Metric.FrameTime | Metric.Load | Metric.Temperature;
    public int ScalePercent { get; init; } = 100;
    public bool OverlayVisible { get; init; } = true;
    public bool TabletSharing { get; init; }
    public bool UsbSharing { get; init; }
    public string? TabletPin { get; init; }
    public string? AdbPath { get; init; }

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Metrik", "overlay.json");

    public static OverlaySettings Load()
    {
        try
        {
            var settings = JsonSerializer.Deserialize<OverlaySettings>(File.ReadAllText(FilePath)) ?? new();
            return settings with { ScalePercent = Math.Clamp(settings.ScalePercent, MinimumScale, MaximumScale) };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return new();
        }
    }

    public bool Save()
    {
        string path = FilePath;
        string temp = path + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(temp, JsonSerializer.Serialize(this));
            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    public string Format(FrameReading? frame, GpuReading gpu, string process, bool preview, string? knownRuntime = null)
    {
        var fields = new List<string>(10);
        if (VisibleMetrics.HasFlag(Metric.Fps)) fields.Add(frame is { } fps ? $"FPS {fps.Fps:0}" : "FPS —");
        if (VisibleMetrics.HasFlag(Metric.FrameTime)) fields.Add(frame is { } timing ? $"{timing.FrameMilliseconds:0.0} ms" : "— ms");
        if (frame is { IsLastSample: true } && fields.Count > 0) fields[^1] += " (son)";
        if (VisibleMetrics.HasFlag(Metric.Load)) fields.Add($"GPU {Value(gpu.Load, "%")}");
        if (VisibleMetrics.HasFlag(Metric.Temperature)) fields.Add($"Sıcaklık {Value(gpu.Temperature, " °C")}");
        if (VisibleMetrics.HasFlag(Metric.Hotspot)) fields.Add($"Hotspot {Value(gpu.Hotspot, " °C")}");
        if (VisibleMetrics.HasFlag(Metric.Clock)) fields.Add($"Saat {Value(gpu.Clock, " MHz")}");
        bool used = VisibleMetrics.HasFlag(Metric.VramUsed);
        bool total = VisibleMetrics.HasFlag(Metric.VramTotal);
        if (used || total)
        {
            string usage = gpu.UsedGiB?.ToString("0.0") ?? "—";
            string capacity = gpu.TotalGiB?.ToString("0.#") ?? "—";
            if (used && total) fields.Add($"VRAM {usage}/{capacity} GiB");
            else if (used) fields.Add($"VRAM {usage} GiB");
            else fields.Add($"Toplam VRAM {capacity} GiB");
        }
        if (VisibleMetrics.HasFlag(Metric.Runtime)) fields.Add(frame?.Runtime ?? knownRuntime ?? "API —");
        if (VisibleMetrics.HasFlag(Metric.Process)) fields.Add(process);
        if (fields.Count == 0) return string.Empty;
        if (preview) fields.Insert(0, "Önizleme");
        return string.Join("   ·   ", fields);
    }

    private static string Value(int? value, string unit) => value is int v ? $"{v}{unit}" : "—";
}
