using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

namespace Metrik;

internal sealed record CpuReading(string Name, double? Load = null, double? Clock = null,
    int LogicalProcessors = 0, float? Temperature = null, float? PackagePower = null,
    double? MemoryUsedGiB = null, double? MemoryTotalGiB = null);

internal sealed class CpuTelemetry : IDisposable
{
    private const double BytesPerGiB = 1024d * 1024 * 1024;
    private readonly string _name = (Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0",
        "ProcessorNameString", null) as string)?.Trim() ?? "CPU";
    private readonly nint _query;
    private readonly nint _clock;
    private readonly HttpClient _hardwareMonitor = new(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false })
    {
        Timeout = TimeSpan.FromMilliseconds(650),
        MaxResponseContentBufferSize = 1_048_576
    };
    private (ulong Idle, ulong Total)? _previous;
    private long _nextPackageRead;
    private (float? Temperature, float? Power) _package;

    public CpuTelemetry()
    {
        if (PdhOpenQuery(null, 0, out _query) == 0)
            PdhAddEnglishCounter(_query, @"\Processor Information(_Total)\Processor Frequency", 0, out _clock);
    }

    public async ValueTask<CpuReading> Read(CancellationToken stop)
    {
        double? load = null;
        if (GetSystemTimes(out ulong idle, out ulong kernel, out ulong user))
        {
            ulong total = kernel + user; // Kernel time includes idle time.
            if (_previous is { } previous && total > previous.Total && idle >= previous.Idle)
                load = Math.Clamp(100d * (1d - (idle - previous.Idle) / (double)(total - previous.Total)), 0, 100);
            _previous = (idle, total);
        }

        double? clock = null;
        if (_clock != 0 && PdhCollectQueryData(_query) == 0 &&
            PdhGetFormattedCounterValue(_clock, 0x200, 0, out CounterValue value) == 0 &&
            value.Status <= 1 && value.Value is > 0 and <= 15000)
            clock = value.Value;

        long now = Environment.TickCount64;
        if (now >= _nextPackageRead)
        {
            _package = await ReadPackage(stop).ConfigureAwait(false);
            _nextPackageRead = Environment.TickCount64 + (_package == (null, null) ? 15000 : 3000);
        }

        var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        bool hasMemory = GlobalMemoryStatusEx(ref memory) && memory.AvailablePhysical <= memory.TotalPhysical;
        return new(_name, load, clock, Environment.ProcessorCount, _package.Temperature, _package.Power,
            hasMemory ? (memory.TotalPhysical - memory.AvailablePhysical) / BytesPerGiB : null,
            hasMemory ? memory.TotalPhysical / BytesPerGiB : null);
    }

    private async Task<(float? Temperature, float? Power)> ReadPackage(CancellationToken stop)
    {
        // The optional LibreHardwareMonitor server runs on the same PC.
        try
        {
            byte[] body = await _hardwareMonitor.GetByteArrayAsync("http://127.0.0.1:8085/data.json", stop).ConfigureAwait(false);
            using var data = JsonDocument.Parse(body);
            float? temperature = null, fallback = null, power = null;
            ReadSensors(data.RootElement, ref temperature, ref fallback, ref power);
            return (temperature ?? fallback, power);
        }
        catch (Exception e) when (e is HttpRequestException or JsonException ||
            e is OperationCanceledException && !stop.IsCancellationRequested)
        {
            return (null, null);
        }
    }

    private static void ReadSensors(JsonElement node, ref float? temperature, ref float? fallback, ref float? power)
    {
        if (node.ValueKind != JsonValueKind.Object) return;
        string? id = GetString(node, "SensorId");
        if (id is not null && (id.StartsWith("/amdcpu/", StringComparison.Ordinal) ||
            id.StartsWith("/intelcpu/", StringComparison.Ordinal)))
        {
            string? name = GetString(node, "Text");
            if (id.Contains("/temperature/", StringComparison.Ordinal) &&
                ReadSensorValue(node, "°C") is > 0 and <= 150 and var degrees)
            {
                if (name is "CPU Package" or "Core (Tdie)") temperature = Math.Max(temperature ?? degrees, degrees);
                else if (name == "Core (Tctl/Tdie)") fallback = Math.Max(fallback ?? degrees, degrees);
            }
            else if (name is ("CPU Package" or "Package") && id.Contains("/power/", StringComparison.Ordinal) &&
                ReadSensorValue(node, "W") is >= 0 and <= 1500 and var watts)
                power = Math.Max(power ?? watts, watts);
        }

        if (node.TryGetProperty("Children", out JsonElement children) && children.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement child in children.EnumerateArray())
                ReadSensors(child, ref temperature, ref fallback, ref power);
        }
    }

    private static string? GetString(JsonElement node, string field) =>
        node.TryGetProperty(field, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static float? ReadSensorValue(JsonElement node, string unit)
    {
        if (node.TryGetProperty("RawValue", out JsonElement raw) && raw.ValueKind == JsonValueKind.Number &&
            raw.TryGetSingle(out float number)) return number;
        var label = GetString(node, "Value")?.Trim();
        if (label is null) return null;
        bool fahrenheit = unit == "°C" && label.EndsWith("°F", StringComparison.Ordinal);
        if (!fahrenheit && !label.EndsWith(unit, StringComparison.Ordinal)) return null;
        if (!float.TryParse(label[..^unit.Length].Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture,
            out float value)) return null;
        return fahrenheit ? (value - 32) * 5 / 9 : value;
    }

    public void Dispose()
    {
        _hardwareMonitor.Dispose();
        if (_query != 0) PdhCloseQuery(_query);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length, Load;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile,
            TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct CounterValue
    {
        [FieldOffset(0)] public uint Status;
        [FieldOffset(8)] public double Value;
    }

    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQuery(string? source, nint userData, out nint query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounter(nint query, string path, nint userData, out nint counter);
    [DllImport("pdh.dll")] private static extern uint PdhCollectQueryData(nint query);
    [DllImport("pdh.dll")] private static extern uint PdhGetFormattedCounterValue(nint counter, uint format, nint type, out CounterValue value);
    [DllImport("pdh.dll")] private static extern uint PdhCloseQuery(nint query);
}
